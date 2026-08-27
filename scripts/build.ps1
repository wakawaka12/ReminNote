param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'ReminNote.sln'
. (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
$dotnetPath = Resolve-DotNetPath

& $dotnetPath restore $solutionPath --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "NuGet restore failed with exit code $LASTEXITCODE"
}

& $dotnetPath build $solutionPath --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}
