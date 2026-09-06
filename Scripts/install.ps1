# Registers DriverWatch to launch at logon in the interactive desktop session,
# elevated (installing a driver needs admin), and starts it now.
#
# It runs as a /target:winexe tray app, so there is no console window and no
# taskbar entry — the exe is launched directly, no wscript wrapper needed.
$ErrorActionPreference = "Stop"

$dir  = Join-Path $env:USERPROFILE "DriverWatch"
$exe  = Join-Path $dir "DriverWatch.exe"
$name = "DriverWatch"

if (-not (Test-Path $exe)) { throw "DriverWatch.exe is not built at $exe" }

# Free the exe so a redeploy can overwrite it.
Get-Process DriverWatch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$me        = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $me
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
                -DontStopIfGoingOnBatteries -StartWhenAvailable `
                -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
"TASK REGISTERED"

schtasks /run /tn $name | Out-Null
Start-Sleep -Seconds 2
$p = Get-Process DriverWatch -ErrorAction SilentlyContinue
if ($p) { "RUNNING pid=" + (($p | ForEach-Object { $_.Id }) -join ' ') }
else    { "NOT RUNNING - see $dir\driverwatch.log" }
