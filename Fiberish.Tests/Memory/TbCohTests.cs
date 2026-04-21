using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.X86.Native;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Fiberish.Tests.Memory;

public class TbCohTests
{
    private const uint CodeAddr = 0x48500000;
    private readonly ITestOutputHelper _output;

    public TbCohTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SharedAnon_ExecPeer_MarksWriterReadOnlyAtNextSync()
    {
        var runtime = new MemoryRuntimeContext();
        using var writeEngine = new Engine(runtime);
        using var execEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            writeMm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "tbcoh-writer", writeEngine));

        var execMm = writeMm.Clone();
        execMm.BindOrAssertAddressSpaceHandle(execEngine);
        execEngine.PageFaultResolver =
            (addr, isWrite) => execMm.HandleFaultDetailed(addr, isWrite, execEngine) == FaultResult.Handled;
        Assert.Equal(0,
            execMm.Mprotect(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec, execEngine, out _));

        Assert.True(writeEngine.CopyToUser(CodeAddr, IncEaxTwice()));
        DumpPage("before-first-exec.write", writeEngine, writeMm);
        DumpPage("before-first-exec.exec", execEngine, execMm);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(execMm, execEngine);
        DumpPage("after-sync-first-exec.exec", execEngine, execMm);
        RunPair(execEngine, execMm, CodeAddr, 10, 12);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
        Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(CodeAddr, false));
        Assert.Equal(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(CodeAddr, true));
    }

    [Fact]
    public void SharedAnon_WriteAfterExec_InvalidatesPeerTbBeforeNextRun()
    {
        var runtime = new MemoryRuntimeContext();
        using var writeEngine = new Engine(runtime);
        using var execEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        Assert.Equal(CodeAddr,
            writeMm.Mmap(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "tbcoh-writer", writeEngine));

        var execMm = writeMm.Clone();
        execMm.BindOrAssertAddressSpaceHandle(execEngine);
        execEngine.PageFaultResolver =
            (addr, isWrite) => execMm.HandleFaultDetailed(addr, isWrite, execEngine) == FaultResult.Handled;
        Assert.Equal(0,
            execMm.Mprotect(CodeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec, execEngine, out _));

        Assert.True(writeEngine.CopyToUser(CodeAddr, IncEaxTwice()));
        DumpPage("before-warmup.write", writeEngine, writeMm);
        DumpPage("before-warmup.exec", execEngine, execMm);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(execMm, execEngine);
        DumpPage("after-sync-warmup.exec", execEngine, execMm);
        RunPair(execEngine, execMm, CodeAddr, 10, 12);
        Assert.True(execEngine.GetBlockCount() > 0);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
        DumpPage("after-sync-write.write", writeEngine, writeMm);
        Assert.True(writeEngine.CopyToUser(CodeAddr, DecEaxTwice()));
        DumpPage("after-write.write", writeEngine, writeMm);

        ProcessAddressSpaceSync.SyncEngineBeforeRun(execMm, execEngine);
        DumpPage("before-rerun.exec", execEngine, execMm);
        RunPair(execEngine, execMm, CodeAddr, 10, 8);
    }

    [Fact]
    public void SameMmu_SharedFileRwRxAliases_InvalidateLocalTbWithoutTbCoh()
    {
        var runtime = new TestRuntimeFactory();
        using var fixture = new TmpfsFileFixture(runtime.MemoryContext, IncEaxTwice());
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48600000;
            const uint rxAddr = 0x48700000;
            Assert.Equal(rwAddr,
                mm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-local-rw", engine));
            Assert.Equal(rxAddr,
                mm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "tbcoh-local-rx", engine));
            Assert.True(mm.HandleFault(rwAddr, false, engine));
            Assert.True(mm.HandleFault(rxAddr, false, engine));

            RunPair(engine, mm, rxAddr, 10, 12);
            Assert.True(engine.GetBlockCount() > 0);

            Assert.True(engine.CopyToUser(rwAddr, DecEaxTwice()));
            RunPair(engine, mm, rxAddr, 10, 8);
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public void SameMmu_SharedFileRwRxAliases_StayDisarmedAfterFirstInvalidateUntilNextDecode()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48610000;
            const uint rxAddr = 0x48710000;
            Assert.Equal(rwAddr,
                mm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-local-rw-disarm", engine));
            Assert.Equal(rxAddr,
                mm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "tbcoh-local-rx-disarm", engine));
            Assert.True(mm.HandleFault(rwAddr, false, engine));
            Assert.True(mm.HandleFault(rxAddr, false, engine));

            RunPair(engine, mm, rxAddr, 10, 12);
            Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
            Assert.True(engine.HasSlowWrite(rwAddr));

            Assert.True(engine.CopyToUser(rwAddr, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));

            Assert.True(engine.CopyToUser(rwAddr + 1, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));

            RunPair(engine, mm, rxAddr, 10, 8);
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public void SameMmu_SharedFileRwRxAliases_RearmAfterDecode()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48620000;
            const uint rxAddr = 0x48720000;
            Assert.Equal(rwAddr,
                mm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-local-rw-rearm", engine));
            Assert.Equal(rxAddr,
                mm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "tbcoh-local-rx-rearm", engine));
            Assert.True(mm.HandleFault(rwAddr, false, engine));
            Assert.True(mm.HandleFault(rxAddr, false, engine));

            RunPair(engine, mm, rxAddr, 10, 12);
            Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
            Assert.True(engine.HasSlowWrite(rwAddr));

            Assert.True(engine.CopyToUser(rwAddr, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));

            RunPair(engine, mm, rxAddr, 10, 10);
            Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
            Assert.True(engine.HasSlowWrite(rwAddr));

            Assert.True(engine.CopyToUser(rwAddr + 1, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));

            RunPair(engine, mm, rxAddr, 10, 8);
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public void SameMmu_CrossPageBlock_DisarmsAllTouchedHostPagesUntilNextDecode()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, CrossPageIncEaxTwice());
        using var engine = new Engine(runtime);
        var mm = new VMAManager(runtime);
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48630000;
            const uint rxAddr = 0x48730000;
            var codeAddr = rxAddr + LinuxConstants.PageSize - 1;
            Assert.Equal(rwAddr,
                mm.Mmap(rwAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-cross-rw", engine));
            Assert.Equal(rxAddr,
                mm.Mmap(rxAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "tbcoh-cross-rx", engine));
            Assert.True(mm.HandleFault(rwAddr + LinuxConstants.PageSize - 1, false, engine));
            Assert.True(mm.HandleFault(rwAddr + LinuxConstants.PageSize, false, engine));
            Assert.True(mm.HandleFault(rxAddr + LinuxConstants.PageSize - 1, false, engine));
            Assert.True(mm.HandleFault(rxAddr + LinuxConstants.PageSize, false, engine));

            RunPair(engine, mm, codeAddr, 10, 12);
            Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
            Assert.True(engine.HasSlowWrite(rwAddr));
            Assert.True(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

            Assert.True(engine.CopyToUser(rwAddr + LinuxConstants.PageSize - 1, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));
            Assert.False(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

            Assert.True(engine.CopyToUser(rwAddr + LinuxConstants.PageSize, new byte[] { 0x48 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));
            Assert.False(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

            RunPair(engine, mm, codeAddr, 10, 8);
            Assert.True(ReadCodeCacheStats(engine).BlockCacheSize > 0);
            Assert.True(engine.HasSlowWrite(rwAddr));
            Assert.True(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

            Assert.True(engine.CopyToUser(rwAddr + LinuxConstants.PageSize, new byte[] { 0x90 }));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);
            Assert.False(engine.HasSlowWrite(rwAddr));
            Assert.False(engine.HasSlowWrite(rwAddr + LinuxConstants.PageSize));

            RunPair(engine, mm, codeAddr, 10, 9);
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public void ForkedMmu_PreservedExternalRwAlias_RehydratesAliasTrackingForLaterRxAlias()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var parentEngine = new Engine(runtime);
        var parentMm = new VMAManager(runtime);
        parentMm.BindOrAssertAddressSpaceHandle(parentEngine);
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        var parentRwFile = fixture.Open();
        var childRxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48680000;
            const uint rxAddr = 0x48690000;
            Assert.Equal(rwAddr,
                parentMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, parentRwFile, 0, "tbcoh-fork-rw", parentEngine));
            Assert.True(parentMm.HandleFault(rwAddr, false, parentEngine));

            using var childEngine = parentEngine.Clone(false);
            var childMm = parentMm.Clone();
            childMm.BindOrAssertAddressSpaceHandle(childEngine);
            childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
            childEngine.PageFaultResolver =
                (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

            Assert.Equal(rxAddr,
                childMm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, childRxFile, 0, "tbcoh-fork-rx", childEngine));
            Assert.True(childMm.HandleFault(rxAddr, false, childEngine));

            RunPair(childEngine, childMm, rxAddr, 10, 12);
            Assert.True(childEngine.GetBlockCount() > 0);

            Assert.True(childEngine.CopyToUser(rwAddr, DecEaxTwice()));
            RunPair(childEngine, childMm, rxAddr, 10, 8);
        }
        finally
        {
            parentRwFile.Close();
            childRxFile.Close();
        }
    }

    [Fact]
    public void MixedLocalAliasAndRemotePeer_InvalidatesLocalAndRemoteTbs()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var remoteEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var rxFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48800000;
            const uint rxAddr = 0x48900000;
            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-mixed-rw", writeEngine));
            Assert.Equal(rxAddr,
                writeMm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, rxFile, 0, "tbcoh-mixed-rx", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));
            Assert.True(writeMm.HandleFault(rxAddr, false, writeEngine));

            var remoteMm = new VMAManager(runtime);
            remoteMm.BindOrAssertAddressSpaceHandle(remoteEngine);
            remoteEngine.PageFaultResolver =
                (addr, isWrite) => remoteMm.HandleFaultDetailed(addr, isWrite, remoteEngine) == FaultResult.Handled;
            var remoteRxFile = fixture.Open();
            try
            {
                Assert.Equal(rxAddr,
                    remoteMm.Mmap(rxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                        MapFlags.Shared | MapFlags.Fixed, remoteRxFile, 0, "tbcoh-mixed-remote-rx", remoteEngine));
                Assert.True(remoteMm.HandleFault(rxAddr, false, remoteEngine));

                ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
                RunPair(writeEngine, writeMm, rxAddr, 10, 12);
                ProcessAddressSpaceSync.SyncEngineBeforeRun(remoteMm, remoteEngine);
                RunPair(remoteEngine, remoteMm, rxAddr, 10, 12);
                Assert.True(remoteEngine.GetBlockCount() > 0);

                ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
                Assert.True(writeEngine.CopyToUser(rwAddr, DecEaxTwice()));

                RunPair(writeEngine, writeMm, rxAddr, 10, 8);
                ProcessAddressSpaceSync.SyncEngineBeforeRun(remoteMm, remoteEngine);
                RunPair(remoteEngine, remoteMm, rxAddr, 10, 8);
            }
            finally
            {
                remoteRxFile.Close();
            }
        }
        finally
        {
            rwFile.Close();
            rxFile.Close();
        }
    }

    [Fact]
    public async Task SysWrite_SharedFileExecAliases_InvalidateLocalAndRemoteTbs()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var remoteEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;
        var writeSyscalls = new SyscallManager(writeEngine, writeMm, 0);

        var rwFile = fixture.Open();
        var localRxFile = fixture.Open();
        var remoteRxFile = fixture.Open();
        var writeFdFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48880000;
            const uint localRxAddr = 0x48890000;
            const uint remoteRxAddr = 0x488A0000;
            const uint writeBufAddr = 0x1A000;

            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-syswrite-rw", writeEngine));
            Assert.Equal(localRxAddr,
                writeMm.Mmap(localRxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, localRxFile, 0, "tbcoh-syswrite-local-rx", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));
            Assert.True(writeMm.HandleFault(localRxAddr, false, writeEngine));

            var remoteMm = new VMAManager(runtime);
            remoteMm.BindOrAssertAddressSpaceHandle(remoteEngine);
            remoteEngine.PageFaultResolver =
                (addr, isWrite) => remoteMm.HandleFaultDetailed(addr, isWrite, remoteEngine) == FaultResult.Handled;
            Assert.Equal(remoteRxAddr,
                remoteMm.Mmap(remoteRxAddr, LinuxConstants.PageSize, Protection.Read | Protection.Exec,
                    MapFlags.Shared | MapFlags.Fixed, remoteRxFile, 0, "tbcoh-syswrite-remote-rx", remoteEngine));
            Assert.True(remoteMm.HandleFault(remoteRxAddr, false, remoteEngine));

            RunPair(writeEngine, writeMm, localRxAddr, 10, 12);
            ProcessAddressSpaceSync.SyncEngineBeforeRun(remoteMm, remoteEngine);
            RunPair(remoteEngine, remoteMm, remoteRxAddr, 10, 12);

            MapWritableUserPage(writeMm, writeEngine, writeBufAddr);
            var newCode = DecEaxTwice();
            Assert.True(writeEngine.CopyToUser(writeBufAddr, newCode));

            var fd = writeSyscalls.AllocFD(writeFdFile);
            Assert.Equal(newCode.Length,
                await writeSyscalls.SysWrite(writeEngine, (uint)fd, writeBufAddr, (uint)newCode.Length, 0, 0, 0));

            // The current engine may continue guest execution in the same native run,
            // so local executable aliases must be invalidated immediately.
            RunPair(writeEngine, writeMm, localRxAddr, 10, 8);

            ProcessAddressSpaceSync.SyncEngineBeforeRun(remoteMm, remoteEngine);
            RunPair(remoteEngine, remoteMm, remoteRxAddr, 10, 8);
        }
        finally
        {
            writeSyscalls.Close();
            rwFile.Close();
            localRxFile.Close();
            remoteRxFile.Close();
        }
    }

    [Fact]
    public void UnfaultedRemoteExecPeer_MprotectUpdatesWriterWpState()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var execEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var execFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48A00000;
            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-unfaulted-rw", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));

            var execMm = new VMAManager(runtime);
            execMm.BindOrAssertAddressSpaceHandle(execEngine);
            execEngine.PageFaultResolver =
                (addr, isWrite) => execMm.HandleFaultDetailed(addr, isWrite, execEngine) == FaultResult.Handled;
            Assert.Equal(rwAddr,
                ProcessAddressSpaceSync.Mmap(execMm, execEngine, rwAddr, LinuxConstants.PageSize, Protection.Read,
                    MapFlags.Shared | MapFlags.Fixed, execFile, 0, "tbcoh-unfaulted-exec"));

            Assert.Equal(0,
                ProcessAddressSpaceSync.Mprotect(execMm, execEngine, rwAddr, LinuxConstants.PageSize,
                    Protection.Read | Protection.Exec));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.Equal(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));

            Assert.Equal(0,
                ProcessAddressSpaceSync.Mprotect(execMm, execEngine, rwAddr, LinuxConstants.PageSize, Protection.Read));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.True(writeEngine.CopyToUser(rwAddr, DecEaxTwice()));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));
        }
        finally
        {
            rwFile.Close();
            execFile.Close();
        }
    }

    [Fact]
    public void UnfaultedRemoteExecPeer_MunmapClearsWriterWpState()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var execEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var execFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48B00000;
            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-unfaulted-munmap-rw", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));

            var execMm = new VMAManager(runtime);
            execMm.BindOrAssertAddressSpaceHandle(execEngine);
            execEngine.PageFaultResolver =
                (addr, isWrite) => execMm.HandleFaultDetailed(addr, isWrite, execEngine) == FaultResult.Handled;
            Assert.Equal(rwAddr,
                ProcessAddressSpaceSync.Mmap(execMm, execEngine, rwAddr, LinuxConstants.PageSize,
                    Protection.Read | Protection.Exec, MapFlags.Shared | MapFlags.Fixed, execFile, 0,
                    "tbcoh-unfaulted-munmap-exec"));

            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.Equal(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));

            ProcessAddressSpaceSync.Munmap(execMm, execEngine, rwAddr, LinuxConstants.PageSize);
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.True(writeEngine.CopyToUser(rwAddr, DecEaxTwice()));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));
        }
        finally
        {
            rwFile.Close();
        }
    }

    [Fact]
    public void UnfaultedRemoteSharedReadPeer_MmapDoesNotChangeWriterWpState()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var readEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var readFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48C00000;
            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-unfaulted-read-rw", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));

            var readMm = new VMAManager(runtime);
            readMm.BindOrAssertAddressSpaceHandle(readEngine);
            readEngine.PageFaultResolver =
                (addr, isWrite) => readMm.HandleFaultDetailed(addr, isWrite, readEngine) == FaultResult.Handled;
            Assert.Equal(rwAddr,
                ProcessAddressSpaceSync.Mmap(readMm, readEngine, rwAddr, LinuxConstants.PageSize, Protection.Read,
                    MapFlags.Shared | MapFlags.Fixed, readFile, 0, "tbcoh-unfaulted-read-peer"));

            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.True(writeEngine.CopyToUser(rwAddr, DecEaxTwice()));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));
        }
        finally
        {
            rwFile.Close();
            readFile.Close();
        }
    }

    [Fact]
    public void UnfaultedRemotePrivateWritePeer_MmapAndMprotectDoNotChangeWriterWpState()
    {
        var runtime = new MemoryRuntimeContext();
        using var fixture = new TmpfsFileFixture(runtime, IncEaxTwice());
        using var writeEngine = new Engine(runtime);
        using var privateEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);
        writeEngine.PageFaultResolver =
            (addr, isWrite) => writeMm.HandleFaultDetailed(addr, isWrite, writeEngine) == FaultResult.Handled;

        var rwFile = fixture.Open();
        var privateFile = fixture.Open();
        try
        {
            const uint rwAddr = 0x48D00000;
            Assert.Equal(rwAddr,
                writeMm.Mmap(rwAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, rwFile, 0, "tbcoh-unfaulted-private-rw", writeEngine));
            Assert.True(writeMm.HandleFault(rwAddr, false, writeEngine));

            var privateMm = new VMAManager(runtime);
            privateMm.BindOrAssertAddressSpaceHandle(privateEngine);
            privateEngine.PageFaultResolver =
                (addr, isWrite) =>
                    privateMm.HandleFaultDetailed(addr, isWrite, privateEngine) == FaultResult.Handled;
            Assert.Equal(rwAddr,
                ProcessAddressSpaceSync.Mmap(privateMm, privateEngine, rwAddr, LinuxConstants.PageSize,
                    Protection.Read, MapFlags.Private | MapFlags.Fixed, privateFile, 0,
                    "tbcoh-unfaulted-private-peer"));

            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.True(writeEngine.CopyToUser(rwAddr, DecEaxTwice()));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));

            Assert.Equal(0,
                ProcessAddressSpaceSync.Mprotect(privateMm, privateEngine, rwAddr, LinuxConstants.PageSize,
                    Protection.Read | Protection.Write));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.True(writeEngine.CopyToUser(rwAddr, IncEaxTwice()));
            ProcessAddressSpaceSync.SyncEngineBeforeRun(writeMm, writeEngine);
            Assert.NotEqual(IntPtr.Zero, writeEngine.GetPhysicalAddressSafe(rwAddr, true));
        }
        finally
        {
            rwFile.Close();
            privateFile.Close();
        }
    }

