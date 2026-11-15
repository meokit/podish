# net-io socket option 台账（阶段 A/B）

状态：2026-11-15 实施记录。范围是 `NetstackSocketInode`（`--network private`）的 IPv4
`SOCK_STREAM`/`SOCK_DGRAM`。未修改上游 LTP。完整设计见 `NET_IO_REMAINING_DESIGN.md`，
后续计划见 `NET_IO_NEXT_IMPLEMENTATION_PLAN.md`。

## Native 依据

`tests/ltp/probes/sockopts.c` 在 WSL Linux 5.15 i386 上以 root 运行，实际输出：

```text
SO_SNDBUF set 65536      rc=0 value=131072 errno=0
SO_RCVBUF set 1024       rc=0 value=2304 errno=0
SO_SNDBUFFORCE neg       rc=0 value=4608 errno=0
TCP_NODELAY              rc=0 value=1 errno=0
SO_KEEPALIVE             rc=0 value=1 errno=0
TCP_KEEPIDLE             rc=0 value=100 errno=0
TCP_KEEPINTVL            rc=0 value=30 errno=0
TCP_KEEPCNT              rc=0 value=5 errno=0
SO_OOBINLINE             rc=0 value=1 errno=0
SO_REUSEPORT             rc=0 value=1 errno=0
SO_LINGER {1,0}          set_rc=0 get_rc=0 onoff=1 linger=0
MCAST_JOIN (TCP)         rc=0 value=0 errno=0
MCAST_LEAVE (TCP)        rc=0 value=0 errno=0
SNDBUF after bind        rc=0 value=65536 errno=0
```

因此 Linux 的 set/get 对下列选项都返回成功，包括本后端暂不能完全实现的几项。Podish
对不支持的项改为明确 errno，并在本文件记录为**能力偏差**，不伪装成功。

## 逐项状态

| 选项 | Podish 行为 | 真实副作用 / 验证 | 偏差 |
| --- | --- | --- | --- |
| `SO_REUSEADDR` | 保存并成功 | 后端没有监听 `TIME_WAIT` 状态，bind 冲突策略不因该位改变 | 无（Linux 同样无条件接受；可观察差异不存在于该后端） |
| `SO_REUSEPORT` | `ENOPROTOOPT` | 无 | 是：不支持端口分发/负载均衡 |
| `SO_KEEPALIVE` | 保存；TCP 流应用 | 映射到 smoltcp keepalive；`TCP_KEEPIDLE` 控制间隔 | 仅部分：smoltcp 不做 Linux 的探测次数/间隔分离 |
| `SO_OOBINLINE` | `ENOPROTOOPT` | 无紧急数据路径 | 是：不伪造 urgent data |
| `SO_SNDBUF`/`SO_RCVBUF` | 按 Linux 倍增/下限计算并应用到真实缓冲区 | `131072`/`2304` 与 native 相同；发送/接收容量和后端队列随之为准 | 部分：socket 已 connect/bind 后无法重建缓冲区，返回 `EOPNOTSUPP`；Linux 此时仍成功 |
| `SO_SNDBUFFORCE`/`SO_RCVBUFFORCE` | `CAP_NET_ADMIN` 门控，绕过自动上限（上限 8 MiB） | 负值/超大值落到协议下限，与 native 一致 | 部分：超过 8 MiB 返回 `ENOMEM`，未逐项对照 kmalloc 失败阈值 |
| `SO_LINGER` | 保存并回读 | `{on,0}` 在最终关闭时 `abort()` 发送 RST；正超时走后端有界优雅关闭 | 部分：正超时不阻塞 `close(2)` 到完整 drain |
| `SO_RCVTIMEO`/`SO_SNDTIMEO` | 保存、回读、影响等待 | 阻塞等待超时路径 | 无（timeval 精度为毫秒级调度） |
| `MCAST_JOIN_GROUP`/`LEAVE_GROUP` | 解析 `group_req`，按 socket 记录、按 namespace 引用计数接入 smoltcp 过滤 | 加入后可接收该组；未加入收不到；accepted socket 不继承（accept02） | 接口索引只接受 0（默认）或 1（lo）；没有 IGMP 外部网络 |
| `TCP_NODELAY` | 保存并应用 smoltcp `nagle_enabled` | native/C# 均可读回 | 无 |
| `TCP_KEEPIDLE` | 保存并作为 keepalive 间隔 | native 100 秒；C# 回读 100 | 部分（与 KEEPALIVE 模型一致） |
| `TCP_KEEPINTVL`/`TCP_KEEPCNT` | `ENOPROTOOPT` | 无 | 是：后端没有独立探测间隔/次数 |

