use std::collections::{HashMap, HashSet, VecDeque};
use std::ptr;
use std::sync::atomic::{AtomicU64, AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};

use smoltcp::iface::{Config, Interface, SocketHandle, SocketSet};
use smoltcp::phy::{Loopback, Medium};
use smoltcp::socket::tcp::{self, Socket as TcpSocket, SocketBuffer as TcpSocketBuffer, State as TcpState};
use smoltcp::socket::udp::{
    PacketBuffer as UdpPacketBuffer, PacketMetadata as UdpPacketMetadata, Socket as UdpSocket,
};
use smoltcp::time::Instant;
use smoltcp::wire::{HardwareAddress, IpAddress, IpCidr, IpEndpoint, IpListenEndpoint, Ipv4Address};

type NotifyCallback = extern "C" fn(usize);

const TCP_BUFFER_SIZE: usize = 16 * 1024;
const UDP_BUFFER_SIZE: usize = 16 * 1024;
const UDP_METADATA_COUNT: usize = 16;
const ERR_INVALID_ARG: i32 = -1;
const ERR_NOT_FOUND: i32 = -2;
const ERR_INVALID_STATE: i32 = -3;
const ERR_WOULD_BLOCK: i32 = -4;
const ERR_PROTOCOL: i32 = -5;
const ERR_ADDR_IN_USE: i32 = -6;

#[derive(Clone)]
enum SocketObject {
    TcpStream(TcpStreamObject),
    TcpListener(TcpListenerObject),
    UdpSocket(UdpSocketObject),
}

#[derive(Clone, Copy)]
struct TcpStreamObject {
    handle: SocketHandle,
    local_port: Option<u16>,
    is_accepted: bool,
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
    local_port: Option<u16>,
}

struct ClosingStreamEntry {
    handle: SocketHandle,
    local_port: Option<u16>,
    is_accepted: bool,
    closed_at_ms: Option<i64>,
    last_send_queue: usize,
    last_progress_ms: Option<i64>,
}

struct NamespaceState {
    iface: Interface,
    device: Loopback,
    sockets: SocketSet<'static>,
    ipv4_be: u32,
    prefix_len: u8,
    next_ephemeral_port: u16,
    last_poll_ms: Option<i64>,
    reserved_tcp_ports: HashSet<u16>,
    tcp_listener_ports: HashMap<u16, u32>,
    tcp_outbound_ports: HashMap<u16, u32>,
    tcp_accepted_ports: HashMap<u16, u32>,
    udp_ports: HashMap<u16, u32>,
    all_ports: HashMap<u16, u32>,
    closing_streams: Vec<ClosingStreamEntry>,
    objects: HashMap<u64, SocketObject>,
    notify_cb: Option<NotifyCallback>,
    notify_userdata: usize,
    notify_pending: AtomicBool,
}


impl NamespaceState {
    fn notify(&self) {
        if let Some(cb) = self.notify_cb {
            if !self.notify_pending.swap(true, Ordering::AcqRel) {
                cb(self.notify_userdata);
            }
        }
    }
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
        last_poll_ms: None,
        reserved_tcp_ports: HashSet::new(),
        tcp_listener_ports: HashMap::new(),
        tcp_outbound_ports: HashMap::new(),
        tcp_accepted_ports: HashMap::new(),
        udp_ports: HashMap::new(),
        all_ports: HashMap::new(),
        closing_streams: Vec::new(),
        objects: HashMap::new(),
        notify_cb: None,
        notify_userdata: 0,
        notify_pending: AtomicBool::new(false),
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
    state.objects.insert(
        object_handle,
        SocketObject::TcpStream(TcpStreamObject {
            handle,
            local_port: None,
            is_accepted: false,
        }),
    );
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
    state.objects.insert(
        object_handle,
        SocketObject::UdpSocket(UdpSocketObject {
            handle,
            local_port: None,
        }),
    );
    object_handle
}

