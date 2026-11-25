#if FIBER_GUEST_AARCH64
using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Loader;

public class Elf64LoaderTests
{
    [Fact]
    public void ProcessEntry_Uses64BitVectorsAndRunsTheInterpreter()
    {
        using var runtime = CreateRuntime();
        using var scheduler = new KernelScheduler();
        WriteImage(runtime, "program", Elf64Image.Create(interpreter: "/loader"));
        WriteImage(runtime, "loader", Elf64Image.Create(0, pie: true));

        var task = Start(runtime, scheduler);
        var cpu = task.CPU;
        Assert.Equal(GuestArchitecture.AArch64, cpu.Architecture);
        Assert.Equal(0ul, cpu.StackPointer & 15);
        Assert.Equal(0ul, cpu.ReadArmRegister(0));
        Assert.Equal(0ul, cpu.ReadArmRegister(30));

        var layout = task.Process.Mem.Layout!;
        var loadBase = layout.InterpreterBaseHint & ~0xffffu;
        Assert.Equal((ulong)loadBase + Elf64Image.EntryOffset, cpu.InstructionPointer);

        var stackPointer = checked((uint)cpu.StackPointer);
        var stack = new byte[layout.InitialStackTop - stackPointer];
        Assert.True(cpu.CopyFromUser(stackPointer, stack));
        ulong Word(int index) => BinaryPrimitives.ReadUInt64LittleEndian(stack.AsSpan(index * 8));
        uint StringOffset(ulong address) => checked((uint)address - stackPointer);
        string CString(ulong address)
        {
            var bytes = stack.AsSpan((int)StringOffset(address));
            return System.Text.Encoding.UTF8.GetString(bytes[..bytes.IndexOf((byte)0)]);
        }

        Assert.Equal(2ul, Word(0));
        Assert.Equal("/program", CString(Word(1)));
        Assert.Equal("arg", CString(Word(2)));
        Assert.Equal(0ul, Word(3));
        Assert.Equal("TEST=value", CString(Word(4)));
        Assert.Equal(0ul, Word(5));

        var auxiliary = new Dictionary<ulong, ulong>();
        for (var index = 6; Word(index) != 0; index += 2)
            auxiliary.Add(Word(index), Word(index + 1));
        Assert.Equal("aarch64", CString(auxiliary[LinuxConstants.AT_PLATFORM]));
        Assert.Equal("/program", CString(auxiliary[LinuxConstants.AT_EXECFN]));
        Assert.Equal(layout.AuxRandomBytes, stack.AsSpan((int)StringOffset(auxiliary[LinuxConstants.AT_RANDOM]), 16).ToArray());
        Assert.Equal((ulong)LinuxConstants.PageSize, auxiliary[LinuxConstants.AT_PAGESZ]);
        Assert.Equal((ulong)loadBase, auxiliary[LinuxConstants.AT_BASE]);
        Assert.Equal((ulong)Elf64Image.VirtualAddress + Elf64Image.EntryOffset, auxiliary[LinuxConstants.AT_ENTRY]);
        Assert.Equal((ulong)Elf64Image.VirtualAddress + Elf64Image.ProgramHeaderOffset, auxiliary[LinuxConstants.AT_PHDR]);
        Assert.Equal((ulong)Elf64Image.ProgramHeaderSize, auxiliary[LinuxConstants.AT_PHENT]);
        Assert.Equal(2ul, auxiliary[LinuxConstants.AT_PHNUM]);

        var bss = new byte[Elf64Image.MemorySize - Elf64Image.FileSize];
        Assert.True(cpu.CopyFromUser(Elf64Image.VirtualAddress + Elf64Image.FileSize, bss));
        Assert.All(bss, value => Assert.Equal((byte)0, value));

        scheduler.Run(100);
        Assert.True(task.Exited);
        Assert.Equal(42, task.ExitStatus);
    }

    [Fact]
    public void ProcessEntry_RejectsA32BitInterpreterForAnAArch64Image()
    {
        using var runtime = CreateRuntime();
        using var scheduler = new KernelScheduler();
        WriteImage(runtime, "program", Elf64Image.Create(interpreter: "/loader"));
        var elf32 = new byte[52];
        Elf64Image.Create().AsSpan(0, 24).CopyTo(elf32);
        elf32[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf32.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(elf32.AsSpan(40), 52);
        WriteImage(runtime, "loader", elf32);

        var error = Assert.Throws<ExecFileException>(() => Start(runtime, scheduler));
        Assert.Equal(Errno.ELIBBAD, error.Error);
    }

    [Fact]
    public void ProcessEntry_RejectsAnAddressOutsideTheGuestMappingRange()
    {
        using var runtime = CreateRuntime();
        using var scheduler = new KernelScheduler();
        var image = Elf64Image.Create();
        var load = image.AsSpan(Elf64Image.ProgramHeaderOffset, Elf64Image.ProgramHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(load[16..], 0x1_0040_0000);
        WriteImage(runtime, "program", image);

        Assert.Throws<FatalExecException>(() => Start(runtime, scheduler));
        Assert.Null(runtime.Memory.FindVmArea(Elf64Image.VirtualAddress));
    }

    private static KernelRuntime CreateRuntime()
    {
        var runtime = KernelRuntime.BootstrapBare(false);
        var tmpfsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memory) => new Tmpfs(memoryContext: memory)
        };
        var root = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(tmpfsType, 0, "elf64-test", null);
        runtime.Syscalls.MountRoot(root, new SyscallManager.RootMountOptions
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        });
        return runtime;
    }

    private static void WriteImage(KernelRuntime runtime, string name, byte[] image) =>
        runtime.Syscalls.WriteFileInDetachedMount(runtime.Syscalls.RootMount!, name, image, 0x1ed);

    private static FiberTask Start(KernelRuntime runtime, KernelScheduler scheduler)
    {
        var (location, path) = runtime.Syscalls.ResolvePath("/program", true);
        return ProcessFactory.CreateInitProcess(runtime, location.Dentry!, path, ["/program", "arg"], ["TEST=value"],
            scheduler, mount: location.Mount);
    }
}
#endif
