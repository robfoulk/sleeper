<#
.SYNOPSIS
  Starts Sleeper.Host detached in the background and waits until it is ready.
.NOTES
  PID is written to .run/host.pid. Use stop-host.ps1 to stop it.
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5757',
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir   = Join-Path $repoRoot '.run'
$pidFile  = Join-Path $runDir 'host.pid'
$logFile  = Join-Path $runDir 'host.log'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (Test-Path $pidFile) {
    $existing = Get-Content $pidFile | Select-Object -First 1
    if ($existing -and (Get-Process -Id $existing -ErrorAction SilentlyContinue)) {
        Write-Host "Sleeper.Host already running (PID $existing). $Url" -ForegroundColor Yellow
        return
    }
    Remove-Item $pidFile -Force
}

$projectPath = Join-Path $repoRoot 'src\Sleeper.Host\Sleeper.Host.csproj'
Write-Host "Starting Sleeper.Host..." -ForegroundColor Cyan

$proc = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run','--project', $projectPath, '--no-launch-profile', '--', '--urls', $Url) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $logFile `
    -RedirectStandardError (Join-Path $runDir 'host.err.log') `
    -PassThru

$proc.Id | Out-File -Encoding ascii $pidFile

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "$Url/health" -UseBasicParsing -TimeoutSec 2
        if ($r.StatusCode -eq 200) {
            Write-Host "Sleeper.Host ready (PID $($proc.Id))" -ForegroundColor Green
            Write-Host "  MCP:     $Url/mcp"
            Write-Host "  Swagger: $Url/swagger"
            Write-Host "  Logs:    $logFile"
            return
        }
    } catch { Start-Sleep -Milliseconds 500 }
}

Write-Error "Sleeper.Host did not become ready within $TimeoutSeconds seconds. See $logFile."
