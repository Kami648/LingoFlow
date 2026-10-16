param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\LingoFlow\LingoFlow.csproj') -Raw)).Project.PropertyGroup.Version
$publishPath = Join-Path $projectRoot ('dist\LingoFlow-' + $appVersion)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish (Join-Path $projectRoot 'src\LingoFlow\LingoFlow.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishPath -Force
if ($Installer) {
    $toolsPath = Join-Path $projectRoot 'artifacts\tools'
    New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
    $compiler = Join-Path $toolsPath 'inno\tools\ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler)) {
        $archive = Join-Path $toolsPath 'inno.zip'
        Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.4.3/tools.innosetup.6.4.3.nupkg' -OutFile $archive
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolsPath 'inno') -Force
    }
    $redist = Join-Path $toolsPath 'vc_redist.x64.exe'
    if (-not (Test-Path -LiteralPath $redist)) { Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redist }
    $signature = Get-AuthenticodeSignature -LiteralPath $redist
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Microsoft runtime signature verification failed' }
    $dependencies = Join-Path $publishPath 'Dependencies'
    New-Item -ItemType Directory -Force -Path $dependencies | Out-Null
    Copy-Item -LiteralPath $redist -Destination $dependencies -Force
    & $compiler (Join-Path $projectRoot 'installer\LingoFlow.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
}
$packageFiles = Get-ChildItem -LiteralPath $publishPath | Where-Object { $_.Name -in @('LingoFlow.exe', 'README.md', 'Assets', 'Dependencies') } | Select-Object -ExpandProperty FullName
Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $projectRoot ('dist\LingoFlow-' + $appVersion + '-win-x64.zip')) -Force
Get-ChildItem (Join-Path $projectRoot 'dist') -File | Select-Object Name,Length

