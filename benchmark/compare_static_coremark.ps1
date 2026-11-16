param(
    [Parameter(Mandatory = $true)][string]$BaselineCli,
    [Parameter(Mandatory = $true)][string]$ChangedCli,
    [Parameter(Mandatory = $true)][string]$Rootfs,
    [int]$Iterations = 24000,
    [int]$Repeats = 3,
    [string]$ResultsPath = 'artifacts/coremark-static/comparison.json'
)

$ErrorActionPreference = 'Stop'
$baseline = (Resolve-Path $BaselineCli).Path
$changed = (Resolve-Path $ChangedCli).Path
$root = (Resolve-Path $Rootfs).Path
if (-not (Test-Path (Join-Path $root 'coremark_i686_linux'))) {
    throw "Static CoreMark binary not found in $root"
}

$samples = @()
for ($round = 1; $round -le $Repeats; $round++) {
    $variants = if ($round % 2) { @('baseline', 'changed') } else { @('changed', 'baseline') }
    foreach ($variant in $variants) {
        $cli = if ($variant -eq 'baseline') { $baseline } else { $changed }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $output = & dotnet --roll-forward Major $cli run --rm --log-driver none --rootfs $root `
            /coremark_i686_linux 0x0 0x0 0x66 $Iterations 2>&1 | Out-String
        $watch.Stop()
        if ($LASTEXITCODE -ne 0 -or $output -notmatch 'Correct operation validated') {
            throw "$variant round $round failed: $output"
        }
        $match = [regex]::Match($output, 'CoreMark 1\.0\s*:\s*([0-9.]+)')
        if (-not $match.Success) { throw "$variant round $round has no CoreMark score: $output" }
        $score = [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        $samples += [pscustomobject]@{ variant = $variant; round = $round; score = $score; wall_seconds = $watch.Elapsed.TotalSeconds }
        Write-Host "$variant round $round : $score iterations/s ($([Math]::Round($watch.Elapsed.TotalSeconds, 2))s wall)"
    }
}

$resultsFile = [IO.Path]::GetFullPath($ResultsPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resultsFile)) | Out-Null
$samples | ConvertTo-Json -Depth 4 | Set-Content -Path $resultsFile -Encoding utf8
foreach ($variant in @('baseline', 'changed')) {
    $ordered = @($samples | Where-Object variant -EQ $variant | Sort-Object score | ForEach-Object score)
    $median = $ordered[[int][Math]::Floor(($ordered.Count - 1) / 2)]
    Write-Host "$variant median: $median iterations/s"
}
Write-Host "Samples: $resultsFile"