## Host 模式（B 阶段同期审计）

`HostSocketInode` 使用 Windows 宿主 socket；能力与 netstack 不同，单独记录：

| 选项 | Podish host 行为 | 说明 |
| --- | --- | --- |
| `SO_REUSEADDR`/`SO_KEEPALIVE`/`SO_OOBINLINE`/`SO_LINGER`/`SO_SNDBUF`/`SO_RCVBUF` | 真正调用宿主 socket 选项 | 可回读 |
| `SO_RCVTIMEO`/`SO_SNDTIMEO` | 保存、参与等待，现可回读 timeval | Linux timeval 语义 |
| `TCP_NODELAY` | 真正应用/回读 | 无 |
| `TCP_KEEPIDLE`/`TCP_KEEPINTVL`/`TCP_KEEPCNT` | 通过 .NET `TcpKeepAliveTime`/`Interval`/`RetryCount` 应用和回读 | 平台不支持时 `ENOPROTOOPT`，不再假成功 |
| `SO_REUSEPORT` | `ENOPROTOOPT` | Windows 没有 Linux 式端口分发 |
| `ICMPV6_FILTER` | `ENOPROTOOPT` | 宿主栈没有对应过滤控制 |
| `SO_ERROR` | 回写**正** errno | 修正原先写负 errno 的 ABI 错误 |

## 顺带修复的真实缺陷

- `MCAST_JOIN_GROUP` 原实现从 `group_req` 的错误偏移（sockaddr 起点）读取，且按宿主字节序
  解释 IPv4 地址，实际所有加入都落到固定值 `2`；leave 因集合键错误而不可靠。现在读取
  `ifindex`、`sin_family`、`sin_addr` 的正确位置并做组地址校验。
- `SO_SNDBUF`/`RCVBUF`/`SO_KEEPALIVE`/`TCP_*` 原先一律返回成功但不保存；现在要么接入
  后端，要么明确拒绝。
- `SO_LINGER` 原先只校验长度；现在真正影响关闭行为。
- host 模式的 `SO_ERROR` 原先返回负 errno；Linux ABI 要求正值，已改正。host 的
  `TCP_KEEPIDLE/KEEPINTVL/KEEPCNT`、`SO_REUSEPORT`、`ICMPV6_FILTER` 也不再假成功。
- 拒绝不支持选项时仍先做 Linux 的 `optlen` 校验：例如 `SO_OOBINLINE`/`SO_REUSEPORT`
  的 0/短长度返回 `EINVAL`，合法长度才返回 `ENOPROTOOPT`。该顺序由未修改的 `setsockopt01`
  回归捕获并修正。
- 缓冲区/日志等默认值按后端实际容量回读（当前默认 16 KiB，而不是固定谎报 16 KiB 以外的值）。

## 测试

- `Fiberish.Tests/VFS/NetstackSocketOptionTests.cs`：真实容量、连接后拒绝扩容、UDP bind 后
  拒绝扩容、Nagle/keepalive 状态、linger RST vs FIN、multicast 过滤与成员引用。
- `SocketSyscallTests`：set/get 的 syscall 级倍增/下限、TCP 选项回读、KEEPINTVL/OOBINLINE/
  REUSEPORT 明确拒绝、accept 不继承 multicast。
- `tests/ltp/probes/sockopts.c`：上述 native 输出。
- `accept02`、`setsockopt01/03/04` 与完整 net-io 批次在本轮重新验收。

## 仍开放

1. live socket 的 buffer 重设需要可重建/可缩放的队列模型，或明确的 API 设计；当前是诚实的
   `EOPNOTSUPP` 偏差。
2. `SO_REUSEPORT` 的端口分发、`TCP_KEEPINTVL/KEEPCNT`、`SO_OOBINLINE` 的紧急数据，
   以及正超时 `SO_LINGER` 的阻塞 drain 尚未实现。
3. user namespace 权限接入后，`SO_*BUFFFORCE` 的检查对象要从 `EUID==0` 改为所属
   netns owner userns 的 `CAP_NET_ADMIN`（阶段 G）。
4. Host 模式 socket 的选项语义独立于本台账，仍需按第 B 阶段方法审计。
