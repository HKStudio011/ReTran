# probe_after.ps1 — scripted post-fix measurement for the M5-fix section.
# Recreated 2026-10-21 after the original %TEMP%\opencode\ab_annotate.ps1 was wiped by temp cleanup.
# Promoted into the repo 2026-10-21 (Tools/Perf/) — repo root is derived from $PSScriptRoot.
#
# What it does: writes %LOCALAPPDATA%\ReTran\core\config.json {captureFps: N} (annotate rate, read at Core
# startup), spawns ReTran.Core.exe --serve, answers translate.request_cache with all-miss exactly like the
# App does, then measures the same numbers the Docs/performance.md M5 tables use:
#   core_cpu1core_avg / core_cpu1core_max (percent of ONE core), core_ramMB_avg/max, core_peak_wsMB,
#   annotated count + fps + line KB + ann w/h, rpc_MBps (stdout), boxes_avg/max (frame.translated),
#   translated / cache-request counts, and the preview MMF header (Local\ReTran.Preview.V1) + JPEG magic.
#
# Phases: settle (no metrics) -> measure window (-Seconds) whose first -DiscardSeconds are dropped.
param(
    [int]$SettleSeconds = 40,
    [int]$Seconds = 60,
    [int]$DiscardSeconds = 15,
    [int]$AnnotFps = 10,
    [string]$Label = 'T6'
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot)   # Tools/Perf -> Tools -> repo root
$core = Join-Path $repo 'ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe'
$cfgPath = Join-Path $env:LOCALAPPDATA 'ReTran\core\config.json'
$outDir = $PSScriptRoot
$csvPath = Join-Path $outDir ("probe_{0}_{1:yyyyMMdd_HHmmss}.csv" -f $Label, (Get-Date))
# Sidecar processes live under $repo\ReTran.OCR (venv) or carry '-m retran_core' on the command line.
$sidecarRe = [regex]::Escape($repo) + '\\ReTran\.OCR|retran_core'

if (-not (Test-Path $core)) { throw "core not found: $core (build first)" }
$cfgBackup = $null
$cfgExisted = Test-Path $cfgPath
if ($cfgExisted) { $cfgBackup = Get-Content $cfgPath -Raw }

