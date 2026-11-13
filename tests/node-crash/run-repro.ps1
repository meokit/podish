param(
    [string]$Image = "localhost/podish-node:latest",
    [string]$RootfsTar = "",
    [string]$Rounds = "6",
    [string]$Objects = "3000",
    [string]$BufferSize = "32768",
    [int]$TimeoutSeconds = 60,
    [int]$Repeats = 1,
    [string]$FsBackend = "tmpfs",
    [string]$LogFile = ""
)

# Runs the Node.js crash reproducer in Podish and reports the failure mode.
# The reproducer is expected to fail: guest SIGSEGV (container exit 139) or a
# hang. A run that prints "[repro] done" is the unexpected pass.

$ErrorActionPreference = "Continue"
$repo = "C:\Users\Jiang\repos\podish"
$cli = Join-Path $repo "Podish.Cli\bin\Debug\net10.0\Podish.Cli.dll"
$work = Join-Path $env:TEMP "podish-node-crash"
$env:DOTNET_ROLL_FORWARD = "LatestMajor"

New-Item -ItemType Directory -Force -Path $work | Out-Null
Set-Location $work

if ($RootfsTar -ne "") {
    Write-Output "importing $RootfsTar as $Image"
    & dotnet $cli import $RootfsTar $Image
    if ($LASTEXITCODE -ne 0) { Write-Error "image import failed"; exit 2 }
}

function Invoke-Repro {
    param([int]$Index)

    $stdout = Join-Path $work ("run-$Index.out")
    $stderr = Join-Path $work ("run-$Index.err")
    $arguments = @($cli)
    if ($LogFile -ne "") { $arguments += @("--log-level", "debug", "--log-file", $LogFile) }
    $arguments += @("run", "--rm", "--init", "--log-driver", "none", "--network", "private",
        "--fs-backend", $FsBackend, $Image, "--",
        "/usr/bin/node", "/app/repro.js", $Rounds, $Objects, $BufferSize)

    $started = Get-Date
    $proc = Start-Process -FilePath "dotnet" -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $timedOut = $false
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill /PID $proc.Id /T /F | Out-Null
        $proc.WaitForExit()
        $timedOut = $true
    }
    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $text = (Get-Content $stdout -Raw -ErrorAction SilentlyContinue)
    $completed = $text -match "\[repro\] done"

    $mode = if ($completed) { "COMPLETED (unexpected pass)" }
            elseif ($timedOut) { "HANG (timeout ${TimeoutSeconds}s)" }
            else { "CRASH (exit $($proc.ExitCode))" }
    Write-Host ("attempt {0}: {1}, {2}s" -f $Index, $mode, $elapsed)
    Remove-Item -Force $stdout, $stderr -ErrorAction SilentlyContinue
    return (-not $completed)
}

$failed = 0
for ($i = 1; $i -le $Repeats; $i++) {
    # Invoke-Repro returns $true when the run failed (crash or hang).
    if (Invoke-Repro -Index $i) { $failed++ }
}
Write-Output "reproduced $failed / $Repeats runs"
if ($failed -eq 0) { exit 1 }
exit 0
