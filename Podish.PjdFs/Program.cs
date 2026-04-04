using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PjdFs;

internal static partial class Program
{
    private const string TestImageRef = "localhost/pjdfstest:latest";
    private const string TestImageArch = "386";
    private const string UpstreamRepoUrl = "https://github.com/pjd/pjdfstest";

    private static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Podish.PjdFs - standalone pjdfstest runner for Podish");

        var runCommand = new Command("run", "Prepare assets and run pjdfstest cases");
        var runFilterOption = new Option<string?>("--filter", "Run only cases whose relative path contains this value");
        var runJobsOption = new Option<string>("--jobs", () => "auto", "Concurrency level or 'auto'");
        var runKeepWorkdirOption = new Option<bool>("--keep-workdir", "Keep per-case work directories");
        var runRebuildOption = new Option<bool>("--rebuild-assets", "Rebuild cached guest assets");
        var runOutputJsonOption = new Option<string?>("--output-json", "Write a JSON summary to this path");
        var runMaxCasesOption = new Option<int?>("--max-cases", "Limit the number of discovered cases");
        runCommand.AddOption(runFilterOption);
        runCommand.AddOption(runJobsOption);
        runCommand.AddOption(runKeepWorkdirOption);
        runCommand.AddOption(runRebuildOption);
        runCommand.AddOption(runOutputJsonOption);
        runCommand.AddOption(runMaxCasesOption);
        runCommand.SetHandler(async context =>
        {
            try
            {
                var repo = RepoLayout.Discover();
                var options = new RunOptions(
                    Filter: context.ParseResult.GetValueForOption(runFilterOption),
                    JobsRaw: context.ParseResult.GetValueForOption(runJobsOption) ?? "auto",
                    KeepWorkdir: context.ParseResult.GetValueForOption(runKeepWorkdirOption),
                    RebuildAssets: context.ParseResult.GetValueForOption(runRebuildOption),
                    OutputJson: context.ParseResult.GetValueForOption(runOutputJsonOption),
                    MaxCases: context.ParseResult.GetValueForOption(runMaxCasesOption));
                context.ExitCode = await RunAsync(repo, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pjd] run failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        var listCommand = new Command("list", "List discovered pjdfstest cases");
        var listFilterOption = new Option<string?>("--filter", "Only list cases whose relative path contains this value");
        listCommand.AddOption(listFilterOption);
        listCommand.SetHandler(async context =>
        {
            try
            {
                var repo = RepoLayout.Discover();
                var snapshot = await UpstreamSnapshot.LoadAsync(repo);
                var cases = snapshot.DiscoverCases(context.ParseResult.GetValueForOption(listFilterOption), null);
                foreach (var testCase in cases)
                    Console.WriteLine(testCase.RelativePath);
                Console.WriteLine($"[pjd] cases={cases.Count}");
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pjd] list failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        var doctorCommand = new Command("doctor", "Check runner prerequisites and layout");
        doctorCommand.SetHandler(context =>
        {
            try
            {
                var repo = RepoLayout.Discover();
                var report = DoctorReport.Create(repo);
                report.WriteToConsole();
                context.ExitCode = report.HasErrors ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pjd] doctor failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        var cleanCommand = new Command("clean", "Remove cached pjdfstest build and run artifacts");
        cleanCommand.SetHandler(context =>
        {
            try
            {
                var repo = RepoLayout.Discover();
                if (Directory.Exists(repo.BuildRoot))
                {
                    Directory.Delete(repo.BuildRoot, recursive: true);
                    Console.WriteLine($"[pjd] removed {repo.BuildRoot}");
                }
                else
                {
                    Console.WriteLine($"[pjd] nothing to clean at {repo.BuildRoot}");
                }

                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pjd] clean failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        root.AddCommand(runCommand);
        root.AddCommand(listCommand);
        root.AddCommand(doctorCommand);
        root.AddCommand(cleanCommand);

        return await root.InvokeAsync(args);
    }

    private static async Task<int> RunAsync(RepoLayout repo, RunOptions options)
    {
        var snapshot = await UpstreamSnapshot.LoadAsync(repo);
        var cases = snapshot.DiscoverCases(options.Filter, options.MaxCases);
        if (cases.Count == 0)
        {
            Console.WriteLine("[pjd] no cases matched");
            return 0;
        }

        var jobs = ParseJobs(options.JobsRaw);
        Console.WriteLine($"[pjd] repo={repo.Root}");
        Console.WriteLine($"[pjd] cases={cases.Count}");
        Console.WriteLine($"[pjd] jobs={jobs}");

        var assetBuilder = new AssetBuilder(repo, snapshot);
        var assets = await assetBuilder.PrepareAsync(options.RebuildAssets);
        var preparedImage = await EnsureTestImageAsync(repo, assets, options.RebuildAssets);

        var runDir = Path.Combine(repo.RunsRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(runDir);

        var scheduler = new CaseScheduler(repo, assets, preparedImage, runDir, options.KeepWorkdir);
        var results = await scheduler.RunAsync(cases, jobs);

        var summary = Summary.FromResults(results, runDir, assets.BinaryPath);
        summary.WriteToConsole();

        var defaultJsonPath = Path.Combine(runDir, "summary.json");
        await File.WriteAllTextAsync(defaultJsonPath, JsonSerializer.Serialize(summary, JsonOptions()));

        var defaultMarkdownPath = Path.Combine(runDir, "report.md");
        await File.WriteAllTextAsync(defaultMarkdownPath, summary.ToMarkdown(repo, options, preparedImage));

        Console.WriteLine($"[pjd] summary-json={defaultJsonPath}");
        Console.WriteLine($"[pjd] report-md={defaultMarkdownPath}");

        if (!string.IsNullOrWhiteSpace(options.OutputJson))
        {
            var outputPath = Path.IsPathRooted(options.OutputJson)
                ? options.OutputJson
                : Path.GetFullPath(Path.Combine(repo.Root, options.OutputJson));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, JsonOptions()));
            Console.WriteLine($"[pjd] wrote {outputPath}");
        }

        return summary.FailedCases == 0 ? 0 : 1;
    }

    private static int ParseJobs(string raw)
    {
        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
            return Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        if (int.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;
        throw new InvalidOperationException($"invalid --jobs value: {raw}");
    }

    private static async Task<string> EnsureTestImageAsync(RepoLayout repo, PreparedAssets assets, bool rebuild)
    {
        Directory.CreateDirectory(repo.BuildRoot);

        var archivePath = Path.Combine(repo.BuildRoot, "pjdfstest.oci.tar");
        var stampPath = Path.Combine(repo.BuildRoot, "image.stamp");
        var contextRoot = Path.Combine(repo.BuildRoot, "image-context");
        var stampValue = string.Join("|",
            File.GetLastWriteTimeUtc(repo.TestContainerfile).Ticks,
            File.GetLastWriteTimeUtc(assets.BinaryPath).Ticks,
            File.GetLastWriteTimeUtc(Path.Combine(assets.TestsRoot, "misc.sh")).Ticks);
        var needsBuild = rebuild || !File.Exists(archivePath) || !File.Exists(stampPath) ||
                         !string.Equals(File.ReadAllText(stampPath).Trim(), stampValue, StringComparison.Ordinal);

        if (needsBuild)
        {
            PrepareImageContext(repo, assets, contextRoot);

            Console.WriteLine($"[pjd] building podman image {TestImageRef}");
            var build = await RunProcessCaptureAsync(
                "podman",
                new[]
                {
                    "build",
                    "--arch",
                    TestImageArch,
                    "-f",
                    Path.Combine(contextRoot, "Containerfile"),
                    "-t",
                    TestImageRef,
                    contextRoot
                },
                contextRoot,
                null,
                CancellationToken.None);
            if (build.ExitCode != 0)
                throw new InvalidOperationException($"failed to build podman image: {build.CombinedOutput}");

            if (File.Exists(archivePath))
                File.Delete(archivePath);

            Console.WriteLine($"[pjd] saving OCI archive {archivePath}");
            var save = await RunProcessCaptureAsync(
                "podman",
                new[]
                {
                    "save",
                    "--format",
                    "oci-archive",
                    "-o",
                    archivePath,
                    TestImageRef
                },
                repo.Root,
                null,
                CancellationToken.None);
            if (save.ExitCode != 0)
                throw new InvalidOperationException($"failed to save podman image: {save.CombinedOutput}");

            await File.WriteAllTextAsync(stampPath, stampValue);
        }

        Console.WriteLine($"[pjd] loading image into Podish from {archivePath}");
        var load = await RunProcessCaptureAsync(
            "dotnet",
            new[]
            {
                "run",
                "--project",
                repo.PodishCliProject,
                "--no-build",
                "--",
                "load",
                "-i",
                archivePath
            },
            repo.Root,
            CleanDotnetEnv(),
            CancellationToken.None);
        if (load.ExitCode != 0)
            throw new InvalidOperationException($"failed to load OCI archive into Podish: {load.CombinedOutput}");

        return TestImageRef;
    }

    private static void PrepareImageContext(RepoLayout repo, PreparedAssets assets, string contextRoot)
    {
        if (Directory.Exists(contextRoot))
            Directory.Delete(contextRoot, recursive: true);

        Directory.CreateDirectory(contextRoot);
        File.Copy(repo.TestContainerfile, Path.Combine(contextRoot, "Containerfile"), overwrite: true);
        CopyDirectory(assets.StageRoot, Path.Combine(contextRoot, "pjdfstest"));
    }

    private static Dictionary<string, string> CleanDotnetEnv()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["DOTNET_CLI_HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_NOLOGO"] = "true"
        };
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IDictionary<string, string>? environment = null,
        bool redirect = true)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        return startInfo;
    }

    private static async Task<ProcessCapture> RunProcessCaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var psi = CreateProcessStartInfo(fileName, arguments, workingDirectory, environment);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessCapture(process.ExitCode, stdout, stderr);
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var result = RunProcessCaptureAsync(
                    "/bin/sh",
                    new[] { "-lc", $"command -v {ShellQuote(command)} >/dev/null 2>&1" },
                    Directory.GetCurrentDirectory(),
                    null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    private sealed record RunOptions(
        string? Filter,
        string JobsRaw,
        bool KeepWorkdir,
        bool RebuildAssets,
        string? OutputJson,
        int? MaxCases);

    private sealed record RepoLayout(
        string Root,
        string SolutionFile,
        string PodishCliProject,
        string PjdFsProject,
        string TestsRoot,
        string UpstreamRoot,
        string TestContainerfile,
        string BuildRoot,
        string AssetsRoot,
        string RunsRoot)
    {
        public static RepoLayout Discover()
        {
            foreach (var start in new[]
                     {
                         Directory.GetCurrentDirectory(),
                         AppContext.BaseDirectory,
                         Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
                     })
            {
                var candidate = TryFindRoot(start);
                if (candidate is not null)
                    return candidate;
            }

            throw new InvalidOperationException("could not locate repository root");
        }

        private static RepoLayout? TryFindRoot(string start)
        {
            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current))
            {
                var solution = Path.Combine(current, "Podish.slnx");
                if (File.Exists(solution))
                {
                    var testsRoot = Path.Combine(current, "tests", "pjdfstest");
                    return new RepoLayout(
                        Root: current,
                        SolutionFile: solution,
                        PodishCliProject: Path.Combine(current, "Podish.Cli", "Podish.Cli.csproj"),
                        PjdFsProject: Path.Combine(current, "Podish.PjdFs", "Podish.PjdFs.csproj"),
                        TestsRoot: testsRoot,
                        UpstreamRoot: Path.Combine(testsRoot, "upstream"),
                        TestContainerfile: Path.Combine(testsRoot, "Containerfile.pjdfstest"),
                        BuildRoot: Path.Combine(current, "build", "pjdfstest"),
                        AssetsRoot: Path.Combine(current, "build", "pjdfstest", "assets"),
                        RunsRoot: Path.Combine(current, "build", "pjdfstest", "runs"));
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                    break;
                current = parent;
            }

            return null;
        }
    }

    private sealed record UpstreamCase(string RelativePath, string FullPath);

    private sealed record UpstreamSnapshot(string Root, string TestsRoot, string SourceFile, string? Commit)
    {
        public static async Task<UpstreamSnapshot> LoadAsync(RepoLayout repo)
        {
            await EnsureCheckoutAsync(repo);

            var root = repo.UpstreamRoot;
            var testsRoot = Path.Combine(root, "tests");
            var sourcePath = Path.Combine(root, "pjdfstest.c");

            if (!IsValidCheckout(root))
                throw new InvalidOperationException($"upstream checkout is incomplete after clone: {root}");

            var commitPath = Path.Combine(repo.TestsRoot, "UPSTREAM_COMMIT");
            var commit = File.Exists(commitPath) ? File.ReadAllText(commitPath).Trim() : null;
            return new UpstreamSnapshot(root, testsRoot, sourcePath, commit);
        }

        public static bool IsValidCheckout(string root)
        {
            var testsRoot = Path.Combine(root, "tests");
            var confPath = Path.Combine(testsRoot, "conf");
            var miscPath = Path.Combine(testsRoot, "misc.sh");
            var sourcePath = Path.Combine(root, "pjdfstest.c");

            return Directory.Exists(root) &&
                   Directory.Exists(testsRoot) &&
                   File.Exists(confPath) &&
                   File.Exists(miscPath) &&
                   File.Exists(sourcePath);
        }

        private static async Task EnsureCheckoutAsync(RepoLayout repo)
        {
            if (IsValidCheckout(repo.UpstreamRoot))
                return;

            if (Directory.Exists(repo.UpstreamRoot) &&
                Directory.EnumerateFileSystemEntries(repo.UpstreamRoot).Any())
                throw new InvalidOperationException(
                    $"upstream checkout exists but is incomplete: {repo.UpstreamRoot}. Remove it and retry.");

            Directory.CreateDirectory(repo.TestsRoot);
            if (Directory.Exists(repo.UpstreamRoot))
                Directory.Delete(repo.UpstreamRoot, recursive: true);

            Console.WriteLine($"[pjd] cloning upstream pjdfstest from {UpstreamRepoUrl} into {repo.UpstreamRoot}");
            var clone = await RunProcessCaptureAsync(
                "git",
                ["clone", UpstreamRepoUrl, repo.UpstreamRoot],
                repo.Root,
                null,
                CancellationToken.None);
            if (clone.ExitCode != 0)
                throw new InvalidOperationException($"failed to clone upstream pjdfstest: {clone.CombinedOutput}");

            var commitPath = Path.Combine(repo.TestsRoot, "UPSTREAM_COMMIT");
            var pinnedCommit = File.Exists(commitPath) ? File.ReadAllText(commitPath).Trim() : null;
            if (string.IsNullOrWhiteSpace(pinnedCommit))
                return;

            Console.WriteLine($"[pjd] checking out upstream commit {pinnedCommit}");
            var checkout = await RunProcessCaptureAsync(
                "git",
                ["checkout", pinnedCommit],
                repo.UpstreamRoot,
                null,
                CancellationToken.None);
            if (checkout.ExitCode != 0)
                throw new InvalidOperationException($"failed to checkout upstream commit {pinnedCommit}: {checkout.CombinedOutput}");
        }

        public IReadOnlyList<UpstreamCase> DiscoverCases(string? filter, int? maxCases)
        {
            var cases = Directory.EnumerateFiles(TestsRoot, "*.t", SearchOption.AllDirectories)
                .Select(path => new UpstreamCase(Path.GetRelativePath(TestsRoot, path).Replace('\\', '/'), path))
                .Where(testCase => string.IsNullOrWhiteSpace(filter) ||
                                   testCase.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(testCase => testCase.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (maxCases is > 0)
                return cases.Take(maxCases.Value).ToList();
            return cases;
        }
    }

    private sealed record PreparedAssets(string StageRoot, string TestsRoot, string BinaryPath, string BuildLogPath);

    private sealed class AssetBuilder
    {
        private readonly RepoLayout _repo;
        private readonly UpstreamSnapshot _snapshot;

        public AssetBuilder(RepoLayout repo, UpstreamSnapshot snapshot)
        {
            _repo = repo;
            _snapshot = snapshot;
        }

        public async Task<PreparedAssets> PrepareAsync(bool rebuild)
        {
            Directory.CreateDirectory(_repo.AssetsRoot);

            var stageRoot = Path.Combine(_repo.AssetsRoot, "stage");
            var testsRoot = Path.Combine(stageRoot, "tests");
            var binaryPath = Path.Combine(stageRoot, "pjdfstest");
            var buildLogPath = Path.Combine(_repo.AssetsRoot, "build.log");

            if (rebuild)
            {
                if (Directory.Exists(stageRoot))
                    Directory.Delete(stageRoot, recursive: true);
                if (File.Exists(buildLogPath))
                    File.Delete(buildLogPath);
            }

            if (!Directory.Exists(testsRoot))
            {
                Directory.CreateDirectory(stageRoot);
                CopyDirectory(_snapshot.TestsRoot, testsRoot);
            }

            if (!File.Exists(binaryPath) || rebuild)
                await TryBuildGuestBinaryAsync(binaryPath, buildLogPath);

            if (!File.Exists(binaryPath))
            {
                var message = new StringBuilder();
                message.AppendLine("guest pjdfstest binary is not available");
                message.AppendLine($"expected: {binaryPath}");
                message.AppendLine($"build log: {buildLogPath}");
                message.AppendLine("run `doctor` to inspect missing toolchain prerequisites");
                throw new InvalidOperationException(message.ToString().TrimEnd());
            }

            TryMakeExecutable(binaryPath);
            return new PreparedAssets(stageRoot, testsRoot, binaryPath, buildLogPath);
        }

        private async Task TryBuildGuestBinaryAsync(string binaryPath, string buildLogPath)
        {
            var buildSourceRoot = Path.Combine(_repo.AssetsRoot, "build-src");
            var zigCacheRoot = Path.Combine(_repo.AssetsRoot, "zig-cache");
            if (Directory.Exists(buildSourceRoot))
                Directory.Delete(buildSourceRoot, recursive: true);
            Directory.CreateDirectory(zigCacheRoot);
            CopyDirectory(_snapshot.Root, buildSourceRoot);

            var log = new StringBuilder();
            log.AppendLine($"timestamp={DateTimeOffset.UtcNow:O}");
            log.AppendLine($"source={buildSourceRoot}");
            log.AppendLine($"zig-cache={zigCacheRoot}");

            var buildEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ZIG_GLOBAL_CACHE_DIR"] = zigCacheRoot,
                ["ZIG_LOCAL_CACHE_DIR"] = zigCacheRoot
            };

            try
            {
                var steps = new[]
                {
                    new BuildStep("autoreconf", new[] { "/bin/sh", "-lc", "autoreconf -ifs" }),
                    new BuildStep("configure",
                        new[]
                        {
                            "/bin/sh",
                            "-lc",
                            "CC='zig cc -target x86-linux-musl -static' ./configure --host=i686-linux-musl"
                        }),
                    new BuildStep("make", new[] { "make", "pjdfstest" })
                };

                foreach (var step in steps)
                {
                    var capture = await RunProcessCaptureAsync(step.FileName, step.Arguments, buildSourceRoot, buildEnv, CancellationToken.None);
                    log.AppendLine($"## {step.Name}");
                    log.AppendLine($"exit={capture.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(capture.Stdout))
                        log.AppendLine(capture.Stdout.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(capture.Stderr))
                        log.AppendLine(capture.Stderr.TrimEnd());

                    if (capture.ExitCode != 0)
                    {
                        await File.WriteAllTextAsync(buildLogPath, log.ToString());
                        return;
                    }
                }

                var builtBinary = Path.Combine(buildSourceRoot, "pjdfstest");
                if (File.Exists(builtBinary))
                {
                    File.Copy(builtBinary, binaryPath, overwrite: true);
                    log.AppendLine($"copied={binaryPath}");
                }
                else
                {
                    log.AppendLine("missing built pjdfstest after successful build steps");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"exception={ex}");
            }

            await File.WriteAllTextAsync(buildLogPath, log.ToString());
        }

        private static void TryMakeExecutable(string path)
        {
            try
            {
                using var process = Process.Start(CreateProcessStartInfo("chmod", new[] { "+x", path }, Directory.GetCurrentDirectory(), null, redirect: false));
                process?.WaitForExit();
            }
            catch
            {
                // Best effort only.
            }
        }

        private sealed record BuildStep(string Name, string[] Command)
        {
            public string FileName => Command[0];
            public IReadOnlyList<string> Arguments => Command.Skip(1).ToArray();
        }
    }

    private sealed class CaseScheduler
    {
        private readonly RepoLayout _repo;
        private readonly PreparedAssets _assets;
        private readonly string _imageOrRootfs;
        private readonly string _runDir;
        private readonly bool _keepWorkdir;

        public CaseScheduler(RepoLayout repo, PreparedAssets assets, string imageOrRootfs, string runDir, bool keepWorkdir)
        {
            _repo = repo;
            _assets = assets;
            _imageOrRootfs = imageOrRootfs;
            _runDir = runDir;
            _keepWorkdir = keepWorkdir;
        }

        public async Task<IReadOnlyList<CaseResult>> RunAsync(IReadOnlyList<UpstreamCase> cases, int jobs)
        {
            var results = new CaseResult[cases.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, cases.Count),
                new ParallelOptions { MaxDegreeOfParallelism = jobs },
                async (index, cancellationToken) =>
                {
                    results[index] = await RunCaseAsync(cases[index], cancellationToken);
                });
            return results;
        }

        private async Task<CaseResult> RunCaseAsync(UpstreamCase testCase, CancellationToken cancellationToken)
        {
            var safeName = Regex.Replace(testCase.RelativePath, "[^A-Za-z0-9._-]+", "_", RegexOptions.CultureInvariant);
            var caseDir = Path.Combine(_runDir, safeName);
            Directory.CreateDirectory(caseDir);

            var command = "cd /pjdfstest && rm -rf work && mkdir work && cd work && /bin/sh /pjdfstest/tests/" +
                          testCase.RelativePath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var args = new List<string>
            {
                "run",
                "--project",
                _repo.PodishCliProject,
                "--no-build",
                "--",
                "run",
                "--rm",
                "--network",
                "host",
                _imageOrRootfs,
                "--",
                "/bin/sh",
                "-c",
                command
            };

            var startedAt = DateTimeOffset.UtcNow;
            var capture = await RunProcessCaptureAsync("dotnet", args, _repo.Root, CleanDotnetEnv(), cancellationToken);
            var finishedAt = DateTimeOffset.UtcNow;

            var output = capture.CombinedOutput;
            var logPath = Path.Combine(caseDir, "stdout-stderr.log");
            await File.WriteAllTextAsync(logPath, output, cancellationToken);

            var tap = TapParser.Parse(output);
            var status = capture.ExitCode == 0 && tap.EffectiveNotOkCount == 0 ? "passed" : "failed";
            var failureExcerpt = status == "passed" ? null : BuildFailureExcerpt(output);
            var failureDetails = status == "passed" ? Array.Empty<FailureDetail>() : FailureParser.Parse(output);

            if (!_keepWorkdir && status == "passed")
                Directory.Delete(caseDir, recursive: true);

            return new CaseResult(
                testCase.RelativePath,
                status,
                capture.ExitCode,
                tap.Plan,
                tap.OkCount,
                tap.NotOkCount,
                tap.SkipCount,
                tap.TodoCount,
                tap.TodoNotOkCount,
                startedAt,
                finishedAt,
                logPath,
                failureExcerpt,
                failureDetails);
        }

        private static string? BuildFailureExcerpt(string output)
        {
            var lines = output.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => line.StartsWith("not ok", StringComparison.OrdinalIgnoreCase) ||
                               line.StartsWith("[Podish.Cli", StringComparison.OrdinalIgnoreCase) ||
                               line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();

            if (lines.Length == 0)
                return null;

            return string.Join(Environment.NewLine, lines);
        }
    }

    private sealed record CaseResult(
        string Case,
        string Status,
        int ExitCode,
        int? Plan,
        int OkCount,
        int NotOkCount,
        int SkipCount,
        int TodoCount,
        int TodoNotOkCount,
        DateTimeOffset StartedAt,
        DateTimeOffset FinishedAt,
        string LogPath,
        string? FailureExcerpt,
        IReadOnlyList<FailureDetail> FailureDetails)
    {
        public double DurationSeconds => (FinishedAt - StartedAt).TotalSeconds;
        public int EffectiveNotOkCount => Math.Max(0, NotOkCount - TodoNotOkCount);
    }

    private sealed record FailureDetail(
        int? TestNumber,
        string? Command,
        string? Syscall,
        string? Expected,
        string? Actual,
        string RawLine);

    private sealed record Summary(
        int TotalCases,
        int PassedCases,
        int FailedCases,
        string RunDirectory,
        string BinaryPath,
        IReadOnlyList<CaseResult> Results)
    {
        public static Summary FromResults(IReadOnlyList<CaseResult> results, string runDirectory, string binaryPath)
        {
            var passed = results.Count(result => result.Status == "passed");
            return new Summary(
                results.Count,
                passed,
                results.Count - passed,
                runDirectory,
                binaryPath,
                results);
        }

        public void WriteToConsole()
        {
            Console.WriteLine($"[pjd] passed={PassedCases} failed={FailedCases} total={TotalCases}");
            foreach (var failed in Results.Where(result => result.Status != "passed"))
                Console.WriteLine($"[pjd] FAIL {failed.Case} exit={failed.ExitCode} log={failed.LogPath}");
        }

        public string ToMarkdown(RepoLayout repo, RunOptions options, string imageRef)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# pjdfstest Report");
            sb.AppendLine();
            sb.AppendLine("## Run Summary");
            sb.AppendLine();
            sb.AppendLine($"- Repository: `{repo.Root}`");
            sb.AppendLine($"- Image: `{imageRef}`");
            sb.AppendLine($"- Guest Binary: `{BinaryPath}`");
            sb.AppendLine($"- Run Directory: `{RunDirectory}`");
            sb.AppendLine($"- Filter: `{options.Filter ?? "<none>"}`");
            sb.AppendLine($"- Jobs: `{options.JobsRaw}`");
            sb.AppendLine($"- Total Cases: `{TotalCases}`");
            sb.AppendLine($"- Passed Cases: `{PassedCases}`");
            sb.AppendLine($"- Failed Cases: `{FailedCases}`");
            sb.AppendLine();

            sb.AppendLine("## Failed Cases");
            sb.AppendLine();
            var failures = Results.Where(result => result.Status != "passed").ToList();
            if (failures.Count == 0)
            {
                sb.AppendLine("No failed cases.");
                sb.AppendLine();
            }
            else
            {
                foreach (var failure in failures)
                {
                    sb.AppendLine($"### {failure.Case}");
                    sb.AppendLine();
                    sb.AppendLine($"- Status: `{failure.Status}`");
                    sb.AppendLine($"- Exit Code: `{failure.ExitCode}`");
                    sb.AppendLine($"- Plan: `{failure.Plan?.ToString() ?? "<none>"}`");
                    sb.AppendLine($"- ok/not ok/skip/todo: `{failure.OkCount}/{failure.NotOkCount}/{failure.SkipCount}/{failure.TodoCount}`");
                    sb.AppendLine($"- Duration: `{failure.DurationSeconds:F2}s`");
                    sb.AppendLine($"- Log: `{failure.LogPath}`");
                    if (!string.IsNullOrWhiteSpace(failure.FailureExcerpt))
                    {
                        sb.AppendLine();
                        sb.AppendLine("```text");
                        sb.AppendLine(failure.FailureExcerpt);
                        sb.AppendLine("```");
                    }

                    sb.AppendLine();
                }
            }

            var clusteredFailures = Results
                .SelectMany(result => result.FailureDetails.Select(detail => (result.Case, Detail: detail)))
                .ToList();

            sb.AppendLine("## Failure Clusters By Syscall");
            sb.AppendLine();
            if (clusteredFailures.Count == 0)
            {
                sb.AppendLine("No parsed TAP failures.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("| Syscall | Count | Cases |");
                sb.AppendLine("| --- | ---: | ---: |");
                foreach (var group in clusteredFailures
                             .GroupBy(item => item.Detail.Syscall ?? "<unknown>", StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(group => group.Count())
                             .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"| {group.Key.Replace("|", "\\|", StringComparison.Ordinal)} | {group.Count()} | {group.Select(item => item.Case).Distinct(StringComparer.OrdinalIgnoreCase).Count()} |");
                }

                sb.AppendLine();
            }

            sb.AppendLine("## Failure Clusters By Errno");
            sb.AppendLine();
            if (clusteredFailures.Count == 0)
            {
                sb.AppendLine("No parsed TAP failures.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("| Errno | Count | Cases |");
                sb.AppendLine("| --- | ---: | ---: |");
                foreach (var group in clusteredFailures
                             .GroupBy(item => item.Detail.Actual ?? "<unknown>", StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(group => group.Count())
                             .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"| {group.Key.Replace("|", "\\|", StringComparison.Ordinal)} | {group.Count()} | {group.Select(item => item.Case).Distinct(StringComparer.OrdinalIgnoreCase).Count()} |");
                }

                sb.AppendLine();
            }

            sb.AppendLine("## All Cases");
            sb.AppendLine();
            sb.AppendLine("| Case | Status | Exit | Plan | ok | not ok | skip | todo | Duration | Log |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var result in Results.OrderBy(result => result.Case, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("| ");
                sb.Append(result.Case.Replace("|", "\\|", StringComparison.Ordinal));
                sb.Append(" | ");
                sb.Append(result.Status);
                sb.Append(" | ");
                sb.Append(result.ExitCode);
                sb.Append(" | ");
                sb.Append(result.Plan?.ToString() ?? "");
                sb.Append(" | ");
                sb.Append(result.OkCount);
                sb.Append(" | ");
                sb.Append(result.NotOkCount);
                sb.Append(" | ");
                sb.Append(result.SkipCount);
                sb.Append(" | ");
                sb.Append(result.TodoCount);
                sb.Append(" | ");
                sb.Append(result.DurationSeconds.ToString("F2"));
                sb.Append("s | ");
                sb.Append(result.LogPath.Replace("|", "\\|", StringComparison.Ordinal));
                sb.AppendLine(" |");
            }

            return sb.ToString();
        }
    }

    private sealed record DoctorCheck(string Name, bool Ok, string Detail);

    private sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
    {
        public bool HasErrors => Checks.Any(check => !check.Ok);

        public static DoctorReport Create(RepoLayout repo)
        {
            var checks = new List<DoctorCheck>
            {
                CheckPath("repo-root", repo.Root, Directory.Exists(repo.Root)),
                CheckPath("podish-cli", repo.PodishCliProject, File.Exists(repo.PodishCliProject)),
                CheckPath("upstream-config", Path.Combine(repo.TestsRoot, "UPSTREAM_COMMIT"), File.Exists(Path.Combine(repo.TestsRoot, "UPSTREAM_COMMIT"))),
                CheckPath(
                    "upstream-checkout",
                    repo.UpstreamRoot,
                    !Directory.Exists(repo.UpstreamRoot) || UpstreamSnapshot.IsValidCheckout(repo.UpstreamRoot)),
                CheckPath("test-containerfile", repo.TestContainerfile, File.Exists(repo.TestContainerfile)),
                CheckTool("dotnet"),
                CheckTool("git"),
                CheckTool("podman"),
                CheckTool("zig"),
                CheckTool("autoreconf"),
                CheckTool("make")
            };

            return new DoctorReport(checks);
        }

        private static DoctorCheck CheckTool(string tool)
        {
            var ok = CommandExists(tool);
            return new DoctorCheck(tool, ok, ok ? "found" : "missing");
        }

        private static DoctorCheck CheckPath(string name, string path, bool ok)
        {
            if (name == "upstream-checkout" && !Directory.Exists(path))
                return new DoctorCheck(name, true, $"missing locally (will auto-clone on demand): {path}");

            return new DoctorCheck(name, ok, ok ? path : $"missing: {path}");
        }

        public void WriteToConsole()
        {
            foreach (var check in Checks)
                Console.WriteLine($"[pjd] {(check.Ok ? "ok  " : "bad ")} {check.Name}: {check.Detail}");
        }
    }

    private sealed record ProcessCapture(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => string.IsNullOrEmpty(Stderr) ? Stdout : $"{Stdout}{Environment.NewLine}{Stderr}";
    }

    private sealed record TapSummary(int? Plan, int OkCount, int NotOkCount, int SkipCount, int TodoCount, int TodoNotOkCount)
    {
        public int EffectiveNotOkCount => Math.Max(0, NotOkCount - TodoNotOkCount);
    }

    private static class TapParser
    {
        private static readonly Regex PlanPattern = new(@"^\s*(?<start>\d+)\.\.(?<end>\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex OkPattern = new(@"^\s*ok\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NotOkPattern = new(@"^\s*not ok\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static TapSummary Parse(string text)
        {
            int? plan = null;
            var ok = 0;
            var notOk = 0;
            var skip = 0;
            var todo = 0;
            var todoNotOk = 0;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                var planMatch = PlanPattern.Match(line);
                if (planMatch.Success)
                {
                    if (int.TryParse(planMatch.Groups["end"].Value, out var parsedPlan))
                        plan = parsedPlan;
                    continue;
                }

                if (NotOkPattern.IsMatch(line))
                {
                    notOk++;
                    if (line.Contains("# TODO", StringComparison.OrdinalIgnoreCase))
                    {
                        todo++;
                        todoNotOk++;
                    }
                    if (line.Contains("# SKIP", StringComparison.OrdinalIgnoreCase))
                        skip++;
                    continue;
                }

                if (OkPattern.IsMatch(line))
                {
                    ok++;
                    if (line.Contains("# TODO", StringComparison.OrdinalIgnoreCase))
                        todo++;
                    if (line.Contains("# SKIP", StringComparison.OrdinalIgnoreCase))
                        skip++;
                }
            }

            return new TapSummary(plan, ok, notOk, skip, todo, todoNotOk);
        }
    }

    private static partial class FailureParser
    {
        [GeneratedRegex(
            @"^not ok(?:\s+(?<num>\d+))?\s+-\s+tried\s+'(?<command>.+?)',\s+expected\s+(?<expected>.+?),\s+got(?:\s+(?<actual>.*?))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FailurePattern();

        public static IReadOnlyList<FailureDetail> Parse(string output)
        {
            var failures = new List<FailureDetail>();
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var match = FailurePattern().Match(line);
                if (!match.Success)
                    continue;

                int? testNumber = null;
                if (int.TryParse(match.Groups["num"].Value, out var parsed))
                    testNumber = parsed;

                var command = match.Groups["command"].Value;
                failures.Add(new FailureDetail(
                    testNumber,
                    command,
                    ExtractSyscall(command),
                    match.Groups["expected"].Value,
                    match.Groups["actual"].Value,
                    line));
            }

            return failures;
        }

        private static string? ExtractSyscall(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part is "-u" or "-g")
                {
                    i++;
                    continue;
                }

                if (!part.StartsWith("-", StringComparison.Ordinal))
                    return part;
            }

            return null;
        }
    }
}
