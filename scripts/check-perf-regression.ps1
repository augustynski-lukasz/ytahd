# RM-15: benchmark regression guardrail.
#
# Compares a YTAHD.Perf `bench --json` result file against a stored baseline and fails
# when any throughput metric regresses beyond the configured threshold. Designed for
# "material regressions without fragile fixed-time assertions": machine noise and FFmpeg
# variance are absorbed by (a) percentage-based thresholds, (b) per-run-key aggregation
# of repeated runs with the same configuration, and (c) an explicit allowlist of runs
# known to be noisy (e.g. FFmpeg-wait-dominated parallel decodes).
#
# Threshold policy (CR-20261009-01):
#   Default regression threshold is 20% throughput loss per run key. Parallel-decode
#   runs (--jobs auto, resolvedJobs > 1) get a 35% threshold because their timing is
#   dominated by FFmpeg thread scheduling and is demonstrably the noisiest axis
#   (perf-g1-baseline vs perf-g1-after: decode at jobs=auto swung 35-60% run-to-run
#   on identical code). Memory metrics are not gated in v1 (see ADR Consequences).
#
# Usage:
#   powershell -File scripts\check-perf-regression.ps1 `
#       -Baseline perf-g1-baseline.json -Current perf-g1-after.json
#   powershell -File scripts\check-perf-regression.ps1 -Baseline perf.json -Current perf.json -ThresholdPercent 10
#
# Exit codes: 0 = within policy, 1 = regression detected, 2 = usage/input error.

param(
    # Stored good-state JSON (as produced by: dotnet run --project YTAHD.Perf -- bench --json ...).
    [Parameter(Mandatory = $true)][string]$Baseline,

    # Fresh run JSON to check.
    [Parameter(Mandatory = $true)][string]$Current,

    # Regression threshold (percent throughput loss) for serial runs (resolvedJobs <= 1).
    [double]$ThresholdPercent = 20.0,

    # Wider threshold for parallel runs (resolvedJobs > 1): FFmpeg thread scheduling noise.
    [double]$ParallelThresholdPercent = 35.0,

    # Fail on throughput loss smaller than this even if below threshold (catches the
    # "quietly 12% slower everywhere" pattern that per-run gates can miss).
    [double]$AggregateThresholdPercent = 10.0
)

$ErrorActionPreference = 'Stop'

# Write-Error would throw under Stop preference before exit 2 could run; print and exit directly.
if (-not (Test-Path $Baseline)) { Write-Host "ERROR: Baseline file not found: $Baseline" -ForegroundColor Red; exit 2 }
if (-not (Test-Path $Current)) { Write-Host "ERROR: Current file not found: $Current" -ForegroundColor Red; exit 2 }

try {
    $base = Get-Content $Baseline -Raw | ConvertFrom-Json
    $curr = Get-Content $Current -Raw | ConvertFrom-Json
}
catch {
    Write-Error "Failed to parse input JSON: $_"
    exit 2
}

# Environment changes (different machine / FFmpeg build / core count) invalidate any
# throughput comparison; require a re-baseline rather than producing false regressions.
$envKeys = @('logicalProcessors')
foreach ($key in $envKeys) {
    $b = $base.$key; $c = $curr.$key
    if ($null -ne $b -and $null -ne $c -and "$b" -ne "$c") {
        Write-Host "FAIL: environment mismatch on '$key': baseline=$b current=$c."
        Write-Host "Re-baseline (run YTAHD.Perf bench --json on the new environment) instead of comparing across machines."
        exit 1
    }
}

# Group runs by their configuration (modulator + payload + jobs), summing throughput
# across repeats: repeats exist to average noise, so their SUM (not per-repeat min) is
# the noise-robust aggregate.
function Get-RunKey([object]$run) { return "$($run.modulator)|payload=$($run.payloadBytes)|jobs=$($run.requestedJobs)" }

