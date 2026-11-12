#Requires -Version 5.1
<#
.SYNOPSIS
  Применяет профиль настройки к работающей Windows.

.DESCRIPTION
  Запускается окном WinFlash.exe --apply при первом входе после установки
  или вручную на уже установленной системе. Нужны права администратора: меняет и HKLM,
  и HKCU текущего пользователя.

  Перед каждым изменением реестра сохраняет старое значение в журнал отката (undo-*.json),
  чтобы Undo-Profile.ps1 мог вернуть всё как было.

  Для окна прогресса печатает служебные строки:
    @@STEP|<номер>|<всего>|<текст>
    @@DONE|<ошибок>|<журнал>

.PARAMETER ProfilePath
  Профиль (JSON): powerPlan, tweaks[], removeApps[], installApps[].

.PARAMETER KitRoot
  Папка набора: catalog\tweaks.json, catalog\apps.json и (необязательно) Apps\ с установщиками.

.PARAMETER WhatIf
  Только показать, что будет сделано, ничего не менять.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ProfilePath,
    [string] $KitRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $LogDir  = "$env:SystemRoot\Setup\WinFlash\Logs"
)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$logFile  = Join-Path $LogDir "apply-$stamp.log"
$undoFile = Join-Path $LogDir "undo-$stamp.json"
$script:errors = 0

function Write-Log([string] $msg, [string] $level = 'INFO') {
    if ($level -eq 'ERROR') { $script:errors++ }
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'HH:mm:ss'), $level, $msg
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

$script:step = 0
function Write-Step([string] $text) {
    $script:step++
    Write-Host ('@@STEP|{0}|{1}|{2}' -f $script:step, $script:total, $text)
}

