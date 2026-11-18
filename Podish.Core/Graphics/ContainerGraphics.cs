using Fiberish.Core;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Graphics;

internal static class ContainerGraphics
{
    internal const string GuestPath = "/run/podish-gles";

    internal static AngleDevice? Mount(SyscallManager syscalls, KernelScheduler scheduler,
        FramebufferDevice? framebuffer, bool gles, bool kmsDrm, KmsEglWindow? kmsWindow, ILogger logger)
    {
        if (gles && (OperatingSystem.IsBrowser() || OperatingSystem.IsIOS()))
            throw new ContainerConfigurationException("ANGLE GLES is only available in a desktop host process.");

        var angle = Environment.GetEnvironmentVariable("PODISH_ANGLE_DIR");
        if (gles && string.IsNullOrEmpty(angle))
            throw new ContainerConfigurationException("Set PODISH_ANGLE_DIR to the host ANGLE EGL/GLES library directory.");
        var guest = Environment.GetEnvironmentVariable("PODISH_GLES_GUEST_DIR") ??
                    Path.GetFullPath("artifacts/podish-gles/guest");

        try
        {
            using var source = new MountFile(syscalls.CreateDetachedTmpfsMount("podish-gles"));
            var root = source.Mount!.Root;
            var libraries = new List<(string Soname, string Alias)>();
            if (gles) libraries.AddRange([("libEGL.so.1", "libEGL.so"), ("libGLESv2.so.2", "libGLESv2.so")]);
            if (kmsDrm) libraries.Add(("libgbm.so.1", "libgbm.so"));
            foreach (var (soname, alias) in libraries)
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

            var device = gles ? new AngleDevice(Path.GetFullPath(angle!), framebuffer, kmsWindow, logger) : null;
            try
            {
                syscalls.BindMountSubtree(source.Mount!, "", GuestPath, readOnly: true);
                if (device != null) syscalls.RegisterVirtualCharacterDevice(AngleDevice.Name, device.CreateInode);
                if (kmsDrm) syscalls.RegisterVirtualCharacterDevice("dri/card0", sb => new DrmCardInode(sb, scheduler, device?.HasKmsOutput == true));
                return device;
            }
            catch
            {
                device?.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or
                                   DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            throw new ContainerConfigurationException($"Could not start guest graphics. {ex.Message}");
        }
    }
}