#if DEBUG
    [Fact]
    public void PartialMprotect_SplitVma_OnlyAppliesWxToTouchedPage()
    {
        using var diagnostics = TbCohDiagnosticsScope.Begin();
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        mm.BindOrAssertAddressSpaceHandle(engine);
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint mapAddr = 0x48E00000;
        const uint pageCount = 4;
        Assert.Equal(mapAddr,
            mm.Mmap(mapAddr, LinuxConstants.PageSize * pageCount, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "tbcoh-split-mprotect", engine));

        for (uint i = 0; i < pageCount; i++)
            Assert.True(engine.CopyToUser(mapAddr + i * LinuxConstants.PageSize, new byte[] { (byte)i }));

        Assert.Equal(0,
            ProcessAddressSpaceSync.Mprotect(mm, engine, mapAddr + LinuxConstants.PageSize, LinuxConstants.PageSize,
                Protection.Read | Protection.Exec));

        var snapshot = diagnostics.Snapshot;
        Assert.True(diagnostics.IsEnabled);
        Assert.Equal(1, snapshot.ApplyWxCalls);
        Assert.Equal(1, snapshot.ApplyWxFastNoWriters);
        Assert.Equal(0, snapshot.ApplyWxSlowScans);
        Assert.Equal(0, snapshot.ApplyWxVisitedWriterPages);
    }
