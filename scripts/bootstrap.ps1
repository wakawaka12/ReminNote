param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'ReminNote.sln'
. (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
$dotnetPath = Resolve-DotNetPath

$sdkVersion = (& $dotnetPath --version).Trim()
if (-not $sdkVersion.StartsWith('10.')) {
    throw "A .NET 10 SDK is required. Current version: $sdkVersion"
}

& $dotnetPath restore $solutionPath --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "NuGet restore failed with exit code $LASTEXITCODE"
}

if (-not $SkipBuild) {
    & $dotnetPath build $solutionPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE"
    }
}
