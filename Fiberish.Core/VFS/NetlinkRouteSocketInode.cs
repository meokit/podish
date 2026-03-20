using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public sealed class NetlinkRouteSocketInode : Inode, ITaskWaitSource, IDispatcherWaitSource
{
    private readonly Queue<byte[]> _responses = new();
    private readonly Func<NetDeviceSetSnapshot> _snapshotProvider;
    private AsyncWaitQueue _readWaitQueue = new();

    public NetlinkRouteSocketInode(ulong ino, SuperBlock sb, Func<NetDeviceSetSnapshot> snapshotProvider)
    {
        Ino = ino;
        SuperBlock = sb;
        Type = InodeType.Socket;
        Mode = 0x1ED;
        _snapshotProvider = snapshotProvider;
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        
        return default;
    }

    public ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> payload, int flags)
    {
        return ValueTask.FromResult(HandleWrite(payload.Span));
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return 0;
        while (true)
        {
            byte[]? message = null;
            AsyncWaitQueue? waitQueue = null;
            using (EnterStateScope())
            {
                if (_responses.Count > 0)
                {
                    message = _responses.Dequeue();
                    if (_responses.Count == 0)
                        _readWaitQueue = new AsyncWaitQueue();
                }
                else
                {
                    waitQueue = _readWaitQueue;
                }
            }

            if (message != null)
            {
                var copyLen = Math.Min(recvLen, message.Length);
                message.AsSpan(0, copyLen).CopyTo(buffer);
                return copyLen;
            }

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                return -(int)Errno.EAGAIN;

            if (task.HasUnblockedPendingSignal())
                return -(int)Errno.ERESTARTSYS;

            var result = await (waitQueue ?? _readWaitQueue).WaitAsync(task);
            if (result == AwaitResult.Interrupted)
                return -(int)Errno.ERESTARTSYS;
        }
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return HandleWrite(buffer);
    }

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        using (EnterStateScope())
        {
            if (_responses.Count == 0)
                return -(int)Errno.EAGAIN;

            var message = _responses.Dequeue();
            if (_responses.Count == 0)
                _readWaitQueue = new AsyncWaitQueue();

            var copyLen = Math.Min(buffer.Length, message.Length);
            message.AsSpan(0, copyLen).CopyTo(buffer);
            return copyLen;
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        if ((events & PollEvents.POLLOUT) != 0)
            revents |= PollEvents.POLLOUT;

        using (EnterStateScope())
        {
            if ((events & PollEvents.POLLIN) != 0 && _responses.Count > 0)
                revents |= PollEvents.POLLIN;
        }

        return revents;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        return null;
    }

    public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events)
    {
        _ = task;
        return RegisterWaitHandle(file, task, callback, events) != null;
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile file, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        return ((IDispatcherWaitSource)this).RegisterWaitHandle(file, dispatcher, callback, events) != null;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        _ = task;
        if ((events & PollEvents.POLLIN) == 0)
            return null;
        using (EnterStateScope())
        {
            return _readWaitQueue.RegisterCancelable(callback, task);
        }
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile file, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        if ((events & PollEvents.POLLIN) == 0)
            return null;
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException(
                            "Netlink readiness wait requires an explicit scheduler.");
        using (EnterStateScope())
        {
            return _readWaitQueue.RegisterCancelable(callback, scheduler);
        }
    }

    private static void BuildLinkDump(NetDeviceSetSnapshot snapshot, uint seq, List<byte[]> responses)
    {
        foreach (var dev in snapshot.Devices)
        {
            var attrs = new List<byte[]>(3)
            {
                BuildAttr(LinuxConstants.IFLA_IFNAME, dev.Name + "\0"),
                BuildAttr(LinuxConstants.IFLA_ADDRESS, dev.MacAddress),
                BuildAttrU32(LinuxConstants.IFLA_MTU, (uint)dev.Mtu)
            };

            var payloadLen = 16 + attrs.Sum(a => a.Length);
            var totalLen = 16 + payloadLen;
            var msg = new byte[Align4(totalLen)];
            WriteNlmsgHeader(msg, (uint)totalLen, LinuxConstants.RTM_NEWLINK,
                LinuxConstants.NLM_F_MULTI, seq, 0);
            var off = 16;
            msg[off] = LinuxConstants.AF_INET;
            msg[off + 1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(off + 2, 2),
                dev.Name == "lo" ? LinuxConstants.ARPHRD_LOOPBACK : LinuxConstants.ARPHRD_ETHER);
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(off + 4, 4), dev.IfIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(off + 8, 4), dev.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(off + 12, 4), 0xffffffffu);
            off += 16;

            foreach (var attr in attrs)
            {
                attr.CopyTo(msg, off);
                off += attr.Length;
            }

            responses.Add(msg.AsSpan(0, totalLen).ToArray());
        }

        responses.Add(BuildDone(seq));
    }

    private static void BuildAddrDump(NetDeviceSetSnapshot snapshot, uint seq, List<byte[]> responses)
    {
        foreach (var dev in snapshot.Devices)
        {
            if (dev.Ipv4Address == null)
                continue;

            var attrs = new List<byte[]>(3)
            {
                BuildAttr(LinuxConstants.IFA_LOCAL, dev.Ipv4Address.GetAddressBytes()),
                BuildAttr(LinuxConstants.IFA_ADDRESS, dev.Ipv4Address.GetAddressBytes()),
                BuildAttr(LinuxConstants.IFA_LABEL, dev.Name + "\0")
            };

            var payloadLen = 8 + attrs.Sum(a => a.Length);
            var totalLen = 16 + payloadLen;
            var msg = new byte[Align4(totalLen)];
            WriteNlmsgHeader(msg, (uint)totalLen, LinuxConstants.RTM_NEWADDR,
                LinuxConstants.NLM_F_MULTI, seq, 0);
            var off = 16;
            msg[off] = LinuxConstants.AF_INET;
            msg[off + 1] = dev.Ipv4PrefixLength;
            msg[off + 2] = 0;
            msg[off + 3] = dev.Name == "lo" ? LinuxConstants.RT_SCOPE_HOST : LinuxConstants.RT_SCOPE_UNIVERSE;
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(off + 4, 4), dev.IfIndex);
            off += 8;

            foreach (var attr in attrs)
            {
                attr.CopyTo(msg, off);
                off += attr.Length;
            }

            responses.Add(msg.AsSpan(0, totalLen).ToArray());
        }

        responses.Add(BuildDone(seq));
    }

    private static byte[] BuildDone(uint seq)
    {
        var done = new byte[16];
        WriteNlmsgHeader(done, 16, LinuxConstants.NLMSG_DONE, 0, seq, 0);
        return done;
    }

    private static void WriteNlmsgHeader(Span<byte> dst, uint len, ushort type, ushort flags, uint seq, uint pid)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), len);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12, 4), pid);
    }

    private static byte[] BuildAttr(ushort type, string ascii)
    {
        return BuildAttr(type, Encoding.ASCII.GetBytes(ascii));
    }

    private static byte[] BuildAttrU32(ushort type, uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return BuildAttr(type, payload);
    }

    private static byte[] BuildAttr(ushort type, byte[] payload)
    {
        var len = 4 + payload.Length;
        var aligned = Align4(len);
        var attr = new byte[aligned];
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(0, 2), (ushort)len);
        BinaryPrimitives.WriteUInt16LittleEndian(attr.AsSpan(2, 2), type);
        payload.CopyTo(attr, 4);
        return attr;
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }

    private int HandleWrite(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16)
            return -(int)Errno.EINVAL;

        var nlmsgLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (nlmsgLen < 16 || nlmsgLen > payload.Length)
            return -(int)Errno.EINVAL;

        var nlmsgType = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var nlmsgFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var nlmsgSeq = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        var snapshot = _snapshotProvider();

        var responses = new List<byte[]>();
        if ((nlmsgFlags & LinuxConstants.NLM_F_DUMP) != 0)
        {
            if (nlmsgType == LinuxConstants.RTM_GETLINK)
                BuildLinkDump(snapshot, nlmsgSeq, responses);
            else if (nlmsgType == LinuxConstants.RTM_GETADDR)
                BuildAddrDump(snapshot, nlmsgSeq, responses);
        }

        if (responses.Count == 0)
            return payload.Length;

        AsyncWaitQueue waitQueue;
        using (EnterStateScope())
        {
            foreach (var response in responses)
                _responses.Enqueue(response);
            waitQueue = _readWaitQueue;
        }

        waitQueue.Set();
        return payload.Length;
    }

    // Single-thread scheduling model: keep using-scope syntax for future lock insertion.
    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
