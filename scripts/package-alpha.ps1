[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-alpha\.\d+$')]
    [string]$Version = '0.3.0-alpha.2',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('NOT_PERFORMED', 'RECORDED')]
    [string]$HumanAcceptance = 'NOT_PERFORMED',

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

if ($Version -eq '0.3.0-alpha.1') {
    throw '0.3.0-alpha.1 是历史构建身份；请使用新的 alpha 版本号，避免生成同版本不同内容。'
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

& (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Release test gate 失败，退出码：$LASTEXITCODE"
}

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

$userDataRoot = Join-Path $packageRoot 'UserData'
[System.IO.Directory]::CreateDirectory($userDataRoot) | Out-Null
Write-Utf8NoBom `
    -Path (Join-Path $userDataRoot '.gitkeep') `
    -Content ''

foreach ($documentName in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')) {
    $documentPath = Join-Path $repositoryRoot $documentName
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
        throw "缺少对外分发必需文档：$documentName"
    }

    Copy-Item -LiteralPath $documentPath -Destination (Join-Path $packageRoot $documentName) -Force
}

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
    automatedGate = 'scripts/test.ps1:PASS'
    humanAcceptance = $HumanAcceptance
    humanAcceptanceNote = if ($HumanAcceptance -eq 'NOT_PERFORMED') {
        '本构建未执行正常桌面人工验收；不得将自动化门禁解释为用户 sign-off。'
    }
    else {
        '人工验收记录由发布负责人另行保存；本清单只记录声明，不替代证据。'
    }
}
Write-Utf8NoBom `
    -Path (Join-Path $packageRoot 'ReminNote.runtime.json') `
    -Content ($marker | ConvertTo-Json -Depth 4)

Write-Utf8NoBom `
    -Path (Join-Path $packageRoot 'run-alpha.cmd') `
    -Content (@"
@echo off
cd /d "%~dp0"
set "ROOT_ARGUMENT="
for %%A in (%*) do (
    if /I "%%~A"=="--data-root" set "ROOT_ARGUMENT=1"
    if /I "%%~A"=="--repo-root" set "ROOT_ARGUMENT=1"
)
if defined ROOT_ARGUMENT (
    ReminNote.Bootstrap.exe %*
) else (
    ReminNote.Bootstrap.exe --data-root "%~dp0UserData" %*
)
"@).TrimStart()

$readme = @"
ReminNote $Version (Portable x64 Alpha)

启动：双击 run-alpha.cmd，或直接运行 ReminNote.Bootstrap.exe --data-root <数据目录>。
本包为 Alpha 试用包，本构建未执行正常桌面人工验收；请在会议前记录问题，
不要把它当作稳定版。run-alpha.cmd 默认把数据写入本包目录下的 UserData/。

已知范围：
- Agent/Main/Widget、Reminder 持久化调度、通知宿主桥和 P2.75 迁移代码已包含；
- 正常 Main 启动会创建并读回 Toast 的 AUMID Start-menu shortcut；Toast 权限、Windows 睡眠/唤醒、真实桌面通知效果和用户 sign-off 仍需人工验收；
- 结构化导出可生成独立 artifact；恢复先建立隔离 Candidate，需先 verify，只有显式 --confirm promotion 才会写入 Active。

用户数据命令（路径必须为仓库外绝对路径）：
  ReminNote.Bootstrap.exe --user-data-export C:\Temp\reminnote-export.json --data-root <UserData>
  ReminNote.Bootstrap.exe --user-data-restore C:\Temp\reminnote-export.json --candidate-root C:\Temp\reminnote-candidate --data-root <UserData> --confirm --import-candidate
  ReminNote.Bootstrap.exe --user-data-verify <candidateRoot> --data-root <UserData>
  ReminNote.Bootstrap.exe --user-data-promote <candidateRoot> --data-root <UserData> --confirm
导入命令输出的 candidateRoot 是 Candidate 目录；stagedArtifact 是目录内的结构化 JSON 文件，不能代替目录参数。
恢复默认只做 dry-run；Candidate 导入后仍需独立验证和受控切换，不会自动覆盖 Active。

从 0.3.0-alpha.1 升级：先退出旧包并复制旧包 .devdata/ 的全部内容到新包 UserData/，
保留旧目录作为回退备份；不要删除旧目录，也不要把它提交到 Git。若旧数据迁移失败，
保留 migration-state、backup 和 Candidate，按 recovery-status 输出处理。

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
    automatedGate = 'scripts/test.ps1:PASS'
    humanAcceptance = $HumanAcceptance
    humanAcceptanceNote = if ($HumanAcceptance -eq 'NOT_PERFORMED') {
        '本构建未执行正常桌面人工验收；NOT_PERFORMED 不等同于稳定性 sign-off。'
    }
    else {
        '人工验收声明不替代独立证据；请在总成报告中记录范围、版本和时间。'
    }
    knownLimitations = @(
        'Toast permission/visual delivery and Windows sleep/wake/desktop visual/audio acceptance are pending',
        'normal desktop user sign-off is pending',
    'structured restore stages an isolated Candidate; explicit verify and promotion remain operator-controlled recovery actions'
)
}
Write-Utf8NoBom -Path $manifestPath -Content ($releaseManifest | ConvertTo-Json -Depth 5)

Write-Output "Alpha package root: $packageRoot"
Write-Output "Alpha ZIP: $zipPath"
Write-Output "SHA256: $hash"
Write-Output "Release manifest: $manifestPath"
