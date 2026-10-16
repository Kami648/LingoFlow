param([switch]$Audio, [switch]$Local, [switch]$Pipeline)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = Join-Path $projectRoot 'artifacts\test-build'
dotnet build (Join-Path $projectRoot 'src\LingoFlow\LingoFlow.csproj') -c Release -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$env:LINGOFLOW_DATA = Join-Path $projectRoot ('artifacts\test-data-' + [Guid]::NewGuid().ToString('N'))
$reports = Join-Path $projectRoot 'artifacts\verification'
New-Item -ItemType Directory -Force -Path $reports | Out-Null
$executable = Join-Path $outputPath 'LingoFlow.exe'
function Invoke-Check([string]$mode, [string]$name, [string]$extra = '') {
    $report = Join-Path $reports ($name + '.json')
    $arguments = $mode + ' ' + $extra + ' --out "' + $report + '"'
    $child = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($child.ExitCode -ne 0) { Get-Content -LiteralPath $report; throw ($name + ' failed') }
    Get-Content -LiteralPath $report
}
Invoke-Check '--self-test' 'self-test'
if ($Audio) { Invoke-Check '--capture-check' 'capture-check' }
if ($Local) { Invoke-Check '--local-check' 'local-check' '--translation' }
if ($Pipeline) { Invoke-Check '--pipeline-check' 'pipeline-check' }
