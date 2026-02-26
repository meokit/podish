using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class OverlayTests
{
    [Fact]
    public void TestRecursiveCopyUpWithHostfs()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempWork = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);
        Directory.CreateDirectory(tempWork);
        
        try {
            var nestedDir = Path.Combine(tempLower, "a/b/c");
            Directory.CreateDirectory(nestedDir);
            var filePath = Path.Combine(nestedDir, "file");
            File.WriteAllText(filePath, "hello");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

            var overlayFs = new OverlayFileSystem();
            var options = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(new FileSystemType { Name = "overlay" }, 0, "overlay", options);

            // Lookup the file in overlay
            var root = overlaySb.Root;
            var a_ov = root.Inode!.Lookup("a");
            var b_ov = a_ov!.Inode!.Lookup("b");
            var c_ov = b_ov!.Inode!.Lookup("c");
            var file_ov = c_ov!.Inode!.Lookup("file")!;

            var overlayInode = file_ov.Inode as OverlayInode;
            Assert.NotNull(overlayInode);
            Assert.Null(overlayInode.UpperDentry);

            // Open the file as O_WRONLY
            var linuxFile = new LinuxFile(file_ov, FileFlags.O_WRONLY, null!);
            overlayInode.Open(linuxFile);
            var initialHandle = linuxFile.PrivateData;
            Assert.NotNull(initialHandle);

            // Trigger CopyUp via Write
            overlayInode.Write(linuxFile, "world"u8.ToArray(), 5);

            Assert.NotNull(overlayInode.UpperDentry);
            Assert.NotEqual(initialHandle, linuxFile.PrivateData); // Handle should have been redirected
            
            // Check if parents were created in upper FS host path
            Assert.True(Directory.Exists(Path.Combine(tempUpper, "a/b/c")));
            Assert.True(File.Exists(Path.Combine(tempUpper, "a/b/c/file")));
            
            // Verify content in upper
            Assert.Equal("helloworld", File.ReadAllText(Path.Combine(tempUpper, "a/b/c/file")));
        } finally {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
            if (Directory.Exists(tempWork)) Directory.Delete(tempWork, true);
        }
    }
}