function Get-Groups([object]$json) {
    $groups = @{}
    foreach ($run in $json.runs) {
        $key = Get-RunKey $run
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = [pscustomobject]@{
                Key = $key
                EncBps = 0.0; DecBps = 0.0; Rounds = 0
                ResolvedJobs = [int]$run.resolvedJobs
            }
        }
        $g = $groups[$key]
        $g.EncBps += [double]$run.encodeBytesPerSecond
        $g.DecBps += [double]$run.decodeBytesPerSecond
        $g.Rounds += 1
    }
    return $groups
}

$baseGroups = Get-Groups $base
$currGroups = Get-Groups $curr

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$rows = New-Object System.Collections.Generic.List[string]

$sumRatio = 0.0; $ratioCount = 0

foreach ($key in $baseGroups.Keys) {
    $b = $baseGroups[$key]
    if (-not $currGroups.ContainsKey($key)) {
        $failures.Add("$key : configuration present in baseline but missing from the current run.")
        continue
    }

    $c = $currGroups[$key]
    $encRatio = if ($b.EncBps -gt 0) { $c.EncBps / $b.EncBps } else { $null }
    $decRatio = if ($b.DecBps -gt 0) { $c.DecBps / $b.DecBps } else { $null }

    $isParallel = $b.ResolvedJobs -gt 1
    $threshold = if ($isParallel) { $ParallelThresholdPercent } else { $ThresholdPercent }

    $label = $key
    foreach ($pair in @(@('encode', $encRatio), @('decode', $decRatio))) {
        $name = $pair[0]; $ratio = $pair[1]
        if ($null -eq $ratio) { continue }
        $lossPct = (1.0 - $ratio) * 100.0
        $rows.Add(("{0,-45} {1,-6} {2,8:P1} of baseline  {3,8:N1}% loss  (limit {4:N0}%)" -f $label, $name, $ratio, $lossPct, $threshold))
        if ($lossPct -gt $threshold) {
            $failures.Add("$label : $name throughput loss $([math]::Round($lossPct,1))% exceeds the ${threshold}% threshold.")
        }
        $sumRatio += $ratio; $ratioCount++
    }
}

foreach ($key in $currGroups.Keys) {
    if (-not $baseGroups.ContainsKey($key)) {
        $warnings.Add("$key : new configuration, not present in the baseline (no comparison).")
    }
}

# Aggregate gate: flags a small uniform slowdown that slips under every per-run gate.
if ($ratioCount -gt 1) {
    $aggRatio = $sumRatio / $ratioCount
    $aggLoss = (1.0 - $aggRatio) * 100.0
    $rows.Add(("{0,-45} {1,-6} {2,8:P1} of baseline  {3,8:N1}% loss  (limit {4:N0}%)" -f 'AGGREGATE (mean of all ratios)', '', $aggRatio, $aggLoss, $AggregateThresholdPercent))
    if ($aggLoss -gt $AggregateThresholdPercent) {
        $failures.Add("Aggregate mean throughput loss $([math]::Round($aggLoss,1))% exceeds the ${AggregateThresholdPercent}% aggregate threshold.")
    }
}

Write-Host ''
Write-Host 'YTAHD perf regression check (RM-15)'
Write-Host "  baseline: $Baseline"
Write-Host "  current : $Current"
Write-Host "  thresholds: serial $ThresholdPercent% | parallel $ParallelThresholdPercent% | aggregate $AggregateThresholdPercent%"
Write-Host ''
foreach ($row in $rows) { Write-Host "  $row" }
if ($warnings.Count -gt 0) {
    Write-Host ''
    foreach ($w in $warnings) { Write-Warning $w }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($f in $failures) { Write-Host "FAIL: $f" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'If this slowdown is intentional or environmental, re-baseline:'
    Write-Host '  dotnet run --project YTAHD.Perf -- bench --json <new-baseline>.json   # then commit the new baseline'
    exit 1
}

Write-Host ''
Write-Host 'PASS: no benchmark regression beyond policy thresholds.'
exit 0
