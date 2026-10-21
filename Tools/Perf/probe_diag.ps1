# probe_diag.ps1 — diagnose why the serve-mode OCR loop produced zero cycles on 2026-10-21.
# Inherits the Core's stderr (so [sidecar] Paddle logs are visible), logs the ping/start replies,
# and samples the sidecar python processes + Core CPU every few seconds.
# Promoted into the repo 2026-10-21 (Tools/Perf/) — repo root is derived from $PSScriptRoot.
param(
    [int]$Minutes = 3,
    [int]$SampleEvery = 10,
    [int]$AnnotFps = 10
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)   # Tools/Perf -> Tools -> repo root
$core = Join-Path $repo 'ReTran.Core\bin\Debug\net10.0-windows\ReTran.Core.exe'
$cfgPath = Join-Path $env:LOCALAPPDATA 'ReTran\core\config.json'
# Sidecar processes live under $repo\ReTran.OCR (venv) or carry '-m retran_core' on the command line.
$sidecarRe = [regex]::Escape($repo) + '\\ReTran\.OCR|retran_core'
$cfgBackup = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw } else { $null }
$cfgExisted = Test-Path $cfgPath

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
    $psi.RedirectStandardError = $false   # inherit -> visible in this console
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdin = $proc.StandardInput
    $stdin.AutoFlush = $true
    Write-Output ("[diag] core pid {0}" -f $proc.Id)

    $stdin.WriteLine('{"jsonrpc":"2.0","id":1,"method":"core.ping","params":{}}')
    $stdin.WriteLine('{"jsonrpc":"2.0","id":2,"method":"core.start","params":{"source":{"kind":"screen","monitor":0},"translate":{"provider":"google","src":"auto","dst":"vi"}}}')

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $nextSample = 0.0
    $ann = 0; $translated = 0; $replies = @()
    while ($sw.Elapsed.TotalMinutes -lt $Minutes) {
        if ($sw.Elapsed.TotalSeconds -ge $nextSample) {
            $t = [math]::Round($sw.Elapsed.TotalSeconds, 0)
            $proc.Refresh()
            $coreCpu = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1)
            $coreRam = [math]::Round($proc.WorkingSet64 / 1MB, 0)
            $py = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
                Where-Object { $_.CommandLine -and $_.CommandLine -match $sidecarRe }
            $pyRam = 0; $pyCpu = 0.0
            foreach ($p in @($py)) {
                $gp = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
                if ($gp) { $pyRam += [math]::Round($gp.WorkingSet64 / 1MB, 0); $pyCpu += $gp.TotalProcessorTime.TotalSeconds }
            }
            Write-Output ("[sample t={0}s] core cpu={1}s ram={2}MB | sidecar procs={3} ram={4}MB cpu={5}s | ann={6} translated={7}" -f $t, $coreCpu, $coreRam, @($py).Count, $pyRam, [math]::Round($pyCpu, 1), $ann, $translated)
            $nextSample = $t + $SampleEvery
        }
        if ($proc.StandardOutput.Peek() -lt 0) { Start-Sleep -Milliseconds 200; continue }
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        if ($line -match '"method"\s*:\s*"translate\.request_cache"') {
            $req = $line | ConvertFrom-Json
            $misses = @($req.params.texts)
            $stdin.WriteLine((@{ jsonrpc = '2.0'; id = $req.id; result = @{ hits = @{}; misses = $misses } } | ConvertTo-Json -Compress -Depth 8))
            continue
        }
        if ($line -match '"method"\s*:\s*"frame\.annotated"') { $ann++; continue }
        if ($line -match '"method"\s*:\s*"frame\.translated"') { $translated++; continue }
        if ($line -match '"id"\s*:\s*2') { $replies += ("start reply: " + $line); Write-Output ("[reply] " + $line) }
        elseif ($line -match '"id"\s*:\s*1') { Write-Output ("[reply] " + $line) }
        elseif ($line -match '"method"') { Write-Output ("[notif] " + ($line.Substring(0, [Math]::Min(160, $line.Length)))) }
    }

    Write-Output ("[diag] done: ann={0} translated={1}; killing" -f $ann, $translated)
    if (-not $proc.HasExited) { $proc.Kill() }
    $proc.WaitForExit(5000) | Out-Null
    try {
        Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine -match $sidecarRe } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
}
finally {
    if ($cfgExisted) { Set-Content -Path $cfgPath -Value $cfgBackup -Encoding utf8 }
    elseif (Test-Path $cfgPath) { Remove-Item $cfgPath -Force }
}
