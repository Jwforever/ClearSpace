param(
    [string]$Dotnet = "dotnet",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'tests\ClearSpace.Checks\ClearSpace.Checks.csproj'
$log = Join-Path $PSScriptRoot 'dist\clearspace-checks.txt'

New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
& $Dotnet run --project $project -c $Configuration -- $log
if ($LASTEXITCODE -ne 0) { throw "ClearSpace safety checks failed with exit code $LASTEXITCODE. See $log" }
& (Join-Path $PSScriptRoot 'publish.ps1') -Dotnet $Dotnet -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "ClearSpace publishing failed with exit code $LASTEXITCODE" }
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\ClearSpace.App\ClearSpace.App.csproj') -Encoding UTF8)).Project.PropertyGroup.Version
& (Join-Path $PSScriptRoot 'ui-smoke.ps1') -Executable (Join-Path $PSScriptRoot "dist\ClearSpace-$version-win-x64\ClearSpace.exe")
