[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(1, 20)]
    [int]$StabilityIterations = 3,

    [ValidateRange(1, 60)]
    [int]$RealProcessObservationSeconds = 5,

    [string]$ArtifactRoot,

    [switch]$SkipContractTests,

    [switch]$RunRealProcess,

    [string]$AgentPath,

    [string]$MainPath,

    [string]$WidgetPath,

    [string]$IsolatedRepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# 这里只把受保护目标作为路径字符串做边界比较；不对该路径调用 Test-Path 或任何文件/数据库 API。
$protectedDatabasePath = [System.IO.Path]::GetFullPath('D:\Anime\.devdata\reminnote.sqlite')
$results = [System.Collections.Generic.List[object]]::new()
$dependencyStatuses = [ordered]@{}
$commandRecords = [System.Collections.Generic.List[string]]::new()
$script:migrationEvidenceRecorded = $false

function Convert-ToCanonicalPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw '路径不能为空。'
    }

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

function Add-GateResult {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$EvidenceLevel,
        [Parameter(Mandatory)][ValidateSet('PASS', 'BLOCKED', 'PENDING', 'FAIL')][string]$Status,
        [Parameter(Mandatory)][string]$Summary,
        [Parameter(Mandatory)][string]$Evidence,
        [string]$NextAction = ''
    )

    $results.Add([ordered]@{
            id            = $Id
            evidenceLevel = $EvidenceLevel
            status        = $Status
            summary       = $Summary
            evidence      = $Evidence
            nextAction    = $NextAction
        })
}

function Get-RepositoryText {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [switch]$Optional
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if ($Optional) {
            return ''
        }

        throw "缺少必需文件：$RelativePath"
    }

    return [System.IO.File]::ReadAllText($path)
}

function Read-SharedText {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    # Start-Process keeps redirected stdout open while a probe is running.
    # File.ReadAllText uses a restrictive share mode and can therefore make a
    # healthy Agent look like a gate execution error. Open the log explicitly
    # with shared read/write access so readiness polling remains observational.
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-TreeText {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [string[]]$Extensions = @('.cs'),
        [string[]]$ExcludeFileNames = @()
    )

    $root = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return ''
    }

    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
            $extensionMatches = $Extensions -contains $_.Extension.ToLowerInvariant()
            $safePath = $_.FullName -notmatch '\\(bin|obj|\.devdata|reviews|second-review)(\\|$)'
            $fileNameAllowed = $ExcludeFileNames -notcontains $_.Name
            $extensionMatches -and $safePath -and $fileNameAllowed
        })

    if ($files.Count -eq 0) {
        return ''
    }

    $chunks = foreach ($file in $files) {
        [System.IO.File]::ReadAllText($file.FullName)
    }

    return [string]::Join("`n", $chunks)
}

function Test-TextMarker {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return $false
    }

    return [System.Text.RegularExpressions.Regex]::IsMatch(
        $Text,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Add-RequiredDocumentCheck {
    param(
        [Parameter(Mandatory)][hashtable]$Spec
    )

    $path = Join-Path $repositoryRoot $Spec.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-GateResult `
            -Id $Spec.Id `
            -EvidenceLevel 'static/contract' `
            -Status 'FAIL' `
            -Summary "缺少 $($Spec.Name)。" `
            -Evidence $Spec.Path `
            -NextAction '补齐该契约或报告后重新运行 P3-09 gate。'
        return
    }

    $text = [System.IO.File]::ReadAllText($path)
    $missingMarkers = @($Spec.Markers | Where-Object { -not $text.Contains($_) })
    if ($missingMarkers.Count -gt 0) {
        Add-GateResult `
            -Id $Spec.Id `
            -EvidenceLevel 'static/contract' `
            -Status 'FAIL' `
            -Summary "$($Spec.Name) 缺少契约标记。" `
            -Evidence "$($Spec.Path); missing=$([string]::Join(',', $missingMarkers))" `
            -NextAction '补齐契约内容后重新运行 P3-09 gate。'
        return
    }

    Add-GateResult `
        -Id $Spec.Id `
        -EvidenceLevel 'static/contract' `
        -Status 'PASS' `
        -Summary "$($Spec.Name) 已存在且包含必要的证据边界。" `
        -Evidence $Spec.Path `
        -NextAction '保持该契约与实现、测试和人工证据同步。'
}

function Add-DependencyMarkerCheck {
    param(
        [Parameter(Mandatory)][string]$Dependency,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Evidence,
        [Parameter(Mandatory)][string]$NextAction
    )

    $present = Test-TextMarker -Text $Text -Pattern $Pattern
    if ($present) {
        Add-GateResult `
            -Id $Id `
            -EvidenceLevel 'static' `
            -Status 'PASS' `
            -Summary "$Dependency：$Description 已发现。" `
            -Evidence $Evidence `
            -NextAction '继续核对运行时、故障和用户可见证据。'
        return $true
    }

    Add-GateResult `
        -Id $Id `
        -EvidenceLevel 'static' `
        -Status 'BLOCKED' `
        -Summary "$Dependency：$Description 尚未接入。" `
        -Evidence $Evidence `
        -NextAction $NextAction
    return $false
}

function Add-DependencyPathCheck {
    param(
        [Parameter(Mandatory)][string]$Dependency,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$NextAction
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (Test-Path -LiteralPath $path -PathType Container) {
        Add-GateResult `
            -Id $Id `
            -EvidenceLevel 'static' `
            -Status 'PASS' `
            -Summary "$Dependency：$Description 已发现。" `
            -Evidence $RelativePath `
            -NextAction '继续核对运行时、故障和用户可见证据。'
        return $true
    }

    Add-GateResult `
        -Id $Id `
        -EvidenceLevel 'static' `
        -Status 'BLOCKED' `
        -Summary "$Dependency：$Description 尚未接入。" `
        -Evidence $RelativePath `
        -NextAction $NextAction
    return $false
}

function Get-OverallStatus {
    if (@($results | Where-Object status -eq 'FAIL').Count -gt 0) {
        return 'FAIL'
    }

    if (@($results | Where-Object status -eq 'BLOCKED').Count -gt 0) {
        return 'BLOCKED'
    }

    if (@($results | Where-Object status -eq 'PENDING').Count -gt 0) {
        return 'PENDING'
    }

    return 'PASS'
}

function Convert-ToMarkdownCell {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return $Value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

function Copy-ContractSourceTree {
    param([Parameter(Mandatory)][string]$RunRoot)

    $contractSourceRoot = Join-Path $RunRoot 'contract-source'
    [System.IO.Directory]::CreateDirectory($contractSourceRoot) | Out-Null

    $rootFiles = @(
        'global.json',
        'Directory.Build.props',
        'Directory.Packages.props'
    )
    foreach ($relativePath in $rootFiles) {
        $sourcePath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "隔离 contract source 缺少根文件：$relativePath"
        }

        $destinationPath = Join-Path $contractSourceRoot $relativePath
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }

    $sourceTrees = @(
        'src\windows\ReminNote.Core',
        'src\windows\ReminNote.Infrastructure',
        'src\windows\ReminNote.Agent',
        'src\windows\ReminNote.Windows',
        'src\windows\ReminNote.Widget',
        'tests\ReminNote.Tests'
    )
    foreach ($relativeRoot in $sourceTrees) {
        $sourceRoot = Join-Path $repositoryRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            throw "隔离 contract source 缺少源码目录：$relativeRoot"
        }

        $destinationRoot = Join-Path $contractSourceRoot $relativeRoot
        $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
                $_.FullName -notmatch '\\(bin|obj|\.devdata|reviews|second-review)(\\|$)'
            })
        foreach ($file in $files) {
            $relativeFile = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
            $destinationPath = Join-Path $destinationRoot $relativeFile
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
        }
    }

    return $contractSourceRoot
}

