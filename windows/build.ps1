# Builds omux for Windows into windows\dist\omux and zips it. The build is self-contained:
# it needs no .NET install, only the Edge WebView2 Runtime that Windows 10/11 ship with.
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "dist\omux"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish Omux.csproj -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$zip = Join-Path $PSScriptRoot "dist\omux-$Runtime.zip"
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
"Built $out\omux.exe and $zip"