function Stop-SidecarLeftovers {
    # The Core owns the sidecar; if it died uncleanly, kill matching python processes (best effort).
    try {
        Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine -match $sidecarRe } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path $cfgPath) | Out-Null
    Set-Content -Path $cfgPath -Value ("{ `"captureFps`": " + $AnnotFps + " }") -Encoding utf8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $core
    $psi.Arguments = '--serve'
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    # Stderr is deliberately NOT redirected: redirecting the Core's stderr while reading it lazily
    # stalled the sidecar's Paddle model load in this environment (observed 2026-10-21: sidecar stuck
    # at ~416 MB, zero OCR cycles). Inheriting it works; filter the [sidecar] flood from the console.
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Output ("[{0}] core pid {1}, annotate {2} fps, settle {3}s + measure {4}s (drop first {5}s)" -f $Label, $proc.Id, $AnnotFps, $SettleSeconds, $Seconds, $DiscardSeconds)

    $stdin = $proc.StandardInput
    $stdin.AutoFlush = $true

    $stdin.WriteLine('{"jsonrpc":"2.0","id":1,"method":"core.ping","params":{}}')
    $startParams = '{"source":{"kind":"screen","monitor":0},"translate":{"provider":"google","src":"auto","dst":"vi"}}'
    $stdin.WriteLine('{"jsonrpc":"2.0","id":2,"method":"core.start","params":' + $startParams + '}')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $measureStart = $SettleSeconds
    $discardEnd = $SettleSeconds + $DiscardSeconds
    $deadline = $SettleSeconds + $Seconds

    $samples = New-Object System.Collections.Generic.List[object]
    $nextCpuSample = 0.0
    $annCount = 0L; $annBytes = 0L; $annSeqMax = 0L; $annW = 0; $annH = 0
    $translated = 0L; $cacheRequests = 0L
    $boxCounts = New-Object System.Collections.Generic.List[int]
    $rpcBytes = 0L
    $states = New-Object System.Collections.Generic.List[string]
    $firstAnnAt = -1.0; $lastAnnAt = -1.0
    $mmf = $null; $mmfAcc = $null

    while ($true) {
        $elapsed = $sw.Elapsed.TotalSeconds
        if ($elapsed -ge $deadline) { break }

        if ($elapsed -ge $nextCpuSample) {
            $proc.Refresh()
            $samples.Add([pscustomobject]@{
                t   = $elapsed
                cpu = $proc.TotalProcessorTime.TotalSeconds
                ws  = [double]$proc.WorkingSet64
            })
            $nextCpuSample = $elapsed + 1.0
        }

        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        $now = $sw.Elapsed.TotalSeconds
        $rpcBytes += [System.Text.Encoding]::UTF8.GetByteCount($line) + 1

        # Requests from Core -> reply all-miss like the App (2 s deadline on the Core side).
        if ($line -match '"method"\s*:\s*"translate\.request_cache"') {
            $cacheRequests++
            $req = $line | ConvertFrom-Json
            $misses = @()
            if ($req.params.texts) { $misses = @($req.params.texts) }
            $reply = @{ jsonrpc = '2.0'; id = $req.id; result = @{ hits = @{}; misses = $misses } } | ConvertTo-Json -Compress -Depth 8
            $stdin.WriteLine($reply)
            continue
        }

        if ($now -lt $measureStart) { continue }   # settle: ignore metrics

        if ($line -match '"method"\s*:\s*"frame\.annotated"') {
            if ($now -ge $discardEnd) {
                $annCount++; $annBytes += [System.Text.Encoding]::UTF8.GetByteCount($line) + 1
                if ($firstAnnAt -lt 0) { $firstAnnAt = $now }
                $lastAnnAt = $now
                $o = $line | ConvertFrom-Json
                if ($o.params.seq -gt $annSeqMax) { $annSeqMax = $o.params.seq }
                if ($annW -eq 0) { $annW = $o.params.w; $annH = $o.params.h }
            }
            continue
        }
        if ($line -match '"method"\s*:\s*"frame\.translated"') {
            $translated++
            if ($now -ge $discardEnd) {
                $o = $line | ConvertFrom-Json
                $boxCounts.Add(@($o.params.boxes).Count)
            }
            continue
        }
        if ($line -match '"method"\s*:\s*"pipeline\.state"') {
            if ($now -ge $discardEnd) { $states.Add($line) }
            continue
        }
    }

    # Preview MMF evidence (before the Core stops): header + JPEG magic of the newest frame.
    $mmfOk = $false; $previewSeq = 0; $previewLen = 0; $previewW = 0; $previewH = 0; $jpegMagic = $false
    try {
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting('Local\ReTran.Preview.V1')
        $mmfAcc = $mmf.CreateViewAccessor()
        $hdr = New-Object byte[] 32
        [void]$mmfAcc.ReadArray(0, $hdr, 0, 32)
        $magic = [BitConverter]::ToUInt32($hdr, 0)
        if ($magic -eq 0x45525052) {
            $mmfOk = $true
            $previewW = [BitConverter]::ToUInt32($hdr, 8)
            $previewH = [BitConverter]::ToUInt32($hdr, 12)
            $previewLen = [BitConverter]::ToUInt32($hdr, 16)
            $previewSeq = [BitConverter]::ToUInt32($hdr, 20)
            $b4 = New-Object byte[] 4
            if ($previewLen -ge 4) { [void]$mmfAcc.ReadArray(32, $b4, 0, 4) }
            $jpegMagic = ($b4[0] -eq 0xFF -and $b4[1] -eq 0xD8)
        }
    } catch { $mmfOk = $false }
    finally {
        if ($mmfAcc) { $mmfAcc.Dispose() }
        if ($mmf) { $mmf.Dispose() }
    }

    # Sample the OCR sidecar processes BEFORE anything is stopped (they are 2: trampoline + interpreter).
    $proc.Refresh()
    $peakWs = [double]$proc.PeakWorkingSet64 / 1MB
    $sidecarCount = 0; $sidecarRamMB = 0
    try {
        $py = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine -match $sidecarRe }
        $sidecarCount = @($py).Count
        foreach ($p in @($py)) { $sidecarRamMB += [math]::Round(((Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue).WorkingSet64 / 1MB), 0) }
    } catch { }

    # Stop cleanly, then make sure nothing is left behind.
    try { $stdin.WriteLine('{"jsonrpc":"2.0","id":3,"method":"core.stop","params":{}}') } catch { }
    Start-Sleep -Milliseconds 1500
    if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
    try { $proc.WaitForExit(5000) | Out-Null } catch { }
    $stdin.Close()
    Stop-SidecarLeftovers

    # ---- metrics ----
    $win = $samples | Where-Object { $_.t -ge $discardEnd }
    if ($win.Count -ge 2) {
        $t0 = $win[0].t; $t1 = $win[-1].t
        $cpuAvg = ($win[-1].cpu - $win[0].cpu) / ($t1 - $t0) * 100.0
        $cpuMax = 0.0
        for ($i = 1; $i -lt $win.Count; $i++) {
            $dt = $win[$i].t - $win[$i - 1].t
            if ($dt -le 0) { continue }
            $r = ($win[$i].cpu - $win[$i - 1].cpu) / $dt * 100.0
            if ($r -gt $cpuMax) { $cpuMax = $r }
        }
        $ramAvg = ($win | Measure-Object ws -Average).Average / 1MB
        $ramMax = ($win | Measure-Object ws -Maximum).Maximum / 1MB
    } else { $cpuAvg = 0; $cpuMax = 0; $ramAvg = 0; $ramMax = 0; $t0 = $discardEnd; $t1 = $deadline }

    $lineWindow = $t1 - $t0
    $annFps = if ($lineWindow -gt 0) { $annCount / $lineWindow } else { 0 }
    $annKb = if ($annCount -gt 0) { ($annBytes / $annCount) / 1KB } else { 0 }
    $rpcMbps = if ($lineWindow -gt 0) { ($rpcBytes / 1MB) / $lineWindow } else { 0 }
    $boxesAvg = if ($boxCounts.Count -gt 0) { ($boxCounts | Measure-Object -Average).Average } else { 0 }
    $boxesMax = if ($boxCounts.Count -gt 0) { ($boxCounts | Measure-Object -Maximum).Maximum } else { 0 }
    $cachePerSec = if ($lineWindow -gt 0) { $cacheRequests / $lineWindow } else { 0 }

    $summary = [ordered]@{
        label               = $Label
        annot_fps           = [math]::Round($AnnotFps, 1)
        window_s            = [math]::Round($lineWindow, 1)
        core_cpu1core_avg   = [math]::Round($cpuAvg, 1)
        core_cpu1core_max   = [math]::Round($cpuMax, 1)
        core_ramMB_avg      = [math]::Round($ramAvg, 0)
        core_ramMB_max      = [math]::Round($ramMax, 0)
        core_peak_wsMB      = [math]::Round($peakWs, 1)
        annotated           = $annCount
        ann_fps             = [math]::Round($annFps, 2)
        ann_line_KB_avg     = [math]::Round($annKb, 1)
        ann_w               = $annW
        ann_h               = $annH
        rpc_MBps            = [math]::Round($rpcMbps, 3)
        translated          = $translated
        boxes_avg           = [math]::Round($boxesAvg, 1)
        boxes_max           = $boxesMax
        cache_requests      = $cacheRequests
        cache_per_s         = [math]::Round($cachePerSec, 2)
        preview_mmf_ok      = $mmfOk
        preview_seq         = $previewSeq
        preview_w           = $previewW
        preview_h           = $previewH
        preview_jpeg_bytes  = $previewLen
        preview_jpeg_magic  = $jpegMagic
        states              = ($states -join ' | ')
        sidecar_processes   = $sidecarCount
        sidecar_ramMB       = $sidecarRamMB
        core_exited         = $proc.HasExited
        core_exit_code      = $(if ($proc.HasExited) { $proc.ExitCode } else { $null })
    }

    Write-Output '--- summary ---'
    $summary.GetEnumerator() | ForEach-Object { "{0} = {1}" -f $_.Key, $_.Value }
    $csv = $summary.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }
    Set-Content -Path $csvPath -Value $csv -Encoding utf8
    Write-Output ("csv: {0}" -f $csvPath)
}
finally {
    if ($cfgExisted) { Set-Content -Path $cfgPath -Value $cfgBackup -Encoding utf8 }
    elseif (Test-Path $cfgPath) { Remove-Item $cfgPath -Force }
}
