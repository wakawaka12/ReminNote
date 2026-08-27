param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "P0-07 verification failed: $Message"
    }
}

function Read-Text {
    param([string]$Path)

    return Get-Content -LiteralPath (Join-Path $repositoryRoot $Path) -Raw -Encoding utf8
}

$globalJson = Read-Text 'global.json' | ConvertFrom-Json
Assert-Condition ($globalJson.sdk.version -like '10.*') 'global.json must select the .NET 10 SDK.'

$solutionText = Read-Text 'ReminNote.sln'
Assert-Condition ($solutionText.Contains('ReminNote.Widget.csproj')) 'ReminNote.Widget must remain registered in the solution.'

$resourceSourcePath = Join-Path $repositoryRoot 'src/windows/ReminNote.Windows/Resources/Localization/UiText.cs'
$resourceFilePath = Join-Path $repositoryRoot 'src/windows/ReminNote.Windows/Resources/Localization/UiText.resx'
$resourceSource = Get-Content -LiteralPath $resourceSourcePath -Raw -Encoding utf8
$resourceXml = [xml](Get-Content -LiteralPath $resourceFilePath -Raw -Encoding utf8)
$resourceNames = @{}
foreach ($resource in $resourceXml.root.data) {
    $resourceNames[$resource.name] = [string]$resource.value
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$resource.value)) "resource '$($resource.name)' must have a non-empty default value."
}

$keyMatches = [regex]::Matches($resourceSource, 'public const string [A-Za-z0-9_]+Key = "([^"]+)"')
Assert-Condition ($keyMatches.Count -gt 0) 'UiText.cs must declare stable resource keys.'
foreach ($match in $keyMatches) {
    $key = $match.Groups[1].Value
    Assert-Condition $resourceNames.ContainsKey($key) "resource key '$key' is missing from UiText.resx."
}

$windowsProject = Read-Text 'src/windows/ReminNote.Windows/ReminNote.Windows.csproj'
$widgetProject = Read-Text 'src/windows/ReminNote.Widget/ReminNote.Widget.csproj'
Assert-Condition ($windowsProject.Contains('ReminNote.Windows.Resources.Localization.UiText.resources')) 'Windows project must embed UiText.resx with the stable logical name.'
Assert-Condition ($widgetProject.Contains('Resources\Localization\UiText.cs')) 'Widget must link the shared UiText.cs source.'
Assert-Condition ($widgetProject.Contains('ReminNote.Windows.Resources.Localization.UiText.resources')) 'Widget must embed the shared default resource with the stable logical name.'

$mainWindow = Read-Text 'src/windows/ReminNote.Windows/MainWindow.xaml'
$todayResources = Read-Text 'src/windows/ReminNote.Windows/Features/Today/TodayResources.xaml'
$animeResources = Read-Text 'src/windows/ReminNote.Windows/Features/Anime/AnimeResources.xaml'
$widgetWindow = Read-Text 'src/windows/ReminNote.Widget/WidgetWindow.xaml'
Assert-Condition (-not $mainWindow.Contains('P0-03')) 'current Shell UI must not expose the stale P0-03 stage label.'
Assert-Condition ($mainWindow.Contains('AutomationProperties.Name')) 'Shell must expose an AutomationProperties name.'
Assert-Condition ($todayResources.Contains('AutomationProperties.Name')) 'TODAY must expose named controls to UI Automation.'
Assert-Condition ($animeResources.Contains('AutomationProperties.Name')) 'ANIME must expose named controls to UI Automation.'
Assert-Condition ($widgetWindow.Contains('AutomationProperties.Name')) 'Widget must expose named controls to UI Automation.'
Assert-Condition ($widgetWindow.Contains('Binding ActivePageTitle, Mode=OneWay')) 'Widget page title bindings must remain one-way because ActivePageTitle is read-only.'
Assert-Condition ($widgetWindow.Contains('Binding ActivePageSubtitle, Mode=OneWay')) 'Widget page subtitle binding must remain one-way because ActivePageSubtitle is read-only.'
Assert-Condition ($animeResources.Contains('Binding ProgressValue, Mode=OneWay')) 'ANIME progress binding must remain one-way because ProgressValue is read-only.'

$lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Filter 'packages.lock.json' -Recurse -File)
$projects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Filter '*.csproj' -Recurse -File)
Assert-Condition ($lockFiles.Count -eq $projects.Count) "each project must have a packages.lock.json ($($projects.Count) projects, $($lockFiles.Count) lock files)."

$cleanScript = Read-Text 'scripts/clean.ps1'
Assert-Condition ($cleanScript.Contains("Join-Path `$repositoryRoot '.devdata'")) 'clean.ps1 must keep development data purge explicit and scoped.'
Assert-Condition ($cleanScript.Contains('Production data is never targeted by default.')) 'clean.ps1 must document its data-safety boundary.'

$testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Filter '*Tests.csproj' -Recurse -File)
if ($testProjects.Count -eq 0) {
    Write-Output 'P0-07 note: no test project is currently present; this gate validates resources, configuration, solution membership, and accessibility markers.'
}

Write-Output "P0-07 verification passed: $($keyMatches.Count) resource keys, $($projects.Count) locked projects, and Shell/TODAY/ANIME/Widget UI markers checked."
