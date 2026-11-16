using System.Buffers.Binary;
using Fiberish.X86.Native;
using Fiberish.X86.Tests.Harness;
using Iced.Intel;
using Xunit;

namespace Fiberish.X86.Tests.Fex;

internal static class FexRunner
{
    public static void Execute(string id)
    {
        var entry = FexAssets.ReadCatalog().Tests.Single(test => test.Id == id);
        Assert.True(entry.Status == "ready", $"{id}: blocked catalog entries must not be executed: {entry.Reason}");
        var manifest = FexAssets.ReadManifest(entry);
        var config = FexConfig.Parse(manifest.Config);
        config.RequireRunnable(); // Never silently ignore a newly added CONFIG field/host feature.
        var binary = FexAssets.ReadBinary(manifest);
        Assert.NotEmpty(manifest.HaltAddresses);

        using var cpu = new CpuSandbox(mapDefaults: false);
        var pages = MapMemory(cpu, manifest, binary.Length, config);
        cpu.WriteMemory(manifest.LoadAddress, binary);
        foreach (var (address, bytes) in config.MemoryData)
        {
            Assert.True(IsMapped(pages, address, bytes.Length), $"{id}: MemoryData is outside declared/default mappings at {address:X8}");
            cpu.WriteMemory(address, bytes);
        }
        // This is sandbox stack setup, not RegData initialization. The precompiled FEX prelude sets EAX=0x17.
        cpu[Reg.ESP] = 0xC0001000;
        cpu.Eip = manifest.EntryPoint;

        for (ulong count = 0; count < manifest.InstructionLimit; count++)
        {
            var ip = cpu.Eip;
            var length = 0;
            while (length < 15 && (ulong)ip + (uint)length <= uint.MaxValue &&
                pages.Contains((ip + (uint)length) & 0xFFFFF000)) length++;
            Assert.True(length != 0, $"{id}: execution escaped mapped memory at EIP={ip:X8} after {count} instructions");
            var decoder = Decoder.Create(32, new ByteArrayCodeReader(cpu.ReadMemory(ip, length)));
            decoder.IP = ip;
            decoder.Decode(out var instruction);
            Assert.True(instruction.Code != Code.INVALID, $"{id}: invalid instruction at EIP={ip:X8}, after {count} instructions");
            if (instruction.Code == Code.Hlt)
            {
                // A real decoded instruction boundary AND an upstream source-level NASM listing record.
                // F4 in immediate/data bytes and arbitrary #GP faults are never termination signals.
                if (!manifest.HaltAddresses.Contains(ip))
                {
                    var image = Assert.Single(manifest.DynamicCode, candidate => candidate.HaltAddress == ip);
                    var expectedCode = Convert.FromHexString(image.Bytes);
                    Assert.True(cpu.ReadMemory(image.Address, expectedCode.Length).SequenceEqual(expectedCode),
                        $"{id}: dynamic HLT at {ip:X8} does not match its declared source-generated code image");
                }
                Assert.True(cpu.Status == EmuStatus.Running, $"{id}: CPU was not running before HLT: {cpu.Status}");
                AssertOutput(cpu, config, id, count, ip);
                return;
            }

            var status = cpu.Step();
            Assert.True(status == EmuStatus.Running,
                $"{id}: {instruction} at EIP={ip:X8}, instruction #{count}, returned {status}; " +
                $"fault vector={cpu.FaultVector}, next EIP={cpu.Eip:X8}. Only a validated HLT may finish the test.");
        }
        Assert.Fail($"{id}: exceeded {manifest.InstructionLimit} instructions without reaching a source-listed HLT (EIP={cpu.Eip:X8})");
    }

    private static HashSet<uint> MapMemory(CpuSandbox cpu, FexManifest manifest, int binaryLength, FexConfig config)
    {
        var pages = new HashSet<uint>();
        void Map(uint address, uint size)
        {
            Assert.True((ulong)address + size <= 0x1_0000_0000UL, "Mapping exceeds 32-bit address space");
            for (ulong page = address & 0xFFFFF000; page < (ulong)address + size; page += 4096)
                if (pages.Add((uint)page)) cpu.MapMemory((uint)page, 4096, 7);
        }

        // Pinned FEX HarnessHelpers.h:493-537, with a fixed 4 KiB guest page size on every host.
        // FEX maps all test ranges RWX, including the generated-code call regression at 0x80000000.
        Map(manifest.LoadAddress, checked((uint)binaryLength));
        Map(0xE0000000, 10 * 4096);
        Map(0xE7FFF000, 2 * 4096);
        Map(0xC0000000, 4096);
        foreach (var (address, size) in config.MemoryRegions) Map(address, size);
        return pages;
    }

    private static bool IsMapped(HashSet<uint> pages, uint address, int count)
    {
        var end = (ulong)address + (ulong)count;
        if (end > 0x1_0000_0000UL) return false;
        for (ulong page = address & 0xFFFFF000; page < end; page += 4096)
            if (!pages.Contains((uint)page)) return false;
        return true;
    }

    private static void AssertOutput(CpuSandbox cpu, FexConfig config, string id, ulong count, uint halt)
    {
        foreach (var (register, expected) in config.Registers)
        {
            var context = $"{id}: {register} at HLT={halt:X8}, after {count} instructions";
            if (register.StartsWith('R'))
            {
                var native = register switch
                {
                    "RAX" => Reg.EAX, "RBX" => Reg.EBX, "RCX" => Reg.ECX, "RDX" => Reg.EDX,
                    "RSP" => Reg.ESP, "RBP" => Reg.EBP, "RSI" => Reg.ESI, "RDI" => Reg.EDI,
                    _ => throw new InvalidDataException($"Unhandled output register: {register}")
                };
                var actual = cpu[native];
                Assert.True(actual == expected[0], $"{context}: expected {expected[0]:X8}, actual {actual:X8}");
            }
            else if (register.StartsWith("MM", StringComparison.Ordinal))
            {
                var physical = register[2] - '0';
                var top = (cpu.Fsw >> 11) & 7;
                var logical = (physical - top + 8) & 7;
                var actual = cpu.ReadFpu(logical);
                var significand = BinaryPrimitives.ReadUInt64LittleEndian(actual);
                Assert.True(significand == expected[0],
                    $"{context} (physical {physical}, ST({logical}), TOP={top}): expected significand {expected[0]:X16}, actual {significand:X16}");
                if (expected.Length == 2)
                {
                    var signExponent = BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(8));
                    Assert.True(signExponent == expected[1],
                        $"{context}: expected sign/exponent {expected[1]:X4}, actual {signExponent:X4}");
                }
            }
            else if (register.StartsWith("XMM", StringComparison.Ordinal))
            {
                var actual = cpu.ReadXmm(register[3] - '0');
                for (var lane = 0; lane < 2; lane++)
                {
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(actual.AsSpan(lane * 8));
                    Assert.True(value == expected[lane], $"{context}, qword {lane}: expected {expected[lane]:X16}, actual {value:X16}");
                }
            }
            else throw new InvalidDataException($"Unhandled output register: {register}");
        }
    }
}
