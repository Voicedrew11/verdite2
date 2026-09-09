# Build the Windows x64 package.
#
# Needs the vendored RecompOne built (scripts/setup_tools.sh) and the .NET 10 SDK. It
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

# One source of the number, for everything that names a build: the launcher's
# csproj reads this same file, so the zip and the installer cannot be named
# something other than what is inside them.
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if (-not $version) { throw "VERSION is empty" }

Write-Host "==> publishing win-x64 ($version)"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

dotnet publish $csproj -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Third-party licences the artifact is obliged to carry. Noto Sans is embedded in
# RecompOne.Runtime.dll (patches/recompone/0033, now upstream's own FontSet)
# and is SIL OFL 1.1, which
# requires its licence to travel with the font; the port's own MIT terms go beside
# it rather than only in the source tree.
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $licenses 'LICENSE')
Copy-Item (Join-Path $root 'patches\recompone\assets\NotoSans-OFL.txt') `
    (Join-Path $licenses 'NotoSans-OFL.txt')

$zip = Join-Path $dist "Verdite2-$version-win-x64.zip"
Write-Host "==> $zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

$env:VERDITE2_VERSION = $version

if (Get-Command iscc -ErrorAction SilentlyContinue) {
    Write-Host "==> installer"
    iscc (Join-Path $PSScriptRoot 'verdite2.iss')
    if ($LASTEXITCODE -ne 0) { throw "iscc failed" }
} else {
    Write-Host "==> iscc not on PATH; skipping the installer (the zip is built)"
}

Write-Host "done."
