using Fiberish.Core.Native;
using System.Net;
using System.Runtime.InteropServices;

namespace Fiberish.Core.Net;

public sealed class LoopbackNetNamespace : IDisposable
{
    private ulong _handle;

    private LoopbackNetNamespace(ulong handle, uint ipv4Be, byte prefixLen)
    {
        _handle = handle;
        Ipv4AddressBe = ipv4Be;
        PrefixLength = prefixLen;
    }

    public uint Ipv4AddressBe { get; }
    public byte PrefixLength { get; }
    public ulong Handle => _handle;
    public IPAddress PrivateIpv4Address => new([(byte)(Ipv4AddressBe >> 24), (byte)(Ipv4AddressBe >> 16), (byte)(Ipv4AddressBe >> 8), (byte)Ipv4AddressBe]);

    public static LoopbackNetNamespace Create(uint ipv4Be, byte prefixLen)
    {
        var handle = NetstackNative.CreateLoopback(ipv4Be, prefixLen);
        if (handle == 0)
            throw new InvalidOperationException("Failed to create loopback net namespace.");

        var rc = NetstackNative.GetIpv4(handle, out var actualIp, out var actualPrefix);
        if (rc != 0)
        {
            NetstackNative.Destroy(handle);
            throw new InvalidOperationException($"Failed to read net namespace address (rc={rc}).");
        }

        return new LoopbackNetNamespace(handle, actualIp, actualPrefix);
    }

    public long Poll(long nowMillis)
    {
        ThrowIfDisposed();
        var rc = NetstackNative.Poll(_handle, nowMillis, out var nextPollMillis);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to poll net namespace (rc={rc}).");
        return nextPollMillis;
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;

        var handle = _handle;
        _handle = 0;
        NetstackNative.Destroy(handle);
    }

    public TcpListenerSocket CreateTcpListener()
    {
        ThrowIfDisposed();
        var socket = NetstackNative.CreateTcpListener(_handle);
        if (socket == 0)
            throw new InvalidOperationException("Failed to create TCP listener socket.");
        return new TcpListenerSocket(this, socket);
    }

    public TcpStreamSocket CreateTcpStream()
    {
        ThrowIfDisposed();
        var socket = NetstackNative.CreateTcpStream(_handle);
        if (socket == 0)
            throw new InvalidOperationException("Failed to create TCP stream socket.");
        return new TcpStreamSocket(this, socket);
    }

    private void ThrowIfDisposed()
    {
        if (_handle == 0)
            throw new ObjectDisposedException(nameof(LoopbackNetNamespace));
    }

    public sealed class TcpListenerSocket : IDisposable
    {
        private readonly LoopbackNetNamespace _namespace;
        private ulong _handle;

        internal TcpListenerSocket(LoopbackNetNamespace @namespace, ulong handle)
        {
            _namespace = @namespace;
            _handle = handle;
        }

        public void Listen(ushort localPort, uint backlog = 16)
        {
            ThrowIfDisposed();
            var rc = NetstackNative.TcpListenerListen(_namespace.Handle, _handle, localPort, backlog);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to listen on TCP socket (rc={rc}).");
        }

        public bool AcceptPending
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpListenerAcceptPending(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query accept state (rc={rc}).");
                return rc != 0;
            }
        }

        public TcpStreamSocket Accept()
        {
            ThrowIfDisposed();
            var rc = NetstackNative.TcpListenerAccept(_namespace.Handle, _handle, out var accepted);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to accept TCP socket (rc={rc}).");
            return new TcpStreamSocket(_namespace, accepted);
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;

            var handle = _handle;
            _handle = 0;
            NetstackNative.CloseSocket(_namespace.Handle, handle);
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(TcpListenerSocket));
        }
    }

    public sealed class TcpStreamSocket : IDisposable
    {
        private readonly LoopbackNetNamespace _namespace;
        private ulong _handle;

        internal TcpStreamSocket(LoopbackNetNamespace @namespace, ulong handle)
        {
            _namespace = @namespace;
            _handle = handle;
        }

        public void Connect(uint remoteIpv4Be, ushort remotePort)
        {
            ThrowIfDisposed();
            var rc = NetstackNative.TcpStreamConnect(_namespace.Handle, _handle, remoteIpv4Be, remotePort);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to connect TCP socket (rc={rc}).");
        }

        public bool CanRead
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamCanRead(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query read readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public bool CanWrite
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamCanWrite(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query write readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public int State
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamState(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP state (rc={rc}).");
                return rc;
            }
        }

        public IPEndPoint LocalEndPoint
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetLocalEndpoint(_namespace.Handle, _handle, out var ipv4Be, out var port);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query local endpoint (rc={rc}).");
                return new IPEndPoint(new IPAddress([(byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be]), port);
            }
        }

        public IPEndPoint RemoteEndPoint
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetRemoteEndpoint(_namespace.Handle, _handle, out var ipv4Be, out var port);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query remote endpoint (rc={rc}).");
                return new IPEndPoint(new IPAddress([(byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be]), port);
            }
        }

        public int Send(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var rc = NetstackNative.TcpStreamSend(_namespace.Handle, _handle, ptr, (nuint)data.Length, out var written);
                    if (rc != 0)
                        throw new InvalidOperationException($"Failed to send TCP data (rc={rc}).");
                    return checked((int)written);
                }
            }
        }

        public int Receive(Span<byte> buffer)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    var rc = NetstackNative.TcpStreamRecv(_namespace.Handle, _handle, ptr, (nuint)buffer.Length, out var read);
                    if (rc != 0)
                        throw new InvalidOperationException($"Failed to receive TCP data (rc={rc}).");
                    return checked((int)read);
                }
            }
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;

            var handle = _handle;
            _handle = 0;
            NetstackNative.CloseSocket(_namespace.Handle, handle);
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(TcpStreamSocket));
        }
    }
}
