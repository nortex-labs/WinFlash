#Requires -Version 5.1
<#
.SYNOPSIS
  Собирает WinFlash и раскладывает готовую папку набора.

.EXAMPLE
  .\build.ps1                    # сборка в dist\WinFlash
  .\build.ps1 -Flash E:\         # сборка и копирование на флешку Ventoy (E:\WinFlash)

  На флешке обновляются программа, каталоги, скрипты и шаблон. Не трогаются:
  config\ (ваш выбор), Apps\ (установщики) и собственные пресеты custom-*.json.
#>
param(
    [string] $Flash,
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist\WinFlash'

dotnet build (Join-Path $root 'src\WinFlash\WinFlash.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root "src\WinFlash\bin\$Configuration\net48\WinFlash.exe") $dist
Copy-Item (Join-Path $root "src\WinFlash\bin\$Configuration\net48\WinFlash.exe.config") $dist -ErrorAction SilentlyContinue
foreach ($d in 'catalog', 'profiles', 'scripts', 'unattend', 'icons') { Copy-Item (Join-Path $root "kit\$d") $dist -Recurse }
Copy-Item (Join-Path $root 'src\WinFlash\WinFlash.ico') $dist
New-Item -ItemType Directory -Force (Join-Path $dist 'Apps'), (Join-Path $dist 'config') | Out-Null
Set-Content (Join-Path $dist 'Apps\ПРОЧТИ.txt') -Encoding UTF8 -Value @'
Сюда можно положить установщики программ, чтобы они ставились без интернета.
Имя файла должно подходить под маску "offline" из catalog\apps.json, например:
  ChromeSetup.msi, 7z2409-x64.exe, vlc-3.0.21-win64.exe, SteamSetup.exe
Если подходящего установщика нет, программа скачается через winget.
'@
Write-Host "Готово: $dist"

if ($Flash) {
    $target = Join-Path $Flash 'WinFlash'
    if (-not (Test-Path (Join-Path $Flash 'ventoy'))) { Write-Warning "На $Flash нет папки ventoy — это точно флешка Ventoy?" }
    New-Item -ItemType Directory -Force $target | Out-Null
    # /XD config Apps — не трогаем выбор пользователя и установщики; /XF custom-* — его пресеты.
    robocopy $dist $target /E /XD (Join-Path $dist 'config') (Join-Path $dist 'Apps') /XF 'custom-*.json' /NP /NJH /NJS | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $target 'Apps'), (Join-Path $target 'config') | Out-Null
    if (-not (Test-Path (Join-Path $target 'Apps\ПРОЧТИ.txt'))) { Copy-Item (Join-Path $dist 'Apps\ПРОЧТИ.txt') (Join-Path $target 'Apps') }

    # Быстрый запуск из корня флешки: копия программы сама находит папку WinFlash рядом.
    $launcher = Join-Path $Flash 'WinFlash.exe'
    Copy-Item (Join-Path $dist 'WinFlash.exe') $launcher -Force
    # Значок и подпись флешки в проводнике. autorun.inf ничего не запускает — Windows берёт из него только значок и имя.
    $autorun = Join-Path $Flash 'autorun.inf'
    if (-not (Test-Path $autorun)) {
        Set-Content $autorun -Encoding Unicode -Value "[autorun]`r`nicon=WinFlash\WinFlash.ico`r`nlabel=WinFlash`r`n"
        (Get-Item $autorun -Force).Attributes = 'Hidden'
    }
    Write-Host "Скопировано на флешку: $target (+ $launcher)"
}
