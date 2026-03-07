use std::collections::{HashMap, VecDeque};
use std::ptr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

use smoltcp::iface::{Config, Interface, SocketHandle, SocketSet};
use smoltcp::phy::{Loopback, Medium};
use smoltcp::socket::tcp::{self, Socket as TcpSocket, SocketBuffer as TcpSocketBuffer, State as TcpState};
use smoltcp::socket::udp::{
    PacketBuffer as UdpPacketBuffer, PacketMetadata as UdpPacketMetadata, Socket as UdpSocket,
};
use smoltcp::time::Instant;
use smoltcp::wire::{HardwareAddress, IpAddress, IpCidr, IpEndpoint, IpListenEndpoint, Ipv4Address};

const TCP_BUFFER_SIZE: usize = 16 * 1024;
const UDP_BUFFER_SIZE: usize = 16 * 1024;
const UDP_METADATA_COUNT: usize = 16;
const ERR_INVALID_ARG: i32 = -1;
const ERR_NOT_FOUND: i32 = -2;
const ERR_INVALID_STATE: i32 = -3;
const ERR_WOULD_BLOCK: i32 = -4;
const ERR_PROTOCOL: i32 = -5;

#[derive(Clone)]
enum SocketObject {
    TcpStream(TcpStreamObject),
    TcpListener(TcpListenerObject),
    UdpSocket(UdpSocketObject),
}

#[derive(Clone, Copy)]
struct TcpStreamObject {
    handle: SocketHandle,
}

#[derive(Clone)]
struct TcpListenerObject {
    handle: SocketHandle,
    local_port: Option<u16>,
    backlog: usize,
    pending_accepts: VecDeque<u64>,
}

#[derive(Clone, Copy)]
struct UdpSocketObject {
    handle: SocketHandle,
}

struct NamespaceState {
    iface: Interface,
    device: Loopback,
    sockets: SocketSet<'static>,
    ipv4_be: u32,
    prefix_len: u8,
    next_ephemeral_port: u16,
    objects: HashMap<u64, SocketObject>,
}

static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static NAMESPACES: OnceLock<Mutex<HashMap<u64, NamespaceState>>> = OnceLock::new();

fn namespaces() -> &'static Mutex<HashMap<u64, NamespaceState>> {
    NAMESPACES.get_or_init(|| Mutex::new(HashMap::new()))
}

fn alloc_handle() -> u64 {
    NEXT_HANDLE.fetch_add(1, Ordering::Relaxed)
}

fn build_namespace(ipv4_be: u32, prefix_len: u8) -> NamespaceState {
    let mut device = Loopback::new(Medium::Ip);
    let config = Config::new(HardwareAddress::Ip);
    let mut iface = Interface::new(config, &mut device, Instant::ZERO);
    let [a, b, c, d] = ipv4_be.to_be_bytes();
    let ipv4 = Ipv4Address::new(a, b, c, d);
    iface.update_ip_addrs(|addrs| {
        addrs
            .push(IpCidr::new(IpAddress::Ipv4(Ipv4Address::new(127, 0, 0, 1)), 8))
            .expect("loopback netns should have capacity for loopback IPv4 CIDR");
        addrs
            .push(IpCidr::new(IpAddress::Ipv4(ipv4), prefix_len))
            .expect("loopback netns should have capacity for private IPv4 CIDR");
    });

    NamespaceState {
        iface,
        device,
        sockets: SocketSet::new(Vec::new()),
        ipv4_be,
        prefix_len,
        next_ephemeral_port: 49152,
        objects: HashMap::new(),
    }
}

fn add_tcp_socket(state: &mut NamespaceState) -> SocketHandle {
    let rx = TcpSocketBuffer::new(vec![0; TCP_BUFFER_SIZE]);
    let tx = TcpSocketBuffer::new(vec![0; TCP_BUFFER_SIZE]);
    let socket = TcpSocket::new(rx, tx);
    state.sockets.add(socket)
}

fn add_udp_socket(state: &mut NamespaceState) -> SocketHandle {
    let rx = UdpPacketBuffer::new(
        vec![UdpPacketMetadata::EMPTY; UDP_METADATA_COUNT],
        vec![0; UDP_BUFFER_SIZE],
    );
    let tx = UdpPacketBuffer::new(
        vec![UdpPacketMetadata::EMPTY; UDP_METADATA_COUNT],
        vec![0; UDP_BUFFER_SIZE],
    );
    let socket = UdpSocket::new(rx, tx);
    state.sockets.add(socket)
}

