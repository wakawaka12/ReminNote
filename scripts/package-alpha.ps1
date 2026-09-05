[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-alpha\.\d+$')]
    [string]$Version = '0.3.0-alpha.1',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'ReminNote.sln'
$protectedDatabasePath = [System.IO.Path]::GetFullPath('D:\Anime\.devdata\reminnote.sqlite')

function Convert-ToCanonicalPath {
    param([Parameter(Mandatory)][string]$Path)

    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        throw "路径必须是绝对路径：$Path"
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Parent
    )

    $candidatePath = [System.IO.Path]::GetFullPath($Candidate) -replace '[\\/]+$', ''
    $parentPath = [System.IO.Path]::GetFullPath($Parent) -replace '[\\/]+$', ''
    return $candidatePath.Equals($parentPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($parentPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($parentPath + [System.IO.Path]::AltDirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string]$DotNetPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Output "[$Description]"
    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description 失败，退出码：$LASTEXITCODE"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "找不到解决方案：$solutionPath"
}

$artifactParent = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path ([System.IO.Path]::GetTempPath()) 'ReminNote-P3-Alpha'
}
else {
    Convert-ToCanonicalPath $OutputRoot
}

if (Test-PathWithin -Candidate $artifactParent -Parent $repositoryRoot) {
    throw "Alpha 产物目录不能位于当前工作树内：$artifactParent"
}

$protectedDataDirectory = [System.IO.Path]::GetDirectoryName($protectedDatabasePath)
if (Test-PathWithin -Candidate $artifactParent -Parent $protectedDataDirectory) {
    throw 'Alpha 产物目录命中受保护数据边界，已拒绝。'
}

[System.IO.Directory]::CreateDirectory($artifactParent) | Out-Null
$runRoot = Join-Path $artifactParent "run-$([Guid]::NewGuid().ToString('N'))"
$packageName = "ReminNote-$Version-$Runtime"
$packageRoot = Join-Path $runRoot $packageName
$publishRoot = Join-Path $runRoot 'publish'
$logRoot = Join-Path $runRoot 'logs'
foreach ($directory in @($runRoot, $packageRoot, $publishRoot, $logRoot)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

. (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
$dotNetPath = Resolve-DotNetPath
$head = ((& git -C $repositoryRoot rev-parse HEAD) -join '').Trim()
$generatedAtUtc = [DateTimeOffset]::UtcNow

Invoke-DotNet `
    -DotNetPath $dotNetPath `
    -Arguments @('restore', $solutionPath, '--locked-mode', '--runtime', $Runtime, '--nologo') `
    -Description 'locked restore'

Invoke-DotNet `
    -DotNetPath $dotNetPath `
    -Arguments @('build', $solutionPath, '--configuration', $Configuration, '--no-restore', '--nologo', "-p:Version=$Version", "-p:AppVersion=$Version") `
    -Description 'Release build'

$projects = @(
    @{ Name = 'Bootstrap'; Path = (Join-Path $repositoryRoot 'src\windows\ReminNote.Bootstrap\ReminNote.Bootstrap.csproj') },
    @{ Name = 'Agent'; Path = (Join-Path $repositoryRoot 'src\windows\ReminNote.Agent\ReminNote.Agent.csproj') },
    @{ Name = 'Windows'; Path = (Join-Path $repositoryRoot 'src\windows\ReminNote.Windows\ReminNote.Windows.csproj') },
    @{ Name = 'Widget'; Path = (Join-Path $repositoryRoot 'src\windows\ReminNote.Widget\ReminNote.Widget.csproj') }
)

foreach ($project in $projects) {
    $projectOutput = Join-Path $publishRoot $project.Name
    [System.IO.Directory]::CreateDirectory($projectOutput) | Out-Null
    Invoke-DotNet `
        -DotNetPath $dotNetPath `
        -Arguments @(
            'publish',
            $project.Path,
            '--configuration',
            $Configuration,
            '--runtime',
            $Runtime,
            '--self-contained',
            'true',
            '--no-restore',
            '--output',
            $projectOutput,
            '--nologo',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            "-p:Version=$Version",
            "-p:AppVersion=$Version"
        ) `
        -Description "publish $($project.Name)"

    $publishFiles = @(Get-ChildItem -LiteralPath $projectOutput -File -Force)
    if ($publishFiles.Count -eq 0) {
        throw "publish $($project.Name) 没有生成文件。"
    }

    foreach ($publishFile in $publishFiles) {
        Copy-Item -LiteralPath $publishFile.FullName -Destination (Join-Path $packageRoot $publishFile.Name) -Force
    }
}

$developmentDataRoot = Join-Path $packageRoot '.devdata'
[System.IO.Directory]::CreateDirectory($developmentDataRoot) | Out-Null
Write-Utf8NoBom `
    -Path (Join-Path $developmentDataRoot '.gitkeep') `
    -Content ''

$marker = [ordered]@{
    product = 'ReminNote'
    formatVersion = 1
    appVersion = $Version
    runtime = $Runtime
    configuration = $Configuration
    commit = $head
    generatedAtUtc = $generatedAtUtc.ToString('O')
    schemaTargets = @(
        '20260902141656_P3ReminderPersistence',
        '20260904090000_P304'
    )
    ipcProtocolVersion = '1.0'
    alphaNotice = '本包跳过正常桌面人工验收；仅用于 Alpha 会议前试用和问题收集。'
}
Write-Utf8NoBom `
    -Path (Join-Path $packageRoot 'ReminNote.runtime.json') `
    -Content ($marker | ConvertTo-Json -Depth 4)

Write-Utf8NoBom `
    -Path (Join-Path $packageRoot 'run-alpha.cmd') `
    -Content (@"
@echo off
cd /d "%~dp0"
ReminNote.Bootstrap.exe %*
"@).TrimStart()

$readme = @"
ReminNote $Version (Portable x64 Alpha)

启动：双击 run-alpha.cmd，或直接运行 ReminNote.Bootstrap.exe。
本包为 Alpha 试用包，本轮明确跳过正常桌面人工验收；请在会议前记录问题，
不要把它当作稳定版。数据默认写入本包目录下的 .devdata/。

已知范围：
- Agent/Main/Widget、Reminder 持久化调度和 P2.75 迁移代码已包含；
- Toast/Tray/Widget/Sound/WakeTimer 的跨进程宿主桥接仍是后续工作；
- Windows 睡眠/唤醒、真实桌面通知效果和用户 sign-off 未完成。

版本身份：AppVersion=$Version；Runtime=$Runtime；Commit=$head。
"@
Write-Utf8NoBom -Path (Join-Path $packageRoot 'README-ALPHA.txt') -Content $readme.TrimStart()

$zipPath = Join-Path $runRoot "$packageName.zip"
$packageEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force | ForEach-Object FullName)
Compress-Archive -Path $packageEntries -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Utf8NoBom -Path "${zipPath}.sha256" -Content "$hash  $([System.IO.Path]::GetFileName($zipPath))`r`n"

$manifestPath = Join-Path $runRoot 'release-manifest.json'
$releaseManifest = [ordered]@{
    product = 'ReminNote'
    channel = 'alpha'
    version = $Version
    runtime = $Runtime
    commit = $head
    package = [System.IO.Path]::GetFileName($zipPath)
    sha256 = $hash
    packageDirectory = $packageRoot
    generatedAtUtc = $generatedAtUtc.ToString('O')
    humanAcceptance = 'SKIPPED_BY_USER'
    knownLimitations = @(
        'cross-process notification host bridge is not enabled',
        'Windows sleep/wake and desktop visual/audio acceptance are pending',
        'P3-08 production export/restore Candidate pipeline remains pending'
    )
}
Write-Utf8NoBom -Path $manifestPath -Content ($releaseManifest | ConvertTo-Json -Depth 5)

Write-Output "Alpha package root: $packageRoot"
Write-Output "Alpha ZIP: $zipPath"
Write-Output "SHA256: $hash"
Write-Output "Release manifest: $manifestPath"
