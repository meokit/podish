namespace Podish.Wayland;

internal sealed class WaylandKeyboardKeymap
{
    private readonly Mount _mount;
    private readonly Dentry _fileDentry;
    private readonly LinuxFile _readOnlyFile;

    public WaylandKeyboardKeymap()
    {
        string keymap = WaylandKeyboardLayout.GenerateXkbKeymap();
        byte[] keymapBytes = System.Text.Encoding.UTF8.GetBytes(keymap);
        if (keymapBytes.Length == 0 || keymapBytes[^1] != 0)
            keymapBytes = [.. keymapBytes, 0];

        var fsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static devMgr => new Tmpfs(devMgr)
        };
        FileSystem fs = fsType.CreateAnonymousFileSystem();
        SuperBlock sb = fs.ReadSuper(fsType, 0, "wayland-keymap", null);
        _mount = new Mount(sb, sb.Root);

        _fileDentry = new Dentry("keymap.xkb", null, sb.Root, sb);
        sb.Root.Inode!.Create(_fileDentry, 0x1A4, 0, 0);

        var writer = new LinuxFile(_fileDentry, FileFlags.O_RDWR, _mount);
        try
        {
            int rc = _fileDentry.Inode!.Write(writer, keymapBytes, 0);
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
