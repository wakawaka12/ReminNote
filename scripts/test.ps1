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

$testProjects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -Recurse -File
foreach ($testProject in $testProjects) {
    # The test tree intentionally contains isolated harness projects that are
    # not part of the product solution. Restore every discovered test project
    # before the no-restore build so a clean checkout exercises the same gate.
    & $dotnetPath restore $testProject.FullName --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Test project restore failed with exit code ${LASTEXITCODE}: $($testProject.FullName)"
    }

    & $dotnetPath build $testProject.FullName --configuration $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Test project build failed with exit code ${LASTEXITCODE}: $($testProject.FullName)"
    }

    # xUnit.net v3 4.x is a self-contained Microsoft Testing Platform
    # executable. Running the produced executable avoids the legacy VSTest
    # adapter path, which reports zero tests under the .NET 10 SDK.
    $outputDirectory = Join-Path $testProject.DirectoryName "bin\$Configuration\net10.0"
    $testExecutable = Join-Path $outputDirectory "$($testProject.BaseName).exe"
    if (-not (Test-Path -LiteralPath $testExecutable)) {
        throw "Test executable was not produced: $testExecutable"
    }

    & $testExecutable -noLogo -noColor
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code ${LASTEXITCODE}: $($testProject.FullName)"
    }
}

& (Join-Path $PSScriptRoot 'verify-p0-07.ps1')
