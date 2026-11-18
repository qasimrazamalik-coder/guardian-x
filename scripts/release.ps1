# Builds a self-contained release and packs the installer with Velopack.
#   .\scripts\release.ps1 -Version 1.0.1
# Output: Releases\GuardianX-win-Setup.exe (installer) plus the files for GitHub Releases.
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet test GuardianX.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src\GuardianX.App\GuardianX.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
vpk pack --packId GuardianX --packVersion $Version --packDir publish --mainExe GuardianX.exe `
  --packTitle "Guardian X" --packAuthors "Qasim Raza Malik" --icon src\GuardianX.App\Assets\guardian.ico `
  --shortcuts "Desktop,StartMenuRoot" --outputDir Releases
if ($LASTEXITCODE -ne 0) { throw "Packing failed" }
Get-ChildItem Releases | Select-Object Name, Length
