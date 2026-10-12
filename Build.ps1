param([string]$VendorDir = (Join-Path $PSScriptRoot 'vendor'))

$ErrorActionPreference = 'Stop'
$faroVendorPath = (Resolve-Path -LiteralPath $VendorDir).Path
$faroProjectPath = Join-Path $PSScriptRoot 'FaroDriver.csproj'
$faroOutputPath = Join-Path $PSScriptRoot 'app'
& dotnet build $faroProjectPath -c Release -o $faroOutputPath "-p:VendorDir=$faroVendorPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Driver build failed. See the dependency instructions in README.md.' }