function Invoke-ContractTests {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][string]$DotNetPath
    )

    if ($SkipContractTests) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-TESTS' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'PENDING' `
            -Summary '按参数跳过已有 P3 contract/isolated 测试。' `
            -Evidence '参数：-SkipContractTests' `
            -NextAction '不跳过测试运行完整 P3-09 gate。'
        return
    }

    $repositoryTestProject = Join-Path $repositoryRoot 'tests\ReminNote.Tests\ReminNote.Tests.csproj'
    if (-not (Test-Path -LiteralPath $repositoryTestProject -PathType Leaf)) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-TESTS' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary 'P3 测试项目不存在。' `
            -Evidence $repositoryTestProject `
            -NextAction '恢复测试项目后重新运行 gate。'
        return
    }

    $buildRoot = Join-Path $RunRoot 'build'
    $binRoot = Join-Path $buildRoot 'bin'
    $testLogRoot = Join-Path $RunRoot 'contract-tests'
    [System.IO.Directory]::CreateDirectory($binRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($testLogRoot) | Out-Null

    $contractSourceRoot = Copy-ContractSourceTree -RunRoot $RunRoot
    $testProject = Join-Path $contractSourceRoot 'tests\ReminNote.Tests\ReminNote.Tests.csproj'
    $commandRecords.Add("copy contract source -> $contractSourceRoot (exclude bin/obj/.devdata/reviews/second-review)")
    $baseOutputArgument = '-p:BaseOutputPath=' + $binRoot + '\'
    $restoreArguments = @(
        'restore',
        $testProject,
        '--locked-mode',
        '--nologo'
    )
    $commandRecords.Add("$DotNetPath $([string]::Join(' ', $restoreArguments))")
    $restoreOutput = & $DotNetPath @restoreArguments 2>&1
    $restoreExitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllText(
        (Join-Path $testLogRoot 'restore.log'),
        ($restoreOutput -join "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    if ($restoreExitCode -ne 0) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-RESTORE' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary "P3 测试项目 locked restore 失败（exit=$restoreExitCode）。" `
            -Evidence (Join-Path $testLogRoot 'restore.log') `
            -NextAction '修复 restore 后重新运行 gate；不得以跳过 restore 代替。'
        return
    }

    Add-GateResult `
        -Id 'P3-09-CONTRACT-RESTORE' `
        -EvidenceLevel 'contract/unit/fake + isolated' `
        -Status 'PASS' `
        -Summary 'P3 测试项目 locked restore 通过。' `
        -Evidence (Join-Path $testLogRoot 'restore.log') `
        -NextAction '继续执行 Release contract tests。'

    $buildArguments = @(
        'build',
        $testProject,
        '--configuration',
        $Configuration,
        '--no-restore',
        '--nologo',
        $baseOutputArgument
    )
    $commandRecords.Add("$DotNetPath $([string]::Join(' ', $buildArguments))")
    $buildOutput = & $DotNetPath @buildArguments 2>&1
    $buildExitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllText(
        (Join-Path $testLogRoot 'build.log'),
        ($buildOutput -join "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    if ($buildExitCode -ne 0) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-BUILD' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary "P3 测试项目 Release build 失败（exit=$buildExitCode）。" `
            -Evidence (Join-Path $testLogRoot 'build.log') `
            -NextAction '修复构建错误后重新运行 gate。'
        return
    }

    Add-GateResult `
        -Id 'P3-09-CONTRACT-BUILD' `
        -EvidenceLevel 'contract/unit/fake + isolated' `
        -Status 'PASS' `
        -Summary 'P3 测试项目 Release build 通过，编译产物位于隔离 build root。' `
        -Evidence (Join-Path $testLogRoot 'build.log') `
        -NextAction '继续重复运行 P3 contract tests，观察稳定性。'

    $testExecutable = Join-Path $binRoot "$Configuration\net10.0\ReminNote.Tests.exe"
    if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-TESTS' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary '没有生成 xUnit/Microsoft Testing Platform 测试可执行文件。' `
            -Evidence $testExecutable `
            -NextAction '检查测试项目输出配置后重新运行 gate。'
        return
    }

    $testArguments = @('-noLogo', '-noColor')
    $testFilters = @(
        'ReminNote.Tests.ReminderDomainTests',
        'ReminNote.Tests.ReminderPersistenceMappingTests',
        'ReminNote.Tests.P302.*',
        'ReminNote.Tests.P3_03.*',
        'ReminNote.Tests.P3_04.*',
        'ReminNote.Tests.P3_05.*',
        'ReminNote.Tests.ReminderUiAndProtocolTests',
        'ReminNote.Tests.P3_07.*',
        'ReminNote.Tests.P308.*'
    )
    foreach ($filter in $testFilters) {
        $testArguments += @('-class', $filter)
    }

    $iterationStatuses = [System.Collections.Generic.List[string]]::new()
    $previousLocation = Get-Location
    try {
        Set-Location -LiteralPath $RunRoot
        for ($iteration = 1; $iteration -le $StabilityIterations; $iteration++) {
            $logPath = Join-Path $testLogRoot "iteration-$iteration.log"
            $commandRecords.Add("$testExecutable $([string]::Join(' ', $testArguments))")
            $testOutput = & $testExecutable @testArguments 2>&1
            $testExitCode = $LASTEXITCODE
            [System.IO.File]::WriteAllText(
                $logPath,
                ($testOutput -join "`r`n"),
                [System.Text.UTF8Encoding]::new($false))
            if ($testExitCode -eq 0) {
                $iterationStatuses.Add("iteration=$iteration PASS")
            }
            else {
                $iterationStatuses.Add("iteration=$iteration FAIL exit=$testExitCode")
            }
        }
    }
    finally {
        Set-Location -LiteralPath $previousLocation
    }

    $failedIterations = @($iterationStatuses | Where-Object { $_ -like '* FAIL *' -or $_ -like '* FAIL' })
    if ($failedIterations.Count -gt 0) {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-TESTS' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary "P3 contract/isolated 测试存在失败（$($failedIterations.Count)/$StabilityIterations 次）。" `
            -Evidence "$testLogRoot; $([string]::Join(', ', $iterationStatuses))" `
            -NextAction '先修复 contract/isolated 测试失败，再进入真实进程或桌面验收。'
        return
    }

    Add-GateResult `
        -Id 'P3-09-CONTRACT-TESTS' `
        -EvidenceLevel 'contract/unit/fake + isolated' `
        -Status 'PASS' `
        -Summary "P3 contract/isolated 测试连续 $StabilityIterations 次通过。" `
        -Evidence "$testLogRoot; $([string]::Join(', ', $iterationStatuses))" `
        -NextAction '该结果不能替代 Agent 真实进程、真实通道或桌面人工验收。'
}

function Stop-LaunchedProcess {
    param([AllowNull()][System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            [void]$Process.CloseMainWindow()
            if (-not $Process.WaitForExit(5000)) {
                $Process.Kill($true)
                [void]$Process.WaitForExit(5000)
            }
        }
    }
    catch [System.InvalidOperationException] {
        # The process already exited between the state check and cleanup.
    }
    finally {
        $Process.Dispose()
    }
}

function Initialize-WindowsProcessEnvironment {
    if (-not [OperatingSystem]::IsWindows()) {
        return
    }

    # The Codex command host may intentionally omit the process-level Windows
    # variables even though the machine environment has them. WPF reads
    # SystemRoot while initializing its font cache; propagate the authoritative
    # machine value to children without changing the product/runtime contract.
    $windowsRoot = [Environment]::GetEnvironmentVariable('SystemRoot')
    if ([string]::IsNullOrWhiteSpace($windowsRoot)) {
        $windowsRoot = [Environment]::GetEnvironmentVariable('SystemRoot', 'Machine')
    }

    if ([string]::IsNullOrWhiteSpace($windowsRoot)) {
        $windowsRoot = [Environment]::GetEnvironmentVariable('WINDIR', 'Machine')
    }

    if (-not [string]::IsNullOrWhiteSpace($windowsRoot) -and
        (Test-Path -LiteralPath $windowsRoot -PathType Container)) {
        $env:SystemRoot = $windowsRoot
        $env:WINDIR = $windowsRoot
    }
}

function Invoke-P25Fixture {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][string]$RepositoryClone,
        [Parameter(Mandatory)][string]$DataRoot,
        [Parameter(Mandatory)][string]$DotNetPath
    )

    $fixtureProject = Join-Path $RepositoryClone 'tools\ReminNote.P3GateFixture\ReminNote.P3GateFixture.csproj'
    if (-not (Test-Path -LiteralPath $fixtureProject -PathType Leaf)) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'FAIL' `
            -Summary '隔离 P2.5 fixture 工具不存在，无法启动真实升级探针。' `
            -Evidence $fixtureProject `
            -NextAction '保留独立 clone，补齐 P3GateFixture 后重新运行；不得以空数据根代替 P2.5 升级证据。'
        return $false
    }

    $fixtureRoot = Join-Path $RunRoot 'p25-fixture'
    $fixtureBinRoot = Join-Path $fixtureRoot 'bin'
    $fixtureLogRoot = Join-Path $fixtureRoot 'logs'
    foreach ($directory in @($fixtureRoot, $fixtureBinRoot, $fixtureLogRoot)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    # Keep the clone's normal obj graph so every ProjectReference receives its
    # own assets file. Redirecting BaseIntermediateOutputPath globally makes
    # referenced projects share one assets path and can overwrite the graph
    # with the last project restored (for example, Core instead of Infrastructure).
    # The clone is disposable, so its obj state is already isolated from the
    # working tree; only compiled output needs to be redirected into the gate
    # artifact for evidence collection.
    $baseOutputArgument = '-p:BaseOutputPath=' + $fixtureBinRoot + '\'
    $restoreArguments = @(
        'restore',
        $fixtureProject,
        '--locked-mode',
        '--nologo',
        $baseOutputArgument
    )
    $restoreOutput = & $DotNetPath @restoreArguments 2>&1
    $restoreExitCode = $LASTEXITCODE
    $restoreLog = Join-Path $fixtureLogRoot 'restore.log'
    [System.IO.File]::WriteAllText(
        $restoreLog,
        ($restoreOutput -join "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    if ($restoreExitCode -ne 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'FAIL' `
            -Summary "P2.5 fixture locked restore 失败（exit=$restoreExitCode）。" `
            -Evidence $restoreLog `
            -NextAction '修复隔离 fixture restore；不得跳过 restore 或使用当前工作树 bin/obj。'
        return $false
    }

    $buildArguments = @(
        'build',
        $fixtureProject,
        '--configuration',
        $Configuration,
        '--no-restore',
        '--nologo',
        $baseOutputArgument
    )
    $buildOutput = & $DotNetPath @buildArguments 2>&1
    $buildExitCode = $LASTEXITCODE
    $buildLog = Join-Path $fixtureLogRoot 'build.log'
    [System.IO.File]::WriteAllText(
        $buildLog,
        ($buildOutput -join "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    if ($buildExitCode -ne 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'FAIL' `
            -Summary "P2.5 fixture Release build 失败（exit=$buildExitCode）。" `
            -Evidence $buildLog `
            -NextAction '修复隔离 fixture build 后重新运行真实进程探针。'
        return $false
    }

    $fixtureDll = Join-Path $fixtureBinRoot "$Configuration\net10.0\ReminNote.P3GateFixture.dll"
    if (-not (Test-Path -LiteralPath $fixtureDll -PathType Leaf)) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'FAIL' `
            -Summary 'P2.5 fixture build 未产生可执行程序集。' `
            -Evidence $fixtureDll `
            -NextAction '检查 fixture 输出路径后重新运行真实进程探针。'
        return $false
    }

    $fixtureRunArguments = @(
        $fixtureDll,
        '--data-root',
        $DataRoot,
        '--profile',
        'p3-09-gate'
    )
    $fixtureOutput = & $DotNetPath @fixtureRunArguments 2>&1
    $fixtureExitCode = $LASTEXITCODE
    $fixtureRunLog = Join-Path $fixtureLogRoot 'run.log'
    [System.IO.File]::WriteAllText(
        $fixtureRunLog,
        ($fixtureOutput -join "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    if ($fixtureExitCode -ne 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'FAIL' `
            -Summary "隔离 P2.5 fixture 初始化失败（exit=$fixtureExitCode）。" `
            -Evidence $fixtureRunLog `
            -NextAction '修复 P2.5 fixture 初始化后重新运行；不得让 Agent 在无 schema 的空目录上伪造升级证据。'
        return $false
    }

    return $true
}

function Invoke-RealProcessProbe {
    param([Parameter(Mandatory)][string]$RunRoot)

    $blockedDependencies = @($dependencyStatuses.GetEnumerator() | Where-Object { $_.Value -ne 'READY' })
    if ($blockedDependencies.Count -gt 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'BLOCKED' `
            -Summary '真实进程检查未启动：P3-03/P3-05/P3-06 依赖未满足。' `
            -Evidence "blocked=$([string]::Join(',', ($blockedDependencies | ForEach-Object { $_.Key })) )" `
            -NextAction '完成 Agent scheduler、实际 channel 和 UI query/action 接线后，使用新的隔离 data-root/clone 重新运行。'
        return
    }

    if (-not $RunRealProcess) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'PENDING' `
            -Summary '真实进程检查具备脚本入口，但本次未请求启动进程。' `
            -Evidence '未提供 -RunRealProcess' `
            -NextAction '只在依赖满足且已准备隔离 data-root/clone 后显式传入 -RunRealProcess。'
        return
    }

    $requiredPaths = @{
        AgentPath = $AgentPath
        MainPath = $MainPath
        WidgetPath = $WidgetPath
        IsolatedRepositoryRoot = $IsolatedRepositoryRoot
    }
    $missingArguments = @($requiredPaths.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) })
    if ($missingArguments.Count -gt 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'BLOCKED' `
            -Summary '真实进程检查缺少显式二进制或隔离 clone 参数。' `
            -Evidence "missing=$([string]::Join(',', ($missingArguments | ForEach-Object { $_.Key })))" `
            -NextAction '显式提供 Agent/Main/Widget 可执行文件和含 .git、ReminNote.sln 的隔离 clone；不得使用当前仓库 root。'
        return
    }

    $canonicalRepositoryClone = Convert-ToCanonicalPath $IsolatedRepositoryRoot
    $cloneInsideCurrentRepository = Test-PathWithin -Candidate $canonicalRepositoryClone -Parent $repositoryRoot
    $currentRepositoryInsideClone = Test-PathWithin -Candidate $repositoryRoot -Parent $canonicalRepositoryClone
    if ($cloneInsideCurrentRepository -or $currentRepositoryInsideClone) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'BLOCKED' `
            -Summary '真实进程 clone 与当前工作树存在包含关系，无法证明隔离。' `
            -Evidence $canonicalRepositoryClone `
            -NextAction '准备当前工作树之外的临时 clone，并再次运行。'
        return
    }

    $cloneGitPath = Join-Path $canonicalRepositoryClone '.git'
    $cloneSolutionPath = Join-Path $canonicalRepositoryClone 'ReminNote.sln'
    $binaryPaths = @($AgentPath, $MainPath, $WidgetPath)
    $invalidInputs = @(
        if (-not (Test-Path -LiteralPath $canonicalRepositoryClone -PathType Container)) { 'isolated clone 不存在' }
        if (-not (Test-Path -LiteralPath $cloneGitPath)) { 'isolated clone 缺少 .git' }
        if (-not (Test-Path -LiteralPath $cloneSolutionPath -PathType Leaf)) { 'isolated clone 缺少 ReminNote.sln' }
        foreach ($binaryPath in $binaryPaths) {
            if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) { "binary 不存在：$binaryPath" }
        }
    )
    if ($invalidInputs.Count -gt 0) {
        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'BLOCKED' `
            -Summary '真实进程参数未通过隔离输入检查。' `
            -Evidence ([string]::Join('; ', $invalidInputs)) `
            -NextAction '修复隔离 clone/二进制路径；不要把当前仓库或其 .devdata 作为运行目标。'
        return
    }

    $realProcessDataRoot = Join-Path $RunRoot 'real-process-data'
    [System.IO.Directory]::CreateDirectory($realProcessDataRoot) | Out-Null
    $processLogRoot = Join-Path $RunRoot 'real-process'
    [System.IO.Directory]::CreateDirectory($processLogRoot) | Out-Null
    $processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    $startedDescriptions = [System.Collections.Generic.List[string]]::new()

    try {
        Initialize-WindowsProcessEnvironment
        if (-not (Invoke-P25Fixture `
                    -RunRoot $RunRoot `
                    -RepositoryClone $canonicalRepositoryClone `
                    -DataRoot $realProcessDataRoot `
                    -DotNetPath $dotNetPath)) {
            return
        }

        $agentLog = Join-Path $processLogRoot 'agent.log'
        $agent = Start-Process `
            -FilePath (Convert-ToCanonicalPath $AgentPath) `
            -WorkingDirectory ([System.IO.Path]::GetDirectoryName((Convert-ToCanonicalPath $AgentPath))) `
            -ArgumentList @('--data-root', $realProcessDataRoot, '--profile', 'p3-09-gate') `
            -RedirectStandardOutput $agentLog `
            -RedirectStandardError (Join-Path $processLogRoot 'agent.err.log') `
            -PassThru
        $processes.Add($agent)
        $startedDescriptions.Add("Agent pid=$($agent.Id) path=$AgentPath")

        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        $agentReady = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $agentLog) {
                $agentOutput = Read-SharedText -Path $agentLog
                # Agent logs the resolved profile scope (a stable hash), not
                # the caller's profile key. Match the readiness contract
                # prefix so a valid isolated profile is not rejected merely
                # because its scope is derived at runtime.
                if ($agentOutput.Contains('ReminNote Agent ready for profile ')) {
                    $agentReady = $true
                    break
                }
            }

            if ($agent.HasExited) {
                break
            }

            Start-Sleep -Milliseconds 200
        }

        if (-not $agentReady) {
            Add-GateResult `
                -Id 'P3-09-REAL-PROCESS' `
                -EvidenceLevel 'real-process isolated' `
                -Status 'FAIL' `
                -Summary 'Agent 未在隔离 data-root 内报告 ready。' `
                -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
                -NextAction '保留日志，修复启动/migration/health 问题后重新运行；不要把失败启动记为稳定性通过。'
            return
        }

        $hostSpecifications = @(
            @{ Name = 'Main'; Path = $MainPath; Log = 'main.log'; ErrorLog = 'main.err.log' },
            @{ Name = 'Widget'; Path = $WidgetPath; Log = 'widget.log'; ErrorLog = 'widget.err.log' }
        )
        foreach ($specification in $hostSpecifications) {
            $hostProcess = Start-Process `
                -FilePath (Convert-ToCanonicalPath $specification.Path) `
                -WorkingDirectory $canonicalRepositoryClone `
                -ArgumentList @('--data-root', $realProcessDataRoot, '--profile', 'p3-09-gate') `
                -RedirectStandardOutput (Join-Path $processLogRoot $specification.Log) `
                -RedirectStandardError (Join-Path $processLogRoot $specification.ErrorLog) `
                -PassThru
            $processes.Add($hostProcess)
            $startedDescriptions.Add("$($specification.Name) pid=$($hostProcess.Id) path=$($specification.Path)")
        }

        Start-Sleep -Seconds $RealProcessObservationSeconds
        $exitedProcesses = @($processes | Where-Object HasExited)
        if ($exitedProcesses.Count -gt 0) {
            Add-GateResult `
                -Id 'P3-09-REAL-PROCESS' `
                -EvidenceLevel 'real-process isolated' `
                -Status 'FAIL' `
                -Summary 'Agent/Main/Widget 隔离启动后在观察窗口内退出。' `
                -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
                -NextAction '分析各进程 stdout/stderr；进程退出或启动异常不得标记为稳定。'
            return
        }

        # Capture migration evidence before the restart. A fully migrated
        # second startup is allowed to overwrite the marker with a no-op READY
        # snapshot that intentionally has no new backup artifact.
        Invoke-IsolatedMigrationEvidence -RunRoot $RunRoot

        # Restart only the Agent while Main/Widget remain alive. The same
        # isolated data-root is intentionally reused so the probe exercises
        # durable migration/recovery state instead of an empty process.
        Stop-LaunchedProcess -Process $agent
        [void]$processes.Remove($agent)
        Start-Sleep -Milliseconds 300

        $restartLog = Join-Path $processLogRoot 'agent-restart.log'
        $restartErrorLog = Join-Path $processLogRoot 'agent-restart.err.log'
        $restartedAgent = Start-Process `
            -FilePath (Convert-ToCanonicalPath $AgentPath) `
            -WorkingDirectory ([System.IO.Path]::GetDirectoryName((Convert-ToCanonicalPath $AgentPath))) `
            -ArgumentList @('--data-root', $realProcessDataRoot, '--profile', 'p3-09-gate') `
            -RedirectStandardOutput $restartLog `
            -RedirectStandardError $restartErrorLog `
            -PassThru
        $processes.Add($restartedAgent)
        $startedDescriptions.Add("Agent restart pid=$($restartedAgent.Id) path=$AgentPath")

        $restartDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        $restartReady = $false
        while ([DateTimeOffset]::UtcNow -lt $restartDeadline) {
            if (Test-Path -LiteralPath $restartLog) {
                $restartOutput = Read-SharedText -Path $restartLog
                if ($restartOutput.Contains('ReminNote Agent ready for profile ')) {
                    $restartReady = $true
                    break
                }
            }

            if ($restartedAgent.HasExited) {
                break
            }

            Start-Sleep -Milliseconds 200
        }

        if (-not $restartReady -or $restartedAgent.HasExited) {
            Add-GateResult `
                -Id 'P3-09-AGENT-RESTART' `
                -EvidenceLevel 'real-process isolated' `
                -Status 'FAIL' `
                -Summary 'Agent 使用同一隔离 data-root 重启后未再次报告 ready。' `
                -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
                -NextAction '分析 agent-restart stdout/stderr 和 migration marker；不得把重启失败记为稳定性通过。'
            return
        }

        Start-Sleep -Seconds $RealProcessObservationSeconds
        if ($restartedAgent.HasExited) {
            Add-GateResult `
                -Id 'P3-09-AGENT-RESTART' `
                -EvidenceLevel 'real-process isolated' `
                -Status 'FAIL' `
                -Summary "Agent 重启后在隔离观察窗口 $RealProcessObservationSeconds 秒内退出。" `
                -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
                -NextAction '分析 agent-restart stdout/stderr 和 durable recovery 状态后重新运行。'
            return
        }

        Add-GateResult `
            -Id 'P3-09-AGENT-RESTART' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'PASS' `
            -Summary "Agent 使用同一隔离 data-root 重启并存活 $RealProcessObservationSeconds 秒。" `
            -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
            -NextAction '该自动结果不替代 Windows 睡眠/唤醒人工验收。'

        Add-GateResult `
            -Id 'P3-09-REAL-PROCESS' `
            -EvidenceLevel 'real-process isolated' `
            -Status 'PASS' `
            -Summary "Agent/Main/Widget 在隔离 root 内存活 $RealProcessObservationSeconds 秒。" `
            -Evidence "$processLogRoot; $([string]::Join(', ', $startedDescriptions))" `
            -NextAction '仍需执行 IPC/通道健康、睡眠唤醒和桌面人工用例；Agent 重启结果见 P3-09-AGENT-RESTART。'
    }
    finally {
        foreach ($process in $processes) {
            Stop-LaunchedProcess -Process $process
        }
    }
}

