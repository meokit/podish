param(
    [string]$Image = "localhost/podish-node:latest",
    [string]$RootfsTar = "",
    [string]$Rounds = "6",
    [string]$Objects = "3000",
    [string]$BufferSize = "32768",
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 60,
    [ValidateRange(1, 10000)][int]$Repeats = 1,
    [string]$FsBackend = "tmpfs",
    [string]$LogFile = "",
    [string]$Cli = "",
    [switch]$ExpectCrash
)

# Regression mode requires the completion marker AND exit 0. Historical crash
# reproduction is opt-in and must not accidentally turn a crash into a green regression.

$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ($Cli -eq "") { $Cli = Join-Path $repo "Podish.Cli\bin\Debug\net11.0\Podish.Cli.dll" }
$Cli = (Resolve-Path $Cli).Path
if ($RootfsTar -ne "") { $RootfsTar = (Resolve-Path $RootfsTar).Path }
$work = Join-Path $repo ("artifacts\verification\node-" + [guid]::NewGuid().ToString("N"))
$env:DOTNET_ROLL_FORWARD = "LatestMajor"

New-Item -ItemType Directory -Force -Path $work | Out-Null

if ($RootfsTar -ne "") {
    Write-Output "importing $RootfsTar as $Image"
    Push-Location $repo
    try { & dotnet $Cli import $RootfsTar $Image }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { Write-Error "image import failed"; exit 2 }
}

function Invoke-Repro {
    param([int]$Index)

    $stdout = Join-Path $work ("run-$Index.out")
    $stderr = Join-Path $work ("run-$Index.err")
    $arguments = @($Cli)
    if ($LogFile -ne "") { $arguments += @("--log-level", "debug", "--log-file", $LogFile) }
    $arguments += @("run", "--rm", "--init", "--log-driver", "none", "--network", "private",
        "--fs-backend", $FsBackend, $Image, "--",
        "/usr/bin/node", "/app/repro.js", $Rounds, $Objects, $BufferSize)

    $started = Get-Date
    # ProcessStartInfo.Arguments is a command line on Windows PowerShell 5, so quote each argument.
    $quoted = $arguments | ForEach-Object { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    # Podish stores images relative to the working directory; use the SAME worktree for import/run.
    # Start-Process -PassThru can lose ExitCode after WaitForExit on Windows PowerShell 5.
    # Own the process handle and drain both pipes asynchronously to preserve the REAL exit code.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = "dotnet"
    $info.Arguments = $quoted -join ' '
    $info.WorkingDirectory = $repo
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $info
    [void]$proc.Start()
    $outputRead = $proc.StandardOutput.ReadToEndAsync()
    $errorRead = $proc.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill /PID $proc.Id /T /F | Out-Null
        $proc.WaitForExit()
        $timedOut = $true
    }
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    $text = $outputRead.GetAwaiter().GetResult()
    $errorText = $errorRead.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($stdout, $text)
    [System.IO.File]::WriteAllText($stderr, $errorText)
    $proc.Dispose()
    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $completed = -not $timedOut -and $exitCode -eq 0 -and $text -match "\[repro\] done"

    $mode = if ($completed) { "COMPLETED" }
            elseif ($timedOut) { "HANG (timeout ${TimeoutSeconds}s)" }
            else { "FAILED (exit $exitCode)" }
    Write-Host ("attempt {0}: {1}, {2}s" -f $Index, $mode, $elapsed)
    $accepted = if ($ExpectCrash) { $timedOut -or $exitCode -eq 139 } else { $completed }
    return [pscustomobject]@{ Attempt = $Index; Accepted = $accepted; Completed = $completed;
        ExitCode = $exitCode; TimedOut = $timedOut; Seconds = $elapsed; Stdout = $stdout; Stderr = $stderr }
}

$results = @(for ($i = 1; $i -le $Repeats; $i++) { Invoke-Repro -Index $i })
$report = [pscustomobject]@{ Cli = $Cli; Image = $Image; FsBackend = $FsBackend;
    Arguments = @($Rounds, $Objects, $BufferSize); ExpectCrash = [bool]$ExpectCrash; Runs = $results }
$report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work "report.json") -Encoding UTF8
$failed = @($results | Where-Object { -not $_.Accepted }).Count
Write-Output "$($Repeats - $failed) / $Repeats accepted; report: $work\report.json"
if ($failed -ne 0) { exit 1 }
exit 0
