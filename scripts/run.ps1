param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\windows\ReminNote.Windows\ReminNote.Windows.csproj'

if ([string]::IsNullOrWhiteSpace($env:WINDIR) -and -not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
    $env:WINDIR = $env:SystemRoot
}

. (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
$dotnetPath = Resolve-DotNetPath

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed; the application cannot start. Exit code: $LASTEXITCODE"
}

& $dotnetPath run --project $projectPath --configuration $Configuration --no-build --no-restore
exit $LASTEXITCODE
