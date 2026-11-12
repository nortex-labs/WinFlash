#Requires -Version 5.1
<#
.SYNOPSIS
  Откатывает изменения реестра, сделанные Apply-Profile.ps1, по журналу undo-*.json.
  Удалённые приложения не возвращаются — их можно поставить из Microsoft Store.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $UndoFile = (Get-ChildItem "$env:SystemRoot\Setup\WinFlash\Logs\undo-*.json" -ErrorAction SilentlyContinue |
                          Sort-Object Name -Descending | Select-Object -First 1).FullName
)
if (-not $UndoFile -or -not (Test-Path -LiteralPath $UndoFile)) { throw 'Журнал отката не найден.' }

$entries = @(Get-Content -LiteralPath $UndoFile -Raw -Encoding UTF8 | ConvertFrom-Json)
[array]::Reverse($entries)   # откатываем в обратном порядке
foreach ($e in $entries) {
    if (-not $PSCmdlet.ShouldProcess("$($e.path)\$($e.name)", 'Вернуть')) { continue }
    if ($e.kind -eq 'task') {
        # Задача планировщика, отключённая настройкой «Отключить задачи телеметрии».
        if ($e.wasEnabled) { Enable-ScheduledTask -TaskPath $e.path -TaskName $e.name -ErrorAction SilentlyContinue | Out-Null }
        continue
    }
    if ($e.existed) {
        New-ItemProperty -LiteralPath $e.path -Name $e.name -PropertyType $e.type -Value $e.value -Force | Out-Null
    } else {
        Remove-ItemProperty -LiteralPath $e.path -Name $e.name -ErrorAction SilentlyContinue
    }
}
Write-Host "Откат выполнен по $UndoFile"
