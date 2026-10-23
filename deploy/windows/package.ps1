# Windows packages for Karvon: an installer (Inno Setup: Start menu entry, `karvon` on the PATH, uninstall) and a .zip
# (unpack anywhere and run Karvon.exe). Karvon is published self-contained, so it needs no .NET runtime; working on
# solutions still needs the .NET SDK. Not signed: SmartScreen asks to confirm it the first time. Runs on Windows:
#   dotnet publish src/Karvon -c Release -r win-x64 --self-contained -o out -p:Version=1.2.3
#   deploy/windows/package.ps1 out 1.2.3 dist
# The installer needs Inno Setup's iscc (ISCC or on the PATH, as GitHub's Windows runners have it); without it the
# .zip is built.
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Dist
)
$ErrorActionPreference = 'Stop'

$Source = (Resolve-Path $Source).Path
New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$Dist = (Resolve-Path $Dist).Path
$Here = $PSScriptRoot

if (-not (Test-Path (Join-Path $Source 'Karvon.exe'))) {
    throw "$Source\Karvon.exe not found: publish Karvon first (see the header of this script)"
}

# ---- .zip ----
$Zip = Join-Path $Dist "karvon-studio-$Version-win-x64.zip"
if (Test-Path $Zip) { Remove-Item $Zip }
Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Zip
Write-Output $Zip

# ---- Installer ----
$Iscc = if ($env:ISCC) { $env:ISCC } else { (Get-Command iscc -ErrorAction SilentlyContinue)?.Source }
if (-not $Iscc) {
    $Default = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    if (Test-Path $Default) { $Iscc = $Default }
}
if (-not $Iscc) {
    Write-Warning 'Inno Setup (iscc) not found: the installer is skipped'
    exit 0
}
& $Iscc "/DAppVersion=$Version" "/DSourceDir=$Source" "/DOutputDir=$Dist" "/DIconFile=$Here\karvon.ico" (Join-Path $Here 'karvon.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw "iscc failed ($LASTEXITCODE)" }
Write-Output (Join-Path $Dist "Karvon-Studio-$Version-Setup.exe")
