using System.Globalization;
using System.Text.Json;

namespace Podish.PerfTools;

internal static partial class Program
{
    private static string OpShortName(string name)
    {
        var shortName = name.Split("::").Last();
        return shortName.StartsWith("Op", StringComparison.Ordinal) ? shortName[2..] : shortName;
    }

    private static OpSemantics InferSemantics(string name)
    {
        var shortName = OpShortName(name);
        var lower = shortName.ToLowerInvariant();
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var controlFlow = false;
        var memorySideEffect = false;

        if (lower.StartsWith("jcc", StringComparison.Ordinal) ||
            lower.StartsWith("jmp", StringComparison.Ordinal) ||
            lower.StartsWith("call", StringComparison.Ordinal) ||
            lower.StartsWith("ret", StringComparison.Ordinal) ||
            lower.StartsWith("loop", StringComparison.Ordinal) ||
            lower.StartsWith("iret", StringComparison.Ordinal) ||
            lower.StartsWith("sys", StringComparison.Ordinal))
            controlFlow = true;

        if (lower.StartsWith("jcc", StringComparison.Ordinal))
        {
            reads.Add("flags");
            notes.Add("conditional branch consumes flags");
        }
        else if (lower.StartsWith("cmov", StringComparison.Ordinal))
        {
            reads.Add("flags");
            reads.Add("gpr");
            writes.Add("gpr");
            notes.Add("cmov consumes flags and forwards a register value");
        }
        else if (lower.StartsWith("setcc", StringComparison.Ordinal))
        {
            reads.Add("flags");
            writes.Add("gpr");
            notes.Add("setcc consumes flags and defines a byte register");
        }

        if (StartsWithAny(lower, "cmp", "test", "bt", "btc", "btr", "bts", "comis", "ucomis"))
        {
            reads.Add("gpr");
            writes.Add("flags");
            notes.Add("compare/test family defines flags");
        }
        else if (StartsWithAny(lower, "adc", "sbb"))
        {
            reads.Add("gpr");
            reads.Add("flags");
            writes.Add("gpr");
            writes.Add("flags");
            notes.Add("adc/sbb both consume and define flags");
        }
        else if (StartsWithAny(lower, "add", "sub", "and", "or", "xor", "inc", "dec", "neg", "shl", "shr", "sar", "sal",
                     "rol", "ror", "shld", "shrd"))
        {
            reads.Add("gpr");
            writes.Add("gpr");
            writes.Add("flags");
            notes.Add("ALU op updates register and flags");
        }

        var isLoad = lower.Contains("load", StringComparison.Ordinal);
        var isStore = lower.Contains("store", StringComparison.Ordinal);

        if (isLoad)
        {
            reads.Add("mem");
            writes.Add("gpr");
            notes.Add("load defines a register from memory");
        }

        if (isStore)
        {
            reads.Add("gpr");
            writes.Add("mem");
            memorySideEffect = true;
            notes.Add("store consumes a register and writes memory");
        }

        if (!isStore && StartsWithAny(lower, "mov", "lea", "movzx", "movsx", "pop"))
            writes.Add("gpr");
        if (!isLoad && StartsWithAny(lower, "mov", "push", "xchg", "cmp", "test"))
            reads.Add("gpr");
        if (lower.StartsWith("push", StringComparison.Ordinal))
        {
            writes.Add("mem");
            memorySideEffect = true;
        }

        if (lower.StartsWith("xchg", StringComparison.Ordinal))
            writes.Add("gpr");
        if (lower.StartsWith("movs", StringComparison.Ordinal))
        {
            reads.Add("gpr");
            reads.Add("mem");
            writes.Add("mem");
            memorySideEffect = true;
        }

        if (StartsWithAny(lower, "cmps", "scas"))
        {
            reads.Add("gpr");
            reads.Add("mem");
            writes.Add("flags");
            notes.Add("string compare defines flags");
        }

        return new OpSemantics(reads, writes, notes, controlFlow, memorySideEffect);
    }

