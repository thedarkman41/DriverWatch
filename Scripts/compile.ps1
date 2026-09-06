# Compile DriverWatch.cs on the Windows host with the .NET Framework csc.
# /target:winexe so the tray app has no console window. Stops any running
# instance first (it holds the exe), proves the rebuild by deleting the old exe.
$ErrorActionPreference = "Stop"
$dir = Join-Path $env:USERPROFILE "DriverWatch"
$exe = Join-Path $dir "DriverWatch.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

for ($attempt = 1; $attempt -le 2; $attempt++) {
    Get-Process DriverWatch -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Remove-Item $exe -ErrorAction SilentlyContinue
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 `
        /r:System.Windows.Forms.dll /r:System.Drawing.dll `
        /r:System.Management.dll /r:Microsoft.CSharp.dll `
        /out:"$exe" "$dir\DriverWatch.cs" 2>&1 | Out-String | Write-Output
    if (Test-Path $exe) { "COMPILED"; break }
    if ($attempt -eq 2) { throw "compile failed: DriverWatch.exe was not produced (held open, or an error above)" }
    Start-Sleep -Seconds 1
}
