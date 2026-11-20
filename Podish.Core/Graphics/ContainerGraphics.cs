using System.Text;
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
                if (kmsDrm)
                {
                    DrmCardInode? card = null;
                    syscalls.RegisterVirtualCharacterDevice("dri/card0",
                        sb => card = new DrmCardInode(sb, scheduler, device?.HasKmsOutput == true, framebuffer,
                            device is { HasKmsOutput: true } ? device.AllocateTextureBo : null));
                    syscalls.RegisterVirtualCharacterDevice("dri/renderD128", _ => card!.CreateRenderNode());
                    if (framebuffer != null) syscalls.RegisterFramebufferEvdev(framebuffer, scheduler);
                    MountGraphicsSysfs(syscalls, framebuffer != null);
                }
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

    private static void MountGraphicsSysfs(SyscallManager syscalls, bool input)
    {
        using var source = new MountFile(syscalls.CreateDetachedTmpfsMount("podish-graphics-sysfs"));
        var mount = source.Mount!;
        // libudev verifies the filesystem type of the canonical device directory.
        mount.Root.SuperBlock.Type = new FileSystemType { Name = "sysfs" };
        const string device = "devices/platform/podish-drm";
        WriteNode("card0", 0);
        WriteNode("renderD128", 128);
        syscalls.WriteFileInDetachedMount(mount, device + "/uevent", "DRIVER=podish\nMODALIAS=platform:podish-drm\n"u8);
        syscalls.WriteFileInDetachedMount(mount, "bus/platform/uevent", []);
        syscalls.CreateSymlinkInDetachedMount(mount, device + "/subsystem", "/sys/bus/platform");

        if (input) MountInputDiscovery(syscalls, mount);
        foreach (var tree in new[] { "dev/char", "class/drm", device, "bus/platform" })
            syscalls.BindMountSubtree(mount, tree, "/sys/" + tree, readOnly: true);

        void WriteNode(string name, int minor)
        {
            var path = device + "/drm/" + name;
            var uevent = $"MAJOR=226\nMINOR={minor}\nDEVNAME=dri/{name}\nDEVTYPE=drm_minor\n";
            syscalls.WriteFileInDetachedMount(mount, path + "/uevent", System.Text.Encoding.ASCII.GetBytes(uevent));
            syscalls.WriteFileInDetachedMount(mount, path + "/dev", System.Text.Encoding.ASCII.GetBytes($"226:{minor}\n"));
            syscalls.CreateSymlinkInDetachedMount(mount, path + "/subsystem", "/sys/class/drm");
            syscalls.CreateSymlinkInDetachedMount(mount, path + "/device", "/sys/" + device);
            syscalls.CreateSymlinkInDetachedMount(mount, $"dev/char/226:{minor}", "/sys/" + path);
            syscalls.CreateSymlinkInDetachedMount(mount, "class/drm/" + name, "/sys/" + path);
        }
    }

    private static void MountInputDiscovery(SyscallManager syscalls, Mount sysfs)
    {
        using var database = new MountFile(syscalls.CreateDetachedTmpfsMount("podish-input-udev"));
        var udev = database.Mount!;
        const string devices = "devices/virtual/input";
        for (var index = 0; index < 2; index++)
        {
            var keyboard = index == 0;
            var parent = $"{devices}/input{index}";
            var name = $"event{index}";
            var path = $"{parent}/{name}";
            var number = $"13:{64 + index}";
            var deviceName = keyboard ? "Podish Keyboard" : "Podish Mouse";
            var category = keyboard ? "KEYBOARD" : "MOUSE";
            syscalls.WriteFileInDetachedMount(sysfs, parent + "/name", Encoding.ASCII.GetBytes(deviceName + "\n"));
            syscalls.WriteFileInDetachedMount(sysfs, parent + "/uevent",
                Encoding.ASCII.GetBytes($"NAME=\"{deviceName}\"\nPRODUCT=6/0/0/1\n"));
            syscalls.WriteFileInDetachedMount(sysfs, path + "/uevent",
                Encoding.ASCII.GetBytes($"MAJOR=13\nMINOR={64 + index}\nDEVNAME=input/{name}\n"));
            syscalls.WriteFileInDetachedMount(sysfs, path + "/dev", Encoding.ASCII.GetBytes(number + "\n"));
            syscalls.CreateSymlinkInDetachedMount(sysfs, parent + "/subsystem", "/sys/class/input");
            syscalls.CreateSymlinkInDetachedMount(sysfs, path + "/subsystem", "/sys/class/input");
            syscalls.CreateSymlinkInDetachedMount(sysfs, $"class/input/input{index}", "/sys/" + parent);
            syscalls.CreateSymlinkInDetachedMount(sysfs, "class/input/" + name, "/sys/" + path);
            syscalls.CreateSymlinkInDetachedMount(sysfs, "dev/char/" + number, "/sys/" + path);
            syscalls.WriteFileInDetachedMount(udev, "data/c" + number, Encoding.ASCII.GetBytes(
                $"I:1\nE:ID_INPUT=1\nE:ID_INPUT_{category}=1\nE:ID_SEAT=seat0\nG:seat\nQ:seat\nV:1\n"));
            syscalls.WriteFileInDetachedMount(udev, "tags/seat/c" + number, []);
        }
        syscalls.BindMountSubtree(sysfs, devices, "/sys/" + devices, readOnly: true);
        syscalls.BindMountSubtree(sysfs, "class/input", "/sys/class/input", readOnly: true);
        syscalls.BindMountSubtree(udev, "", "/run/udev", readOnly: true);
    }
}
