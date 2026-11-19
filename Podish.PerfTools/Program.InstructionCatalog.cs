using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static readonly string[] InstructionCatalogInputs =
    ["instruction_spec.h", "instruction_kernel_spec.h", "instruction_catalog.json", "instruction_variants.cpp", "instruction_variants.h", "specialization.h"];

    internal static string InstructionCatalogSourceHash()
    {
        var text = new StringBuilder();
        foreach (var name in InstructionCatalogInputs)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "libfibercpu", "src", name)).Replace("\r\n", "\n");
            var bytes = Encoding.UTF8.GetBytes(source);
            text.Append(name).Append(':').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static Dictionary<string, string[]> LoadInstructionFusionFamilies(string? path = null)
    {
        path ??= Path.Combine(RepoRoot(), "libfibercpu", "src", "generated", "instruction_fusion_families.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1 ||
            root.GetProperty("source_sha256").GetString() != InstructionCatalogSourceHash())
            throw new InvalidDataException("Instruction catalog snapshot is stale. Build the native library, then run export-instruction-catalog --library <native-library>.");
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var family in root.GetProperty("families").EnumerateObject())
        {
            var choices = family.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (!Regex.IsMatch(family.Name, @"^op::Op[A-Za-z0-9_]+$") || choices.Any(x => !Regex.IsMatch(x, @"^op::Op[A-Za-z0-9_]+$")))
                throw new InvalidDataException("Invalid instruction catalog code generation reference");
            result.Add(family.Name, choices);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeInstructionVariant
    {
        public uint SchemaVersion, Opcode;
        public ulong Key;
        public IntPtr Handler, Logic, Family, Parent;
        public int Priority;
        public uint Bake, Fallback;
        public fixed uint Constraints[19];
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CatalogCount();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CatalogEntry(int index, out NativeInstructionVariant entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CatalogHash();

    private static int RunExportInstructionCatalog(string[] args)
    {
        var library = Path.GetFullPath(RequireValue(args, "--library", 0));
        var output = GetValue(args, "--output") ?? Path.Combine(RepoRoot(), "libfibercpu", "src", "generated", "instruction_fusion_families.json");
        var handle = NativeLibrary.Load(library);
        try
        {
            var count = Marshal.GetDelegateForFunctionPointer<CatalogCount>(NativeLibrary.GetExport(handle, "X86_GetInstructionVariantCount"));
            var entry = Marshal.GetDelegateForFunctionPointer<CatalogEntry>(NativeLibrary.GetExport(handle, "X86_GetInstructionVariant"));
            var hash = Marshal.GetDelegateForFunctionPointer<CatalogHash>(NativeLibrary.GetExport(handle, "X86_GetInstructionCatalogHash"));
            var sourceHash = Marshal.PtrToStringUTF8(hash());
            if (sourceHash != InstructionCatalogSourceHash()) throw new InvalidDataException("Native instruction catalog is stale; rebuild the native library first.");
            var families = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            for (var i = 0; i < count(); ++i)
            {
                if (entry(i, out var variant) == 0 || variant.SchemaVersion != 1) throw new InvalidDataException("Unsupported native instruction catalog");
                if (variant.Bake == 0 || variant.Fallback != 0) continue;
                var parent = Marshal.PtrToStringUTF8(variant.Parent)!;
                var logic = Marshal.PtrToStringUTF8(variant.Logic)!;
                if (!families.TryGetValue(parent, out var values)) families[parent] = values = new(StringComparer.Ordinal);
                values.Add(logic);
            }
            var snapshot = new { schema_version = 1, source_sha256 = sourceHash, families };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8NoBom);
            Console.WriteLine($"Wrote {families.Count} instruction fusion families to {Path.GetFullPath(output)}");
            return 0;
        }
        finally { NativeLibrary.Free(handle); }
    }
}
