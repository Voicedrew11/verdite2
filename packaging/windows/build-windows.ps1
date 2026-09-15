# Build the Windows x64 package.
#
# Needs the vendored RecompOne built (scripts/setup_tools.sh) and the .NET 10 SDK. It
# does NOT need the disc: the launcher carries the inputs to a build and makes
# the game on the player's machine.
#
# Produces dist/Verdite2-<version>-win-x64.zip, and the Inno Setup installer too
# if iscc is on PATH. The zip's layout is Verdite2.exe (a tiny stub) + bin/ (the
# self-contained runtime) + content/ + licenses/; see "What the release contains"
# in docs/PACKAGING.md.
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

# The self-contained apphost has to sit next to its DLLs, so the whole publish
# lands in bin/ and a tiny Framework stub at the stage root is what Explorer
# and the installer shortcuts launch. content/ is lifted next to that stub so
# the payload is not mixed in with the runtime.
$bin = Join-Path $stage 'bin'
dotnet publish $csproj -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:DebugSymbols=false -o $bin
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$content = Join-Path $bin 'content'
if (-not (Test-Path $content)) { throw "publish did not stage content/" }
Move-Item $content (Join-Path $stage 'content')

# The csproj also copies LICENSE next to the published exe; the licence tree
# below is the one the zip carries, so do not leave a second copy in bin/.
$publishedLicense = Join-Path $bin 'LICENSE'
if (Test-Path $publishedLicense) { Remove-Item $publishedLicense }

Write-Host "==> stub"
$stubProj = Join-Path $PSScriptRoot 'Stub\Verdite2.Stub.csproj'
$stubOut = Join-Path $dist 'stub'
if (Test-Path $stubOut) { Remove-Item -Recurse -Force $stubOut }
dotnet publish $stubProj -c Release -o $stubOut
if ($LASTEXITCODE -ne 0) { throw "stub publish failed" }
Copy-Item (Join-Path $stubOut 'Verdite2.exe') (Join-Path $stage 'Verdite2.exe')
$stubConfig = Join-Path $stubOut 'Verdite2.exe.config'
if (Test-Path $stubConfig) {
    Copy-Item $stubConfig (Join-Path $stage 'Verdite2.exe.config')
}
Remove-Item -Recurse -Force $stubOut

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
