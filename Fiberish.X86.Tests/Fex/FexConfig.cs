using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fiberish.X86.Tests.Fex;

// Shared with the offline importer. CONFIG describes expected OUTPUT, never initial registers.
internal sealed class FexConfig
{
    public Dictionary<string, ulong[]> Registers { get; } = new(StringComparer.Ordinal);
    public Dictionary<uint, uint> MemoryRegions { get; } = new();
    public Dictionary<uint, byte[]> MemoryData { get; } = new();
    public List<string> HostFeatures { get; } = new();
    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public static FexConfig Parse(JsonElement json)
    {
        Require(json.ValueKind == JsonValueKind.Object, "CONFIG must be an object");
        var result = new FexConfig();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
        {
            Require(seen.Add(property.Name), $"Duplicate CONFIG key: {property.Name}");
            switch (property.Name)
            {
                case "Mode":
                    Require(property.Value.GetString() == "32BIT", "Only Mode=32BIT is supported");
                    break;
                case "RegData":
                    foreach (var register in property.Value.EnumerateObject())
                    {
                        var values = register.Value.ValueKind == JsonValueKind.Array
                            ? register.Value.EnumerateArray().Select(Number).ToArray()
                            : new[] { Number(register.Value) };
                        if (Regex.IsMatch(register.Name, "^R(AX|BX|CX|DX|SP|BP|SI|DI)$"))
                            Require(values.Length == 1 && values[0] <= uint.MaxValue,
                                $"Invalid 32-bit GPR expectation: {register.Name}");
                        else if (Regex.IsMatch(register.Name, "^MM[0-7]$"))
                            Require(values.Length is 1 or 2 && (values.Length == 1 || values[1] <= ushort.MaxValue),
                                $"MM expectation must be 64-bit MMX or physical 80-bit x87: {register.Name}");
                        else if (Regex.IsMatch(register.Name, "^XMM[0-7]$"))
                            Require(values.Length is 2 or 4, $"Invalid vector expectation: {register.Name}");
                        else
                            throw new InvalidDataException($"Unsupported RegData register: {register.Name}");
                        Require(result.Registers.TryAdd(register.Name, values), $"Duplicate register: {register.Name}");
                    }
                    break;
                case "MemoryRegions":
                    foreach (var region in property.Value.EnumerateObject())
                    {
                        var address = checked((uint)Number(region.Name));
                        var size = checked((uint)Number(region.Value));
                        Require(size != 0 && (address & 4095) == 0 && (size & 4095) == 0 &&
                            (ulong)address + size <= 0x1_0000_0000UL, "MemoryRegions must be nonempty, page-aligned 32-bit ranges");
                        Require(result.MemoryRegions.TryAdd(address, size), "Duplicate memory region");
                    }
                    break;
                case "MemoryData":
                    foreach (var data in property.Value.EnumerateObject())
                    {
                        var address = checked((uint)Number(data.Name));
                        var bytes = MemoryBytes(data.Value.GetString()!);
                        Require((ulong)address + (ulong)bytes.Length <= 0x1_0000_0000UL, "MemoryData overflows address space");
                        Require(result.MemoryData.TryAdd(address, bytes), "Duplicate memory initializer");
                    }
                    break;
                case "HostFeatures":
                    foreach (var feature in property.Value.EnumerateArray())
                        result.HostFeatures.Add(feature.GetString() ?? throw new InvalidDataException("Null host feature"));
                    break;
                case "Env":
                    foreach (var variable in property.Value.EnumerateObject())
                        Require(result.Environment.TryAdd(variable.Name, variable.Value.GetString()!), "Duplicate environment option");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported CONFIG key: {property.Name}");
            }
        }
        Require(seen.Contains("Mode"), "CONFIG must explicitly declare Mode");
        return result;
    }

    public void RequireRunnable()
    {
        Require(HostFeatures.Count == 0, $"Unsupported declared HostFeatures: {string.Join(", ", HostFeatures)}");
        Require(Environment.Count == 0, $"Unsupported FEX-specific Env settings: {string.Join(", ", Environment.Keys)}");
        foreach (var (register, values) in Registers)
            Require(!register.StartsWith("XMM", StringComparison.Ordinal) || values.Length == 2,
                $"256-bit vector state is unavailable: {register}");
    }

    public static ulong Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? Number(value.GetString()!)
        : throw new InvalidDataException("FEX numeric values must be strings");

    public static ulong Number(string value)
    {
        value = value.Replace("_", "", StringComparison.Ordinal);
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    // FEX json_config_parse.py: each space-delimited hexadecimal word is emitted low byte first.
    public static byte[] MemoryBytes(string text)
    {
        var bytes = new List<byte>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = word.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? word[2..] : word;
            Require(digits.Length != 0 && digits.All(Uri.IsHexDigit), "Malformed MemoryData hex string");
            for (var end = digits.Length; end > 0; end -= 2)
                bytes.Add(byte.Parse(digits.AsSpan(Math.Max(0, end - 2), Math.Min(2, end)),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }
        Require(bytes.Count != 0, "MemoryData must not be empty");
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
