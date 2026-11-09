param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$Output = (Join-Path $PSScriptRoot 'dist\ui-smoke')
)

$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $Executable).Path
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
foreach ($name in @('ui-smoke.json', 'ui-smoke-error.txt')) {
    $priorResult = Join-Path $outputPath $name
    if (Test-Path -LiteralPath $priorResult) { Remove-Item -LiteralPath $priorResult -Force }
}

$process = Start-Process -FilePath $resolvedExe -ArgumentList ('--ui-smoke "' + $outputPath + '"') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id -Force
    throw 'UI smoke check timed out.'
}
$errorFile = Join-Path $outputPath 'ui-smoke-error.txt'
if (Test-Path -LiteralPath $errorFile) { throw (Get-Content -LiteralPath $errorFile -Raw -Encoding UTF8) }
$reportFile = Join-Path $outputPath 'ui-smoke.json'
if (-not (Test-Path -LiteralPath $reportFile)) { throw 'UI smoke report was not written.' }
$report = Get-Content -LiteralPath $reportFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $report.ok -or $report.unhandledException -or -not $report.windowIconLoaded -or $report.diskBinding -ne 'OneWay' -or
    -not $report.usageSortingVerified -or -not $report.unknownUsageLast -or -not $report.automaticRefreshVerified -or -not $report.refreshPreservesState -or
    $report.sortCombinations -ne 10 -or -not $report.residuePreviewVerified) {
    throw 'UI smoke regression check failed.'
}
Write-Host "UI checks passed: startup, icon, $($report.pages) pages, search, 10 sort combinations, residue preview and automatic uninstall refresh."