fn inc_port(map: &mut HashMap<u16, u32>, port: u16) {
    *map.entry(port).or_insert(0) += 1;
}

fn dec_port(map: &mut HashMap<u16, u32>, port: u16) {
    if let Some(count) = map.get_mut(&port) {
        if *count <= 1 {
            map.remove(&port);
        } else {
            *count -= 1;
        }
    }
}

fn release_stream_port(state: &mut NamespaceState, local_port: Option<u16>, is_accepted: bool) {
    if let Some(port) = local_port {
        if is_accepted {
            dec_port(&mut state.tcp_accepted_ports, port);
        } else {
            dec_port(&mut state.tcp_outbound_ports, port);
        }
        dec_port(&mut state.all_ports, port);
    }
}

fn with_namespace<R>(ns_handle: u64, f: impl FnOnce(&mut NamespaceState) -> R) -> Result<R, i32> {
    let mut guard = namespaces().lock().unwrap();
    let Some(state) = guard.get_mut(&ns_handle) else {
        return Err(ERR_NOT_FOUND);
    };

    Ok(f(state))
}

fn poll_namespace(state: &mut NamespaceState, now: Instant) {
    let poll_res = state.iface.poll(now, &mut state.device, &mut state.sockets);
    let mut changed = !matches!(poll_res, smoltcp::iface::PollResult::None);
    let now_ms = now.total_millis();
    state.last_poll_ms = Some(now_ms);

    let listener_handles: Vec<u64> = state
        .objects
        .iter()
        .filter_map(|(key, value)| match value {
            SocketObject::TcpListener(_) => Some(*key),
            _ => None,
        })
        .collect();

    for listener_handle in listener_handles {
        changed |= promote_pending_accept(state, listener_handle);
    }

    if !state.closing_streams.is_empty() {
        const FIN_LINGER_MS: i64 = 5_000;
        const NO_PROGRESS_CLOSE_MS: i64 = 30_000;
        const MAX_CLOSING_LINGER_MS: i64 = 120_000;

        let mut idx = 0;
        while idx < state.closing_streams.len() {
            let should_remove = {
                let entry = &mut state.closing_streams[idx];
                let closed_at_ms = *entry.closed_at_ms.get_or_insert(now_ms);
                let socket = state.sockets.get_mut::<TcpSocket>(entry.handle);
                let cur_send_queue = socket.send_queue();
                let last_progress_ms = match entry.last_progress_ms {
                    Some(ts) => {
                        if cur_send_queue < entry.last_send_queue {
                            entry.last_send_queue = cur_send_queue;
                            entry.last_progress_ms = Some(now_ms);
                            now_ms
                        } else {
                            ts
                        }
                    }
                    None => {
                        entry.last_send_queue = cur_send_queue;
                        entry.last_progress_ms = Some(now_ms);
                        now_ms
                    }
                };

                let remove = match socket.state() {
                    TcpState::Closed => true,
                    TcpState::FinWait2 | TcpState::TimeWait
                        if cur_send_queue == 0 && now_ms.saturating_sub(closed_at_ms) >= FIN_LINGER_MS =>
                    {
                        true
                    }
                    _ if cur_send_queue == 0 && now_ms.saturating_sub(closed_at_ms) >= NO_PROGRESS_CLOSE_MS => true,
                    _ if now_ms.saturating_sub(last_progress_ms) >= NO_PROGRESS_CLOSE_MS => true,
                    _ if now_ms.saturating_sub(closed_at_ms) >= MAX_CLOSING_LINGER_MS => true,
                    _ => false,
                };
                if remove && socket.state() != TcpState::Closed {
                    socket.abort();
                }
                remove
            };

            if should_remove {
                let entry = state.closing_streams.swap_remove(idx);
                state.sockets.remove(entry.handle);
                release_stream_port(state, entry.local_port, entry.is_accepted);
                changed = true;
            } else {
                idx += 1;
            }
        }
    }

    if changed {
        state.notify();
    }
}

