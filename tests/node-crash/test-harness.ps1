param(
    [string]$WorkDirectory = ""
)

# Host-only regression tests for run-repro.ps1. The fake CLI ignores Podish
# arguments and supplies controlled marker/exit/timeout outcomes, not guest passes.
$ErrorActionPreference = "Stop"
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
if ($WorkDirectory -eq "") {
    $WorkDirectory = Join-Path $env:LOCALAPPDATA ("Temp\opencode\node harness test-" + [guid]::NewGuid().ToString("N"))
}
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$project = Join-Path $WorkDirectory "Probe.csproj"
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content $project -Encoding UTF8
@'
var mode = args[^3];
if (mode == "timeout") Thread.Sleep(10000);
if (mode != "no-marker") Console.WriteLine("[repro] done");
return mode == "marker-fail" ? 139 : 0;
'@ | Set-Content (Join-Path $WorkDirectory "Program.cs") -Encoding UTF8

& dotnet build $project --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Fake CLI build failed: $LASTEXITCODE" }
$cli = Join-Path $WorkDirectory "bin\Debug\net10.0\Probe.dll"
$harness = Join-Path $PSScriptRoot "run-repro.ps1"
$cases = @(
    @{ Mode = "success"; Expected = 0; Crash = $false },
    @{ Mode = "marker-fail"; Expected = 1; Crash = $false },
    @{ Mode = "no-marker"; Expected = 1; Crash = $false },
    @{ Mode = "timeout"; Expected = 1; Crash = $false },
    @{ Mode = "marker-fail"; Expected = 0; Crash = $true },
    @{ Mode = "no-marker"; Expected = 1; Crash = $true }
)
foreach ($case in $cases) {
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $harness,
        "-Cli", $cli, "-Rounds", $case.Mode, "-Repeats", "1", "-TimeoutSeconds", "2")
    if ($case.Crash) { $arguments += "-ExpectCrash" }
    & powershell @arguments
    if ($LASTEXITCODE -ne $case.Expected) {
        throw "Mode $($case.Mode), crash=$($case.Crash): exit $LASTEXITCODE, expected $($case.Expected)"
    }
}
Write-Output "Harness gates: 6 / 6 passed (host-only fake CLI, not Node workload results)."
exit 0
