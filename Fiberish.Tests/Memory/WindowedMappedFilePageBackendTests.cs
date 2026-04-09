using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

public class WindowedMappedFilePageBackendTests
{
    private static readonly HostMemoryMapGeometry Geometry16K =
        new(LinuxConstants.PageSize, 16384, 16384, true,
            true);

    [Fact]
    public void FourGuestPagesWithin16KWindow_UseSingleWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapped-backend-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[LinuxConstants.PageSize * 8]);

        try
        {
            using var backend = new WindowedMappedFilePageBackend(path, Geometry16K);
            var leases = new List<(IntPtr Pointer, long ReleaseToken)>();
            for (var i = 0; i < 4; i++)
            {
                Assert.True(backend.TryAcquirePageLease(i, new FileInfo(path).Length, false,
                    out var pointer, out var releaseToken));
                Assert.NotEqual(IntPtr.Zero, pointer);
                Assert.NotEqual(0, releaseToken);
                leases.Add((pointer, releaseToken));
            }

            var diagnostics = backend.GetDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(16384, diagnostics.WindowBytes);
            Assert.Equal(4, diagnostics.GuestPageCount);

            Assert.True(backend.TryAcquirePageLease(4, new FileInfo(path).Length, false, out var extraPointer,
                out var extraReleaseToken));
            Assert.NotEqual(IntPtr.Zero, extraPointer);
            Assert.NotEqual(0, extraReleaseToken);
            leases.Add((extraPointer, extraReleaseToken));

            diagnostics = backend.GetDiagnostics();
            Assert.Equal(2, diagnostics.WindowCount);
            Assert.Equal(32768, diagnostics.WindowBytes);
            Assert.Equal(5, diagnostics.GuestPageCount);

            foreach (var lease in leases)
                backend.ReleasePageLease(lease.ReleaseToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UpdatePath_RetiresOldWindows_AndNewFaultUsesNewFile()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"mapped-backend-a-{Guid.NewGuid():N}.bin");
        var pathB = Path.Combine(Path.GetTempPath(), $"mapped-backend-b-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(pathA, BuildPage((byte)'A'));
        File.WriteAllBytes(pathB, BuildPage((byte)'B'));

        try
        {
            using var backend = new WindowedMappedFilePageBackend(pathA, Geometry16K);
            Assert.True(backend.TryAcquirePageLease(0, LinuxConstants.PageSize, false, out var oldPointer,
                out var oldReleaseToken));
            Assert.NotEqual(0, oldReleaseToken);
            Assert.Equal("AAAA", ReadString(oldPointer, 4));

            backend.UpdatePath(pathB);

            Assert.True(backend.TryAcquirePageLease(0, LinuxConstants.PageSize, false, out var newPointer,
                out var newReleaseToken));
            Assert.NotEqual(0, newReleaseToken);
            Assert.Equal("BBBB", ReadString(newPointer, 4));
            Assert.Equal("AAAA", ReadString(oldPointer, 4));

            backend.ReleasePageLease(oldReleaseToken);
            backend.ReleasePageLease(newReleaseToken);
        }
        finally
        {
            if (File.Exists(pathA)) File.Delete(pathA);
            if (File.Exists(pathB)) File.Delete(pathB);
        }
    }

    [Fact]
    public void ReleasingRetiredLease_DoesNotAffectReplacementWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapped-backend-replace-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, BuildPage((byte)'x'));

        try
        {
            using var backend = new WindowedMappedFilePageBackend(path, Geometry16K);
            Assert.True(backend.TryAcquirePageLease(0, LinuxConstants.PageSize, false, out _, out var readonlyToken));
            Assert.NotEqual(0, readonlyToken);

            Assert.True(backend.TryAcquirePageLease(0, LinuxConstants.PageSize, true, out var writablePointer,
                out var writableToken));
            Assert.NotEqual(0, writableToken);

            backend.ReleasePageLease(readonlyToken);

            Marshal.Copy("AB"u8.ToArray(), 0, writablePointer, 2);
            Assert.True(backend.TryFlushPage(0));
            Assert.Equal("ABxx", File.ReadAllText(path)[..4]);

            var diagnostics = backend.GetDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(LinuxConstants.PageSize, diagnostics.WindowBytes);

            backend.ReleasePageLease(writableToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WritableWindow_FlushesToDisk_AndTruncateRetiresMappings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapped-backend-flush-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, BuildPage((byte)'x'));

        try
        {
            using var backend = new WindowedMappedFilePageBackend(path, Geometry16K);
            Assert.True(backend.TryAcquirePageLease(0, LinuxConstants.PageSize, true, out var pointer,
                out var releaseToken));
            Assert.NotEqual(0, releaseToken);

            Marshal.Copy("YZ"u8.ToArray(), 0, pointer + 1, 2);
            Assert.True(backend.TryFlushPage(0));
            Assert.Equal("xYZx", File.ReadAllText(path)[..4]);

            backend.Truncate(0);
            var diagnostics = backend.GetDiagnostics();
            Assert.Equal(0, diagnostics.WindowCount);
            Assert.False(backend.TryAcquirePageLease(0, 0, false, out _, out _));

            backend.ReleasePageLease(releaseToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PartialTailPage_OnUnix_UsesDirectMappedWindow()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"mapped-backend-tail-{Guid.NewGuid():N}.bin");
        var fileSize = LinuxConstants.PageSize + 123;
        File.WriteAllBytes(path, BuildFile(fileSize, (byte)'t'));

        try
        {
            using var backend = new WindowedMappedFilePageBackend(path, Geometry16K);

            Assert.True(backend.TryAcquirePageLease(1, fileSize, true, out var pointer, out var releaseToken));
            Assert.NotEqual(0, releaseToken);

            var tailBytes = new byte[24];
            Marshal.Copy(pointer + 123, tailBytes, 0, tailBytes.Length);
            Assert.All(tailBytes, b => Assert.Equal((byte)0, b));

            Marshal.Copy("TAIL!"u8.ToArray(), 0, pointer + 123 + 16, 5);
            Assert.True(backend.TryAcquirePageLease(1, fileSize, true, out var peerPointer, out var peerReleaseToken));
            Assert.NotEqual(0, peerReleaseToken);
            Assert.Equal("TAIL!", ReadString(peerPointer + 123 + 16, 5));

            backend.ReleasePageLease(releaseToken);
            backend.ReleasePageLease(peerReleaseToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] BuildPage(byte fill)
    {
        var data = new byte[LinuxConstants.PageSize];
        Array.Fill(data, fill);
        return data;
    }

    private static byte[] BuildFile(int length, byte fill)
    {
        var data = new byte[length];
        Array.Fill(data, fill);
        return data;
    }

    private static string ReadString(IntPtr ptr, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(ptr, bytes, 0, count);
        return Encoding.ASCII.GetString(bytes);
    }
}