    private static OpSemantics GetOpSemantics(JsonElement opNode, string normalizedName)
    {
        var opId = TryGetNullableInt(opNode, "op_id") ?? TryGetNullableIntFromHex(opNode, "op_id_hex");
        var modrm = opNode.TryGetProperty("modrm", out var modrmNode) && modrmNode.ValueKind == JsonValueKind.Number
            ? modrmNode.GetInt32()
            : 0;
        var meta = opNode.TryGetProperty("meta", out var metaNode) && metaNode.ValueKind == JsonValueKind.Number
            ? metaNode.GetInt32()
            : 0;
        var prefixes =
            opNode.TryGetProperty("prefixes", out var prefixesNode) && prefixesNode.ValueKind == JsonValueKind.Number
                ? prefixesNode.GetInt32()
                : 0;
        var eaDesc = 0;
        if (opNode.TryGetProperty("mem", out var memNode) && memNode.ValueKind == JsonValueKind.Object &&
            memNode.TryGetProperty("ea_desc", out var eaDescNode) && eaDescNode.ValueKind == JsonValueKind.Number)
            eaDesc = eaDescNode.GetInt32();

        var analyzed = AnalyzeDefUse(opId, modrm, meta, prefixes, eaDesc);
        if (analyzed is not null)
            return analyzed;

        if (!opNode.TryGetProperty("def_use", out var defUse) || defUse.ValueKind != JsonValueKind.Object)
            return InferSemantics(normalizedName);

        var reads = new HashSet<string>(ReadStringArray(defUse, "reads_data_gpr"), StringComparer.Ordinal);
        if (reads.Count == 0)
            reads.UnionWith(ReadStringArray(defUse, "reads_gpr"));
        var writes = new HashSet<string>(ReadStringArray(defUse, "writes_gpr"), StringComparer.Ordinal);
        var addrReads = ReadStringArray(defUse, "reads_addr_gpr");

        foreach (var flag in ReadStringArray(defUse, "reads_flags"))
            reads.Add($"flag:{flag}");
        foreach (var flag in ReadStringArray(defUse, "writes_flags"))
            writes.Add($"flag:{flag}");

        if (GetBool(defUse, "reads_memory"))
            reads.Add("mem");
        if (GetBool(defUse, "writes_memory"))
            writes.Add("mem");

        var fallback = InferSemantics(normalizedName);
        var controlFlow = fallback.ControlFlow;
        var memorySideEffect = GetBool(defUse, "writes_memory") || fallback.MemorySideEffect;
        var notes = ReadStringArray(defUse, "notes");
        foreach (var note in fallback.Notes)
            if (!notes.Contains(note, StringComparer.Ordinal))
                notes.Add(note);
        if (addrReads.Count > 0)
            notes.Add($"address regs: {string.Join(", ", addrReads)}");

        if (reads.Count == 0 && writes.Count == 0 && notes.Count == 0 && !memorySideEffect)
            return fallback;

        return new OpSemantics(reads, writes, notes, controlFlow, memorySideEffect);
    }

    private static OpSemantics? AnalyzeDefUse(int? opId, int modrm, int meta, int prefixes, int eaDesc)
    {
        _ = prefixes;
        if (opId is null || opId < 0)
            return null;

        var id = opId.Value;
        var hasModrm = (meta & 0x01) != 0;
        var hasMem = (meta & 0x02) != 0;
        var state = new DefUseState();

        if ((id >= 0x70 && id <= 0x7F) || (id >= 0x180 && id <= 0x18F) || (id >= 0xE0 && id <= 0xE3))
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            state.ControlFlow = true;
            if (id >= 0xE0 && id <= 0xE2)
            {
                state.ReadsGprMask |= RegMask(1);
                state.WritesGprMask |= RegMask(1);
                state.Note("loop family decrements ECX");
            }
            else if (id == 0xE3)
            {
                state.ReadsGprMask |= RegMask(1);
                state.Note("jecxz reads ECX");
            }

            state.Note("conditional control flow consumes flags");
            return state.ToOpSemantics();
        }

        if (id is 0xE9 or 0xEB)
        {
            state.Note("unconditional jump");
            return state.ToOpSemantics();
        }