#endif

    private static byte[] IncEaxTwice()
    {
        return [0x40, 0x40];
    }

    private static byte[] DecEaxTwice()
    {
        return [0x48, 0x48];
    }

    private static byte[] CrossPageIncEaxTwice()
    {
        var bytes = new byte[LinuxConstants.PageSize + 1];
        bytes[LinuxConstants.PageSize - 1] = 0x40;
        bytes[LinuxConstants.PageSize] = 0x40;
        return bytes;
    }

    private void RunPair(Engine engine, VMAManager mm, uint codeAddr, uint initialEax, uint expectedEax)
    {
        engine.RegWrite(Reg.EAX, initialEax);
        engine.Eip = codeAddr;
        engine.Run(codeAddr + 2, 16);
        _output.WriteLine(
            $"run status={engine.Status} eip=0x{engine.Eip:X8} eax=0x{engine.RegRead(Reg.EAX):X8} faultVec={engine.FaultVector}");
        DumpPage("after-run", engine, mm, codeAddr);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(codeAddr + 2, engine.Eip);
        Assert.Equal(expectedEax, engine.RegRead(Reg.EAX));
    }

    private void DumpPage(string tag, Engine engine, VMAManager mm, uint codeAddr = CodeAddr)
    {
        var readPtr = engine.GetPhysicalAddressSafe(codeAddr, false);
        var writePtr = engine.GetPhysicalAddressSafe(codeAddr, true);
        var bytes = new byte[2];
        var canRead = engine.CopyFromUser(codeAddr, bytes);
        var vma = mm.FindVmArea(codeAddr);
        _output.WriteLine(
            $"{tag}: perms={vma?.Perms} readPtr=0x{readPtr.ToInt64():X} writePtr=0x{writePtr.ToInt64():X} canRead={canRead} bytes={BitConverter.ToString(bytes)} faultVec={engine.FaultVector}");
    }

    private static void MapWritableUserPage(VMAManager mm, Engine engine, uint addr)
    {
        Assert.Equal(addr,
            mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[tbcoh-user]", engine));
        Assert.True(mm.HandleFault(addr, true, engine));
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
            Dentry = new Dentry(FsName.FromString("tbcoh.bin"), null, _root, _superBlock);
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
}
