using System.Runtime.InteropServices;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

public class WindowedMappedFilePageBackendTests
{
    private static readonly HostMemoryMapGeometry Geometry16K =
        new(LinuxConstants.PageSize, 16384, 16384, SupportsMappedFileBackend: true);

    [Fact]
    public void FourGuestPagesWithin16KWindow_UseSingleWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapped-backend-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[LinuxConstants.PageSize * 8]);

        try
        {
            using var backend = new WindowedMappedFilePageBackend(path, Geometry16K);
            var handles = new List<IPageHandle>();
            for (var i = 0; i < 4; i++)
            {
                Assert.True(backend.TryAcquirePageHandle(i, new FileInfo(path).Length, writable: false, out var handle));
                Assert.NotNull(handle);
                handles.Add(handle!);
            }

            var diagnostics = backend.GetDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(16384, diagnostics.WindowBytes);
            Assert.Equal(4, diagnostics.GuestPageCount);

            Assert.True(backend.TryAcquirePageHandle(4, new FileInfo(path).Length, writable: false, out var extra));
            Assert.NotNull(extra);
            handles.Add(extra!);

            diagnostics = backend.GetDiagnostics();
            Assert.Equal(2, diagnostics.WindowCount);
            Assert.Equal(32768, diagnostics.WindowBytes);
            Assert.Equal(5, diagnostics.GuestPageCount);

            foreach (var handle in handles)
                handle.Dispose();
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
            Assert.True(backend.TryAcquirePageHandle(0, LinuxConstants.PageSize, writable: false, out var oldHandle));
            Assert.NotNull(oldHandle);
            Assert.Equal("AAAA", ReadString(oldHandle!.Pointer, 4));

            backend.UpdatePath(pathB);

            Assert.True(backend.TryAcquirePageHandle(0, LinuxConstants.PageSize, writable: false, out var newHandle));
            Assert.NotNull(newHandle);
            Assert.Equal("BBBB", ReadString(newHandle!.Pointer, 4));
            Assert.Equal("AAAA", ReadString(oldHandle.Pointer, 4));

            oldHandle.Dispose();
            newHandle.Dispose();
        }
        finally
        {
            if (File.Exists(pathA)) File.Delete(pathA);
            if (File.Exists(pathB)) File.Delete(pathB);
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
            Assert.True(backend.TryAcquirePageHandle(0, LinuxConstants.PageSize, writable: true, out var handle));
            Assert.NotNull(handle);

            Marshal.Copy("YZ"u8.ToArray(), 0, handle!.Pointer + 1, 2);
            Assert.True(backend.TryFlushPage(0));
            Assert.Equal("xYZx", File.ReadAllText(path)[..4]);

            backend.Truncate(0);
            var diagnostics = backend.GetDiagnostics();
            Assert.Equal(0, diagnostics.WindowCount);
            Assert.False(backend.TryAcquirePageHandle(0, 0, writable: false, out _));

            handle.Dispose();
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

    private static string ReadString(IntPtr ptr, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(ptr, bytes, 0, count);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}
