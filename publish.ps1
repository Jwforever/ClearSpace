param(
    [string]$Dotnet = "dotnet",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$appProject = Join-Path $PSScriptRoot 'src\ClearSpace.App\ClearSpace.App.csproj'
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\ClearSpace.App\ClearSpace.App.csproj') -Encoding UTF8)).Project.PropertyGroup.Version
$output = Join-Path $PSScriptRoot "dist\ClearSpace-$version-win-x64"
$archive = Join-Path $PSScriptRoot "dist\ClearSpace-$version-win-x64.zip"

& $Dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -p:PublishReadyToRun=false --nologo -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
$exe = Join-Path $output 'ClearSpace.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output not found: $exe" }

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -LiteralPath $output -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ASCII
Write-Host "SHA256: $hash"
Write-Host "Version: $version"
Write-Host "Portable app: $exe"
Write-Host "Distribution archive: $archive"
