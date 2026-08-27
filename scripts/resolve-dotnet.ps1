function Resolve-DotNetPath {
    [CmdletBinding()]
    param()

    $standardDotNetPath = Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $standardDotNetPath) {
        $standardSdkList = & $standardDotNetPath --list-sdks 2>$null
        if ($standardSdkList -match '^10\.') {
            return $standardDotNetPath
        }
    }

    $dotnetCommands = Get-Command dotnet -All -ErrorAction SilentlyContinue
    foreach ($dotnetCommand in $dotnetCommands) {
        $commandPath = $dotnetCommand.Source
        $sdkList = & $commandPath --list-sdks 2>$null
        if ($sdkList -match '^10\.') {
            return $commandPath
        }
    }

    throw 'The .NET 10 SDK was not found. Install the .NET 10 LTS SDK.'
}