fn promote_pending_accept(state: &mut NamespaceState, listener_handle: u64) -> bool {
    let Some(SocketObject::TcpListener(listener)) = state.objects.get(&listener_handle).cloned() else {
        return false;
    };

    let socket = state.sockets.get::<TcpSocket>(listener.handle);
    if socket.is_listening() || !socket.is_active() {
        return false;
    }

    let Some(local_port) = listener.local_port else {
        return false;
    };

    let accepted_handle = alloc_handle();
    state.objects.insert(
        accepted_handle,
        SocketObject::TcpStream(TcpStreamObject {
            handle: listener.handle,
            local_port: Some(local_port),
            is_accepted: true,
        }),
    );

    let replacement_socket_handle = add_tcp_socket(state);
    {
        let replacement = state.sockets.get_mut::<TcpSocket>(replacement_socket_handle);
        if replacement.listen(local_port).is_err() {
            let _ = state.objects.remove(&accepted_handle);
            state.sockets.remove(replacement_socket_handle);
            return false;
        }
    }

    inc_port(&mut state.tcp_accepted_ports, local_port);
    inc_port(&mut state.all_ports, local_port);

    if let Some(SocketObject::TcpListener(listener_mut)) = state.objects.get_mut(&listener_handle) {
        listener_mut.handle = replacement_socket_handle;
        listener_mut.pending_accepts.push_back(accepted_handle);
        true
    } else {
        false
    }
}

fn is_tcp_listener_port_in_use(
    state: &NamespaceState,
    port: u16,
    ignore_socket_handle: Option<u64>,
) -> bool {
    let count = state.tcp_listener_ports.get(&port).copied().unwrap_or(0);
    if count == 0 {
        return false;
    }
    if let Some(handle) = ignore_socket_handle {
        if let Some(SocketObject::TcpListener(listener)) = state.objects.get(&handle) {
            if listener.local_port == Some(port) {
                return count > 1;
            }
        }
    }
    true
}

fn is_outbound_tcp_port_in_use(state: &NamespaceState, port: u16) -> bool {
    state.tcp_outbound_ports.contains_key(&port)
}

fn is_udp_port_in_use(
    state: &NamespaceState,
    port: u16,
    ignore_socket_handle: Option<u64>,
) -> bool {
    let count = state.udp_ports.get(&port).copied().unwrap_or(0);
    if count == 0 {
        return false;
    }
    if let Some(handle) = ignore_socket_handle {
        if let Some(SocketObject::UdpSocket(udp)) = state.objects.get(&handle) {
            if udp.local_port == Some(port) {
                return count > 1;
            }
        }
    }
    true
}

fn is_port_in_use(state: &NamespaceState, port: u16) -> bool {
    state.all_ports.contains_key(&port)
}

