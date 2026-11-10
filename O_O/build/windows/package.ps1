# Builds O_O for Windows (NativeAOT) and packs a portable .zip and an Inno Setup installer.
#   build/windows/package.ps1 -Arch x64|arm64 -Version 1.2.3
param(
  [ValidateSet("x64", "arm64")][string]$Arch = "x64",
  [string]$Version = "0.0.0"
)
$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot\..\.."              # the O_O folder
$Out = Join-Path $Root "build\out\windows-$Arch"
$Publish = Join-Path $Out "publish"
$Dist = Join-Path $Root "build\out"

Write-Host "==> Publishing NativeAOT ($Arch)"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
dotnet publish "$Root\src\Island.App" -c Release -r "win-$Arch" -p:PublishAot=true -p:Version=$Version -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# Debug symbols stay out of the package.
Get-ChildItem $Publish -Include *.pdb -Recurse | Remove-Item -Force

Write-Host "==> Portable zip"
$Zip = Join-Path $Dist "O_O-$Version-windows-$Arch.zip"
if (Test-Path $Zip) { Remove-Item $Zip -Force }
Compress-Archive -Path "$Publish\*" -DestinationPath $Zip

Write-Host "==> Installer"
# Simplified Chinese is one of Inno Setup 6's unofficial translations, not installed with it (pinned: an older translation
# works with any newer Inno Setup 6, which falls back to English for messages it lacks).
$Chinese = Join-Path $PSScriptRoot "ChineseSimplified.isl"
if (-not (Test-Path $Chinese)) {
  Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jrsoftware/issrc/is-6_4_3/Files/Languages/Unofficial/ChineseSimplified.isl" -OutFile $Chinese
}
$Iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $Iscc) { $Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $Iscc)) { throw "Inno Setup (ISCC.exe) not found" }
& $Iscc "/DAppVersion=$Version" "/DArch=$Arch" "/DSourceDir=$Publish" "/DOutputDir=$Dist" "/DIconFile=$Root\assets\icon\O_O.ico" "$PSScriptRoot\O_O.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Write-Host "==> Done"
Get-ChildItem $Dist -Filter "O_O-$Version-windows-$Arch*"
