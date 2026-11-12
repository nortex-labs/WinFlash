#Requires -Version 5.1
<#
.SYNOPSIS
  Наводит порядок на флешке Ventoy с WinFlash: раскладывает образы по папкам, даёт понятные
  названия в меню загрузки, убирает служебное с глаз и кладёт «Что где лежит.txt».

.DESCRIPTION
  Ничего не удаляет — только переносит и переименовывает. Каждое действие записывается
  в _Архив\organize-<дата>.log, по нему всё можно вернуть вручную.

  Не трогает то, что должно лежать в корне:
    ventoy\               — настройки Ventoy (обязательно в корне);
    WinFlash\         — программа (её ищут загрузочный образ и установщик Windows);
    Start.exe, PortableApps\, Documents\ — меню PortableApps.com (работает только из корня).

.EXAMPLE
  .\tools\Organize-Flash.ps1 -Flash E:\ -WhatIf   # показать, что будет сделано
  .\tools\Organize-Flash.ps1 -Flash E:\
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)] [string] $Flash)

$ErrorActionPreference = 'Stop'
$Flash = (Resolve-Path $Flash).Path
if (-not (Test-Path (Join-Path $Flash 'ventoy'))) { throw "На $Flash нет папки ventoy — это не флешка Ventoy." }

$archive = Join-Path $Flash '_Архив'
$log = Join-Path $archive ("organize-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
function Log([string] $text) {
    Write-Host $text
    if (-not $WhatIfPreference) { New-Item -ItemType Directory -Force $archive | Out-Null; Add-Content $log $text -Encoding UTF8 }
}
function Move-Safe([string] $from, [string] $toDir, [string] $newName = $null) {
    if (-not (Test-Path -LiteralPath $from)) { return }
    $name = if ($newName) { $newName } else { Split-Path $from -Leaf }
    $to = Join-Path $toDir $name
    if ((Test-Path -LiteralPath $to) -and ((Resolve-Path -LiteralPath $from).Path -ne (Resolve-Path -LiteralPath $to).Path)) {
        Log "ПРОПУЩЕНО (уже есть): $from → $to"; return
    }
    if ($PSCmdlet.ShouldProcess("$from → $to", 'Перенести')) {
        New-Item -ItemType Directory -Force $toDir | Out-Null
        Move-Item -LiteralPath $from -Destination $to
        Log "ПЕРЕНЕСЕНО: $from → $to"
    }
}

# ------------------------------------------------------------ образы
$iso = Join-Path $Flash 'ISO'
$winDir = Join-Path $iso 'Windows'
$recDir = Join-Path $iso 'Recovery'
Get-ChildItem $iso -Filter *.iso -File -ErrorAction SilentlyContinue | ForEach-Object {
    $n = $_.Name
    if ($n -like 'WinFlash-Setup*') { return }                              # остаётся в ISO\ — первым в меню
    elseif ($n -match 'win' -and $n -notmatch 'hbcd|pe_') { Move-Safe $_.FullName $winDir }
    else { Move-Safe $_.FullName $recDir }
}

# ------------------------------------------------------------ программы для Windows
$programs = Join-Path $Flash 'Programs'
$tools = Join-Path $Flash 'Утилиты'
if ((Test-Path $programs) -and -not (Test-Path $tools)) {
    if ($PSCmdlet.ShouldProcess("$programs → $tools", 'Переименовать')) { Rename-Item $programs 'Утилиты'; Log "ПЕРЕИМЕНОВАНО: $programs → $tools" }
}

# ------------------------------------------------------------ ненужное — в архив
Move-Safe (Join-Path $Flash 'ventoy\ventoy.json.before-winflash') $archive 'ventoy.json (до WinFlash).json'

# ------------------------------------------------------------ ventoy.json: пути, понятные названия, только папка ISO
$vjPath = Join-Path $Flash 'ventoy\ventoy.json'
$vj = if (Test-Path $vjPath) { Get-Content $vjPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
function VPath([string] $full) { '/' + $full.Substring($Flash.TrimEnd('\').Length).TrimStart('\').Replace('\', '/') }
$images = @(Get-ChildItem $iso -Filter *.iso -File -Recurse -ErrorAction SilentlyContinue)

# Шаблоны автоустановки: старые записи переносим на новые пути образов (по имени файла).
$auto = @()
foreach ($e in @($vj.auto_install)) {
    if (-not $e) { continue }
    $leaf = Split-Path ($e.image -replace '/', '\') -Leaf
    $img = $images | Where-Object Name -eq $leaf | Select-Object -First 1
    if ($img) { $e.image = VPath $img.FullName }
    if ($auto.image -notcontains $e.image) { $auto += $e }
}
$names = @{
    'WinFlash-Setup.iso' = 'WinFlash — настроить и установить Windows'
    'Windows11.iso'          = 'Windows 11 — установка'
    'Windows10.iso'          = 'Windows 10 — установка'
    'HBCD_PE_x64.iso'        = "Hiren's BootCD PE — восстановление и утилиты"
    'memtest.iso'            = 'MemTest — проверка оперативной памяти'
}
$alias = @(foreach ($img in $images) {
    $title = $names[$img.Name]; if (-not $title) { continue }
    [pscustomobject]@{ image = VPath $img.FullName; alias = $title }
})
$out = [ordered]@{}
foreach ($p in $vj.PSObject.Properties) { $out[$p.Name] = $p.Value }
$out['auto_install'] = $auto
$out['menu_alias'] = $alias
# Меню Ventoy показывает только папку ISO, а не каждый .iso на флешке (в том числе внутри программ).
$control = @(@($vj.control) | Where-Object { $_ -and -not $_.PSObject.Properties['VTOY_DEFAULT_SEARCH_ROOT'] })
$control += [pscustomobject]@{ VTOY_DEFAULT_SEARCH_ROOT = '/ISO' }
$out['control'] = $control
$json = [pscustomobject]$out | ConvertTo-Json -Depth 6
# PowerShell 5.1 пишет апостроф как ' — меню Ventoy может показать это буквально.
$json = $json -replace '\\u0027', "'"
if ($PSCmdlet.ShouldProcess($vjPath, 'Обновить пути и названия в меню')) {
    Copy-Item $vjPath (Join-Path $archive ("ventoy.json.{0:yyyyMMdd-HHmmss}.bak" -f (Get-Date))) -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText($vjPath, $json, (New-Object Text.UTF8Encoding $false))
    Log "ОБНОВЛЁН: $vjPath (старая копия — в _Архив)"
} else { $json }

# ------------------------------------------------------------ служебное — скрыть
foreach ($h in 'ventoy', '_Архив') {
    $p = Join-Path $Flash $h
    if ((Test-Path $p) -and $PSCmdlet.ShouldProcess($p, 'Сделать скрытой')) {
        $i = Get-Item $p -Force; $i.Attributes = $i.Attributes -bor [IO.FileAttributes]::Hidden; Log "СКРЫТО: $p"
    }
}

# ------------------------------------------------------------ «Что где лежит»
$readme = Join-Path $Flash 'Что где лежит.txt'
if ($PSCmdlet.ShouldProcess($readme, 'Записать')) {
    $text = @'
ФЛЕШКА WINFLASH — ЧТО ГДЕ ЛЕЖИТ

  WinFlash.exe     Запустить настройку будущей Windows (можно на любом компьютере).
  WinFlash\        Программа, каталоги настроек и ваш выбор (config\selected.json).
                       Apps\ — сюда можно положить установщики программ для установки без интернета.
  ISO\                 Загрузочные образы. Ventoy показывает в меню только эту папку.
    WinFlash-Setup.iso   «WinFlash — настроить и установить Windows» в меню загрузки.
    Windows\                 Образы Windows 10 и 11.
    Recovery\                Восстановление и диагностика: Hiren's BootCD, MemTest.
  Утилиты\             Полезные программы для Windows: Rufus, HWiNFO, AdwCleaner, Everything, Sysinternals.
  Start.exe            Меню портативных программ PortableApps (Chrome, 7-Zip, VLC, CPU-Z и др.).
  PortableApps\, Documents\   Файлы PortableApps — не переносите, иначе меню перестанет работать.

Скрытые папки (видны, если включить «Показывать скрытые файлы»):
  ventoy\              Настройки загрузочного меню. Не удаляйте.
  _Архив\              Старые копии настроек и журнал переноса файлов. Можно удалить.

КАК ПЕРЕУСТАНОВИТЬ WINDOWS
  1. Загрузитесь с флешки (Boot Menu: F8 / F11 / F12 / Esc — зависит от материнской платы).
  2. Выберите «WinFlash — настроить и установить Windows».
  3. Выберите Windows 11 или 10, отметьте нужное и нажмите «Начать установку Windows».
'@
    [IO.File]::WriteAllText($readme, $text, (New-Object Text.UTF8Encoding $true))
    Log "ЗАПИСАНО: $readme"
}
Log 'Готово.'
