<#
.SYNOPSIS
  Stops the background Sleeper.Host process started by start-host.ps1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pidFile  = Join-Path $repoRoot '.run\host.pid'

if (-not (Test-Path $pidFile)) {
    Write-Host "No PID file at $pidFile -- nothing to stop." -ForegroundColor Yellow
    return
}

$processId = (Get-Content $pidFile | Select-Object -First 1).Trim()
if (-not $processId) {
    Remove-Item $pidFile -Force
    return
}

$proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($null -eq $proc) {
    Write-Host "Process $processId not running. Cleaning up." -ForegroundColor Yellow
    Remove-Item $pidFile -Force
    return
}

Stop-Process -Id $processId -Force
Remove-Item $pidFile -Force
Write-Host "Stopped Sleeper.Host (PID $processId)." -ForegroundColor Green
