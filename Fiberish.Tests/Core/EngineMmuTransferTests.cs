using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class EngineMmuTransferTests
{
    private const uint CodeAddr = 0x00401000;
    private const uint CrossPageCodeAddr = CodeAddr + LinuxConstants.PageSize - 1;
    private const uint BudgetTestCodeBaseAddr = 0x00500000;
    private static readonly byte[] SimpleCode = [0x90, 0x90];

    [Fact]
    public void DetachThenAttach_RestoresMappings_AndConsumesHandle()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        const uint addr = 0x00400000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        Marshal.WriteByte(page, 0x5A);

        var beforeIdentity = engine.CurrentMmuIdentity;
        var detached = engine.DetachMmu();
        Assert.NotEqual(beforeIdentity, engine.CurrentMmuIdentity);

        var read = new byte[1];
        Assert.False(engine.CopyFromUser(addr, read));

        engine.ReplaceMmu(detached);
        Assert.Equal(beforeIdentity, engine.CurrentMmuIdentity);

        Assert.True(engine.CopyFromUser(addr, read));
        Assert.Equal((byte)0x5A, read[0]);
        detached.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.ReplaceMmu(detached));
    }

    [Fact]
    public void ShareMmuFrom_UsesSameMmuIdentity()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var sharedId = parent.CurrentMmuIdentity;
        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));

        using (var child = runtime.CreateEngine())
        {
            child.ShareMmuFrom(parent);
            Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);
            Assert.Equal(2, Engine.GetAttachmentCount(sharedId));
        }

        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));
    }

    [Fact]
    public void SharedClone_ReusesSharedCodeCache()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        var parentStats = ReadCodeCacheStats(parent);
        Assert.True(parent.GetBlockCount() > 0);
        Assert.True(parentStats.BlockCacheSize > 0);

        using var child = parent.Clone(true);
        Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);
        Assert.Equal(parent.GetBlockCount(), child.GetBlockCount());
        Assert.Equal(parentStats.BlockCacheSize, ReadCodeCacheStats(child).BlockCacheSize);

        WarmSimpleCode(child);

        Assert.Equal(parent.GetBlockCount(), child.GetBlockCount());
        Assert.Equal(parentStats.BlockCacheSize, ReadCodeCacheStats(parent).BlockCacheSize);
    }

    [Fact]
    public void SharedClone_ResetAllCodeCache_InvalidatesSharedCore()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);

        child.ResetAllCodeCache();

        var parentStats = ReadCodeCacheStats(parent);
        var childStats = ReadCodeCacheStats(child);
        Assert.Equal(0, parentStats.BlockCacheSize);
        Assert.Equal(0, childStats.BlockCacheSize);
        Assert.Equal(0, parentStats.AllBlocksCount);
        Assert.Equal(0ul, parentStats.CodeCacheBytesRequested);
        Assert.Equal(parentStats.CodeCacheGeneration, childStats.CodeCacheGeneration);
    }

    [Fact]
    public void SharedClone_ResetAllCodeCache_ForcesParentToRehydrateBlocks()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);

        // Prime the engine-local lookup cache before clearing the shared block map.
        WarmSimpleCode(parent);
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);

        child.ResetAllCodeCache();
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);

        WarmSimpleCode(parent);

        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);
    }

    [Fact]
    public void ExternalAlias_ResetAllCodeCache_SelfHealsOnFirstWrite()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00408000;
        const uint rxAddr = 0x00409000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.ResetAllCodeCache();

        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.True(engine.HasSlowWrite(rwAddr));

        Assert.True(engine.CopyToUser(rwAddr, DecEaxTwice()));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.False(engine.HasSlowWrite(rwAddr));

        Assert.True(engine.CopyToUser(rwAddr, IncEaxTwice()));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.False(engine.HasSlowWrite(rwAddr));

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(engine.HasSlowWrite(rwAddr));
    }

    [Fact]
    public void ExternalAlias_ResetAllCodeCache_CrossPageWriteSelfHealsAllTouchedPages()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePages = new UnmanagedPageArrayFixture(2, static i => CrossPageIncEaxTwicePage(i));

        const uint rwAddr = 0x0040A000;
        const uint rxAddr = 0x0040C000;
        var codeAddr = rxAddr + LinuxConstants.PageSize - 1;
        for (var i = 0; i < codePages.Count; i++)
        {
            var pageAddr = (uint)(i * LinuxConstants.PageSize);
            MapExternalPage(engine, rwAddr + pageAddr, codePages[i], Protection.Read | Protection.Write);
            MapExternalPage(engine, rxAddr + pageAddr, codePages[i], Protection.Read | Protection.Exec);
        }

        RunPair(engine, codeAddr, 10, 12);
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));
        Assert.True(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

        engine.ResetAllCodeCache();

        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.True(engine.HasSlowWrite(rwAddr));
        Assert.True(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

        Assert.True(engine.CopyToUser(rwAddr + LinuxConstants.PageSize - 1, new byte[] { 0x48, 0x48 }));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.False(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

        RunPair(engine, codeAddr, 10, 8);
        Assert.True(engine.HasSlowWrite(rwAddr));
        Assert.True(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));
    }

    [Fact]
    public void CodeCacheBudgetFlush_LogsWarning_AndResetsRequestedBytes()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var warnings = new List<string>();
        engine.LogHandler = (_, level, message) =>
        {
            if (level >= 3 && message.Contains("CodeCacheBudgetFlush", StringComparison.Ordinal))
                warnings.Add(message);
        };

        engine.SetCodeCacheBudgetBytesForTesting(256);
        WarmUniqueSimpleBlocks(engine, BudgetTestCodeBaseAddr, 16);

        var stats = ReadCodeCacheStats(engine);
        Assert.True(stats.CodeCacheFlushCount > 0);
        Assert.True(stats.CodeCacheBytesRequested > 0);
        Assert.True(stats.CodeCacheBytesRequested <= stats.CodeCacheLimitBytes,
            $"requested={stats.CodeCacheBytesRequested} limit={stats.CodeCacheLimitBytes}");
        Assert.NotEmpty(warnings);
        Assert.Contains("budget=256", warnings[0], StringComparison.Ordinal);
        Assert.Contains("guest_eip=", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAlias_CodeCacheBudgetFlush_SelfHealsOnFirstWrite()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x0040E000;
        const uint rxAddr = 0x0040F000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.SetCodeCacheBudgetBytesForTesting(256);
        WarmUniqueSimpleBlocks(engine, BudgetTestCodeBaseAddr, 16);

        Assert.True(ReadCodeCacheStats(engine).CodeCacheFlushCount > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.SetCodeCacheBudgetBytesForTesting(0);

        Assert.True(engine.CopyToUser(rwAddr, DecEaxTwice()));
        Assert.False(engine.HasSlowWrite(rwAddr));

        Assert.True(engine.CopyToUser(rwAddr, IncEaxTwice()));
        Assert.False(engine.HasSlowWrite(rwAddr));

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(engine.HasSlowWrite(rwAddr));
    }

    [Fact]
    public void SharedClone_CodeCacheBudgetFlush_ClearsSiblingLookupCacheAndRehydrates()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        parent.SetCodeCacheBudgetBytesForTesting(256);
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        WarmSimpleCode(parent);
        var generationBeforeFlush = ReadCodeCacheStats(parent).CodeCacheGeneration;

        WarmUniqueSimpleBlocks(child, BudgetTestCodeBaseAddr, 16);

        var afterFlushStats = ReadCodeCacheStats(parent);
        Assert.True(afterFlushStats.CodeCacheFlushCount > 0);
        Assert.True(afterFlushStats.CodeCacheGeneration > generationBeforeFlush);

        WarmSimpleCode(parent);

        var rehydratedStats = ReadCodeCacheStats(parent);
        Assert.True(rehydratedStats.BlockCacheSize > 0);
        Assert.Equal(rehydratedStats.CodeCacheGeneration, ReadCodeCacheStats(child).CodeCacheGeneration);
    }

    [Fact]
    public void ExternalAlias_ResetAllCodeCache_ExplicitInvalidationSelfHealsRequestedPages()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00430000;
        const uint rxAddr = 0x00431000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.ResetAllCodeCache();
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.ResetCodeCacheByRange(rwAddr, 1);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.False(engine.HasSlowWrite(rwAddr));

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.ResetAllCodeCache();
        Assert.True(engine.HasSlowWrite(rwAddr));

        Span<nint> hostPages = stackalloc nint[1];
        hostPages[0] = codePage.Pointer;
        engine.InvalidateCodeCacheHostPages(hostPages);

        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
        Assert.False(engine.HasSlowWrite(rwAddr));
    }

    [Fact]
    public void CodeCacheBudgetFlush_ConcatSuccessorDecodeRestartStillRuns()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        InstallConcatDirectJmpCode(engine, BudgetTestCodeBaseAddr);
        engine.SetCodeCacheBudgetBytesForTesting(200);

        engine.Eip = BudgetTestCodeBaseAddr;
        engine.Run(BudgetTestCodeBaseAddr + 6, 32);

        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(BudgetTestCodeBaseAddr + 6, engine.Eip);
        Assert.True(ReadCodeCacheStats(engine).CodeCacheFlushCount > 0);
    }

    [Fact]
    public void CodeCacheBudgetFlush_DirectJmpConcatFlushStillBuildsReusableBlock()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        InstallConcatDirectJmpCode(engine, BudgetTestCodeBaseAddr);
        engine.SetCodeCacheBudgetBytesForTesting(320);

        engine.Eip = BudgetTestCodeBaseAddr;
        engine.Run(BudgetTestCodeBaseAddr + 6, 32);

        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(BudgetTestCodeBaseAddr + 6, engine.Eip);

        var afterFirstRun = ReadCodeCacheStats(engine);
        Assert.True(afterFirstRun.CodeCacheFlushCount > 0);
        Assert.True(afterFirstRun.BlockCacheSize > 0);

        engine.Eip = BudgetTestCodeBaseAddr;
        engine.Run(BudgetTestCodeBaseAddr + 6, 32);

        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(BudgetTestCodeBaseAddr + 6, engine.Eip);
        Assert.Equal(afterFirstRun.CodeCacheFlushCount, ReadCodeCacheStats(engine).CodeCacheFlushCount);
    }

    [Fact]
    public void SharedClone_ResetCodeCacheByRange_AndMemUnmap_InvalidateSharedCore()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        child.ResetCodeCacheByRange(CodeAddr, 1);
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);

        WarmSimpleCode(parent);
        Assert.True(ReadCodeCacheStats(child).BlockCacheSize > 0);

        child.MemUnmap(CodeAddr, LinuxConstants.PageSize);

        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(parent.HasMappedPage(CodeAddr, 1));
        Assert.False(child.HasMappedPage(CodeAddr, 1));
    }

    [Fact]
    public void SyncEngineBeforeRun_ReplaysPendingCodeCacheInvalidation()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(parent);
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        mm.BindOrAssertAddressSpaceHandle(child);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, parent);
        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, child);
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);

        var sequence = mm.BumpCodeCacheSequence();
        mm.RecordCodeCacheResetRange(sequence, CodeAddr, (uint)SimpleCode.Length);
        Assert.True(child.AddressSpaceCodeCacheSequenceSeen < sequence);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, child);

        Assert.Equal(sequence, mm.CurrentCodeCacheSequence);
        Assert.Equal(sequence, child.AddressSpaceCodeCacheSequenceSeen);
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
    }

    [Fact]
    public void SharedClone_SmcRearmFlushesSiblingWriteTlbBeforeRun()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00412000;
        const uint rxAddr = 0x00413000;
        const uint writerAddr = 0x00414000;
        MapExternalPage(parent, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(parent, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);
        InstallAbsoluteAlByteWriter(parent, writerAddr, rwAddr);

        using var child = parent.Clone(true);
        Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);

        RunAbsoluteAlByteWriter(child, writerAddr, 0x40);
        Assert.Equal(1, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(child.HasSlowWrite(rwAddr));

        RunPair(parent, rxAddr, 10, 12);
        Assert.Equal(2, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.True(child.HasSlowWrite(rwAddr));

        RunAbsoluteAlByteWriter(child, writerAddr, 0x48);
        Assert.Equal(1, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(child.HasSlowWrite(rwAddr));

        RunPair(parent, rxAddr, 10, 10);
    }

    [Fact]
    public void SharedClone_SmcRearmFlushesSiblingWriteTlbBeforeStep()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00415000;
        const uint rxAddr = 0x00416000;
        const uint writerAddr = 0x00417000;
        MapExternalPage(parent, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(parent, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);
        InstallAbsoluteAlByteWriter(parent, writerAddr, rwAddr);

        using var child = parent.Clone(true);
        Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);

        StepAbsoluteAlByteWriter(child, writerAddr, 0x40);
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(child.HasSlowWrite(rwAddr));

        RunPair(parent, rxAddr, 10, 12);
        Assert.Equal(1, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.True(child.HasSlowWrite(rwAddr));

        StepAbsoluteAlByteWriter(child, writerAddr, 0x48);
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(child.HasSlowWrite(rwAddr));

        RunPair(parent, rxAddr, 10, 10);
    }

    [Fact]
    public void SharedClone_FaultDrivenPrivateRemap_MustFlushSiblingReadTlb()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(parent);
        parent.FaultHandler =
            (engine, addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
        parent.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, parent) == FaultResult.Handled;

        using var child = parent.Clone(true);
        mm.BindOrAssertAddressSpaceHandle(child);
        child.FaultHandler =
            (engine, addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
        child.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, child) == FaultResult.Handled;

        const uint addr = 0x00419000;
        Assert.Equal(addr,
            mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "shared-remap-stale-read",
                parent));

        // Align both attachments to the current code-cache sequence first so the later SyncEngineBeforeRun call
        // only observes the fault-driven remap, not the original mmap.
        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, parent);
        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, child);

        var read = new byte[1];
        Assert.True(parent.CopyFromUser(addr, read));
        Assert.Equal(0, read[0]);
        Assert.True(mm.PageMapping.TryGet(addr, out var zeroPage));
        Assert.NotEqual(IntPtr.Zero, zeroPage);

        var stableSeq = mm.CurrentCodeCacheSequence;
        Assert.True(child.CopyToUser(addr, new[] { (byte)0x5A }));
        Assert.Equal(stableSeq, mm.CurrentCodeCacheSequence);

        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.GetPageIndex(addr);
        var privatePage = vma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, privatePage);
        Assert.NotEqual(zeroPage, privatePage);
        Assert.True(mm.PageMapping.TryGet(addr, out var remappedPage));
        Assert.Equal(privatePage, remappedPage);

        // Today's managed pre-run sync keys off code-cache sequence. The fault-driven remap above never bumped it,
        // so this remains a no-op and would leave the sibling's read TLB stale if native shootdown is missing.
        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, parent);

        Assert.True(parent.CopyFromUser(addr, read));
        Assert.Equal((byte)0x5A, read[0]);
    }

    [Fact]
    public void SharedClone_FaultDrivenPrivateRemap_GuestRunMustFlushSiblingReadTlb()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(parent);
        parent.FaultHandler =
            (engine, addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
        parent.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, parent) == FaultResult.Handled;

        using var child = parent.Clone(true);
        mm.BindOrAssertAddressSpaceHandle(child);
        child.FaultHandler =
            (engine, addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;
        child.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, child) == FaultResult.Handled;

        const uint dataAddr = 0x0041A000;
        const uint readerAddr = 0x0041B000;
        const uint writerAddr = 0x0041C000;
        Assert.Equal(dataAddr,
            mm.Mmap(dataAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "shared-remap-guest-run",
                parent));
        InstallAbsoluteAlByteReader(parent, readerAddr, dataAddr);
        InstallAbsoluteAlByteWriter(parent, writerAddr, dataAddr);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, parent);
        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, child);

        RunAbsoluteAlByteReader(parent, readerAddr, 0x00);
        Assert.True(mm.PageMapping.TryGet(dataAddr, out var zeroPage));
        Assert.NotEqual(IntPtr.Zero, zeroPage);

        var stableSeq = mm.CurrentCodeCacheSequence;
        RunAbsoluteAlByteWriter(child, writerAddr, 0x5A);
        Assert.Equal(stableSeq, mm.CurrentCodeCacheSequence);

        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.GetPageIndex(dataAddr);
        var privatePage = vma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, privatePage);
        Assert.NotEqual(zeroPage, privatePage);
        Assert.True(mm.PageMapping.TryGet(dataAddr, out var remappedPage));
        Assert.Equal(privatePage, remappedPage);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, parent);

        RunAbsoluteAlByteReader(parent, readerAddr, 0x5A);
    }

    [Fact]
    public void SamePageSelfModifyingRun_SecondPatchMustNotExecuteStaleTailBytes()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        const uint codeAddr = 0x00418000;
        using var codePage = new UnmanagedPageFixture(TwoSamePagePatchesThenTail(codeAddr));

        MapExternalPage(engine, codeAddr, codePage.Pointer, Protection.Read | Protection.Write | Protection.Exec);

        engine.RegWrite(Reg.EAX, 0);
        engine.Eip = codeAddr;
        engine.Run(codeAddr + 16, 32);

        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(codeAddr + 16, engine.Eip);
        Assert.Equal((uint)0x42, engine.RegRead(Reg.EAX));
        Assert.Equal((byte)0x40, (byte)Marshal.ReadByte(codePage.Pointer, 14));
        Assert.Equal((byte)0x40, (byte)Marshal.ReadByte(codePage.Pointer, 15));
    }

    [Fact]
    public void ResetCodeCacheByRange_ReusesInvalidatedBlock_WhenCodeUnchanged()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        InstallSimpleCode(engine);
        WarmSimpleCode(engine);

        var initialBlockCount = engine.GetBlockCount();
        Assert.True(initialBlockCount > 0);

        engine.ResetCodeCacheByRange(CodeAddr, 1);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        WarmSimpleCode(engine);

        Assert.Equal(initialBlockCount, engine.GetBlockCount());
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void ResetCodeCacheByRange_OnTrailingPage_InvalidatesCrossPageBlock()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        InstallCrossPageCode(engine);
        WarmSimpleCode(engine, CrossPageCodeAddr);

        var initialBlockCount = engine.GetBlockCount();
        Assert.True(initialBlockCount > 0);
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);

        engine.ResetCodeCacheByRange(CrossPageCodeAddr + 1, 1);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        WarmSimpleCode(engine, CrossPageCodeAddr);

        Assert.Equal(initialBlockCount, engine.GetBlockCount());
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void DetachAndReattach_PreservesOriginalCoreCodeCache()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        InstallSimpleCode(engine);
        WarmSimpleCode(engine);

        var warmedBlockCount = engine.GetBlockCount();
        var warmedStats = ReadCodeCacheStats(engine);
        Assert.True(warmedBlockCount > 0);
        Assert.True(warmedStats.BlockCacheSize > 0);

        using var detached = engine.DetachMmu();
        Assert.Equal(0, engine.GetBlockCount());
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        engine.ReplaceMmu(detached);

        Assert.Equal(warmedBlockCount, engine.GetBlockCount());
        Assert.Equal(warmedStats.BlockCacheSize, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void ForkedOrClonedMmu_StartsWithEmptyCodeCache()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);
        Assert.True(parent.GetBlockCount() > 0);

        using (var forked = parent.Clone(false))
        {
            Assert.NotEqual(parent.CurrentMmuIdentity, forked.CurrentMmuIdentity);
            Assert.Equal(0, forked.GetBlockCount());
            Assert.Equal(0, ReadCodeCacheStats(forked).BlockCacheSize);
            WarmSimpleCode(forked);
            Assert.True(forked.GetBlockCount() > 0);
        }

        using var freshEngine = runtime.CreateEngine();
        using var cloned = parent.CurrentMmu.CloneSkipExternal();
        freshEngine.ReplaceMmu(cloned);
        Assert.Equal(0, freshEngine.GetBlockCount());
        Assert.Equal(0, ReadCodeCacheStats(freshEngine).BlockCacheSize);
    }

    [Fact]
    public void DisposingSharedPeer_DoesNotCorruptSharedCodeCache()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);
        var warmedBlockCount = parent.GetBlockCount();

        using (var child = parent.Clone(true))
        {
            WarmSimpleCode(child);
            Assert.Equal(warmedBlockCount, child.GetBlockCount());
        }

        WarmSimpleCode(parent);

        Assert.Equal(EmuStatus.Stopped, parent.Status);
        Assert.Equal(warmedBlockCount, parent.GetBlockCount());
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);
    }

    [Fact]
    public void SharedClone_ResetMemory_ClearsMappingsAndSharedCodeCache()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        child.ResetMemory();

        var stats = ReadCodeCacheStats(parent);
        Assert.Equal(0, stats.BlockCacheSize);
        Assert.Equal(0, stats.AllBlocksCount);
        Assert.Equal(0ul, stats.CodeCacheBytesRequested);
        Assert.False(parent.HasMappedPage(CodeAddr, 1));
        Assert.False(child.HasMappedPage(CodeAddr, 1));
    }

    [Fact]
    public void AddressSpaceHandle_BindsSharedClone_AndRejectsForkedMmu()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(parent);

        using var shared = parent.Clone(true);
        mm.BindOrAssertAddressSpaceHandle(shared);

        using var forked = parent.Clone(false);
        var ex = Assert.Throws<InvalidOperationException>(() => mm.BindOrAssertAddressSpaceHandle(forked));
        Assert.Contains("does not match address-space MMU identity", ex.Message);
    }

    [Fact]
    public void AddressSpaceHandle_DetachFromSharedEngine_CreatesNewIdentity()
    {
        var runtime = new TestRuntimeFactory();
        using var parent = runtime.CreateEngine();
        var sharedMm = runtime.CreateAddressSpace();
        sharedMm.BindOrAssertAddressSpaceHandle(parent);
        var sharedIdentity = sharedMm.AddressSpaceIdentity;

        using var child = parent.Clone(true);
        Assert.Equal(sharedIdentity, child.CurrentMmuIdentity);

        var detachedMm = runtime.CreateAddressSpace();
        detachedMm.BindAddressSpaceHandle(ProcessAddressSpaceHandle.DetachFromSharedEngine(child));

        Assert.Equal(detachedMm.AddressSpaceIdentity, child.CurrentMmuIdentity);
        Assert.NotEqual(sharedIdentity, detachedMm.AddressSpaceIdentity);
        Assert.Equal(sharedIdentity, parent.CurrentMmuIdentity);
    }

    [Fact]
    public void ForkedClone_ExternalMappingsStartDisarmedUntilChildDecodes()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var parentEngine = new Engine(runtime);
        var parentMm = new VMAManager(runtime);
        parentMm.BindOrAssertAddressSpaceHandle(parentEngine);
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x00410000;
            const uint rxAddr = 0x00411000;
            Assert.Equal(rwAddr,
                parentMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "forked-clone-rw", parentEngine));
            Assert.Equal(rxAddr,
                parentMm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "forked-clone-rx", parentEngine));
            Assert.True(parentMm.HandleFault(rwAddr, false, parentEngine));
            Assert.True(parentMm.HandleFault(rxAddr, false, parentEngine));

            RunPair(parentEngine, rxAddr, 10, 12);
            Assert.True(ReadCodeCacheStats(parentEngine).BlockCacheSize > 0);

            using var childEngine = parentEngine.Clone(false);
            var childMm = parentMm.Clone();
            childMm.BindOrAssertAddressSpaceHandle(childEngine);
            childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
            childEngine.PageFaultResolver =
                (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

            Assert.Equal(0, ReadCodeCacheStats(childEngine).BlockCacheSize);
            Assert.False(childEngine.HasSlowWrite(rwAddr));

            Assert.True(childEngine.CopyToUser(rwAddr, DecEaxTwice()));
            Assert.Equal(0, ReadCodeCacheStats(childEngine).BlockCacheSize);
            Assert.False(childEngine.HasSlowWrite(rwAddr));

            RunPair(childEngine, rxAddr, 10, 8);
            Assert.True(ReadCodeCacheStats(childEngine).BlockCacheSize > 0);
            Assert.True(childEngine.HasSlowWrite(rwAddr));

            Assert.True(childEngine.CopyToUser(rwAddr, IncEaxTwice()));
            Assert.Equal(0, ReadCodeCacheStats(childEngine).BlockCacheSize);
            Assert.False(childEngine.HasSlowWrite(rwAddr));

            RunPair(childEngine, rxAddr, 10, 12);
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public void ExternalAlias_ReprotectDroppingExec_DisarmsSlowWriteWithoutInvalidatingLiveBlocks()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00420000;
        const uint rxAddr = 0x00421000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.ReprotectMappedRange(rxAddr, LinuxConstants.PageSize, (byte)Protection.Read);

        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);

        Assert.True(engine.CopyToUser(rwAddr, DecEaxTwice()));
        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
    }

    [Fact]
    public void ExternalAlias_UnmappingExecAlias_DisarmsSlowWriteWithoutInvalidatingLiveBlocks()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());

        const uint rwAddr = 0x00422000;
        const uint rxAddr = 0x00423000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));

        engine.MemUnmap(rxAddr, LinuxConstants.PageSize);

        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        Assert.True(engine.CopyToUser(rwAddr, DecEaxTwice()));
        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void ExternalAlias_RemapWritableAliasToDifferentHostPage_DisarmsOldExecCoupling()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var codePage = new UnmanagedPageFixture(IncEaxTwice());
        using var replacementPage = new UnmanagedPageFixture([0x90, 0x90]);

        const uint rwAddr = 0x00424000;
        const uint rxAddr = 0x00425000;
        MapExternalPage(engine, rwAddr, codePage.Pointer, Protection.Read | Protection.Write);
        MapExternalPage(engine, rxAddr, codePage.Pointer, Protection.Read | Protection.Exec);

        RunPair(engine, rxAddr, 10, 12);
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
        Assert.True(engine.HasSlowWrite(rwAddr));

        MapExternalPage(engine, rwAddr, replacementPage.Pointer, Protection.Read | Protection.Write);

        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);

        Assert.True(engine.CopyToUser(rwAddr, new byte[] { 0xCC }));
        Assert.False(engine.HasSlowWrite(rwAddr));
        Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);

        RunPair(engine, rxAddr, 10, 12);
    }

    [Fact]
    public void CodeCacheInvalidation_SupportsHostPageSetSpillAndDedup()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        using var pages = new UnmanagedPageArrayFixture(6, static _ => IncEaxTwice());

        const uint baseAddr = 0x00430000;
        for (var i = 0; i < pages.Count; i++)
        {
            MapExternalPage(engine, baseAddr + (uint)(i * LinuxConstants.PageSize), pages[i], Protection.Read | Protection.Exec);
            RunPair(engine, baseAddr + (uint)(i * LinuxConstants.PageSize), 10, 12);
        }

        Assert.Equal(pages.Count, ReadCodeCacheStats(engine).BlockCacheSize);

        engine.ResetCodeCacheByRange(baseAddr, (uint)(pages.Count * LinuxConstants.PageSize));
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        for (var i = 0; i < pages.Count; i++)
            RunPair(engine, baseAddr + (uint)(i * LinuxConstants.PageSize), 10, 12);

        Span<nint> hostPages = stackalloc nint[8];
        for (var i = 0; i < pages.Count; i++)
            hostPages[i] = pages[i];
        hostPages[6] = pages[4];
        hostPages[7] = pages[4];

        engine.InvalidateCodeCacheHostPages(hostPages);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void HostPageSet_DedupsFifthInsertedPageAfterSpill()
    {
        using var pages = new UnmanagedPageArrayFixture(5, static _ => IncEaxTwice());

        Span<nint> hostPages = stackalloc nint[6];
        for (var i = 0; i < pages.Count; i++)
            hostPages[i] = pages[i];
        hostPages[5] = pages[4];

        Assert.Equal(5, X86Native.DebugHostPageSetUniqueCount(hostPages));
    }

    private static void InstallSimpleCode(Engine engine)
    {
        InstallSimpleCode(engine, CodeAddr);
    }

    private static void InstallSimpleCode(Engine engine, uint addr)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        Marshal.Copy(SimpleCode, 0, page, SimpleCode.Length);
    }

    private static void InstallConcatDirectJmpCode(Engine engine, uint addr)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        byte[] code = [0xEB, 0x02, 0x90, 0x90, 0x40, 0x40];
        Marshal.Copy(code, 0, page, code.Length);
    }

    private static void InstallCrossPageCode(Engine engine)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var firstPage = engine.AllocatePage(CodeAddr, perms);
        var secondPage = engine.AllocatePage(CodeAddr + LinuxConstants.PageSize, perms);
        Assert.NotEqual(IntPtr.Zero, firstPage);
        Assert.NotEqual(IntPtr.Zero, secondPage);

        Marshal.WriteByte(firstPage, LinuxConstants.PageSize - 1, SimpleCode[0]);
        Marshal.WriteByte(secondPage, 0, SimpleCode[1]);
    }

    private static void WarmSimpleCode(Engine engine)
    {
        WarmSimpleCode(engine, CodeAddr);
    }

    private static void WarmSimpleCode(Engine engine, uint addr)
    {
        engine.Eip = addr;
        engine.Run(addr + (uint)SimpleCode.Length, 16);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(addr + (uint)SimpleCode.Length, engine.Eip);
    }

    private static void WarmUniqueSimpleBlocks(Engine engine, uint baseAddr, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var addr = baseAddr + (uint)(i * LinuxConstants.PageSize);
            InstallSimpleCode(engine, addr);
            WarmSimpleCode(engine, addr);
        }
    }

    private static byte[] IncEaxTwice()
    {
        return [0x40, 0x40];
    }

    private static byte[] DecEaxTwice()
    {
        return [0x48, 0x48];
    }

    private static byte[] CrossPageIncEaxTwicePage(int pageIndex)
    {
        var bytes = new byte[LinuxConstants.PageSize];
        if (pageIndex == 0)
            bytes[LinuxConstants.PageSize - 1] = 0x40;
        else if (pageIndex == 1)
            bytes[0] = 0x40;
        return bytes;
    }

    private static byte[] TwoSamePagePatchesThenTail(uint addr)
    {
        byte[] code = new byte[16];
        code[0] = 0xB0; // mov al, 0x40
        code[1] = 0x40;
        code[2] = 0xA2; // mov [moffs8], al
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(3, 4), addr + 14);
        code[7] = 0xA2; // mov [moffs8], al
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(8, 4), addr + 15);
        code[12] = 0x90; // nop
        code[13] = 0x90; // nop
        code[14] = 0x48; // dec eax, patched to inc eax
        code[15] = 0x48; // dec eax, patched to inc eax
        return code;
    }

    private static void InstallAbsoluteAlByteWriter(Engine engine, uint addr, uint targetAddr)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);

        Span<byte> code = stackalloc byte[5];
        code[0] = 0xA2; // mov [moffs8], al
        BinaryPrimitives.WriteUInt32LittleEndian(code[1..], targetAddr);
        Assert.True(engine.CopyToUser(addr, code));
    }

    private static void InstallAbsoluteAlByteReader(Engine engine, uint addr, uint targetAddr)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);

        Span<byte> code = stackalloc byte[5];
        code[0] = 0xA0; // mov al, [moffs8]
        BinaryPrimitives.WriteUInt32LittleEndian(code[1..], targetAddr);
        Assert.True(engine.CopyToUser(addr, code));
    }

    private static void RunAbsoluteAlByteWriter(Engine engine, uint addr, byte value)
    {
        engine.RegWrite(Reg.EAX, value);
        engine.Eip = addr;
        engine.Run(addr + 5, 16);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(addr + 5, engine.Eip);
    }

    private static void RunAbsoluteAlByteReader(Engine engine, uint addr, byte expectedValue)
    {
        engine.RegWrite(Reg.EAX, 0);
        engine.Eip = addr;
        engine.Run(addr + 5, 16);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(addr + 5, engine.Eip);
        Assert.Equal((uint)expectedValue, engine.RegRead(Reg.EAX));
    }

    private static void StepAbsoluteAlByteWriter(Engine engine, uint addr, byte value)
    {
        engine.RegWrite(Reg.EAX, value);
        engine.Eip = addr;
        var stepResult = engine.Step();
        Assert.NotEqual((int)EmuStatus.Fault, stepResult);
        Assert.Equal(addr + 5, engine.Eip);
    }

    private static void MapExternalPage(Engine engine, uint addr, nint hostPage, Protection perms)
    {
        Assert.True(engine.MapManagedPage(addr, hostPage, (byte)perms));
    }

    private static void RunPair(Engine engine, uint codeAddr, uint initialEax, uint expectedEax)
    {
        engine.RegWrite(Reg.EAX, initialEax);
        engine.Eip = codeAddr;
        engine.Run(codeAddr + 2, 16);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(codeAddr + 2, engine.Eip);
        Assert.Equal(expectedEax, engine.RegRead(Reg.EAX));
    }

    private static CodeCacheStats ReadCodeCacheStats(Engine engine)
    {
        var json = engine.DumpStats();
        Assert.False(string.IsNullOrEmpty(json));

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        return new CodeCacheStats(
            root.GetProperty("all_blocks_count").GetInt32(),
            root.GetProperty("block_cache_size").GetInt32(),
            root.GetProperty("page_to_blocks_size").GetInt32(),
            root.GetProperty("code_cache_bytes_requested").GetUInt64(),
            root.GetProperty("code_cache_limit_bytes").GetUInt64(),
            root.GetProperty("code_cache_flush_count").GetUInt64(),
            root.GetProperty("code_cache_generation").GetUInt64());
    }

    private readonly record struct CodeCacheStats(int AllBlocksCount, int BlockCacheSize, int PageToBlocksSize,
        ulong CodeCacheBytesRequested, ulong CodeCacheLimitBytes, ulong CodeCacheFlushCount, ulong CodeCacheGeneration);

    private sealed class TmpfsFileFixture : IDisposable
    {
        private readonly SuperBlock _superBlock;
        private readonly Dentry _root;

        public TmpfsFileFixture(MemoryRuntimeContext memoryContext, byte[] contents)
        {
            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            _superBlock = fsType.CreateAnonymousFileSystem(memoryContext).ReadSuper(fsType, 0, "tmp", null);
            _root = _superBlock.Root;
            Dentry = new Dentry(FsName.FromString("engine-mmu-transfer.bin"), null, _root, _superBlock);
            _root.Inode!.Create(Dentry, 0x1B6, 0, 0);

            var file = Open();
            try
            {
                Assert.Equal(contents.Length, Dentry.Inode!.WriteFromHost(null, file, contents, 0));
            }
            finally
            {
                file.Close();
            }
        }

        public Dentry Dentry { get; }

        public LinuxFile Open()
        {
            return new LinuxFile(Dentry, FileFlags.O_RDWR, null!);
        }

        public void Dispose()
        {
        }
    }

    private sealed class UnmanagedPageFixture : IDisposable
    {
        public UnmanagedPageFixture(byte[] contents)
        {
            Pointer = Marshal.AllocHGlobal(LinuxConstants.PageSize);
            var buffer = new byte[LinuxConstants.PageSize];
            Array.Copy(contents, buffer, Math.Min(contents.Length, buffer.Length));
            Marshal.Copy(buffer, 0, Pointer, buffer.Length);
        }

        public nint Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class UnmanagedPageArrayFixture : IDisposable
    {
        private readonly UnmanagedPageFixture[] _pages;

        public UnmanagedPageArrayFixture(int count, Func<int, byte[]> factory)
        {
            _pages = new UnmanagedPageFixture[count];
            for (var i = 0; i < count; i++)
                _pages[i] = new UnmanagedPageFixture(factory(i));
        }

        public int Count => _pages.Length;

        public nint this[int index] => _pages[index].Pointer;

        public void Dispose()
        {
            foreach (var page in _pages)
                page.Dispose();
        }
    }

    private sealed class MappedPageFixture : IDisposable
    {
        private readonly Dentry _fileDentry;
        private readonly Mount _mount;
        private readonly Dentry _root;
        private readonly TestSuperBlock _sb;

        public MappedPageFixture()
        {
            _sb = new TestSuperBlock();
            var rootInode = new TestInode(_sb);
            _root = new Dentry(FsName.Empty, rootInode, null, _sb);
            _sb.Root = _root;

            Inode = new TrackingMappedPageInode(_sb);
            _fileDentry = new Dentry(FsName.FromBytes("mapped"u8), Inode, _root, _sb);
            _mount = new Mount(_sb, _root);
            File = new LinuxFile(_fileDentry, FileFlags.O_RDONLY, _mount);
            Vma = new VmArea
            {
                Start = 0x40000000,
                End = 0x40001000,
                Perms = Protection.Read,
                Flags = MapFlags.Shared,
                FileMapping = new VmaFileMapping(File),
                Offset = 0,
                VmPgoff = 0,
                Name = "mapped-test",
                VmMapping = null,
                VmAnonVma = null
            };
        }

        public TrackingMappedPageInode Inode { get; }
        public LinuxFile File { get; }
        public VmArea Vma { get; }

        public void Dispose()
        {
            Vma.FileMapping?.Release();
            _mount.Put();
            Inode.DisposePage();
        }
    }

    private sealed class TrackingMappedPageInode : TestInode
    {
        private readonly IntPtr _ptr = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        public TrackingMappedPageInode(SuperBlock sb) : base(sb)
        {
        }

        public int DisposeCount { get; private set; }

        public void DisposePage()
        {
            Marshal.FreeHGlobal(_ptr);
        }

        public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
            bool writable, out BackingPageHandle backingPageHandle)
        {
            _ = linuxFile;
            _ = pageIndex;
            _ = absoluteFileOffset;
            _ = writable;
            backingPageHandle = BackingPageHandle.CreateOwned(_ptr, this, 1);
            return true;
        }

        protected internal override void ReleaseMappedPageHandle(long releaseToken)
        {
            Assert.Equal(1, releaseToken);
            DisposeCount++;
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
        public TestSuperBlock() : base(null, new MemoryRuntimeContext())
        {
        }
    }

    private class TestInode : Inode
    {
        public TestInode(SuperBlock sb)
        {
            SuperBlock = sb;
            Type = InodeType.File;
            Mode = 0x1A4;
            sb.EnsureInodeTracked(this);
        }

        public override bool SupportsMmap => true;
    }
}
