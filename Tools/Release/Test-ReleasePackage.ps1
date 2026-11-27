<#
.SYNOPSIS
Validates a self-contained ReTran release and optionally merges Core publish output.
Kiểm tra release ReTran self-contained và có thể gộp output publish của Core.

.DESCRIPTION
When CorePublishDir is supplied, records App payload SHA-256 hashes, copies only the four
Core artifacts plus Llm/**, and verifies that the App payload remains byte-identical.
The publish gate checks all required files and non-empty includedFrameworks, then runs
Core capture list-monitors and verifies that updater no-args prints usage and exits 1.
Khi có CorePublishDir, script ghi hash SHA-256 của payload App, chỉ copy bốn artifact Core
cùng Llm/**, rồi xác nhận payload App không đổi. Publish gate kiểm tra mọi file bắt buộc và
includedFrameworks không rỗng, sau đó chạy Core capture list-monitors và xác nhận updater
không tham số in usage rồi thoát với mã 1.

.PARAMETER OutputDir
Path to the App publish directory, or the merged package after Core copy.
Đường dẫn output publish App, hoặc package đã gộp Core.

.PARAMETER CorePublishDir
Optional self-contained Core publish directory to merge with hash protection.
Thư mục publish Core self-contained tùy chọn để gộp có bảo vệ hash.

.PARAMETER ExpectedTag
Optional release tag; must equal ApplicationDisplayVersion after TrimStart('v').
Tag release tùy chọn; sau TrimStart('v') phải bằng ApplicationDisplayVersion.

.PARAMETER ProjectPath
Path to ReTran.App.csproj used for version validation; defaults to the repository project.
Đường dẫn ReTran.App.csproj để kiểm version; mặc định là project trong repository.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDir,

    [string]$CorePublishDir,

    [string]$ExpectedTag,

    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\..\ReTran.App\ReTran.App.csproj")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$coreOwnedFiles = @(
    "ReTran.Core.exe",
    "ReTran.Core.dll",
    "ReTran.Core.deps.json",
    "ReTran.Core.runtimeconfig.json"
)

function Assert-ReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$CsprojPath,
        [string]$Tag
    )

    if (-not (Test-Path -LiteralPath $CsprojPath -PathType Leaf)) {
        throw "Project file not found: $CsprojPath"
    }

    [xml]$csproj = Get-Content -LiteralPath $CsprojPath -Raw
    $version = [string]($csproj.Project.PropertyGroup.Version | Select-Object -First 1)
    $display = [string]($csproj.Project.PropertyGroup.ApplicationDisplayVersion | Select-Object -First 1)

    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version '$version' must contain exactly four numeric components."
    }

    $versionDisplay = ($version.Split('.') | Select-Object -First 3) -join '.'
    if ($display -cne $versionDisplay) {
        throw "Version '$version' has display prefix '$versionDisplay', but ApplicationDisplayVersion is '$display'."
    }

    if ($PSBoundParameters.ContainsKey('Tag')) {
        $trimmedTag = $Tag.TrimStart('v')
        if ($trimmedTag -cne $display) {
            throw "Tag '$Tag' does not match ApplicationDisplayVersion '$display' after TrimStart('v')."
        }
    }

    Write-Host "Version guard passed: Version=$version; ApplicationDisplayVersion=$display."
}

function Get-AppPayloadHashes {
    param([Parameter(Mandatory = $true)][string]$Root)

    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($coreOwnedFiles -contains $relative -or $relative.StartsWith('Llm/', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $hashes
}

function Copy-CorePublishPayload {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    # Copy only Core-owned artifacts so the App dependency closure remains untouched.
    # Chỉ copy artifact thuộc Core để không thay đổi dependency closure của App.
    $sourcePaths = @()
    foreach ($name in $coreOwnedFiles) {
        $sourcePath = Join-Path $Source $name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Core publish artifact missing: $sourcePath"
        }
        $sourcePaths += $sourcePath
    }

    $llmSource = Join-Path $Source "Llm"
    if (-not (Test-Path -LiteralPath $llmSource -PathType Container)) {
        throw "Core publish LLM payload missing: $llmSource"
    }
    $llmFiles = @(Get-ChildItem -LiteralPath $llmSource -Recurse -File)
    if ($llmFiles.Count -eq 0) {
        throw "Core publish LLM payload is empty: $llmSource"
    }

    Copy-Item -LiteralPath $sourcePaths -Destination $Destination -Force

    # Copy files individually so merging an existing Llm directory is idempotent.
    # Copy từng file để gộp vào thư mục Llm có sẵn luôn idempotent.
    foreach ($file in $llmFiles) {
        $relative = [IO.Path]::GetRelativePath($llmSource, $file.FullName)
        $targetPath = Join-Path (Join-Path $Destination "Llm") $relative
        $targetDirectory = Split-Path -Parent $targetPath
        if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $targetPath -Force
    }
}

function Assert-AppPayloadUnchanged {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Before,
        [Parameter(Mandatory = $true)][hashtable]$After
    )

    $changes = @()
    foreach ($relative in $Before.Keys) {
        if (-not $After.ContainsKey($relative)) {
            $changes += "$relative (missing after Core copy)"
        }
        elseif ($Before[$relative] -cne $After[$relative]) {
            $changes += "$relative (hash changed)"
        }
    }
    foreach ($relative in $After.Keys) {
        if (-not $Before.ContainsKey($relative)) {
            $changes += "$relative (added after Core copy)"
        }
    }

    if ($changes.Count -gt 0) {
        throw "Core publish changed the App payload: $($changes -join '; ')"
    }

    Write-Host "Verified $($Before.Count) App payload hashes unchanged and no unexpected files added across Core copy."
}

function Assert-PublishGate {
    param([Parameter(Mandatory = $true)][string]$Root)

    $requiredFiles = @(
        "ReTran.App.exe",
        "ReTran.Core.exe",
        "ReTran.Updater.exe",
        "ReTran.App.runtimeconfig.json",
        "ReTran.Core.runtimeconfig.json",
        "ReTran.Updater.runtimeconfig.json",
        "ReTran.App.deps.json",
        "ReTran.Core.deps.json",
        "ReTran.Updater.deps.json",
        "hostfxr.dll",
        "hostpolicy.dll",
        "Vortice.Direct3D11.dll",
        "Vortice.DirectX.dll",
        "Vortice.DXGI.dll",
        "Vortice.Mathematics.dll",
        "SharpGen.Runtime.dll",
        "SharpGen.Runtime.COM.dll",
        "ReTran.OCR/pyproject.toml",
        "ReTran.OCR/uv.lock",
        "runtimes/win-x64/uv.exe",
        "Llm/llm-models.json"
    )

    $missing = @()
    foreach ($relative in $requiredFiles) {
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing += $relative
        }
    }
    if ($missing.Count -gt 0) {
        throw "Publish gate failed; missing files: $($missing -join ', ')"
    }

    foreach ($name in @("ReTran.App", "ReTran.Core", "ReTran.Updater")) {
        $configPath = Join-Path $Root "$name.runtimeconfig.json"
        try {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $frameworks = $config.runtimeOptions.includedFrameworks
        }
        catch {
            throw "Publish gate failed; invalid runtime config '$configPath': $($_.Exception.Message)"
        }

        if ($null -eq $frameworks -or @($frameworks).Count -eq 0) {
            throw "Publish gate failed; '$name.runtimeconfig.json' has an empty includedFrameworks list."
        }
    }

    Write-Host "Publish gate passed: executables, dependencies, self-contained runtime, OCR and LLM payload present."
}

function Invoke-NativeCheck {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "Native check timed out: $FilePath $($Arguments -join ' ')"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = ($stdoutTask.GetAwaiter().GetResult() + $stderrTask.GetAwaiter().GetResult()).Trim()
        }
    }
    finally {
        $process.Dispose()
    }
}

$versionArguments = @{ CsprojPath = $ProjectPath }
if ($PSBoundParameters.ContainsKey('ExpectedTag')) {
    $versionArguments.Tag = $ExpectedTag
}
Assert-ReleaseVersion @versionArguments

if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
    throw "Publish output directory not found: $OutputDir"
}
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

if ($PSBoundParameters.ContainsKey('CorePublishDir')) {
    if (-not (Test-Path -LiteralPath $CorePublishDir -PathType Container)) {
        throw "Core publish directory not found: $CorePublishDir"
    }
    $CorePublishDir = (Resolve-Path -LiteralPath $CorePublishDir).Path

    $before = Get-AppPayloadHashes -Root $OutputDir
    Write-Host "Recorded $($before.Count) App payload hashes before Core copy."
    $copyError = $null
    try {
        Copy-CorePublishPayload -Source $CorePublishDir -Destination $OutputDir
    }
    catch {
        $copyError = $_
    }

    $after = Get-AppPayloadHashes -Root $OutputDir
    Assert-AppPayloadUnchanged -Before $before -After $after
    if ($null -ne $copyError) {
        throw "Core publish copy failed: $($copyError.Exception.Message)"
    }
}

Assert-PublishGate -Root $OutputDir

$coreCheck = Invoke-NativeCheck `
    -FilePath (Join-Path $OutputDir "ReTran.Core.exe") `
    -Arguments @("capture", "list-monitors") `
    -WorkingDirectory $OutputDir
if ($coreCheck.ExitCode -ne 0) {
    throw "ReTran.Core capture list-monitors exited $($coreCheck.ExitCode): $($coreCheck.Output)"
}
Write-Host "Core capture list-monitors exited 0. $($coreCheck.Output)"

$updaterCheck = Invoke-NativeCheck `
    -FilePath (Join-Path $OutputDir "ReTran.Updater.exe") `
    -WorkingDirectory $OutputDir
if ($updaterCheck.ExitCode -ne 1 -or $updaterCheck.Output -notmatch 'Usage: ReTran\.Updater') {
    throw "Updater no-args smoke expected usage and exit 1; got exit $($updaterCheck.ExitCode): $($updaterCheck.Output)"
}
Write-Host "Updater no-args usage check exited 1. $($updaterCheck.Output)"
Write-Host "Release package smoke passed: $OutputDir"
