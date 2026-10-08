# Builds:
#   dist\DeskWall.exe        - single file, needs the .NET 9 Desktop Runtime (-SelfContained: bundles it)
#   dist\DeskWall-Setup.exe  - self-contained installer (runs on any Windows 10/11 x64 without .NET);
#                              started as "...Setup.exe" it installs itself per user (see src\Installer.cs)
param([switch]$SelfContained, [switch]$NoSetup)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$running = Get-Process DeskWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot\dist\*" }
if ($running) { Write-Host "Closing the running dist\DeskWall.exe..."; $running | Stop-Process; Start-Sleep -Milliseconds 500 }

$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish DeskWall.csproj -c Release -r win-x64 --self-contained $sc `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o dist
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Done: $PSScriptRoot\dist\DeskWall.exe"

if ($NoSetup) { return }
# uncompressed on purpose: a compressed bundle is unpacked into RAM on every start
dotnet publish DeskWall.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o obj\setup-publish
if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }
Copy-Item obj\setup-publish\DeskWall.exe dist\DeskWall-Setup.exe -Force
Write-Host "Installer: $PSScriptRoot\dist\DeskWall-Setup.exe"
