param(
    [switch]$PurgeDevData
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'src'

if (Test-Path -LiteralPath $sourceRoot) {
    $buildDirectories = Get-ChildItem -LiteralPath $sourceRoot -Recurse -Directory -Force |
        Where-Object { $_.Name -in @('bin', 'obj') }

    foreach ($buildDirectory in $buildDirectories) {
        Remove-Item -LiteralPath $buildDirectory.FullName -Recurse -Force
    }
}

if ($PurgeDevData) {
    $developmentDataRoot = Join-Path $repositoryRoot '.devdata'
    if (Test-Path -LiteralPath $developmentDataRoot) {
        Get-ChildItem -LiteralPath $developmentDataRoot -Force |
            Remove-Item -Recurse -Force
    }
}

Write-Output 'Build outputs were cleaned. Production data is never targeted by default.'
