using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

internal sealed class MappedFileBackendTestEnvironment : IDisposable
{
    private readonly string _scratch;
    public const uint Address = 0x51000000;

    public MappedFileBackendTestEnvironment(string backend, int length, bool buffered = false)
    {
        var temp = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
            : Path.GetTempPath();
        _scratch = Path.Combine(temp, $"mapped-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
        Context = buffered
            ? new MemoryRuntimeContext(new HostMemoryMapGeometry(4096, 4096, 4096, false, false))
            : new MemoryRuntimeContext();
        Engine = new Engine(Context);
        Memory = new VMAManager(Context);
        Syscalls = new SyscallManager(Engine, Memory, 0);
        try
        {
            if (backend == "hostfs")
            {
                BackingPath = Path.Combine(_scratch, "data.bin");
                System.IO.File.WriteAllBytes(BackingPath, []);
                Syscalls.MountRootHostfs(_scratch);
            }
            else
            {
                Assert.Equal("silkfs", backend);
                var type = FileSystemRegistry.Get("silkfs")!;
                var sb = type.CreateFileSystem(Syscalls.DeviceNumbers, Context).ReadSuper(type, 0, _scratch, null);
                Syscalls.MountRoot(sb, new SyscallManager.RootMountOptions { FsType = "silkfs", Source = _scratch });
                var root = Syscalls.Root.Dentry!;
                var child = new Dentry(FsName.FromString("data.bin"), null, root, root.SuperBlock);
                Assert.Equal(0, root.Inode!.Create(child, 0x1A4, 0, 0));
            }

            var location = Syscalls.PathWalk("/data.bin");
            Assert.True(location.IsValid);
            Assert.Equal(backend, location.Mount!.FsType);
            Inode = Assert.IsAssignableFrom<MappingBackedInode>(location.Dentry!.Inode);
            File = new LinuxFile(location.Dentry, FileFlags.O_RDWR, location.Mount);
            Inode.Open(File);
            Assert.Equal(0, Inode.Truncate(length));
            if (backend == "silkfs")
                BackingPath = Assert.IsType<SilkSuperBlock>(Inode.SuperBlock).Repository.GetLiveInodePath((long)Inode.Ino);

            // Seed native backing only during setup, before any guest mapping/cache lease.
            // SilkFS's size metadata was established via its API above.
            using var seed = new FileStream(BackingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var page = new byte[LinuxConstants.PageSize];
            for (var offset = 0; offset < length; offset += page.Length)
            {
                Array.Fill(page, Pattern((uint)(offset / page.Length)));
                seed.Write(page, 0, Math.Min(page.Length, length - offset));
            }
            seed.Flush();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MemoryRuntimeContext Context { get; }
    public Engine Engine { get; }
    public VMAManager Memory { get; }
    public SyscallManager Syscalls { get; }
    public LinuxFile File { get; private set; } = null!;
    public MappingBackedInode Inode { get; private set; } = null!;
    public string BackingPath { get; private set; } = null!;
    public long TotalPooledAllocations => Context.GetAllocationClassStats().Sum(stat => stat.AllocatedPages);
    public static byte Pattern(uint index) => (byte)(index % 251 + 1);

    public void Map(long offset = 0, uint address = Address, bool shared = true)
    {
        File.Get();
        try
        {
            Memory.Mmap(address, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                (shared ? MapFlags.Shared : MapFlags.Private) | MapFlags.Fixed, File, offset, "contract", Engine);
        }
        catch
        {
            File.Close();
            throw;
        }
    }

    public byte Read(uint address)
    {
        var bytes = new byte[1];
        Assert.True(Engine.CopyFromUser(address, bytes));
        return bytes[0];
    }

    public void CloseFile()
    {
        if (File != null)
        {
            File.Close();
            File = null!;
        }
    }

    public void Dispose()
    {
        try
        {
            Memory.Clear(Engine);
            if (File != null)
            {
                File.Close();
                File = null!;
            }
            Syscalls.Close();
        }
        finally
        {
            Engine.Dispose();
            Context.Dispose();
            TestWorkspace.DeleteDirectory(_scratch);
        }
    }
}