fn tcp_stream_create_internal(state: &mut NamespaceState) -> u64 {
    let handle = add_tcp_socket(state);
    let object_handle = alloc_handle();
    state
        .objects
        .insert(object_handle, SocketObject::TcpStream(TcpStreamObject { handle }));
    object_handle
}

fn tcp_listener_create_internal(state: &mut NamespaceState) -> u64 {
    let handle = add_tcp_socket(state);
    let object_handle = alloc_handle();
    state.objects.insert(
        object_handle,
        SocketObject::TcpListener(TcpListenerObject {
            handle,
            local_port: None,
            backlog: 0,
            pending_accepts: VecDeque::new(),
        }),
    );
    object_handle
}

fn udp_socket_create_internal(state: &mut NamespaceState) -> u64 {
    let handle = add_udp_socket(state);
    let object_handle = alloc_handle();
    state
        .objects
        .insert(object_handle, SocketObject::UdpSocket(UdpSocketObject { handle }));
    object_handle
}

fn with_namespace<R>(ns_handle: u64, f: impl FnOnce(&mut NamespaceState) -> R) -> Result<R, i32> {
    let mut guard = namespaces().lock().unwrap();
    let Some(state) = guard.get_mut(&ns_handle) else {
        return Err(ERR_NOT_FOUND);
    };

    Ok(f(state))
}

fn poll_namespace(state: &mut NamespaceState, now: Instant) {
    let _ = state.iface.poll(now, &mut state.device, &mut state.sockets);

    let listener_handles: Vec<u64> = state
        .objects
        .iter()
        .filter_map(|(key, value)| match value {
            SocketObject::TcpListener(_) => Some(*key),
            _ => None,
        })
        .collect();

    for listener_handle in listener_handles {
        promote_pending_accept(state, listener_handle);
    }
}

fn promote_pending_accept(state: &mut NamespaceState, listener_handle: u64) {
    let Some(SocketObject::TcpListener(listener)) = state.objects.get(&listener_handle).cloned() else {
        return;
    };

    let socket = state.sockets.get::<TcpSocket>(listener.handle);
    if socket.is_listening() || !socket.is_active() {
        return;
    }

    let Some(local_port) = listener.local_port else {
        return;
    };

    let accepted_handle = alloc_handle();
    state.objects.insert(
        accepted_handle,
        SocketObject::TcpStream(TcpStreamObject {
            handle: listener.handle,
        }),
    );

    let replacement_socket_handle = add_tcp_socket(state);
    {
        let replacement = state.sockets.get_mut::<TcpSocket>(replacement_socket_handle);
        if replacement.listen(local_port).is_err() {
            let _ = state.objects.remove(&accepted_handle);
            state.sockets.remove(replacement_socket_handle);
            return;
        }
    }

    if let Some(SocketObject::TcpListener(listener_mut)) = state.objects.get_mut(&listener_handle) {
        listener_mut.handle = replacement_socket_handle;
        listener_mut.pending_accepts.push_back(accepted_handle);
    }
}

fn allocate_local_port(state: &mut NamespaceState) -> u16 {
    let port = state.next_ephemeral_port;
    state.next_ephemeral_port = if state.next_ephemeral_port == u16::MAX {
        49152
    } else {
        state.next_ephemeral_port + 1
    };
    port
}

fn next_poll_delay(state: &mut NamespaceState, now: Instant) -> i64 {
    state
        .iface
        .poll_at(now, &state.sockets)
        .map(|deadline| (deadline.total_millis() - now.total_millis()).max(0))
        .unwrap_or(50)
}

fn encode_ipv4_be(addr: Ipv4Address) -> u32 {
    u32::from_be_bytes(addr.octets())
}

fn write_endpoint(endpoint: IpEndpoint, out_ipv4_be: *mut u32, out_port: *mut u16) -> i32 {
    let IpAddress::Ipv4(ipv4) = endpoint.addr;

    if !out_ipv4_be.is_null() {
        unsafe { ptr::write(out_ipv4_be, encode_ipv4_be(ipv4)) };
    }
    if !out_port.is_null() {
        unsafe { ptr::write(out_port, endpoint.port) };
    }
    0
}

#[no_mangle]
pub extern "C" fn fiber_netns_create_loopback(ipv4_be: u32, prefix_len: u8) -> u64 {
    let state = build_namespace(ipv4_be, prefix_len);
    let handle = alloc_handle();
    namespaces().lock().unwrap().insert(handle, state);
    handle
}

#[no_mangle]
pub extern "C" fn fiber_netns_destroy(handle: u64) -> i32 {
    let removed = namespaces().lock().unwrap().remove(&handle);
    if removed.is_some() { 0 } else { ERR_NOT_FOUND }
}

