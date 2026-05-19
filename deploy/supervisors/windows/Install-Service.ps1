<#
.SYNOPSIS
    Installs / uninstalls dotnet-assembly-mcp as a per-user Scheduled Task on Windows.

.DESCRIPTION
    The Scheduled Task approach keeps the install per-user (no admin required), survives
    reboots (-AtLogOn trigger), and restarts the process if it exits non-zero. This is
    the Windows counterpart of the systemd --user unit shipped in
    deploy/supervisors/linux/.

.PARAMETER Action
    Install or Uninstall. Default: Install.

.PARAMETER TaskName
    Scheduled Task name. Default: dotnet-assembly-mcp.

.PARAMETER ExecutablePath
    Full path to the dotnet-assembly-mcp executable. Default resolves
    "$env:USERPROFILE\.dotnet\tools\dotnet-assembly-mcp.exe" (the `dotnet tool install -g`
    location). Override to point at a downloaded single-file binary.

.PARAMETER Url
    HTTP URL the server should listen on. Default: http://127.0.0.1:8788. Pinned to match
    the conventional port split with dotnet-diagnostics-mcp (which uses 8787).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-Service.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-Service.ps1 -Action Uninstall

.NOTES
    Verify:   curl http://127.0.0.1:8788/health
    Logs:     Get-WinEvent -LogName 'Microsoft-Windows-TaskScheduler/Operational' -MaxEvents 50
              Output of the server itself goes to stderr; capture by redirecting in the action
              (see -RedirectStandardError below) if you need persistent logs.
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install',

    [string]$TaskName = 'dotnet-assembly-mcp',

    [string]$ExecutablePath = "$env:USERPROFILE\.dotnet\tools\dotnet-assembly-mcp.exe",

    [string]$Url = 'http://127.0.0.1:8788'
)

$ErrorActionPreference = 'Stop'

if ($Action -eq 'Uninstall') {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'."
    } else {
        Write-Host "No scheduled task named '$TaskName' was found."
    }
    return
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Executable not found at '$ExecutablePath'. Install the tool first: 'dotnet tool install -g dotnet-assembly-mcp', or pass -ExecutablePath."
}

$workDir = Split-Path -Parent $ExecutablePath
$logDir = Join-Path $env:LOCALAPPDATA 'dotnet-assembly-mcp\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stderrLog = Join-Path $logDir 'server.stderr.log'

# Wrap in cmd so we can redirect stderr to a rolling file without losing the exit code.
$arguments = "/c `"$ExecutablePath`" 2>> `"$stderrLog`""
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $arguments -WorkingDirectory $workDir

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Seconds 30) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)  # 0 = unlimited

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

# ASP.NET Core honors ASPNETCORE_URLS; setting it user-wide is the easiest knob.
[Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $Url, 'User')

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "dotnet-assembly-mcp - static MCP server for .NET assemblies (listens on $Url)" `
    -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

# Liveness probe - the server exposes --health-check for exactly this gate.
$probeArgs = @('--health-check', "--url=$Url/health")
$probe = Start-Process -FilePath $ExecutablePath -ArgumentList $probeArgs -NoNewWindow -PassThru -Wait
if ($probe.ExitCode -ne 0) {
    Write-Warning "Scheduled task started but the health probe ($Url/health) failed. Check $stderrLog."
    exit 1
}

Write-Host "Installed scheduled task '$TaskName'. Server live at $Url/mcp."
Write-Host "Verify:  curl $Url/health"
Write-Host "Logs:    Get-Content '$stderrLog' -Tail 50 -Wait"