function Read-Json([string] $path) {
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

$cfg          = Read-Json $ProfilePath
$tweakCatalog = Read-Json (Join-Path $KitRoot 'catalog\tweaks.json')
$appCatalog   = Read-Json (Join-Path $KitRoot 'catalog\apps.json')

$tweakIds   = @($cfg.tweaks      | Where-Object { $_ })
$removeIds  = @($cfg.removeApps  | Where-Object { $_ })
$installIds = @($cfg.installApps | Where-Object { $_ })
$script:total = $tweakIds.Count + $removeIds.Count + $installIds.Count + 2   # + питание + завершение
Write-Log "Профиль: $($cfg.name)"

# ---------------------------------------------------------------- реестр
$undo = New-Object System.Collections.Generic.List[object]

function Set-RegValue($op) {
    $path = $op.path
    $existing = $null; $existed = $false
    if (Test-Path -LiteralPath $path) {
        $item = Get-ItemProperty -LiteralPath $path -Name $op.name -ErrorAction SilentlyContinue
        if ($null -ne $item) { $existed = $true; $existing = $item.($op.name) }
    }
    $undo.Add([pscustomobject]@{ path = $path; name = $op.name; type = $op.type; existed = $existed; value = $existing })

    if ($PSCmdlet.ShouldProcess("$path\$($op.name)", "= $($op.value)")) {
        if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -LiteralPath $path -Name $op.name -PropertyType $op.type -Value $op.value -Force | Out-Null
    }
}

function Disable-Task([string] $fullName) {
    # "\Microsoft\Windows\Feedback\Siuf\DmClient" → путь "\Microsoft\Windows\Feedback\Siuf\" и имя "DmClient".
    $i = $fullName.LastIndexOf('\')
    $taskPath = $fullName.Substring(0, $i + 1); $taskName = $fullName.Substring($i + 1)
    $task = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) { Write-Log "  задачи нет в этой версии Windows: $fullName"; return }
    $undo.Add([pscustomobject]@{ kind = 'task'; path = $taskPath; name = $taskName; wasEnabled = ($task.State -ne 'Disabled') })
    if ($PSCmdlet.ShouldProcess($fullName, 'Отключить задачу')) {
        Disable-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction Stop | Out-Null
    }
}

foreach ($id in $tweakIds) {
    $tweak = $tweakCatalog.tweaks | Where-Object id -eq $id
    if (-not $tweak) { Write-Step $id; Write-Log "Неизвестная настройка: $id" 'WARN'; continue }
    Write-Step $tweak.title
    try {
        foreach ($op in $tweak.reg) { Set-RegValue $op }
        foreach ($t in @($tweak.tasks | Where-Object { $_ })) {
            try { Disable-Task $t } catch { Write-Log "  не удалось отключить задачу ${t}: $($_.Exception.Message)" 'WARN' }
        }
        Write-Log "Настройка: $($tweak.title)"
    } catch {
        Write-Log "Ошибка в настройке «$($tweak.title)»: $($_.Exception.Message)" 'ERROR'
    }
}
$undo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $undoFile -Encoding UTF8
Write-Log "Журнал отката: $undoFile"

# ---------------------------------------------------------------- электропитание
Write-Step 'Схема электропитания'
$plans = @{
    balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
    high     = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
    ultimate = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
}
if ($cfg.powerPlan -and $plans.ContainsKey($cfg.powerPlan)) {
    $guid = $plans[$cfg.powerPlan]
    if ($PSCmdlet.ShouldProcess('Электропитание', $cfg.powerPlan)) {
        # Схемы «Высокая» и «Максимальная» на многих ПК скрыты — создаём копию и включаем её.
        $existing = powercfg /list | Select-String $guid
        if (-not $existing -and $cfg.powerPlan -ne 'balanced') {
            $out = powercfg /duplicatescheme $guid 2>&1
            if ("$out" -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $guid = $Matches[1] }
        }
        powercfg /setactive $guid
        if ($LASTEXITCODE -eq 0) { Write-Log "Электропитание: $($cfg.powerPlan)" }
        else { Write-Log "Не удалось включить схему $($cfg.powerPlan), остаётся текущая" 'WARN' }
    }
}

# ---------------------------------------------------------------- удаление встроенных приложений
if ($removeIds.Count) {
    $provisioned = Get-AppxProvisionedPackage -Online
    foreach ($id in $removeIds) {
        $title = ($appCatalog.remove | Where-Object id -eq $id).title
        if (-not $title) { $title = $id }
        Write-Step "Удаление: $title"
        if (-not $PSCmdlet.ShouldProcess($id, 'Удалить приложение')) { continue }
        if ($id -eq 'OneDrive') {
            # OneDrive — обычная программа, а не приложение Store: удаляем её собственным установщиком.
            Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            $setups = @("$env:SystemRoot\System32\OneDriveSetup.exe", "$env:SystemRoot\SysWOW64\OneDriveSetup.exe",
                        "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDriveSetup.exe") + @(Get-ChildItem "$env:ProgramFiles\Microsoft OneDrive\*\OneDriveSetup.exe", "$env:LOCALAPPDATA\Microsoft\OneDrive\*\OneDriveSetup.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName)
            $ran = $false
            foreach ($s in $setups | Where-Object { Test-Path -LiteralPath $_ }) {
                $p = Start-Process $s '/uninstall' -PassThru -WindowStyle Hidden
                if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { } }
                $ran = $true
            }
            # Чтобы OneDrive не поставился заново новым пользователям этого ПК.
            & reg.exe load 'HKU\RK_Default' "$env:SystemDrive\Users\Default\NTUSER.DAT" 2>$null | Out-Null
            & reg.exe delete 'HKU\RK_Default\Software\Microsoft\Windows\CurrentVersion\Run' /v OneDriveSetup /f 2>$null | Out-Null
            [gc]::Collect(); & reg.exe unload 'HKU\RK_Default' 2>$null | Out-Null
            if ($ran) { Write-Log "Удалено приложение: $title" } else { Write-Log "OneDrive не найден — уже удалён" }
            continue
        }
        # Убираем и из образа (чтобы не появилось у новых пользователей), и у всех текущих.
        # Сразу после первого входа Store обновляет приложения, и Remove-AppxPackage может зависнуть —
        # поэтому каждое удаление идёт в отдельном потоке с таймаутом.
        $packages = @($provisioned | Where-Object DisplayName -eq $id | ForEach-Object PackageName)
        $ps = [powershell]::Create().AddScript({
            param($id, $packages)
            foreach ($p in $packages) { Remove-AppxProvisionedPackage -Online -PackageName $p -AllUsers -ErrorAction SilentlyContinue | Out-Null }
            Get-AppxPackage -Name $id -AllUsers -ErrorAction SilentlyContinue | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
        }).AddArgument($id).AddArgument($packages)
        $handle = $ps.BeginInvoke()
        if ($handle.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(90))) {
            try { $ps.EndInvoke($handle) | Out-Null } catch { }
            $ps.Dispose()
            Write-Log "Удалено приложение: $title"
        } else {
            [void] $ps.BeginStop($null, $null)   # не ждём: зависший вызов останется в фоне
            Write-Log "Не удалось удалить «$title» за 90 секунд — пропускаю (можно удалить вручную в «Параметрах»)" 'WARN'
        }
    }
}

# ---------------------------------------------------------------- установка программ
# Установщики ищем в Apps рядом с набором и в WinFlash\Apps на любом подключённом диске (флешке).
$appsDirs = @(Join-Path $KitRoot 'Apps')
$appsDirs += Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.Root 'WinFlash\Apps' }
$appsDirs = $appsDirs | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique

