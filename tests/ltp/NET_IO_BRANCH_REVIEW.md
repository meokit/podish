# net-io 全分支 Code Review（进行中）

范围：基线 `c321caa`（不含）到 `5bff69f`（含）以及当前工作区全部修改、新增文件。
包括 `690e94e`、`5bff69f`，不是只审查最后一轮补丁。未提交、合并或推送。

## B 轮：socket option 真实语义（验证进行中）

- 新台账与 native 依据：`NET_IO_SOCKET_OPTION_MATRIX.md`、`probes/sockopts.c`。
- 已把 `SO_SNDBUF/RCVBUF`、`SO_SNDBUFFORCE/RCVBUFORCE`、`SO_KEEPALIVE`、`TCP_NODELAY`、
  `TCP_KEEPIDLE`、`SO_LINGER` 接入真实 smoltcp 状态；`getsockopt` 现在回读实际容量与状态，
  `SO_RCVTIMEO/SNDTIMEO` 也可回读。
- 原 multicast 实现读错 `group_req` 偏移并按宿主字节序解释地址，所有加入实际都落到常量 2；
  已修正为正确的接口索引/family/网络字节序地址，并用 namespace 引用计数接入 smoltcp 组过滤。
  新增测试证明未加入时收不到、加入后可收到、离开后再次收不到，且 accepted socket 不继承。
- 无后端等价物的 `SO_REUSEPORT`、`SO_OOBINLINE`、`TCP_KEEPINTVL`、`TCP_KEEPCNT` 改为明确
  `ENOPROTOOPT`；live socket 的 buffer 重设返回 `EOPNOTSUPP`。这些是相对 Linux 的能力偏差，
  已逐项记录，不允许再用“成功 no-op”维持旧 PASS。
- 本轮 C#：核心 **1900 PASS / 0 FAIL / 1 SKIP**；SilkFS 13、Pulse 49、Wayland 19 全过；
  runner 22/22；`diff --check` 通过。TRX：`artifacts/verification/net-io-sockopts.trx` 等。
- 本轮完整 net-io：**63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP，0 FAIL / 0 TIMEOUT**；
  73 例状态与修改前完全一致，`accept02` 与 `setsockopt01/03/04` 均 PASS。首轮曾因
  `SO_OOBINLINE` 的 optlen 校验顺序让 `setsockopt01` FAIL，已按 Linux 先 `EINVAL` 后
  `ENOPROTOOPT` 修正并加 C# 回归。
- Node tmpfs/silkfs **24/24 PASS**（真实 exit 0 + 标记，无 ExpectCrash），汇总
  `artifacts/verification/node-matrix-sockopts.json`；源 digest
  `7fe518f46ef3ad69877ac5d504ddf6d2d2f251a9473ab9b47fe44ff054ce9451`。
- 仍开放：live buffer 重建、REUSEPORT 分发、正超时 linger drain、TCP_KEEPINTVL/KEEPCNT、
  OOBINLINE，以及 user-namespace 权限接入；host 与 netstack 的选项已无“成功 no-op”。

## 上一轮 P1 修复（已完成）

- 通知队列新增可取消 delivery 票据：订阅注销可取消尚未执行的 foreign-dispatcher 回调，
  不消耗独占预算，并推进同一源的其余监听/后续 publication；迟到回调不能重复推进。
- publication 完成与取消改用串行 trampoline，避免重入通知或大积压取消导致递归栈溢出。
  队列专项目前 10/10 PASS，覆盖 2 万次积压/重入及 100 轮取消与回调竞态。
- epoll 的最后关闭路径已改为先退役 watch、取消 thread-safe 源租约，再把 ready-list、
  legacy 注册及父子关系清理投递至 owner scheduler。所有目标 Poll/重新注册以 watch 生命周期
  门锁与目标关闭同步，避免 Inode.Release/PrivateData teardown 之后再 Poll。
- 本轮 C#：**1891 PASS / 0 FAIL / 1 SKIP**，包括新增 6 个队列用例和 3 个关闭用例；
  SilkFS/Pulse/Wayland **13/49/19 全过**；runner **22/22**；diff --check 通过。
  TRX：`artifacts/verification/net-io-p1-full.trx`、`silkfs-p1.trx`、`pulse-p1.trx`、`wayland-p1.trx`。
- 本轮重新生成的 CLI 已串行通过完整 guest 回归：net-io **63 PASS / 5 PARTIAL / 4 BROKEN /
  1 SKIP，0 FAIL / 0 TIMEOUT**；73 个用例的状态与上一检查点完全一致。
  本轮源 digest：`e68c2a29680fa18ca106c6965df95f6811b90a10d5418ad7db7d9ff4199b876e`。
- 本轮 Node tmpfs/silkfs **24/24 PASS**，每个后端仍为三组参数 10+1+1 次，逐次核验真实 exit 0、
  完成标记、无超时、未启用 ExpectCrash。汇总 `artifacts/verification/node-matrix-p1.json`。
  上一轮 guest 原始结果保留在 `net-io-pre-p1.json`、`node-matrix.json`；下方历史记录未冒充新结果。