fn allocate_local_port(state: &mut NamespaceState) -> Option<u16> {
    let mut candidate = state.next_ephemeral_port.max(49152);
    for _ in 0..=(u16::MAX - 49152) {
        let next = if candidate == u16::MAX { 49152 } else { candidate + 1 };
        if !is_port_in_use(state, candidate) {
            state.next_ephemeral_port = next;
            return Some(candidate);
        }
        candidate = next;
    }
    None
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
pub extern "C" fn fiber_netns_set_notify_callback(
    handle: u64,
    cb: Option<NotifyCallback>,
    userdata: usize,
) -> i32 {
    with_namespace(handle, |state| {
        state.notify_cb = cb;
        state.notify_userdata = userdata;
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_netns_clear_notify(handle: u64) -> i32 {
    with_namespace(handle, |state| {
        state.notify_pending.store(false, Ordering::Release);
        0
    })
    .unwrap_or_else(|rc| rc)
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
pub extern "C" fn fiber_netns_allocate_ephemeral_port(ns_handle: u64, out_port: *mut u16) -> i32 {
    if out_port.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(port) = allocate_local_port(state) else {
            return ERR_ADDR_IN_USE;
        };
        if state.reserved_tcp_ports.insert(port) {
            inc_port(&mut state.all_ports, port);
        }
        unsafe { ptr::write(out_port, port) };
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_netns_reserve_tcp_port(ns_handle: u64, port: u16) -> i32 {
    if port == 0 {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        if state.reserved_tcp_ports.contains(&port)
            || is_tcp_listener_port_in_use(state, port, None)
            || is_outbound_tcp_port_in_use(state, port)
        {
            return ERR_ADDR_IN_USE;
        }
        if state.reserved_tcp_ports.insert(port) {
            inc_port(&mut state.all_ports, port);
        }
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_netns_release_tcp_port(ns_handle: u64, port: u16) -> i32 {
    if port == 0 {
        return 0;
    }

    with_namespace(ns_handle, |state| {
        if state.reserved_tcp_ports.remove(&port) {
            dec_port(&mut state.all_ports, port);
        }
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_listen(ns_handle: u64, socket_handle: u64, local_port: u16, backlog: u32) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpListener(listener)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let listen_port = if local_port == 0 {
            let Some(port) = allocate_local_port(state) else {
                return ERR_ADDR_IN_USE;
            };
            port
        } else if is_tcp_listener_port_in_use(state, local_port, Some(socket_handle)) {
            return ERR_ADDR_IN_USE;
        } else {
            local_port
        };

        let socket = state.sockets.get_mut::<TcpSocket>(listener.handle);
        if socket.listen(listen_port).is_err() {
            return ERR_PROTOCOL;
        }

        if listener.local_port != Some(listen_port) {
            if let Some(old_port) = listener.local_port {
                dec_port(&mut state.tcp_listener_ports, old_port);
                dec_port(&mut state.all_ports, old_port);
            }
            inc_port(&mut state.tcp_listener_ports, listen_port);
            inc_port(&mut state.all_ports, listen_port);
        }

        if let Some(SocketObject::TcpListener(listener_mut)) = state.objects.get_mut(&socket_handle) {
            listener_mut.local_port = Some(listen_port);
            listener_mut.backlog = backlog as usize;
        }

        state.notify();
        0
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_listener_get_local_port(ns_handle: u64, socket_handle: u64, out_port: *mut u16) -> i32 {
    if out_port.is_null() {
        return ERR_INVALID_ARG;
    }

    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpListener(listener)) = state.objects.get(&socket_handle) else {
            return ERR_NOT_FOUND;
        };
        let Some(port) = listener.local_port else {
            return ERR_INVALID_STATE;
        };
        unsafe { ptr::write(out_port, port) };
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

        let Some(local_port) = allocate_local_port(state) else {
            return ERR_ADDR_IN_USE;
        };
        let [a, b, c, d] = remote_ipv4_be.to_be_bytes();
        let remote_ip = Ipv4Address::new(a, b, c, d);

        let cx = state.iface.context();
        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        if socket
            .connect(cx, (IpAddress::Ipv4(remote_ip), remote_port), local_port)
            .is_err()
        {
            return ERR_PROTOCOL;
        }

        if stream.local_port != Some(local_port) {
            release_stream_port(state, stream.local_port, stream.is_accepted);
            inc_port(&mut state.tcp_outbound_ports, local_port);
            inc_port(&mut state.all_ports, local_port);
        }
        if let Some(SocketObject::TcpStream(stream_mut)) = state.objects.get_mut(&socket_handle) {
            stream_mut.local_port = Some(local_port);
        }

        state.notify();
        0
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
        state.notify();
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
                state.notify();
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
            if socket.may_recv() {
                return ERR_WOULD_BLOCK;
            }

            unsafe { ptr::write(out_read, 0) };
            return 0;
        }

        let slice = unsafe { std::slice::from_raw_parts_mut(buffer, len) };
        match socket.recv_slice(slice) {
            Ok(read) => {
                unsafe { ptr::write(out_read, read) };
                state.notify();
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
pub extern "C" fn fiber_tcp_stream_send_available(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        if !socket.may_send() {
            return 0;
        }
        socket.send_capacity().saturating_sub(socket.send_queue()) as i32
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_may_read(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        if socket.may_recv() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_may_write(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get::<TcpSocket>(stream.handle);
        if socket.may_send() { 1 } else { 0 }
    })
    .unwrap_or_else(|rc| rc)
}

#[no_mangle]
pub extern "C" fn fiber_tcp_stream_close(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(SocketObject::TcpStream(stream)) = state.objects.get(&socket_handle).cloned() else {
            return ERR_NOT_FOUND;
        };

        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        socket.close();
        state.notify();
        0
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

fn close_or_linger_tcp_stream(state: &mut NamespaceState, stream: TcpStreamObject) {
    let (should_remove_immediately, send_queue) = {
        let socket = state.sockets.get_mut::<TcpSocket>(stream.handle);
        let remove = match socket.state() {
            TcpState::Closed | TcpState::Listen => true,
            _ => {
                socket.close();
                socket.state() == TcpState::Closed
            }
        };
        (remove, socket.send_queue())
    };

    if should_remove_immediately {
        state.sockets.remove(stream.handle);
        release_stream_port(state, stream.local_port, stream.is_accepted);
    } else {
        let ts = state.last_poll_ms;
        state.closing_streams.push(ClosingStreamEntry {
            handle: stream.handle,
            local_port: stream.local_port,
            is_accepted: stream.is_accepted,
            closed_at_ms: ts,
            last_send_queue: send_queue,
            last_progress_ms: ts,
        });
    }
}

#[no_mangle]
pub extern "C" fn fiber_socket_close(ns_handle: u64, socket_handle: u64) -> i32 {
    with_namespace(ns_handle, |state| {
        let Some(object) = state.objects.remove(&socket_handle) else {
            return ERR_NOT_FOUND;
        };

        match object {
            SocketObject::TcpStream(stream) => {
                close_or_linger_tcp_stream(state, stream);
            }
            SocketObject::TcpListener(listener) => {
                state.sockets.remove(listener.handle);
                if let Some(port) = listener.local_port {
                    dec_port(&mut state.tcp_listener_ports, port);
                    dec_port(&mut state.all_ports, port);
                }
                for pending in listener.pending_accepts {
                    if let Some(SocketObject::TcpStream(stream)) = state.objects.remove(&pending) {
                        close_or_linger_tcp_stream(state, stream);
                    }
                }
            }
            SocketObject::UdpSocket(udp) => {
                state.sockets.remove(udp.handle);
                if let Some(port) = udp.local_port {
                    dec_port(&mut state.udp_ports, port);
                    dec_port(&mut state.all_ports, port);
                }
            }
        }

        state.notify();
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

        let bind_port = if local_port == 0 {
            let Some(port) = allocate_local_port(state) else {
                return ERR_ADDR_IN_USE;
            };
            port
        } else if is_udp_port_in_use(state, local_port, Some(socket_handle)) {
            return ERR_ADDR_IN_USE;
        } else {
            local_port
        };

        let socket = state.sockets.get_mut::<UdpSocket>(udp.handle);
        if socket.bind(bind_port).is_err() {
            return ERR_PROTOCOL;
        }

        if udp.local_port != Some(bind_port) {
            if let Some(old_port) = udp.local_port {
                dec_port(&mut state.udp_ports, old_port);
                dec_port(&mut state.all_ports, old_port);
            }
            inc_port(&mut state.udp_ports, bind_port);
            inc_port(&mut state.all_ports, bind_port);
        }

        if let Some(SocketObject::UdpSocket(udp_mut)) = state.objects.get_mut(&socket_handle) {
            udp_mut.local_port = Some(bind_port);
        }

        state.notify();
        0
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
