using System.Formats.Tar;
using System.Reflection;
using System.Text;
using Fiberish.VFS;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class RootfsArchiveLoaderTests
{
    [Fact]
    public void Silkfs_ImageImport_LooksUpParentsWithoutExistingNamespaceLinks()
    {
        var load = typeof(RootfsArchiveLoader).GetMethod("LoadFromTar", BindingFlags.Static | BindingFlags.Public)!;
        var type = new FileSystemType { Name = "silkfs", Factory = devices => new SilkFileSystem(devices) };
        var tarPath = Path.Combine(
            @"C:\Users\Jiang\repos\podish\artifacts\ltp-memory\runs",
            "20261112T142241.534522Z-podish-silkfs",
            "image.tar");
        if (File.Exists(tarPath))
        {
            using var fileTar = File.OpenRead(tarPath);
            var tarRoot = Path.Combine(Path.GetTempPath(), $"silkfs-tar-test-{Guid.NewGuid():N}");
            try
            {
                var sbTar = (SuperBlock)load.Invoke(null, [fileTar, new DeviceNumberManager(), type, tarRoot, null])!;
                ((IDisposable)sbTar).Dispose();
            }
            finally
            {
                TestWorkspace.DeleteDirectory(tarRoot);
            }
        }
        else
        {
            using var image = new MemoryStream();
            using (var archive = new TarWriter(image, TarEntryFormat.Pax, true))
            {
                foreach (var directory in new[] { "etc/", "opt/", "opt/ltp/", "opt/ltp/testcases/", "opt/ltp/testcases/bin/" })
                    archive.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
                archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "etc/passwd") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("root\n")), Mode = UnixFileMode.UserRead });
                archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "opt/ltp/testcases/bin/probe") { DataStream = new MemoryStream(new byte[] { 1, 2, 3 }), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            }
            image.Position = 0;
            var root = Path.Combine(Path.GetTempPath(), $"silkfs-import-{Guid.NewGuid():N}");
            try
            {
                var sb = (SuperBlock)load.Invoke(null, [image, new DeviceNumberManager(), type, root, null])!;
                Assert.NotNull(sb.Root!.Inode!.Lookup("etc")!.Inode!.Lookup("passwd")!.Inode);
                Assert.NotNull(sb.Root.Inode.Lookup("opt")!.Inode!.Lookup("ltp")!.Inode!.Lookup("testcases")!.Inode!
                    .Lookup("bin")!.Inode!.Lookup("probe")!.Inode);
                ((IDisposable)sb).Dispose();
            }
            finally
            {
                TestWorkspace.DeleteDirectory(root);
            }
        }
    }
}