## 结论

**当前不能宣称设计已全部实施或分支可以合并。** mounts 通知与若干 review 修复已有测试；
完整源适配、userns/netns 和下列未解决语义仍是明确缺口。LTP PASS 数不替代 ABI 审查。

## 已修复的发现

- **P1 pipe 扩容损坏数据**：旧实现只修改容量、不扩存储；后续 resize 补丁仍按固定 64 KiB
  回绕。现在 resize/read/write/peek 全部以逻辑容量为准，复制按旧逻辑边界，full-ring head
  也按新容量取模；加入大于 64 KiB、增长/缩小和实际环绕回归。
- **P1 fd 分配失败重复关闭**：`AllocFD` 失败也消费引用，socket/accept/socketpair 不可再 Close；
  socketpair 第二个 fd 失败回滚首个 fd。加入 descriptor-limit 回归。
- **P1 SCM_RIGHTS 安装失败/截断泄漏**：EMFILE 不再作为负 fd 写入 cmsg；未安装引用释放、
  `MSG_CTRUNC` 反映真实安装数、控制区 copyout 失败回滚已安装 fd。加入 EMFILE 回归。
- **P1 UDP cork 静默截断**：超过 IPv4 UDP 有效负载上限时返回 EMSGSIZE 并丢弃 pending cork，
  不再成功确认被截断字节。C# 回归及独立 native i386 oracle 已覆盖。
- **P1 consuming Poll 重复消费**：持久源回调只入候选列表，LT 重入队不再立即 Poll；select 成功
  后不二次扫描；mounts read/seek 不清除事件。关闭目标时在 inode Release 前注销 watch。
- **P1 同一 epoll 多个 LT 等待者漏唤醒**：从源只唤醒一个等待者后，LT 交付留下的 ready candidate
  必须向下一等待者交接；不能要求另一次源边沿。ET 不因此广播。双等待者回归已随全套测试通过。
- **P2 sockaddr/copyout**：输入 sockaddr 上限在分配前校验；recvmsg/recvfrom 传播地址 copyout 错误；
  recvmsg 的地址容量是输出容量，不应因大于 128 字节被拒绝；msg_flags 是输出，不得充当输入 flags。
- **P2 host OOB 被全局拒绝**：保留 HostSocketInode 已有的 OutOfBand 路径，不再被通用 syscall
  入口的新检查屏蔽。其他协议/flags 的完整差分矩阵仍未完成。
- **P2 mount namespace 列表逸出**：有锁 getter 返回可变底层 List 仍无法保护调用方枚举；改为快照。
- **P1 Unix 持久适配接错源**：撤下尚不完整的 adapter，恢复原 readiness 路径。原型将自身
  write queue 当作 peer 写空间源，无掩码普通通知还可能引入不相关 ET 事件。后续实现必须包含
  peer 变化重绑、按实际事件方向过滤、backpressure relief 和半关闭测试，不是简单恢复该原型。
- **P2 接口索引**：SIOCGIFINDEX 返回 snapshot 的真实 IfIndex，而非枚举位置 + 1。
- **P2 Windows 等待错误**：WAIT_FAILED 不能当作正常定时器到期；退回 managed wait/错误路径。
- **P2 runner 指纹不可靠**：按 raw diff bytes 哈希，untracked 名称用 NUL 分隔并对名称/内容作长度
  定界，避免 replacement UTF-8 解码与文件名边界碰撞。runner 单测 22/22。
- **P2 Node 验证脚本跑错 checkout/误判成功**：CLI 从当前 worktree 定位；import/run 使用同一 image
  store 工作目录；必须 marker + exit 0 且每轮成功；日志/JSON 留存；历史失败复现改为显式开关。

## 未解决 / 合并前必须评估

1. **P1 socket option（netstack 部分已按能力收敛）** — 原先 KEEPALIVE/OOBINLINE/SNDBUF/RCVBUF/
   BUFFORCE/LINGER/TCP_*/multicast 全部成功 no-op。当前 netstack 已接入真实后端或明确拒绝，
   台账见 `NET_IO_SOCKET_OPTION_MATRIX.md`。仍开放：live socket 的 buffer 重建（现为
   `EOPNOTSUPP`）、REUSEPORT 分发、TCP_KEEPINTVL/KEEPCNT、OOBINLINE、正超时 linger drain；
   **HostSocketInode 的对应选项尚未按同一方法审计**，因此整个 P1 项不能关闭。
2. **P1 EPOLLEXCLUSIVE 前置未满足** — `AsyncWaitQueue.SubscribePoll` 仍限制到源 scheduler；
   Unix/Host/Netstack/TTY/eventfd/timerfd/inotify/netlink/nested epoll 未全部接入同一持久仲裁契约。
   `PollNotificationQueue` 的跨 dispatcher 反馈原型不等于所有共享源均已支持。syscall 保持拒绝。