function Invoke-IsolatedMigrationEvidence {
    param([Parameter(Mandatory)][string]$RunRoot)

    if ($script:migrationEvidenceRecorded) {
        return
    }

    $script:migrationEvidenceRecorded = $true

    if (-not $RunRealProcess) {
        Add-GateResult `
            -Id 'P3-09-MIGRATION-ISOLATED' `
            -EvidenceLevel 'CLI/harness/temporary clone' `
            -Status 'PENDING' `
            -Summary '本次未启动隔离 Agent，尚无 Candidate migration/verify/promote 运行证据。' `
            -Evidence '未提供 -RunRealProcess' `
            -NextAction '准备隔离 P2.5 fixture 和 Release 二进制后运行 -RunRealProcess；不得使用 Active 或受保护数据库。'
        return
    }

    $isolatedDataRoot = Join-Path $RunRoot 'real-process-data'
    $markerFiles = @(Get-ChildItem `
            -LiteralPath $isolatedDataRoot `
            -Recurse `
            -File `
            -Filter 'migration-state.json' `
            -ErrorAction SilentlyContinue)
    if ($markerFiles.Count -eq 0) {
        Add-GateResult `
            -Id 'P3-09-MIGRATION-ISOLATED' `
            -EvidenceLevel 'CLI/harness/temporary clone' `
            -Status 'PENDING' `
            -Summary '隔离 Agent 已运行，但未发现 P2.75 migration marker。' `
            -Evidence $isolatedDataRoot `
            -NextAction '确认隔离 data-root 预置了 P2.5 schema，并保留 migration-state、backup 和 recovery history 证据。'
        return
    }

    $failures = [System.Collections.Generic.List[string]]::new()
    $evidence = [System.Collections.Generic.List[string]]::new()
    foreach ($markerFile in $markerFiles) {
        try {
            $marker = [System.IO.File]::ReadAllText($markerFile.FullName) | ConvertFrom-Json
        }
        catch {
            $failures.Add("marker parse failed: $($markerFile.FullName)")
            continue
        }

        $targetSchema = @($marker.targetSchema | ForEach-Object { [string]$_ })
        $hasP3Reminder = $targetSchema -contains '20260902141656_P3ReminderPersistence'
        $hasP304 = $targetSchema -contains '20260904090000_P304'
        $stateReady = [string]$marker.state -eq 'READY'
        $backupArtifact = [string]$marker.backupArtifact
        $candidateArtifact = [string]$marker.candidateArtifact
        $runId = [string]$marker.runId
        $profileRoot = Split-Path (Split-Path $markerFile.FullName -Parent) -Parent
        $activePath = Join-Path $profileRoot 'reminnote.sqlite'
        $backupPath = if (-not [string]::IsNullOrWhiteSpace($backupArtifact) -and
            [System.IO.Path]::GetFileName($backupArtifact) -eq $backupArtifact) {
            Join-Path (Join-Path $profileRoot 'backups') $backupArtifact
        }
        else {
            $null
        }
        $historyPath = if (-not [string]::IsNullOrWhiteSpace($runId)) {
            Join-Path (Join-Path (Join-Path $profileRoot 'recovery') 'history') $runId
        }
        else {
            $null
        }

        $evidence.Add("marker=$($markerFile.FullName)")
        $evidence.Add("state=$($marker.state); target=$([string]::Join(',', $targetSchema))")
        if (-not $stateReady) { $failures.Add("marker state is not READY: $($marker.state)") }
        if (-not $hasP3Reminder -or -not $hasP304) { $failures.Add("P3 target migrations are missing: $($markerFile.FullName)") }
        if ([string]::IsNullOrWhiteSpace($backupArtifact) -or $null -eq $backupPath -or
            -not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            $failures.Add("verified backup artifact is missing: $backupArtifact")
        }
        if ([string]::IsNullOrWhiteSpace($candidateArtifact)) { $failures.Add('candidate artifact is missing from marker') }
        if (-not (Test-Path -LiteralPath $activePath -PathType Leaf)) { $failures.Add("promoted Active database is missing: $activePath") }
        if ($null -eq $historyPath -or -not (Test-Path -LiteralPath $historyPath -PathType Container)) {
            $failures.Add("promotion history is missing: $historyPath")
        }
    }

    if ($failures.Count -gt 0) {
        Add-GateResult `
            -Id 'P3-09-MIGRATION-ISOLATED' `
            -EvidenceLevel 'CLI/harness/temporary clone' `
            -Status 'FAIL' `
            -Summary '隔离 migration marker/backup/promote 证据不完整。' `
            -Evidence "$([string]::Join('; ', $evidence)); failures=$([string]::Join('; ', $failures))" `
            -NextAction '保留隔离 artifact，修复 backup→Candidate→verify→promote 证据链；不得把不完整 marker 记为通过。'
        return
    }

    Add-GateResult `
        -Id 'P3-09-MIGRATION-ISOLATED' `
        -EvidenceLevel 'CLI/harness/temporary clone' `
        -Status 'PASS' `
        -Summary '隔离 P2.5→P3 migration 已完成 backup→Candidate→verify→atomic promote，并留下恢复历史。' `
        -Evidence ([string]::Join('; ', $evidence)) `
        -NextAction '继续执行 Agent restart；失败恢复路径仍需单独的故障注入证据。'
}

function Write-GateReport {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][string]$Head,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$OverallStatus,
        [Parameter(Mandatory)][string]$GeneratedAtUtc,
        [Parameter(Mandatory)][string]$DotNetDescription
    )

    $jsonPath = Join-Path $RunRoot 'p3-09-gate-report.json'
    $markdownPath = Join-Path $RunRoot 'p3-09-gate-report.md'
    $report = [ordered]@{
        schema = 'reminnote.p3-09.gate-report'
        schemaVersion = 1
        generatedAtUtc = $GeneratedAtUtc
        overallStatus = $OverallStatus
        baseline = [ordered]@{
            repositoryRoot = $repositoryRoot
            head = $Head
            branch = $Branch
            configuration = $Configuration
            dotNet = $DotNetDescription
        }
        isolation = [ordered]@{
            artifactRoot = $RunRoot
            isolatedRoot = (Join-Path $RunRoot 'isolated-root')
            isolatedDataRoot = (Join-Path $RunRoot 'isolated-data')
            protectedDatabaseAccess = 'not attempted; the protected target is never opened, copied, locked, or connected'
        }
        dependencies = $dependencyStatuses
        commands = @($commandRecords)
        checks = @($results.ToArray())
        evidenceLevels = @(
            'contract/unit/fake',
            'CLI/harness/temporary clone',
            'real-process isolated',
            'normal desktop manual',
            'user sign-off'
        )
        signOff = [ordered]@{
            status = 'PENDING'
            recorded = $false
            note = '脚本、fake、CLI、UI Automation 或真实进程 smoke 均不能代替用户正常桌面 sign-off。'
        }
    }

    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($jsonPath, $json, [System.Text.UTF8Encoding]::new($false))

    $markdown = [System.Text.StringBuilder]::new()
    [void]$markdown.AppendLine('# ReminNote P3-09 最终总成 Gate 报告')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine("状态：**$OverallStatus**")
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('本报告由 `scripts/verify-p3-09.ps1` 生成。`BLOCKED`/`PENDING` 是 fail-closed 结果，不是功能完成或用户 sign-off。')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('## 基线与隔离')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine("- 生成时间（UTC）：$GeneratedAtUtc")
    [void]$markdown.AppendLine("- 当前 HEAD：``$Head``")
    [void]$markdown.AppendLine("- 当前分支显示：``$Branch``")
    [void]$markdown.AppendLine("- 构建配置：``$Configuration``")
    [void]$markdown.AppendLine("- 隔离 artifact root：``$RunRoot``")
    [void]$markdown.AppendLine("- 隔离验证 root：``$(Join-Path $RunRoot 'isolated-root')``")
    [void]$markdown.AppendLine('- 受保护数据库：未访问；本轮没有打开、复制、锁定、连接或写入。')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('## 检查结果')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('| ID | 证据级别 | 状态 | 摘要 | 证据 | 下一步 |')
    [void]$markdown.AppendLine('| --- | --- | --- | --- | --- | --- |')
    foreach ($result in $results) {
        [void]$markdown.AppendLine("| $(Convert-ToMarkdownCell $result.id) | $(Convert-ToMarkdownCell $result.evidenceLevel) | $(Convert-ToMarkdownCell $result.status) | $(Convert-ToMarkdownCell $result.summary) | $(Convert-ToMarkdownCell $result.evidence) | $(Convert-ToMarkdownCell $result.nextAction) |")
    }
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('## 依赖结论')
    [void]$markdown.AppendLine()
    foreach ($dependency in $dependencyStatuses.GetEnumerator()) {
        [void]$markdown.AppendLine("- **$($dependency.Key)**：$($dependency.Value)")
    }
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('P3-03/P3-05/P3-06 任一项不是 `READY` 时，不能进入 P3 Alpha；不能用已有契约测试、fake、CLI 或 UI Automation 代替对应真实进程和桌面证据。')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('## 证据边界')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('- contract/unit/fake：证明纯契约、策略或接口语义。')
    [void]$markdown.AppendLine('- CLI/harness/temporary clone：证明隔离 fixture/命令，不证明桌面体验。')
    [void]$markdown.AppendLine('- real-process isolated：必须记录 PID、二进制路径、隔离 root、日志、重启/睡眠唤醒结果。')
    [void]$markdown.AppendLine('- normal desktop manual：必须由用户在正常桌面执行 Main/Widget/通知交互。')
    [void]$markdown.AppendLine('- user sign-off：必须单独记录范围、版本、时间和未覆盖项；当前为 `PENDING`。')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('## 用户 sign-off')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('- [ ] Agent 重启恢复')
    [void]$markdown.AppendLine('- [ ] Windows 睡眠/唤醒恢复')
    [void]$markdown.AppendLine('- [ ] Toast/Tray/Widget 不可用时核心提醒事实仍保留')
    [void]$markdown.AppendLine('- [ ] Main Reminder Drawer/Center 查询、READ/RESOLVED、Snooze/DONE')
    [void]$markdown.AppendLine('- [ ] 隔离 Candidate migration/verify/promote 与失败恢复')
    [void]$markdown.AppendLine('- [ ] 用户已明确签署本版本 P3 Alpha 范围')
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('用户签署人：`待填写`；签署时间：`待填写`；未覆盖项：`待填写`。')

    [System.IO.File]::WriteAllText($markdownPath, $markdown.ToString(), [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ JsonPath = $jsonPath; MarkdownPath = $markdownPath }
}

try {
    $artifactParent = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        Join-Path ([System.IO.Path]::GetTempPath()) 'ReminNote.P3-09'
    }
    else {
        Convert-ToCanonicalPath $ArtifactRoot
    }

    $artifactParent = [System.IO.Path]::GetFullPath($artifactParent)
    $protectedDataDirectory = [System.IO.Path]::GetDirectoryName($protectedDatabasePath)
    if (Test-PathWithin -Candidate $artifactParent -Parent $repositoryRoot) {
        throw "artifact root 不能位于当前仓库内：$artifactParent"
    }

    $artifactUnderProtectedData = Test-PathWithin -Candidate $artifactParent -Parent $protectedDataDirectory
    $artifactIsProtectedDatabase = $artifactParent.Equals($protectedDatabasePath, [System.StringComparison]::OrdinalIgnoreCase)
    if ($artifactUnderProtectedData -or $artifactIsProtectedDatabase) {
        throw 'artifact root 命中受保护数据边界，已拒绝。'
    }

    [System.IO.Directory]::CreateDirectory($artifactParent) | Out-Null
    $runRoot = Join-Path $artifactParent "run-$([Guid]::NewGuid().ToString('N'))"
    [System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $runRoot 'isolated-root')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $runRoot 'isolated-data')) | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $runRoot 'isolated-root\p3-09-isolation.marker'),
        'P3-09 isolated artifact; no product database is created by this marker.',
        [System.Text.UTF8Encoding]::new($false))

    $generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $headOutput = & git -C $repositoryRoot rev-parse HEAD
    $head = ($headOutput -join '').Trim()
    $branchOutput = & git -C $repositoryRoot branch --show-current
    $branch = ($branchOutput -join '').Trim()
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = '(detached HEAD)'
    }

    $protectedStatus = (& git -C $repositoryRoot status --porcelain -- reviews second-review)
    if (-not [string]::IsNullOrWhiteSpace(($protectedStatus -join ''))) {
        Add-GateResult `
            -Id 'P3-09-BOUNDARY-REVIEWS' `
            -EvidenceLevel 'static' `
            -Status 'FAIL' `
            -Summary '受保护 reviews/ 或 second-review/ 存在工作树变更。' `
            -Evidence ($protectedStatus -join "`n") `
            -NextAction '停止 gate，保留并恢复受保护材料的原状后再运行。'
    }
    else {
        Add-GateResult `
            -Id 'P3-09-BOUNDARY-REVIEWS' `
            -EvidenceLevel 'static' `
            -Status 'PASS' `
            -Summary 'reviews/ 与 second-review/ 未被本工作树修改。' `
            -Evidence 'git status --porcelain -- reviews second-review：clean' `
            -NextAction '后续总成变更继续禁止修改受保护材料。'
    }

    Add-GateResult `
        -Id 'P3-09-BOUNDARY-ARTIFACT' `
        -EvidenceLevel 'CLI/harness/temporary clone' `
        -Status 'PASS' `
        -Summary '验证 marker、build、test log 和报告均位于新的隔离 artifact root。' `
        -Evidence $runRoot `
        -NextAction '真实进程模式也必须复用该隔离边界，不得改指向正式或当前工作树数据。'

    $requiredDocuments = @(
        @{ Id = 'P3-09-DOC-P300'; Name = 'P3-00 契约'; Path = 'docs\slices\P3-00-contract-freeze.md'; Markers = @('证据级别', 'P3-03', 'P3-09') },
        @{ Id = 'P3-09-DOC-P301'; Name = 'P3-01 报告'; Path = 'docs\slices\P3-01-report.md'; Markers = @('验证', '后续接入点') },
        @{ Id = 'P3-09-DOC-P302'; Name = 'P3-02 计算契约'; Path = 'docs\slices\P3-02-reminder-calculation.md'; Markers = @('隔离验证记录', '总成接入适配点') },
        @{ Id = 'P3-09-DOC-P304'; Name = 'P3-04 通知契约'; Path = 'docs\slices\P3-04-notification-contract.md'; Markers = @('验证', '未决集成点') },
        @{ Id = 'P3-09-DOC-P307'; Name = 'P3-07 策略契约'; Path = 'docs\slices\P3-07-policy-recovery.md'; Markers = @('验证矩阵', '桌面人工验收') },
        @{ Id = 'P3-09-DOC-P308'; Name = 'P3-08 导出恢复契约'; Path = 'docs\slices\P3-08-export-restore-contract.md'; Markers = @('隔离测试', 'P3-09') }
    )
    foreach ($requiredDocument in $requiredDocuments) {
        Add-RequiredDocumentCheck -Spec $requiredDocument
    }

    $coreText = Get-TreeText -RelativePath 'src\windows\ReminNote.Core' -Extensions @('.cs', '.csproj')
    $agentText = Get-TreeText -RelativePath 'src\windows\ReminNote.Agent' -Extensions @('.cs', '.csproj')
    $infrastructureText = Get-TreeText -RelativePath 'src\windows\ReminNote.Infrastructure' -Extensions @('.cs', '.csproj')
    $databaseContextText = Get-RepositoryText -RelativePath 'src\windows\ReminNote.Infrastructure\Persistence\ReminNoteDbContext.cs'
    $windowsText = Get-TreeText -RelativePath 'src\windows\ReminNote.Windows' -Extensions @('.cs', '.xaml', '.csproj')
    $widgetText = Get-TreeText -RelativePath 'src\windows\ReminNote.Widget' -Extensions @('.cs', '.xaml', '.csproj')
    $windowsProductionText = Get-TreeText `
        -RelativePath 'src\windows\ReminNote.Windows' `
        -Extensions @('.cs', '.xaml', '.csproj') `
        -ExcludeFileNames @('UiText.cs')
    $widgetProductionText = Get-TreeText `
        -RelativePath 'src\windows\ReminNote.Widget' `
        -Extensions @('.cs', '.xaml', '.csproj') `
        -ExcludeFileNames @('UiText.cs')
    $uiProductionText = "$windowsProductionText`n$widgetProductionText"
    # P3-05 adapters and P3-06 application contracts are shared Core seams;
    # host composition remains in Windows/Widget. Scan both layers so the
    # gate does not report a false BLOCKED result when the owner implementation
    # is deliberately kept out of a host project.
    $p305Text = "$coreText`n$uiProductionText"
    $p306Text = "$coreText`n$agentText`n$uiProductionText"
    $testRoot = Join-Path $repositoryRoot 'tests\ReminNote.Tests'
    $testSourceText = Get-TreeText -RelativePath 'tests\ReminNote.Tests' -Extensions @('.cs', '.csproj')

    $p303Checks = @(
        Add-DependencyMarkerCheck `
            -Dependency 'P3-03' `
            -Id 'P3-03-DB-WIRING' `
            -Description 'Reminder model 显式接入正式 DbContext' `
            -Text $databaseContextText `
            -Pattern '\b(?:public|internal)\s+DbSet\s*<\s*Reminder(?:Rule|Schedule|Instance)\b|\bApplyReminderConfigurations\s*\(' `
            -Evidence 'src/windows/ReminNote.Infrastructure/Persistence/ReminNoteDbContext.cs 及 Reminder model 配置' `
            -NextAction '在总成串行窗口完成 Reminder DbSet/configuration、migration/model snapshot，并通过隔离 Candidate 验证。'
        Add-DependencyMarkerCheck `
            -Dependency 'P3-03' `
            -Id 'P3-03-SCHEDULER' `
            -Description 'Agent 内有 Reminder scheduler/planner/instance 运行时组合' `
            -Text $agentText `
            -Pattern '\bclass\s+\w*(?:ReminderScheduler|ReminderSchedulePlanner|ReminderInstance)\b' `
            -Evidence 'src/windows/ReminNote.Agent/**/*.cs' `
            -NextAction '实现唯一 Agent writer 内的 due scheduler、schedule consume、instance append 和 recovery 接线。'
        Add-DependencyMarkerCheck `
            -Dependency 'P3-03' `
            -Id 'P3-03-TESTS' `
            -Description 'P3-03 scheduler contract/transaction 测试目录' `
            -Text $testSourceText `
            -Pattern 'P3_03|P3-03' `
            -Evidence 'tests/ReminNote.Tests/P3_03 或等价带明确证据等级的测试' `
            -NextAction '补齐 due/restart/idempotency/transaction 测试；fake 只能作为 contract 证据，不能替代真实进程。'
    )
    $dependencyStatuses['P3-03'] = if (@($p303Checks | Where-Object { $_ -eq $false }).Count -eq 0) { 'READY' } else { 'BLOCKED' }
    Add-GateResult `
        -Id 'P3-03-SUMMARY' `
        -EvidenceLevel 'static' `
        -Status $(if ($dependencyStatuses['P3-03'] -eq 'READY') { 'PASS' } else { 'BLOCKED' }) `
        -Summary "P3-03 依赖审查：$($dependencyStatuses['P3-03'])。" `
        -Evidence 'DB wiring + Agent scheduler + dedicated tests must all be present' `
        -NextAction 'P3-03 未 READY 时禁止启动 P3-09 真实进程总成。'

    $p305Checks = @(
        Add-DependencyMarkerCheck `
            -Dependency 'P3-05' `
            -Id 'P3-05-CHANNEL-ADAPTER' `
            -Description 'Windows/Widget 存在 INotificationChannel 实际实现' `
            -Text $p305Text `
            -Pattern '\bclass\s+\w*NotificationChannel\s*:\s*(?:NotificationChannelAdapter|[^\r\n{]*\bINotificationChannel\b)' `
            -Evidence 'src/windows/ReminNote.Core adapter 实现及 Windows/Widget host composition' `
            -NextAction '实现 Toast/Tray/Widget/Sound/WakeTimer adapter；adapter 只能消费 core fact，不能写 Rule/Schedule/Instance。'
        Add-DependencyMarkerCheck `
            -Dependency 'P3-05' `
            -Id 'P3-05-DELIVERY' `
            -Description '实际 adapter 提供 DeliverAsync 且记录 channel health/result' `
            -Text $p305Text `
            -Pattern '\bDeliverAsync\s*\(|\bNotificationChannelHealth\b|\bNotificationDeliveryOutcome\b' `
            -Evidence 'Core adapter source and Windows/Widget health/result composition' `
            -NextAction '补齐不可用/blocked/failed/replace 行为，并以真实进程日志证明核心事实不丢失。'
        Add-DependencyPathCheck `
            -Dependency 'P3-05' `
            -Id 'P3-05-TESTS' `
            -Description 'P3-05 channel adapter 测试目录' `
            -RelativePath 'tests\ReminNote.Tests\P3_05' `
            -NextAction '增加 channel capability/health/replace 与真实 Windows 通道的分层测试。'
    )
    $dependencyStatuses['P3-05'] = if (@($p305Checks | Where-Object { $_ -eq $false }).Count -eq 0) { 'READY' } else { 'BLOCKED' }
    Add-GateResult `
        -Id 'P3-05-SUMMARY' `
        -EvidenceLevel 'static' `
        -Status $(if ($dependencyStatuses['P3-05'] -eq 'READY') { 'PASS' } else { 'BLOCKED' }) `
        -Summary "P3-05 依赖审查：$($dependencyStatuses['P3-05'])。" `
        -Evidence 'channel adapter + delivery/health mapping + dedicated tests must all be present' `
        -NextAction 'P3-05 未 READY 时不能把 P3-04 contract/fake 结果写成真实通知验收。'

    $p306Checks = @(
        Add-DependencyMarkerCheck `
            -Dependency 'P3-06' `
            -Id 'P3-06-QUERY' `
            -Description 'Main/Widget 有 Reminder read-only query/read-model 接线' `
            -Text $p306Text `
            -Pattern '\b(?:interface|class|record|enum)\s+\w*(?:Reminder(?:Query(?:Service|Client)?|ReadModel|Snapshot|Projection)|AgentReminder\w*)\b|\bIReminderQueryService\b' `
            -Evidence 'Core reminder application contracts plus Agent/Main/Widget query composition' `
            -NextAction '接入 Agent read-only query、revision/stale/unavailable 状态；禁止 UI/第二 DbContext 直接写 Reminder。'
        Add-DependencyMarkerCheck `
            -Dependency 'P3-06' `
            -Id 'P3-06-ACTIONS' `
            -Description 'Reminder UI 有 READ/RESOLVED/Snooze/DONE 动作入口' `
            -Text $p306Text `
            -Pattern '\b(?:class|record)\s+\w*ReminderCommand\w*\b|\b(?:AgentReminder|ResolveReminder|SnoozeReminder|SendReminder)Command\s*(?:\(|=)' `
            -Evidence 'Core reminder command contract plus Main/Widget Drawer/Center action source' `
            -NextAction '完成 Drawer/Center、生命周期动作和失败反馈；动作必须走 Agent command/receipt。'
        Add-DependencyPathCheck `
            -Dependency 'P3-06' `
            -Id 'P3-06-TESTS' `
            -Description 'P3-06 UI/query/action 测试目录' `
            -RelativePath 'tests\ReminNote.Tests\P3_06' `
            -NextAction '增加 read-only query、stale snapshot、Agent unavailable 和用户动作测试；不能以旧 UIA 测试代替。'
    )
    $dependencyStatuses['P3-06'] = if (@($p306Checks | Where-Object { $_ -eq $false }).Count -eq 0) { 'READY' } else { 'BLOCKED' }
    Add-GateResult `
        -Id 'P3-06-SUMMARY' `
        -EvidenceLevel 'static' `
        -Status $(if ($dependencyStatuses['P3-06'] -eq 'READY') { 'PASS' } else { 'BLOCKED' }) `
        -Summary "P3-06 依赖审查：$($dependencyStatuses['P3-06'])。" `
        -Evidence 'read-only query + actions + dedicated tests must all be present' `
        -NextAction 'P3-06 未 READY 时不能把现有 Main/Widget Task UI 或 UIA 记录写成 Reminder 桌面验收。'

    $migrationText = Get-RepositoryText -RelativePath 'src\windows\ReminNote.Agent\Runtime\AgentMigrationStartup.cs'
    $migrationCatalogText = Get-RepositoryText -RelativePath 'src\windows\ReminNote.Infrastructure\Persistence\P275\P275MigrationPlanCatalog.cs'
    $p308Text = Get-RepositoryText -RelativePath 'src\windows\ReminNote.Core\Reminders\Export\ReminderRestorePlanner.cs'
    $legacyMigrationPlanIsActive = $migrationText.Contains('20260831090000_P25StorageConsistency')
    $migrationPlanHasNoReminderTarget = -not $migrationText.Contains('Reminder')
    $migrationPlanText = $migrationText + "`n" + $migrationCatalogText
    $migrationPlanHasP3Targets = $migrationPlanText.Contains('20260902141656_P3ReminderPersistence') -and
        $migrationPlanText.Contains('20260904090000_P304')
    if ($legacyMigrationPlanIsActive -and $migrationPlanHasNoReminderTarget) {
        Add-GateResult `
            -Id 'P3-09-MIGRATION' `
            -EvidenceLevel 'static/contract' `
            -Status 'BLOCKED' `
            -Summary 'Agent migration target 仍为 P2.5；尚无 P3 Reminder schema/forward migration 接线。' `
            -Evidence 'AgentMigrationStartup.cs DefaultPlan; ReminderRestorePlanner remains pure plan' `
            -NextAction '待 P3-03 schema 接线后，串行更新 target plan/migration/snapshot，并在隔离 Candidate root 完成 backup→verify→promote。'
    }
    elseif ($migrationPlanHasP3Targets) {
        Add-GateResult `
            -Id 'P3-09-MIGRATION' `
            -EvidenceLevel 'static/contract' `
            -Status 'PASS' `
            -Summary 'Agent 默认迁移计划已包含 P3 Reminder 与 P3-04 增量 schema。' `
            -Evidence 'AgentMigrationStartup.cs + P275MigrationPlanCatalog.cs' `
            -NextAction '仍需以隔离 P2.5 fixture 运行 Candidate migration/verify/promote，并保留 marker/backup/history。'
    }
    else {
        Add-GateResult `
            -Id 'P3-09-MIGRATION' `
            -EvidenceLevel 'static/contract' `
            -Status 'PENDING' `
            -Summary '迁移接线需要总成复核。' `
            -Evidence 'AgentMigrationStartup.cs + ReminderRestorePlanner.cs' `
            -NextAction '不得在 Active 或当前工作树数据库上试迁移。'
    }

    $restorePlanExposesActiveWriteFlag = $p308Text.Contains('WritesActiveDatabase')
    $restorePlanHasActiveWriteError = $p308Text.Contains('RestoreActiveWriteForbidden')
    $restorePlanDefaultsToNoActiveWrite = $p308Text.Contains('WritesActiveDatabase: false')
    $restoreContractIsSafe = $restorePlanExposesActiveWriteFlag -and $restorePlanHasActiveWriteError -and $restorePlanDefaultsToNoActiveWrite
    if ($restoreContractIsSafe) {
        Add-GateResult `
            -Id 'P3-09-RESTORE-CONTRACT' `
            -EvidenceLevel 'contract/unit/fake' `
            -Status 'PASS' `
            -Summary 'P3-08 受控恢复仍保持 Active 写入拒绝契约。' `
            -Evidence 'ReminderRestorePlanner.cs' `
            -NextAction '仍需在隔离 Candidate 上运行独立 verify/promotion；不得在 Active 上试验。'
    }
    else {
        Add-GateResult `
            -Id 'P3-09-RESTORE-CONTRACT' `
            -EvidenceLevel 'contract/unit/fake' `
            -Status 'FAIL' `
            -Summary 'P3-08 Active 写入拒绝契约标记缺失。' `
            -Evidence 'ReminderRestorePlanner.cs' `
            -NextAction '恢复 fail-closed restore contract；不能进入 Candidate/Active 操作。'
    }

    $dotNetPath = $null
    $dotNetDescription = '未解析'
    try {
        . (Join-Path $PSScriptRoot 'resolve-dotnet.ps1')
        $dotNetPath = Resolve-DotNetPath
        $dotNetDescription = (& $dotNetPath --version).Trim()
        Invoke-ContractTests -RunRoot $runRoot -DotNetPath $dotNetPath
    }
    catch {
        Add-GateResult `
            -Id 'P3-09-CONTRACT-EXECUTION' `
            -EvidenceLevel 'contract/unit/fake + isolated' `
            -Status 'FAIL' `
            -Summary "无法执行 P3 contract/isolated 测试：$($_.Exception.Message)" `
            -Evidence $runRoot `
            -NextAction '修复本机 SDK/restore/build 环境后重新运行 gate；不能把未执行当作通过。'
    }

    Invoke-RealProcessProbe -RunRoot $runRoot

    Invoke-IsolatedMigrationEvidence -RunRoot $runRoot

    Add-GateResult `
        -Id 'P3-09-STABILITY-AGENT' `
        -EvidenceLevel 'real-process isolated' `
        -Status 'PENDING' `
        -Summary 'Agent 重启由自动探针单独记录；Windows 睡眠/唤醒仍属于必要人工项。' `
        -Evidence 'P3-09-AGENT-RESTART；sleep/wake 不能在本门禁中安全模拟。' `
        -NextAction '确认 P3-09-AGENT-RESTART 已 PASS；随后由用户执行真实 Windows 睡眠/唤醒并记录恢复结果。'

    Add-GateResult `
        -Id 'P3-09-DESKTOP-MANUAL' `
        -EvidenceLevel 'normal desktop manual' `
        -Status 'PENDING' `
        -Summary '正常桌面人工验收未执行。' `
        -Evidence '当前没有用户操作、窗口/通知截图、版本范围或签署记录。' `
        -NextAction '依赖 READY 后，按 docs/reports/P3-09-最终总成报告-模板.md 的清单由用户在隔离 clone 执行。'

    Add-GateResult `
        -Id 'P3-09-USER-SIGNOFF' `
        -EvidenceLevel 'user sign-off' `
        -Status 'PENDING' `
        -Summary '用户 sign-off 未提供。' `
        -Evidence '脚本、CLI、fake、真实进程 smoke 和 UI Automation 都不构成 sign-off。' `
        -NextAction '所有硬依赖、真实进程和桌面清单完成后，由用户明确记录版本、范围、时间及未覆盖项。'

    $overallStatus = Get-OverallStatus
    $reportPaths = Write-GateReport `
        -RunRoot $runRoot `
        -Head $head `
        -Branch $branch `
        -OverallStatus $overallStatus `
        -GeneratedAtUtc $generatedAtUtc `
        -DotNetDescription $dotNetDescription

    Write-Output "P3-09 gate status: $overallStatus"
    Write-Output "P3-03: $($dependencyStatuses['P3-03']); P3-05: $($dependencyStatuses['P3-05']); P3-06: $($dependencyStatuses['P3-06'])"
    Write-Output "Artifact root: $runRoot"
    Write-Output "Markdown report: $($reportPaths.MarkdownPath)"
    Write-Output "JSON report: $($reportPaths.JsonPath)"

    if ($overallStatus -eq 'PASS') {
        exit 0
    }

    if ($overallStatus -eq 'FAIL') {
        exit 1
    }

    exit 2
}
catch {
    [System.Console]::Error.WriteLine("P3-09 gate execution error: $($_.Exception.Message)")
    exit 3
}
