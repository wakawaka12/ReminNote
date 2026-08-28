function Resolve-DotNetPath {
    [CmdletBinding()]
    param()

    $standardDotNetPaths = @(
        if ($env:ProgramW6432) { Join-Path $env:ProgramW6432 'dotnet\dotnet.exe' }
        if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    ) | Where-Object { $_ } | Select-Object -Unique

    foreach ($standardDotNetPath in $standardDotNetPaths) {
        if (Test-Path -LiteralPath $standardDotNetPath) {
            $standardSdkList = & $standardDotNetPath --list-sdks 2>$null
            if ($standardSdkList -match '^10\.') {
                return $standardDotNetPath
            }
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
