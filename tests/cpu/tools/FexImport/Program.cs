using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fiberish.X86.Tests.Fex;

var command = args.FirstOrDefault() ?? "verify";
var rootOption = Array.IndexOf(args, "--root");
var root = rootOption >= 0 ? Path.GetFullPath(args[rootOption + 1]) : FindRoot();
var cpuRoot = Path.Combine(root, "tests", "cpu");
var upstream = Path.Combine(cpuRoot, "upstream", "fex");
var assets = Path.Combine(cpuRoot, "assets", "fex");
var lockPath = Path.Combine(cpuRoot, "fex.lock.json");
const string assemblyPrefix = "unittests/32Bit_ASM/";
string[] supportingFiles = ["LICENSE", "unittests/32Bit_ASM/CMakeLists.txt",
    "Source/Tools/CommonTools/HarnessHelpers.h", "Scripts/json_config_parse.py"];

try
{
    switch (command)
    {
        case "fetch":
            await Fetch();
            VerifySources();
            break;
        case "regenerate":
            VerifySources();
            var nasmOption = Array.IndexOf(args, "--nasm");
            if (nasmOption < 0) throw new ArgumentException("regenerate requires --nasm <path-to-NASM-3.01>");
            await Regenerate(args[nasmOption + 1]);
            VerifyAssets();
            break;
        case "verify":
            VerifySources();
            VerifyAssets();
            break;
        default:
            throw new ArgumentException("Usage: FexImport [fetch|regenerate --nasm PATH|verify] [--root REPO]");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

async Task Fetch()
{
    // The pinned archive supplies a complete inventory, without GitHub API auth/rate-limit dependence.
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    var archiveBytes = await client.GetByteArrayAsync($"https://codeload.github.com/FEX-Emu/FEX/zip/{FexJson.Revision}");
    using var archive = new ZipArchive(new MemoryStream(archiveBytes));
    var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    var prefix = $"FEX-{FexJson.Revision}/";
    foreach (var entry in archive.Entries)
    {
        if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
        var path = entry.FullName[prefix.Length..];
        if (!(path.StartsWith(assemblyPrefix, StringComparison.Ordinal) &&
              (path.EndsWith(".asm", StringComparison.Ordinal) || path.EndsWith(".mac", StringComparison.Ordinal))) &&
            !supportingFiles.Contains(path, StringComparer.Ordinal)) continue;
        using var stream = entry.Open();
        using var contents = new MemoryStream();
        await stream.CopyToAsync(contents);
        files.Add(path, contents.ToArray());
    }
    var sourceLock = new SourceLock(1, FexJson.Repository, FexJson.Revision, "MIT", 260,
        files.Select(file => new LockedFile(file.Key, Hash(file.Value),
            $"https://raw.githubusercontent.com/FEX-Emu/FEX/{FexJson.Revision}/{file.Key}")).ToArray());
    Require(files.Keys.Count(path => path.EndsWith(".asm", StringComparison.Ordinal)) == 260,
        "Pinned archive does not contain the expected 260 assembly sources");
    if (File.Exists(lockPath))
        Require(JsonSerializer.Serialize(sourceLock, FexJson.Options) ==
            JsonSerializer.Serialize(ReadJson<SourceLock>(lockPath), FexJson.Options), "Downloaded sources differ from the committed source lock");
    foreach (var (path, bytes) in files) WriteBytes(Path.Combine(upstream, path), bytes);
    WriteJson(lockPath, sourceLock);
    Console.WriteLine($"Fetched {files.Count} byte-for-byte original files at {FexJson.Revision}.");
}

void VerifySources()
{
    var sourceLock = ReadJson<SourceLock>(lockPath);
    Require(sourceLock.SchemaVersion == 1 && sourceLock.Repository == FexJson.Repository &&
        sourceLock.Revision == FexJson.Revision && sourceLock.AssemblyCount == 260 && sourceLock.License == "MIT", "Unexpected source lock");
    foreach (var file in sourceLock.Files)
        Require(Hash(File.ReadAllBytes(Path.Combine(upstream, file.Path))) == file.Sha256, $"Source hash mismatch: {file.Path}");
    var actual = Directory.GetFiles(upstream, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(upstream, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
    Require(actual.SequenceEqual(sourceLock.Files.Select(file => file.Path).Order(StringComparer.Ordinal)), "Source inventory differs from lock");
    Require(actual.Count(path => path.EndsWith(".asm", StringComparison.Ordinal)) == 260, "Assembly inventory is incomplete");
    Console.WriteLine($"Verified {sourceLock.Files.Length} pinned original files (260 assembly tests).");
}

async Task Regenerate(string nasm)
{
    var version = await Run(nasm, ["-v"]);
    Require(version.ExitCode == 0 && version.Output.StartsWith("NASM version 3.01 ", StringComparison.Ordinal),
        $"Regeneration is pinned to NASM 3.01, got: {version.Output}");
    var entries = new List<FexCatalogEntry>();
    var assemblyRoot = Path.Combine(upstream, assemblyPrefix);
    var sources = Directory.GetFiles(assemblyRoot, "*.asm", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
    // Temporary assembler wrappers/listings stay in the tool's ignored obj directory, not among upstream originals.
    var temporary = Path.Combine(cpuRoot, "tools", "FexImport", "obj", "nasm");
    Directory.CreateDirectory(temporary);
    foreach (var source in sources)
    {
        var relative = Path.GetRelativePath(assemblyRoot, source).Replace('\\', '/');
        var id = relative[..^4];
        var sourceHash = Hash(File.ReadAllBytes(source));
        var text = File.ReadAllText(source);
        var configMatch = Regex.Match(text, @"(?s)%ifdef CONFIG\s*(.*?)%endif");
        Require(configMatch.Success, $"Missing CONFIG: {relative}");
        using var configDocument = JsonDocument.Parse(configMatch.Groups[1].Value);
        string? reason = id == "X87/D9_F9"
            ? "FYL2XP1 input ST(0)=2 is outside its architecturally defined range; the upstream expected result is not a portable oracle. See tests/cpu/X87-NOTES.md."
            : null;
        try
        {
            FexConfig.Parse(configDocument.RootElement).RequireRunnable();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        {
            reason = exception.Message;
        }
        if (Regex.IsMatch(text, @"(?im)^\s*(?:mov\s+(?:ds|es|ss|fs|gs)\s*,|(?:push|pop)\s+(?:cs|ds|es|ss|fs|gs)\b)"))
            reason ??= "Explicit segment-selector operations require a descriptor/selector model; only flat zero-base segments are adapted.";
        if (Regex.IsMatch(text, @"(?im)^\s*iret(?:d|w)?\b"))
            reason ??= "IRET requires validating/loading the hard-coded Linux CS selector and protection state; the sandbox models segment bases, not descriptors.";
        if (Regex.IsMatch(text, @"(?im)^\s*int\s+0x80\b"))
            reason ??= "Requires the Linux int 0x80 syscall ABI; the CPU-only sandbox has no OS syscall handler.";
        if (id.StartsWith("TwoByte/0F_82", StringComparison.Ordinal))
            reason ??= "Generates executable code and HLT outside the source binary; requires a separately validated dynamic-code termination contract.";
        if (id == "FEX_bugs/VEXW_Bug")
            reason ??= "Uses AVX/FMA VEX instructions (not declared in upstream HostFeatures); the sandbox has no AVX/FMA register/execution contract.";
        if (Regex.IsMatch(text, @"(?im)^\s*(?:sgdt|sldt|smsw)\b"))
            reason ??= "Expected values depend on FEX/Linux's synthetic GDTR, LDTR or CR0 state, which the flat CPU-only sandbox does not reproduce.";

        var safeName = id.Replace('/', '_');
        var wrapper = Path.Combine(temporary, safeName + ".asm");
        var listing = Path.Combine(temporary, safeName + ".lst");
        var binaryRelative = "bin/" + id + ".bin";
        var binaryPath = Path.Combine(assets, binaryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        // Preserve the original wrapper's instruction addresses: the two selector
        // loads occupy four bytes. Replacing them with four one-byte NOPs keeps
        // hard-coded GOT/return-address expectations valid without loading Linux CS/DS/ES.
        File.WriteAllText(wrapper, "BITS 32\norg 10000h\nmov eax, 0x17\ntimes 4 nop\n" + text + "\nret\n", new UTF8Encoding(false));
        var assembled = await Run(nasm, ["-f", "bin", "-I", Path.GetDirectoryName(source)! + Path.DirectorySeparatorChar,
            "-l", listing, "-o", binaryPath, wrapper]);
        string? binaryHash = null;
        uint[] halts = [];
        if (assembled.ExitCode != 0)
        {
            reason ??= "NASM conversion failed: " + assembled.Output.Replace(wrapper, "<wrapper>", StringComparison.Ordinal).Trim();
            binaryRelative = null;
        }
        else
        {
            binaryHash = Hash(File.ReadAllBytes(binaryPath));
            // Source/listing instruction records, NOT searching for F4 bytes (which may be immediate/data bytes).
            halts = Regex.Matches(File.ReadAllText(listing), @"(?im)^\s*\d+\s+([0-9a-f]{8})\s+F4\s+(?:<\d+>\s+)?hlt\b")
                .Select(match => 0x10000U + Convert.ToUInt32(match.Groups[1].Value, 16)).Distinct().Order().ToArray();
            if (halts.Length == 0) reason ??= "No source-level HLT instruction was recorded in the NASM listing.";
        }
        // These two sources explicitly synthesize exactly `mov eax,41424344h;hlt`
        // at E0000000. Declare that reviewed code image instead of accepting any
        // arbitrary HLT in writable memory. Never derive it from emulator output.
        FexDynamicCode[] dynamicCode = id is "Primary/Primary_E8" or "Primary/Primary_E9"
            ? [new(0xE0000000, "B844434241F4", 0xE0000005)] : [];
        var manifest = new FexManifest(1, id, FexJson.Revision, sourceHash, 0x10000, 0x10000,
            binaryRelative, binaryHash, halts, 1_000_000, dynamicCode, configDocument.RootElement.Clone());
        var manifestRelative = "manifests/" + id + ".json";
        var manifestPath = Path.Combine(assets, manifestRelative);
        WriteJson(manifestPath, manifest);
        entries.Add(new FexCatalogEntry(id, assemblyPrefix + relative, sourceHash, manifestRelative,
            Hash(File.ReadAllBytes(manifestPath)), reason == null ? "ready" : "blocked", reason));
    }
    var catalog = new FexCatalog(1, FexJson.Repository, FexJson.Revision, "MIT",
        "BITS 32; org 10000h; mov eax,0x17; four NOP bytes replacing FEX Linux ds/es selector loads; unchanged upstream body; ret. Preserve original instruction addresses and flat zero-base segments.",
        "3.01", entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray());
    WriteBytes(Path.Combine(assets, "LICENSE"), File.ReadAllBytes(Path.Combine(upstream, "LICENSE")));
    WriteJson(Path.Combine(assets, "catalog.json"), catalog);
    Console.WriteLine($"Generated {entries.Count(entry => entry.Status == "ready")} runnable tests; {entries.Count(entry => entry.Status == "blocked")} explicitly blocked.");
}

void VerifyAssets()
{
    var catalog = ReadJson<FexCatalog>(Path.Combine(assets, "catalog.json"));
    Require(catalog.SchemaVersion == 1 && catalog.Revision == FexJson.Revision && catalog.Tests.Length == 260 &&
        catalog.Tests.Select(test => test.Id).Distinct(StringComparer.Ordinal).Count() == 260, "Invalid catalog inventory");
    var sourceLock = ReadJson<SourceLock>(lockPath);
    Require(catalog.Tests.Select(test => test.Source).Order(StringComparer.Ordinal).SequenceEqual(
        sourceLock.Files.Where(file => file.Path.EndsWith(".asm", StringComparison.Ordinal)).Select(file => file.Path).Order(StringComparer.Ordinal)),
        "Catalog is not a complete source inventory");
    Require(File.ReadAllBytes(Path.Combine(assets, "LICENSE")).SequenceEqual(File.ReadAllBytes(Path.Combine(upstream, "LICENSE"))), "Asset license differs from upstream");
    foreach (var entry in catalog.Tests)
    {
        Require(entry.Status is "ready" or "blocked" && (entry.Status == "blocked") == !string.IsNullOrWhiteSpace(entry.Reason),
            $"Invalid disposition: {entry.Id}");
        Require(Hash(File.ReadAllBytes(Path.Combine(upstream, entry.Source))) == entry.SourceSha256, $"Catalog source mismatch: {entry.Id}");
        var path = Path.Combine(assets, entry.Manifest);
        Require(Hash(File.ReadAllBytes(path)) == entry.ManifestSha256, $"Manifest hash mismatch: {entry.Id}");
        var manifest = ReadJson<FexManifest>(path);
        Require(manifest.Id == entry.Id && manifest.Revision == FexJson.Revision && manifest.SourceSha256 == entry.SourceSha256,
            $"Manifest provenance mismatch: {entry.Id}");
        var sourceText = File.ReadAllText(Path.Combine(upstream, entry.Source));
        var configMatch = Regex.Match(sourceText, @"(?s)%ifdef CONFIG\s*(.*?)%endif");
        Require(configMatch.Success, $"Missing source CONFIG: {entry.Id}");
        using var sourceConfig = JsonDocument.Parse(configMatch.Groups[1].Value);
        Require(JsonElement.DeepEquals(sourceConfig.RootElement, manifest.Config), $"Manifest CONFIG differs from upstream: {entry.Id}");
        if (manifest.Binary != null)
        {
            var binary = File.ReadAllBytes(Path.Combine(assets, manifest.Binary));
            Require(Hash(binary) == manifest.BinarySha256, $"Binary hash mismatch: {entry.Id}");
            foreach (var halt in manifest.HaltAddresses)
                Require(halt >= manifest.LoadAddress && (ulong)halt - manifest.LoadAddress < (ulong)binary.Length &&
                    binary[halt - manifest.LoadAddress] == 0xF4, $"Invalid listed HLT address: {entry.Id}");
        }
        if (entry.Status == "ready")
        {
            FexConfig.Parse(manifest.Config).RequireRunnable();
            Require(manifest.Binary != null && manifest.HaltAddresses.Length != 0, $"Runnable test has no executable asset: {entry.Id}");
        }
    }
    Console.WriteLine($"Verified catalog, manifests, binary hashes and strict runnable CONFIG for {catalog.Tests.Length} tests.");
}

static string FindRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, "Fiberish.X86"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run from the repository or pass --root REPO");
}

static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), FexJson.Options)
    ?? throw new InvalidDataException($"Empty JSON: {path}");
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void WriteJson<T>(string path, T value) => WriteBytes(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, FexJson.Options) + "\n"));
static void WriteBytes(string path, byte[] bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static async Task<(int ExitCode, string Output)> Run(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await stdout + await stderr);
}

internal sealed record LockedFile(string Path, string Sha256, string Url);
internal sealed record SourceLock(int SchemaVersion, string Repository, string Revision, string License,
    int AssemblyCount, LockedFile[] Files);
