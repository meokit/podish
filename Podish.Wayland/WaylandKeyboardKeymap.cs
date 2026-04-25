using System.Text;
using Fiberish.VFS;

namespace Podish.Wayland;

internal sealed class WaylandKeyboardKeymap
{
    private readonly Dentry _fileDentry;
    private readonly Mount _mount;
    private readonly LinuxFile _readOnlyFile;

    public WaylandKeyboardKeymap()
    {
        var keymap = WaylandKeyboardLayout.GenerateXkbKeymap();
        var keymapBytes = Encoding.UTF8.GetBytes(keymap);
        if (keymapBytes.Length == 0 || keymapBytes[^1] != 0)
            keymapBytes = [.. keymapBytes, 0];

        var fsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static devMgr => new Tmpfs(devMgr)
        };
        var fs = fsType.CreateAnonymousFileSystem();
        var sb = fs.ReadSuper(fsType, 0, "wayland-keymap", null);
        _mount = new Mount(sb, sb.Root);

        _fileDentry = new Dentry(FsName.FromString("keymap.xkb"), null, sb.Root, sb);
        sb.Root.Inode!.Create(_fileDentry, 0x1A4, 0, 0);

        var writer = new LinuxFile(_fileDentry, FileFlags.O_RDWR, _mount);
        try
        {
            var rc = _fileDentry.Inode!.WriteFromHost(null, writer, keymapBytes, 0);
            if (rc < 0)
                throw new IOException($"Failed to write Wayland keymap: rc={rc}");
        }
        finally
        {
            writer.Close();
        }

        _readOnlyFile = new LinuxFile(_fileDentry, FileFlags.O_RDONLY, _mount);
        Size = (uint)keymapBytes.Length;
    }

    public uint Size { get; }

    public LinuxFile OpenReadOnly()
    {
        return _readOnlyFile;
    }
}