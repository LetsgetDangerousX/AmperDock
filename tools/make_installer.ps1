# Build AmperDock's installer with Velopack.
#
#   powershell -ExecutionPolicy Bypass -File tools\make_installer.ps1 -Version 0.1.0
#
# Makes artifacts\releases\Ampersite.AmperDock-win-Setup.exe (installs for the current user, no admin needed) plus
# the update packages. To publish an update, upload artifacts\releases to a GitHub release:
#   vpk upload github --repoUrl https://github.com/LetsgetDangerousX/AmperDock --publish --releaseName "AmperDock 0.1.0" --tag v0.1.0 --token <a GitHub token>
# Installed copies check that page and update themselves (see src\AmperDock.App\Updates.cs).
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# the Velopack packaging tool
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk }

# a self-contained build: the installer works on PCs without .NET installed
$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src\AmperDock.App -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

vpk pack --packId Ampersite.AmperDock --packVersion $Version --packDir $publish --mainExe AmperDock.exe `
    --packTitle "AmperDock" --packAuthors "AmperLabs LLC" --icon src\AmperDock.App\Assets\AmperDock.ico `
    --outputDir (Join-Path $root "artifacts\releases")
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
Write-Host "Installer: $(Join-Path $root 'artifacts\releases\Ampersite.AmperDock-win-Setup.exe')"