#[no_mangle]
pub extern "C" fn fiber_netns_get_ipv4(handle: u64, out_ipv4_be: *mut u32, out_prefix_len: *mut u8) -> i32 {
    if out_ipv4_be.is_null() || out_prefix_len.is_null() {
        return ERR_INVALID_ARG;
    }

    let guard = namespaces().lock().unwrap();
    let Some(ns) = guard.get(&handle) else {
        return ERR_NOT_FOUND;
    };

    unsafe {
        ptr::write(out_ipv4_be, ns.ipv4_be);
        ptr::write(out_prefix_len, ns.prefix_len);
    }

    0
}

#[no_mangle]
pub extern "C" fn fiber_netns_poll(handle: u64, now_millis: i64, out_next_poll_millis: *mut i64) -> i32 {
    with_namespace(handle, |state| {
        let now = Instant::from_millis(now_millis);
        poll_namespace(state, now);
        let next = next_poll_delay(state, now);

        if !out_next_poll_millis.is_null() {
            unsafe { ptr::write(out_next_poll_millis, next) };
        }

        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_create(ns_handle: u64) -> u64 {
    with_namespace(ns_handle, tcp_stream_create_internal).unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_create(ns_handle: u64) -> u64 {
    with_namespace(ns_handle, tcp_listener_create_internal).unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_create(ns_handle: u64) -> u64 {
    with_namespace(ns_handle, udp_socket_create_internal).unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_listen(ns_handle: u64, socket_handle: u64, local_port: u16, backlog: u32) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpListener(listener)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<TcpSocket>(listener.handle);
        if socket.listen(local_port).is_err() {
            return ERR_PROTOCOL;
        }

        if let Some(SocketObject::TcpListener(listener_mut)) = state.objects.get_mut(&socket_handle) {
            listener_mut.local_port = Some(local_port);
            listener_mut.backlog = backlog as usize;
        }

        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_connect(
    ns_handle: u64,
    socket_handle: u64,
    remote_ipv4_be: u32,
    remote_port: u16,
) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let local_port = allocate_local_port(state);
        let [a, b, c, d] = remote_ipv4_be.to_be_bytes();
        let remote_ip = Ipv4Address::new(a, b, c, d);

        let cx = state.iface.context();
        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        socket
            .connect(cx, (IpAddress::Ipv4(remote_ip), remote_port), local_port)
            .map(|_| 0)
            .unwrap_or(ERR_PROTOCOL)
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_accept(ns_handle: u64, socket_handle: u64, out_socket_handle: *mut u64) -> i32 {
    if out_socket_handle.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpListener(listener)) = state.objects.get_mut(&socket_handle) else {
            return ERR_NOT_FOUND;
        };

        let Some(accepted) = listener.pending_accepts.pop_front() else {
            return ERR_WOULD_BLOCK;
        };

        unsafe { ptr::write(out_socket_handle, accepted) };
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_send(
    ns_handle: u64,
    socket_handle: u64,
    data: *const u8,
    len: usize,
    out_written: *mut usize,
) -> i32 {
    if data.is_null() || (len > 0 && out_written.is_null()) {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        if !socket.may_send() {
            return ERR_INVALID_STATE;
        }

        let slice = unsafe { std::slice::from_raw_parts(data, len) };
        match socket.send_slice(slice) {
            Ok(written) => {
                unsafe { ptr::write(out_written, written) };
                0
            }
            Err(tcp::SendError::InvalidState) => ERR_INVALID_STATE,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_recv(
    ns_handle: u64,
    socket_handle: u64,
    buffer: *mut u8,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    if buffer.is_null() || out_read.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        if !socket.can_recv() {
            return if socket.may_recv() { ERR_WOULD_BLOCK } else { ERR_INVALID_STATE };
        }

        let slice = unsafe { std::slice::from_raw_parts_mut(buffer, len) };
        match socket.recv_slice(slice) {
            Ok(read) => {
                unsafe { ptr::write(out_read, read) };
                0
            }
            Err(tcp::RecvError::InvalidState) => ERR_INVALID_STATE,
            Err(tcp::RecvError::Finished) => {
                unsafe { ptr::write(out_read, 0) };
                0
            }
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_can_read(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        if socket.can_recv() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_can_write(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        if socket.can_send() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_accept_pending(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpListener(listener)) = state.objects.get(&socket_handle) else {
            return ERR_NOT_FOUND;
        };

        if listener.pending_accepts.is_empty() { 0 } else { 1 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_socket_close(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(object) = state.objects.remove(&socket_handle) else {
            return ERR_NOT_FOUND;
        };

        match object {
            SocketObject::TcpStream(stream) => {
                state.sockets.remove(stream.handle);
            }
            SocketObject::TcpListener(listener) => {
                state.sockets.remove(listener.handle);
                for pending in listener.pending_accepts {
                    if let Some(SocketObject::TcpStream(stream)) = state.objects.remove(&pending) {
                        state.sockets.remove(stream.handle);
                    }
                }
            }
            SocketObject::UdpSocket(udp) => {
                state.sockets.remove(udp.handle);
            }
        }

        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_state(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        match socket.state() {
            TcpState::Closed => 0,
            TcpState::Listen => 1,
            TcpState::SynSent => 2,
            TcpState::SynReceived => 3,
            TcpState::Established => 4,
            TcpState::FinWait1 => 5,
            TcpState::FinWait2 => 6,
            TcpState::CloseWait => 7,
            TcpState::Closing => 8,
            TcpState::LastAck => 9,
            TcpState::TimeWait => 10,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_get_local_endpoint(
    ns_handle: u64,
    socket_handle: u64,
    out_ipv4_be: *mut u32,
    out_port: *mut u16,
) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        match socket.local_endpoint() {
            Some(endpoint) => write_endpoint(endpoint, out_ipv4_be, out_port),
            None => ERR_INVALID_STATE,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_get_remote_endpoint(
    ns_handle: u64,
    socket_handle: u64,
    out_ipv4_be: *mut u32,
    out_port: *mut u16,
) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        match socket.remote_endpoint() {
            Some(endpoint) => write_endpoint(endpoint, out_ipv4_be, out_port),
            None => ERR_INVALID_STATE,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_bind(ns_handle: u64, socket_handle: u64, local_port: u16) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<UdpSocket>(udp.handle);
        socket
            .bind(local_port)
            .map(|_| 0)
            .unwrap_or(ERR_PROTOCOL)
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_send_to(
    ns_handle: u64,
    socket_handle: u64,
    remote_ipv4_be: u32,
    remote_port: u16,
    data: *const u8,
    len: usize,
    out_written: *mut usize,
) -> i32 {
    if data.is_null() || out_written.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let [a, b, c, d] = remote_ipv4_be.to_be_bytes();
        let remote_ip = Ipv4Address::new(a, b, c, d);
        let endpoint = IpEndpoint::new(IpAddress::Ipv4(remote_ip), remote_port);
        let socket = state.sockets.get_mut::<UdpSocket>(udp.handle);
        let slice = unsafe { std::slice::from_raw_parts(data, len) };

        match socket.send_slice(slice, endpoint) {
            Ok(()) => {
                unsafe { ptr::write(out_written, len) };
                0
            }
            Err(_) => ERR_WOULD_BLOCK,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_recv_from(
    ns_handle: u64,
    socket_handle: u64,
    buffer: *mut u8,
    len: usize,
    out_read: *mut usize,
    out_ipv4_be: *mut u32,
    out_port: *mut u16,
) -> i32 {
    if buffer.is_null() || out_read.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<UdpSocket>(udp.handle);
        if !socket.can_recv() {
            return ERR_WOULD_BLOCK;
        }

        let slice = unsafe { std::slice::from_raw_parts_mut(buffer, len) };
        match socket.recv_slice(slice) {
            Ok((read, metadata)) => {
                unsafe { ptr::write(out_read, read) };
                write_endpoint(metadata.endpoint, out_ipv4_be, out_port)
            }
            Err(_) => ERR_WOULD_BLOCK,
        }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_can_read(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<UdpSocket>(udp.handle);
        if socket.can_recv() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_can_write(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<UdpSocket>(udp.handle);
        if socket.can_send() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_udp_socket_get_local_endpoint(
    ns_handle: u64,
    socket_handle: u64,
    out_ipv4_be: *mut u32,
    out_port: *mut u16,
) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<UdpSocket>(udp.handle);
        match socket.endpoint() {
            IpListenEndpoint { addr: Some(IpAddress::Ipv4(ipv4)), port } => {
                if !out_ipv4_be.is_null() {
                    unsafe { ptr::write(out_ipv4_be, encode_ipv4_be(ipv4)) };
                }
                if !out_port.is_null() {
                    unsafe { ptr::write(out_port, port) };
                }
                0
            }
            IpListenEndpoint { addr: None, port } => {
                if !out_ipv4_be.is_null() {
                    unsafe { ptr::write(out_ipv4_be, 0) };
                }
                if !out_port.is_null() {
                    unsafe { ptr::write(out_port, port) };
                }
                0
            }
        }
    })
    .unwrap_or_else(|rc| rc)
}