3. **P1 跨 scheduler 作用域仍需完整审计** — 本轮已修复 epoll 自身的跨线程最后关闭与目标最后
   关闭、租约取消以及关闭与 in-flight Poll 同步；不会直接从 foreign 线程修改 ready-list。
   队列注销后的同源阻塞已修复，并覆盖停止 dispatcher、迟到回调、100 轮取消/执行竞态。
   但这**不代表任意共享源的跨 scheduler 使用已经支持**：Pipe/Unix 等 inode 自身的 Release、
   Ctl 调用及嵌套 epoll 图仍需 owner/共享状态审计，legacy 等待注册仍由 owner 延迟清理。
   暂不因此开启 EPOLLEXCLUSIVE；源适配阶段必须逐项覆盖真实 inode，而非只依赖测试源。
4. **P1 userns/netns 未实施** — 仍缺映射内部身份、能力作用域、setgroups/map 规则、unshare 原子提交、
   空 netns 与旧 socket 租约、设备 setter 的实际数据面效果、abstract Unix/netlink 隔离。
5. **P2 pipe POLLOUT/缩容模型** — free-byte/PIPE_BUF 近似不等于 Linux page-slot 占用，packet
   pipe 尤甚；现有 resize EBUSY 只看字节数，未实现完整 page-buffer 布局。数据损坏修复不代表该项完成。
6. **P2 mounts 完整差分** — 需继续覆盖跨 namespace/chroot、并发 remount/rename、目标进程退出及
   pinned root 的全部生命周期；mount namespace 锁不自动保护外部对 Mount 属性的直接写入。
7. **P2 通知队列剩余生命周期** — 大积压/重入递归与注销后的停止 dispatcher 阻塞已有回归和修复；
   仍需审计活跃但永不恢复的 dispatcher 的源销毁策略、积压内存上界、已取消迟到票据的引用释放，
   以及真实源的信号/取消/确认完整组合；不能以已有队列测试代替全源验证。

## 上一轮验证记录（最新 P1 结果见文首）

- 上一轮 `Fiberish.Tests`：1882 PASS / 0 FAIL / 1 SKIP（`artifacts/verification/net-io-full.trx`），
  含双等待者、wrapped resize 和 recvmsg 回归，且 Unix 持久适配已撤下。
- `Fiberish.SilkFS.Tests`：13/13；`Podish.Pulse.Tests`：49/49；`Podish.Wayland.Tests`：19/19。
  SilkFS 首次缺 sqlite3 native DLL；补齐测试输出目录的 winsqlite3 依赖后重跑通过，没有改动断言。
- runner：22/22 PASS。
- Node harness 退出码采集验证：`tests/node-crash/test-harness.ps1` **6/6 PASS**；
  以独立 .NET probe 分别产生 marker+exit0、marker+exit139、无 marker+exit0、超时；默认模式
  仅第一项被接受，另验证显式历史 crash 模式及带空格 CLI 路径。旧 Start-Process 运行捕获的
  null exit code 不计为 PASS。这些是 host harness 测试，不是 guest Node 结果。
- native i386 `mounts_poll`：read/seek 不消费，Poll 消费，PRI|ERR=0xa，events=0 得 ERR=0x8。
- native i386 `udp_cork`：overflow EMSGSIZE 丢弃旧 cork，后续 send 只交付新字节。
- native i386 `recvmsg_flags`：地址容量 128/4096 均成功回报实际长度 16；输入 msg_flags=0/3
  均被忽略且数据报被消费。探针设置接收超时等待真实 loopback 到达，避免非阻塞首读的时序误判。
- 未改上游的 `epoll_wait10`：PASS（2 assertions）。
- 完整 net-io（73 例，当前 worktree CLI，40s 超时）：**63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP**，
  0 FAIL / 0 TIMEOUT；epoll_wait10 从 BROKEN 转 PASS，其余历史 PASS 保留。
  残余 BROKEN：epoll_wait16（入口仍拒绝独占）、setsockopt05/08（unshare 未实现）、sendmsg01（guest 工具）。
  JSON：`artifacts/ltp-net-io/results/podish-tmpfs.json`；源 digest 见 `NET_IO_BASELINE.md`。
- Node tmpfs/silkfs 矩阵：**24/24 PASS**，均为当前 worktree CLI、真实 exit 0 + `[repro] done`，
  无超时、未启用 ExpectCrash。每个后端各跑 `6 3000 32768` ×10、`12 3000 32768` ×1、
  `12 200000 64` ×1。汇总：`artifacts/verification/node-matrix.json`；其中保存六份原始 JSON
  与逐次 stdout/stderr 路径。旧脚本的 null exit code 运行未计入本次 24 次。

两轮验证均已收尾；最新 P1 修复轮见文首。review 和设计实施仍未全部完成，未解决项保持开放。
没有提交、合并或推送。
