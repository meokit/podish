using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

public sealed class GuestAddressSpaceLayout
{
    public const uint CompatStackRandomMaskPages = 0x7FF;
    public const uint CompatMmapRandomMaskPages = 0x7FF;
    public const uint DefaultStackGuardGap = 256u * LinuxConstants.PageSize;
    public const uint MinimumStackGap = 128u * 1024 * 1024;
    private const uint PieBaseOffset = 0x01000000;
    private const uint InterpreterGap = 0x02000000;
    private const uint ReservedTopPages = 2;

    public required uint TaskSize { get; init; }
    public required uint StackTopMax { get; init; }
    public required uint InitialStackTop { get; init; }
    public required uint StackLowerBound { get; init; }
    public required uint MmapBase { get; init; }
    public required uint LegacyMmapBase { get; init; }
    public required uint PieBase { get; init; }
    public required uint InterpreterBaseHint { get; init; }
    public required uint VdsoBaseHint { get; init; }
    public required uint StackRandomOffset { get; init; }
    public required uint MmapRandomOffset { get; init; }
    public required uint StackGuardGap { get; init; }
    public required byte[] AuxRandomBytes { get; init; }

    public GuestAddressSpaceLayout Clone()
    {
        return new GuestAddressSpaceLayout
        {
            TaskSize = TaskSize,
            StackTopMax = StackTopMax,
            InitialStackTop = InitialStackTop,
            StackLowerBound = StackLowerBound,
            MmapBase = MmapBase,
            LegacyMmapBase = LegacyMmapBase,
            PieBase = PieBase,
            InterpreterBaseHint = InterpreterBaseHint,
            VdsoBaseHint = VdsoBaseHint,
            StackRandomOffset = StackRandomOffset,
            MmapRandomOffset = MmapRandomOffset,
            StackGuardGap = StackGuardGap,
            AuxRandomBytes = AuxRandomBytes.ToArray()
        };
    }

    public static GuestAddressSpaceLayout CreateCompat32(ResourceLimit stackLimit, ReadOnlySpan<byte> randomBytes)
    {
        if (randomBytes.Length < 24)
            throw new ArgumentException("Compat layout requires at least 24 bytes of exec-scoped randomness.",
                nameof(randomBytes));

        var taskSize = LinuxConstants.TaskSize32;
        var stackTopMax = AlignDown(taskSize - ReservedTopPages * LinuxConstants.PageSize);
        var vdsoBaseHint = AlignDown(taskSize - LinuxConstants.PageSize);
        var stackGuardGap = DefaultStackGuardGap;

        var stackRandomPages = BinaryPrimitives.ReadUInt32LittleEndian(randomBytes[16..20]) &
                               CompatStackRandomMaskPages;
        var mmapRandomPages = BinaryPrimitives.ReadUInt32LittleEndian(randomBytes[20..24]) &
                              CompatMmapRandomMaskPages;
        var stackRandomOffset = stackRandomPages * LinuxConstants.PageSize;
        var mmapRandomOffset = mmapRandomPages * LinuxConstants.PageSize;

        var effectiveStackLimit = ClampStackLimit(stackLimit.Soft, stackTopMax);
        var initialStackTop = AlignDown(stackTopMax - stackRandomOffset);
        var stackFloor = initialStackTop > effectiveStackLimit ? initialStackTop - effectiveStackLimit : 0;
        var stackLowerBound = Math.Max(LinuxConstants.MinMmapAddr, AlignDown(stackFloor));

        var gapTarget = SaturatingAdd(effectiveStackLimit, stackGuardGap);
        gapTarget = SaturatingAdd(gapTarget,
            (CompatStackRandomMaskPages + 1) * LinuxConstants.PageSize);
        var gapMax = ((ulong)taskSize / 6) * 5;
        var gap = Clamp(gapTarget, MinimumStackGap, gapMax);
        var mmapBase = AlignDown(taskSize - (uint)Math.Min(gap, taskSize - LinuxConstants.MinMmapAddr));
        mmapBase = mmapBase > mmapRandomOffset ? AlignDown(mmapBase - mmapRandomOffset) : LinuxConstants.MinMmapAddr;

        var legacyMmapBase = AlignUp(taskSize / 3);
        var pieBase = AlignDown(legacyMmapBase + PieBaseOffset);
        var interpreterBaseHint = AlignDown((uint)Math.Max((ulong)(pieBase + InterpreterGap),
            mmapBase > InterpreterGap ? (ulong)(mmapBase - InterpreterGap) : (ulong)pieBase));
        if (interpreterBaseHint >= vdsoBaseHint)
            interpreterBaseHint = AlignDown(vdsoBaseHint - InterpreterGap);
        if (interpreterBaseHint <= pieBase)
            interpreterBaseHint = AlignDown(pieBase + LinuxConstants.PageSize);

        return new GuestAddressSpaceLayout
        {
            TaskSize = taskSize,
            StackTopMax = stackTopMax,
            InitialStackTop = initialStackTop,
            StackLowerBound = stackLowerBound,
            MmapBase = Math.Max(mmapBase, LinuxConstants.MinMmapAddr),
            LegacyMmapBase = legacyMmapBase,
            PieBase = pieBase,
            InterpreterBaseHint = interpreterBaseHint,
            VdsoBaseHint = vdsoBaseHint,
            StackRandomOffset = stackRandomOffset,
            MmapRandomOffset = mmapRandomOffset,
            StackGuardGap = stackGuardGap,
            AuxRandomBytes = randomBytes[..16].ToArray()
        };
    }

    private static uint ClampStackLimit(ulong requestedLimit, uint stackTopMax)
    {
        if (requestedLimit == 0)
            return LinuxConstants.PageSize;

        var maxStackSpan = (ulong)stackTopMax - LinuxConstants.MinMmapAddr;
        if (requestedLimit == LinuxConstants.RLIM64_INFINITY || requestedLimit > maxStackSpan)
            requestedLimit = maxStackSpan;

        return AlignUp((uint)requestedLimit);
    }

    private static ulong SaturatingAdd(ulong left, uint right)
    {
        var sum = left + right;
        return sum < left ? ulong.MaxValue : sum;
    }

    private static ulong Clamp(ulong value, uint min, ulong max)
    {
        if (value < min)
            return min;
        return value > max ? max : value;
    }

    private static uint AlignUp(uint value)
    {
        return (value + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
    }

    private static uint AlignDown(uint value)
    {
        return value & LinuxConstants.PageMask;
    }
}
