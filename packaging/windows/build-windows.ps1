# Build the Windows x64 package.
#
# Needs the RecompOne checkout (scripts/setup_tools.sh) and the .NET 10 SDK. It
# does NOT need the disc: the launcher carries the inputs to a build and makes
# the game on the player's machine.
#
# Produces dist/Verdite2-<version>-win-x64.zip, and the Inno Setup installer too
# if iscc is on PATH.
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'win-x64'

$csproj = Join-Path $root 'Verdite2.Launcher\Verdite2.Launcher.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "==> publishing win-x64 ($version)"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

dotnet publish $csproj -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$zip = Join-Path $dist "Verdite2-$version-win-x64.zip"
Write-Host "==> $zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

if (Get-Command iscc -ErrorAction SilentlyContinue) {
    Write-Host "==> installer"
    $env:VERDITE2_VERSION = $version
    iscc (Join-Path $PSScriptRoot 'verdite2.iss')
    if ($LASTEXITCODE -ne 0) { throw "iscc failed" }
} else {
    Write-Host "==> iscc not on PATH; skipping the installer (the zip is built)"
}

Write-Host "done."