        if (id >= 0x140 && id <= 0x14F && hasModrm)
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.Note("cmov reads destination because the old value is kept on false predicate");
            return state.ToOpSemantics();
        }

        if (id >= 0x190 && id <= 0x19F && hasModrm)
        {
            state.ReadsFlagsMask |= AllStatusFlagsMask;
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }

        if (id is 0x38 or 0x39 or 0x3A or 0x3B or 0x84 or 0x85 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if (id is 0x89 or 0x200 or 0x201 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }

        if (id is 0x8B or 0x202 or 0x203 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }

        if (id == 0x88 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }

        if (id == 0x8A && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }

        if (id is 0xC6 or 0xC7 && hasModrm)
        {
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "write");
            return state.ToOpSemantics();
        }

        if (id == 0x87 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            return state.ToOpSemantics();
        }

        if (id >= 0x91 && id <= 0x97)
        {
            var reg = id - 0x90;
            state.ReadsGprMask |= RegMask(0, reg);
            state.WritesGprMask |= RegMask(0, reg);
            return state.ToOpSemantics();
        }

        if (id == 0x8D && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            state.ReadsAddrGprMask |= DecodeMemoryAddressRegs(eaDesc);
            state.Note("lea reads effective-address registers without touching memory");
            return state.ToOpSemantics();
        }

        if (id is 0x1B6 or 0x1B7 or 0x1BE or 0x1BF && hasModrm)
        {
            ApplyRegOperand(state, modrm, "write");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            return state.ToOpSemantics();
        }

        if (id is 0x00 or 0x01 or 0x08 or 0x09 or 0x10 or 0x11 or 0x18 or 0x19 or 0x20 or 0x21 or 0x28 or 0x29 or 0x30
                or 0x31 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x10 or 0x11 or 0x18 or 0x19)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if (id is 0x02 or 0x03 or 0x0A or 0x0B or 0x12 or 0x13 or 0x1A or 0x1B or 0x22 or 0x23 or 0x2A or 0x2B or 0x32
                or 0x33 && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x12 or 0x13 or 0x1A or 0x1B)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if (id is 0x80 or 0x81 or 0x83 && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop == 7)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            }
            else
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                if (subop is 2 or 3)
                    state.ReadsFlagsMask |= CfBit;
            }

            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if (id is 0xC0 or 0xC1 or 0xD0 or 0xD1 or 0xD2 or 0xD3 && hasModrm)
        {
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            if (id is 0xD2 or 0xD3)
            {
                state.ReadsGprMask |= RegMask(1);
                state.Note("shift count comes from CL");
            }

            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if (id is 0xF6 or 0xF7 && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop is 0 or 1)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop == 2)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
            }
            else if (subop == 3)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop is 4 or 5)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(0);
                state.WritesGprMask |= RegMask(0, 2);
                state.WritesFlagsMask |= AllStatusFlagsMask;
            }
            else if (subop is 6 or 7)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(0, 2);
                state.WritesGprMask |= RegMask(0, 2);
            }

            return state.ToOpSemantics();
        }

        if (id is 0x1A3 or 0x1AB or 0x1B3 or 0x1BB && hasModrm)
        {
            ApplyRegOperand(state, modrm, "read");
            var rmRole = id is 0x1AB or 0x1B3 or 0x1BB ? "readwrite" : "read";
            ApplyRmOperand(state, modrm, hasMem, eaDesc, rmRole);
            state.WritesFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if (id == 0x1AF && hasModrm)
        {
            ApplyRegOperand(state, modrm, "readwrite");
            ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            state.WritesFlagsMask |= CfBit | OfBit;
            return state.ToOpSemantics();
        }

        if (id >= 0x50 && id <= 0x57)
        {
            state.ReadsGprMask |= RegMask(id - 0x50, 4);
            state.WritesGprMask |= RegMask(4);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }

        if (id >= 0x58 && id <= 0x5F)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(id - 0x58, 4);
            state.ReadsMemory = true;
            return state.ToOpSemantics();
        }

        if (id is 0xE8 or 0xC2 or 0xC3)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(4);
            state.ReadsMemory = id is 0xC2 or 0xC3;
            state.WritesMemory = id == 0xE8;
            return state.ToOpSemantics();
        }

        if (id == 0x6A)
        {
            state.ReadsGprMask |= RegMask(4);
            state.WritesGprMask |= RegMask(4);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }

        if (id == 0xFF && hasModrm)
        {
            var subop = (modrm >> 3) & 7;
            if (subop is 0 or 1)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "readwrite");
                state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            }
            else if (subop == 2)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(4);
                state.WritesGprMask |= RegMask(4);
                state.WritesMemory = true;
            }
            else if (subop == 4)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
            }
            else if (subop == 6)
            {
                ApplyRmOperand(state, modrm, hasMem, eaDesc, "read");
                state.ReadsGprMask |= RegMask(4);
                state.WritesGprMask |= RegMask(4);
                state.WritesMemory = true;
            }

            return state.ToOpSemantics();
        }

        if (id >= 0xB8 && id <= 0xBF)
        {
            state.WritesGprMask |= RegMask(id - 0xB8);
            return state.ToOpSemantics();
        }

        if (id >= 0xB0 && id <= 0xB7)
        {
            state.WritesGprMask |= RegMask(id - 0xB0);
            return state.ToOpSemantics();
        }

        if (id is 0x05 or 0x0D or 0x15 or 0x1D or 0x25 or 0x2D or 0x35 or 0x3D or 0xA9)
        {
            state.ReadsGprMask |= RegMask(0);
            if (id is not 0x3D and not 0xA9)
                state.WritesGprMask |= RegMask(0);
            state.WritesFlagsMask |= AllStatusFlagsMask;
            if (id is 0x15 or 0x1D)
                state.ReadsFlagsMask |= CfBit;
            return state.ToOpSemantics();
        }

        if (id is 0x3C or 0xA8)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesFlagsMask |= AllStatusFlagsMask;
            return state.ToOpSemantics();
        }

        if (id >= 0x40 && id <= 0x47)
        {
            var reg = id - 0x40;
            state.ReadsGprMask |= RegMask(reg);
            state.WritesGprMask |= RegMask(reg);
            state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            return state.ToOpSemantics();
        }

        if (id >= 0x48 && id <= 0x4F)
        {
            var reg = id - 0x48;
            state.ReadsGprMask |= RegMask(reg);
            state.WritesGprMask |= RegMask(reg);
            state.WritesFlagsMask |= AllStatusFlagsMask & ~CfBit;
            return state.ToOpSemantics();
        }

        if (id == 0x99)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesGprMask |= RegMask(2);
            return state.ToOpSemantics();
        }

        if (id == 0xFC)
        {
            state.ReadsFlagsMask &= 0;
            state.WritesFlagsMask |= 1u << 10;
            state.Note("cld clears direction flag");
            return state.ToOpSemantics();
        }

        if (id is 0xA0 or 0xA1)
        {
            state.WritesGprMask |= RegMask(0);
            state.ReadsMemory = true;
            return state.ToOpSemantics();
        }

        if (id is 0xA2 or 0xA3)
        {
            state.ReadsGprMask |= RegMask(0);
            state.WritesMemory = true;
            return state.ToOpSemantics();
        }

        return null;
    }

    private static uint RegMask(params int[] regs)
    {
        var mask = 0u;
        foreach (var reg in regs)
            if (reg >= 0 && reg < GprNames.Length)
                mask |= 1u << reg;
        return mask;
    }

    private static uint DecodeMemoryAddressRegs(int eaDesc)
    {
        var mask = 0u;
        var baseOffset = eaDesc & 0x3F;
        var indexOffset = (eaDesc >> 6) & 0x3F;

        if (baseOffset != 32)
            mask |= 1u << (baseOffset / 4);
        if (indexOffset != 32)
            mask |= 1u << (indexOffset / 4);
        return mask;
    }

    private static void ApplyRmOperand(DefUseState state, int modrm, bool hasMem, int eaDesc, string role)
    {
        if (role == "none")
            return;

        if (hasMem)
        {
            state.ReadsAddrGprMask |= DecodeMemoryAddressRegs(eaDesc);
            if (role is "read" or "readwrite")
                state.ReadsMemory = true;
            if (role is "write" or "readwrite")
                state.WritesMemory = true;
            return;
        }

        var rm = modrm & 7;
        if (role is "read" or "readwrite")
            state.ReadsGprMask |= RegMask(rm);
        if (role is "write" or "readwrite")
            state.WritesGprMask |= RegMask(rm);
    }

    private static void ApplyRegOperand(DefUseState state, int modrm, string role)
    {
        if (role == "none")
            return;

        var reg = (modrm >> 3) & 7;
        if (role is "read" or "readwrite")
            state.ReadsGprMask |= RegMask(reg);
        if (role is "write" or "readwrite")
            state.WritesGprMask |= RegMask(reg);
    }

    private static List<string> MaskToRegNames(uint mask)
    {
        var names = new List<string>();
        for (var i = 0; i < GprNames.Length; i++)
            if ((mask & (1u << i)) != 0)
                names.Add(GprNames[i]);
        return names;
    }

    private static List<string> MaskToFlagNames(uint mask)
    {
        var flags = new List<string>();
        if ((mask & CfBit) != 0) flags.Add("cf");
        if ((mask & PfBit) != 0) flags.Add("pf");
        if ((mask & AfBit) != 0) flags.Add("af");
        if ((mask & ZfBit) != 0) flags.Add("zf");
        if ((mask & SfBit) != 0) flags.Add("sf");
        if ((mask & OfBit) != 0) flags.Add("of");
        return flags;
    }

    private static int? TryGetNullableInt(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value))
            return value;
        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value))
            return value;
        return null;
    }

    private static int? TryGetNullableIntFromHex(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return null;
        var text = node.GetString();
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("0x", StringComparison.Ordinal))
            return null;
        return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static PairRelation? ClassifyPair(string firstName, string secondName, JsonElement firstOp,
        JsonElement secondOp)
    {
        var firstInfo = GetOpSemantics(firstOp, firstName);
        var secondInfo = GetOpSemantics(secondOp, secondName);
        var sharedRaw = firstInfo.Writes.Intersect(secondInfo.Reads, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sharedRar = firstInfo.Reads.Intersect(secondInfo.Reads, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sharedWaw = firstInfo.Writes.Intersect(secondInfo.Writes, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (sharedRaw.Length == 0 && sharedRar.Length == 0 && sharedWaw.Length == 0)
            return null;

        string relationKind;
        string[] sharedResources;
        var depWeight = 1;
        if (sharedRaw.Length > 0)
        {
            relationKind = "RAW";
            sharedResources = sharedRaw;
            depWeight = 2;
        }
        else if (sharedRar.Length > 0 && sharedWaw.Length > 0)
        {
            relationKind = "RAR/WAW";
            sharedResources = sharedRar.Concat(sharedWaw).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        else if (sharedRar.Length > 0)
        {
            relationKind = "RAR";
            sharedResources = sharedRar;
        }
        else
        {
            relationKind = "WAW";
            sharedResources = sharedWaw;
        }

        var legalityNotes = new List<string>();
        if (secondInfo.ControlFlow)
            legalityNotes.Add("second op is control-flow and needs strict mid-exit handling");
        if (firstInfo.MemorySideEffect || secondInfo.MemorySideEffect)
            legalityNotes.Add("pair touches memory side effects and should keep restart semantics conservative");
        if (!string.Equals(relationKind, "RAW", StringComparison.Ordinal))
            legalityNotes.Add(
                "pair is non-RAW and may only be profitable when repeated reads or write coalescing can be shared");

        return new PairRelation(relationKind, depWeight, sharedResources, firstInfo, secondInfo,
            legalityNotes.ToArray());
    }

    private static bool IsJccPair(IEnumerable<string> pair)
    {
        return pair.Any(name => OpShortName(name).StartsWith("Jcc", StringComparison.Ordinal));
    }

    private static int RelationDepWeight(string relationKind, int rawWeight, int rarWeight, int wawWeight)
    {
        return relationKind switch
        {
            "RAW" => rawWeight,
            "RAR" => rarWeight,
            "WAW" => wawWeight,
            _ => Math.Max(rarWeight, wawWeight)
        };
    }

    private static int RelationJccWeight(IEnumerable<string> pair, string relationKind, int jccMultiplier,
        string jccMode)
    {
        if (jccMultiplier <= 1 || !IsJccPair(pair))
            return 1;
        if (string.Equals(jccMode, "none", StringComparison.Ordinal))
            return 1;
        if (string.Equals(jccMode, "raw-only", StringComparison.Ordinal) &&
            !string.Equals(relationKind, "RAW", StringComparison.Ordinal))
            return 1;
        return jccMultiplier;
    }

    private static bool StartsWithAny(string text, params string[] prefixes)
    {
        return prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static List<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var values = new List<string>();
        foreach (var item in node.EnumerateArray())
        {
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        return values;
    }

    private static bool GetBool(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var node) &&
               node.ValueKind is JsonValueKind.True or JsonValueKind.False && node.GetBoolean();
    }

    private sealed class DefUseState
    {
        public uint ReadsGprMask { get; set; }
        public uint ReadsAddrGprMask { get; set; }
        public uint WritesGprMask { get; set; }
        public uint ReadsFlagsMask { get; set; }
        public uint WritesFlagsMask { get; set; }
        public bool ReadsMemory { get; set; }
        public bool WritesMemory { get; set; }
        public bool ControlFlow { get; set; }
        public List<string>? Notes { get; set; }

        public void Note(string message)
        {
            Notes ??= new List<string>();
            Notes.Add(message);
        }

        public OpSemantics? ToOpSemantics()
        {
            var combinedReads = ReadsGprMask | ReadsAddrGprMask;
            var reads = new HashSet<string>(MaskToRegNames(combinedReads), StringComparer.Ordinal);
            var writes = new HashSet<string>(MaskToRegNames(WritesGprMask), StringComparer.Ordinal);
            foreach (var flag in MaskToFlagNames(ReadsFlagsMask))
                reads.Add($"flag:{flag}");
            foreach (var flag in MaskToFlagNames(WritesFlagsMask))
                writes.Add($"flag:{flag}");
            if (ReadsMemory)
                reads.Add("mem");
            if (WritesMemory)
                writes.Add("mem");

            var notes = Notes ?? new List<string>();
            if (reads.Count == 0 && writes.Count == 0 && notes.Count == 0 && !WritesMemory && !ControlFlow)
                return null;

            var outputNotes = new List<string>(notes);
            return new OpSemantics(reads, writes, outputNotes, ControlFlow, WritesMemory);
        }
    }
}