$script:wingetReady = $null
function Test-Winget {
    if ($null -ne $script:wingetReady) { return $script:wingetReady }
    # На свежей Windows winget (App Installer) регистрируется не сразу после входа — ждём до 3 минут.
    $deadline = (Get-Date).AddMinutes(3)
    do {
        if (Get-Command winget.exe -ErrorAction SilentlyContinue) { $script:wingetReady = $true; break }
        try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop } catch { }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    if (-not $script:wingetReady) { $script:wingetReady = $false; Write-Log 'winget недоступен: программы без установщика на флешке будут пропущены' 'WARN' }
    return $script:wingetReady
}

function Test-Internet {
    # PowerShell 5.1 по умолчанию не включает TLS 1.2.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    # msftconnecttest работает только по http — это та же проверка, что делает сама Windows.
    $urls = 'http://www.msftconnecttest.com/connecttest.txt', 'https://cdn.winget.microsoft.com/cache/source.msix', 'https://www.google.com/generate_204'
    for ($i = 0; $i -lt 24; $i++) {   # до 2 минут ждём сеть
        foreach ($u in $urls) {
            try {
                $r = Invoke-WebRequest $u -Method Head -UseBasicParsing -TimeoutSec 5
                if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400) { return $true }
            } catch { }
        }
        Start-Sleep -Seconds 5
    }
    return $false
}
$script:online = $null

# Запуск установщика с ограничением по времени: зависший установщик не должен держать всё окно.
$installTimeoutMin = 15
function Start-Timed([string] $file, [string] $arguments) {
    $p = Start-Process -FilePath $file -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $null = $p.Handle   # без этого в PowerShell 5.1 ExitCode после WaitForExit(мс) бывает пустым
    if (-not $p.WaitForExit($installTimeoutMin * 60 * 1000)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
        return $null
    }
    return $p.ExitCode
}

foreach ($id in $installIds) {
    $app = $appCatalog.install | Where-Object id -eq $id
    if (-not $app) { Write-Step $id; Write-Log "Неизвестная программа: $id" 'WARN'; continue }
    Write-Step "Установка: $($app.title)"
    if (-not $PSCmdlet.ShouldProcess($app.title, 'Установить')) { continue }

    $installer = $null
    if ($app.offline) {
        foreach ($dir in $appsDirs) {
            $installer = Get-ChildItem -LiteralPath $dir -Filter $app.offline -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($installer) { break }
        }
    }

    try {
        if ($installer) {
            Write-Log "Устанавливаю $($app.title) с флешки: $($installer.Name)"
            if ($installer.Extension -eq '.msi') {
                $code = Start-Timed msiexec.exe "/i `"$($installer.FullName)`" $($app.args)"
            } else {
                $code = Start-Timed $installer.FullName $app.args
            }
            if ($null -eq $code) { Write-Log "  установщик не завершился за $installTimeoutMin минут — пропускаю" 'ERROR' }
            elseif ($code -in 0, 3010, 1641) { Write-Log "  готово" }
            else { Write-Log "  установщик завершился с кодом $code" 'ERROR' }
        } else {
            if ($null -eq $script:online) { $script:online = Test-Internet }
            if (-not $script:online) { Write-Log "Пропущено $($app.title): нет интернета и нет установщика на флешке" 'ERROR'; continue }
            if (-not (Test-Winget))  { Write-Log "Пропущено $($app.title): нет winget и нет установщика на флешке" 'ERROR'; continue }
            Write-Log "Устанавливаю $($app.title) через winget"
            $winget = (Get-Command winget.exe).Source
            $common = "install --id $($app.winget) -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"
            $code = Start-Timed $winget "$common --scope machine"
            if ($null -ne $code -and $code -notin 0, -1978335189) {
                # Часть пакетов не поддерживает установку «для всех» — пробуем без --scope.
                $code = Start-Timed $winget $common
            }
            # -1978335189 = уже установлено
            if ($null -eq $code) { Write-Log "  установка не завершилась за $installTimeoutMin минут — пропускаю" 'ERROR' }
            elseif ($code -in 0, -1978335189) { Write-Log "  готово" }
            else { Write-Log "  winget завершился с кодом $code" 'ERROR' }
        }
    } catch {
        Write-Log "Ошибка установки $($app.title): $($_.Exception.Message)" 'ERROR'
    }
}

# ---------------------------------------------------------------- завершение
Write-Step 'Завершение'
if ($PSCmdlet.ShouldProcess('Проводник', 'Перезапустить, чтобы применить вид панели задач')) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
}
Write-Log "Готово. Ошибок: $script:errors. Журнал: $logFile"
Write-Host "@@DONE|$script:errors|$logFile"
