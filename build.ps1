# Builds Citrus.exe (Windows) from src\ using the C# compiler built into Windows.
# No SDK or downloads required. Run:  powershell -ExecutionPolicy Bypass -File build.ps1

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Error "The .NET Framework 4.0 compiler wasn't found. Install .NET Framework 4.x."; exit 1 }

Push-Location "$PSScriptRoot\src"
& $csc /nologo /optimize `
    /reference:Microsoft.VisualBasic.dll `
    /reference:System.Management.dll `
    /win32icon:citrus.ico `
    /out:"$PSScriptRoot\Citrus.exe" StrataCmd.cs
Pop-Location

if (Test-Path "$PSScriptRoot\Citrus.exe") { Write-Host "Built Citrus.exe" -ForegroundColor Green }
