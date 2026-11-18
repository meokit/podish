using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Podish.Core.Graphics;

internal static class ContainerGles
{
    internal const string GuestPath = "/run/podish-gles";

    internal static AngleDevice Mount(SyscallManager syscalls, FramebufferDevice? framebuffer)
    {
        if (OperatingSystem.IsBrowser() || OperatingSystem.IsIOS())
            throw new ContainerConfigurationException("ANGLE GLES is only available in a desktop host process.");

        var angle = Environment.GetEnvironmentVariable("PODISH_ANGLE_DIR");
        if (string.IsNullOrEmpty(angle))
            throw new ContainerConfigurationException("Set PODISH_ANGLE_DIR to the host ANGLE EGL/GLES library directory.");
        var guest = Environment.GetEnvironmentVariable("PODISH_GLES_GUEST_DIR") ??
                    Path.GetFullPath("artifacts/podish-gles/guest");

        try
        {
            using var source = new MountFile(syscalls.CreateDetachedTmpfsMount("podish-gles"));
            var root = source.Mount!.Root;
            foreach (var (soname, alias) in new[] { ("libEGL.so.1", "libEGL.so"), ("libGLESv2.so.2", "libGLESv2.so") })
            {
                var image = File.ReadAllBytes(Path.Combine(guest, soname));
                if (image.Length < 20 || !image.AsSpan(0, 7).SequenceEqual("\u007fELF\u0001\u0001\u0001"u8) ||
                    image[16] != 3 || image[17] != 0 || image[18] != 3 || image[19] != 0)
                    throw new IOException($"{soname} is not an IA-32 ELF shared library.");
                syscalls.WriteFileInDetachedMount(source.Mount!, soname, image);
                var link = new Dentry(FsName.FromString(alias), null, root, root.SuperBlock);
                var rc = root.Inode!.Symlink(link, soname, 0, 0);
                if (rc != 0) throw new IOException($"Could not create {alias}: rc={rc}");
            }

            var device = new AngleDevice(Path.GetFullPath(angle), framebuffer);
            try
            {
                syscalls.BindMountSubtree(source.Mount!, "", GuestPath, readOnly: true);
                syscalls.RegisterVirtualCharacterDevice(AngleDevice.Name, device.CreateInode);
                return device;
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or
                                   DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            throw new ContainerConfigurationException($"Could not start ANGLE GLES. {ex.Message}");
        }
    }
}
