# net-io 剩余问题实施计划

状态：执行中，2026-11-15。阶段 A 台账与阶段 B（socket options）已实施完成，guest 回归进行中；
详细选项矩阵见 `NET_IO_SOCKET_OPTION_MATRIX.md`。细节契约沿用 `NET_IO_REMAINING_DESIGN.md`；
实际发现及证据见 `NET_IO_BRANCH_REVIEW.md`。本文件不表示批准提交、合并或推送。

## 1. 起点、范围和原则

已验证检查点：C# 核心 1891 PASS / 0 FAIL / 1 SKIP，其他三项目 13/49/19 PASS，runner 22/22，
net-io 63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP，Node tmpfs/silkfs 24/24。
已完成的取消票据、trampoline、epoll 自身跨线程最后关闭不重写，但要继续纳入真实源测试。

仍有七组工作：socket options、真实共享源归属、pipe page-slot、mounts 并发/租约、
完整持久源与 EPOLLEXCLUSIVE、userns/credentials、netns/数据面。

原则：

- 上游 LTP 程序和断言不变；不能按测试名称返回特殊值，不伪造能力或 errno。
- 支持一个 socket option 必须体现后端语义，不是仅保存字段让 getsockopt 回读相同值。
- 未实现功能按该协议/后端实际不支持的 ABI 路径拒绝。具体 errno 经 native 对照确定，
  不把所有情况机械改成同一个错误码。
- 原有成功 no-op 被纠正后，可能暴露之前虚假的 PASS。这种变化单列为“基线纠偏”，
  保留逐条证据和待实现项；既不能为了保住数字继续假成功，也不能用新增 SKIP 掩盖普通回归。
- 以一个可独立验证的阶段为交付单位；不在完整验收期间同时修改运行时代码。
- fs-fd/proc-time 相关实现只做接口和重复修改核对，不自动合并或 cherry-pick 其他工作树。

## 2. 依赖顺序

```text
A 审计清单 / native oracle / 失败注入点
├─ B socket options 真实语义 ───────────────────────┐
└─ C 真实源归属 / 停止与销毁 / 取消引用释放         │
   ├─ D1 pipe page-slot                            │
   ├─ D2 mounts 事务与打开视图                     │
   └─ E 各源持久适配（依赖 D1）                   │
      └─ F EPOLLEXCLUSIVE 启用门槛                 │
                                                   │
G credentials / namespace-aware capabilities（依赖 A，先不启用 NEWUSER）
└─ H NEWUSER / proc maps / 原子 unshare
   └─ I 空 NEWNET / socket 租约 / 名字与视图隔离
      └─ J 设备数据面 / SO_NO_CHECK（同时依赖 B、C、E）

B–J 已支持范围验收 + K guest 资产复核 + 全分支二次 review
```

建议执行顺序：A → B → C → D1/D2 → E/F → G/H → I/J → K。
没有新增并行 agent 或新 worktree 的授权需求；表中分支表示代码依赖，不表示自动派发并行工作。

## A. 建立可核对的实施清单（已完成选项台账）

- 已交付 `NET_IO_SOCKET_OPTION_MATRIX.md` 和 native `probes/sockopts.c`，逐项记录 Linux 实测、
  Podish 行为与偏差。
- source 台账与权限台账随 C/G 阶段继续补充；它们不是本阶段的代码交付。

还需交付的两份台账（附在 review 或测试中）：

1. **source 台账**：状态 owner、通知源、共享范围、waiter dispatcher、Poll 是否消费、最终释放位置。
2. **权限台账**：每个 root/capability 敏感入口检查哪个资源的哪个 user namespace。

固定 native 内核版本、配置、身份、套接字类型及设备；增加失败注入接口，覆盖分配/订阅/accept/
构造/事务提交失败。新测例先能复现当前缺陷，再用于验收修复。

## B. 纠正 socket option 成功 no-op（已实施，等待 guest 回归）

主要文件：`NetstackSocketInode.cs`、`SyscallHandlers.Socket.cs`、`Fiberish.Netstack/src/lib.rs`
及 native bindings、端口/连接管理代码。

- 缓冲区：SNDBUF/RCVBUF 与 FORCE 接入实际队列容量、背压和资源限制；验证 Linux 倍增/下限/
  截断回读规则，而不是固定返回 16 KiB。FORCE 的权限检查日后接入所属 netns 的 owner userns。
- TCP：审计 smoltcp 实际提供的 Nagle/keepalive 能力；有能力才扩展绑定并应用，验证计时、
  发送和连接生命周期。不能只让 NODELAY/KEEPIDLE/KEEPINTVL/KEEPCNT 回读成功。
- LINGER：区分禁用、零超时的 abort、非零超时的 drain，以及 close 等待的取消/超时清理。
- REUSEADDR/REUSEPORT：必须落实到 bind 冲突和端口分发/连接选择，不把布尔字段当作实现。
- OOBINLINE：没有 urgent-data 数据面时不宣称支持；保留 HostSocket 的实际 OutOfBand 能力。
- multicast：需要真实的成员关系与收包过滤、协议/地址族/接口校验及退出/继承语义；否则诚实拒绝。
- 同时覆盖 getsockopt 截断、指针、长度、协议适用性、accept 继承和设置前后创建的 endpoint。

验收：每个返回成功的 option 至少有 set/get 和一个适用的后端行为断言；无残留的成功 no-op。
不能以当前 setsockopt01/accept02 PASS 代替上述验收。若暂时拒绝，必须记录造成的基线纠偏。

**执行结果**：netstack 缓冲区/Nagle/keepalive/linger/multicast 已接入真实后端，host 侧也完成
审计；不支持的 REUSEPORT/OOBINLINE/KEEPINTVL/KEEPCNT、live buffer 重设改为明确 errno。
逐项内容与 native 依据在 `NET_IO_SOCKET_OPTION_MATRIX.md`。最终验收：C# 核心
1900 PASS / 0 FAIL / 1 SKIP，其他 13/49/19，runner 22/22；完整 net-io 63 PASS / 5 PARTIAL /
4 BROKEN / 1 SKIP（0 FAIL/TIMEOUT，73 例状态不变）；Node tmpfs/silkfs 24/24。
其余未实现能力已列为明确偏差，下一阶段进入 C（真实源归属与生命周期）。

## C. 完成真实共享源的 scheduler 与生命周期契约（P1）

主要文件：`PollNotifications.cs`、`ReadinessDispatcher.cs`、`WaitHandle.cs`、`EpollInode.cs`、
`LinuxFile` 以及 Pipe/Unix/Host/Netstack 等真实 inode。

- 分开定义“源状态 owner”与“等待任务 dispatcher”；共享 FD 不能靠访问者当前 scheduler 推断 owner。
- 审计 Pipe/Unix 等自身的 Release、读写、Ctl 和 nested-epoll 图；所有跨 owner 修改通过显式
  调度/共享状态同步，不只保护 epoll ready-list。阻塞 syscall 的返回/取消也走同一归属契约。
- 区分 scheduler 暂停和永久退役。只有明确的生命周期退役/取消才撤销注册，不能因等待过久
  偷跳一个仍合法的独占监听；停止/销毁时不能要求一个永远不会再运行的 dispatcher 来完成清理。
- 取消和已完成 delivery 清除不再需要的源/目标引用，迟到 posted callback 本身应成为无害空壳；
  用 weak-reference/最终租约释放测试检测，而不只验证回调不执行。
- 覆盖 cleanup 回调异常：不能使其他 watch 清理或 inode 最终释放被中断；错误必须可观测。
- 测量长期停止 owner 与大量 wake 的积压。若做合并，只合并已证明等价的 readiness 候选，
  不丢失实际 wake budget/代次；不以固定队列上限静默丢通知。

验收：真实 Pipe/Unix/Host/Netstack 的 dup/fork/SCM_RIGHTS、原创建者退出、foreign final close、
停止 owner、close/DEL/MOD/信号/超时竞态，无 UAF、重复释放、漏唤醒或引用悬挂。
现有 fake consuming source 测试保留，但不能作为真实源已经支持的证明。

## D1. pipe 改用真实 page-slot 模型（E/F 的前置）

- 使用有页身份的 buffer ring/segment，分别记录有效字节、页槽和可合并尾段；不能继续用
  `capacity - count >= PIPE_BUF` 近似 Linux POLLOUT。
- stream/packet 模式遵守各自的合并与槽占用；短 packet read 仍丢弃本 packet 剩余部分。
- resize 依据已占页槽判断 EBUSY；先准备新 ring，成功后一次切换，失败保留原容量、数据和通知状态。
- 核对容量上限、资源权限与 fs-fd 已有实现的接口，避免在两个分支各实现一套冲突的 fcntl 语义。

验收：native 对照“小写入占槽、部分读取、剩余字节很少但槽已满、packet 小包、多轮 wrap、
增长/缩小/失败回滚”；验证 Poll 与实际可接受写入的关系、PIPE_BUF 原子性和 LT/ET。

## D2. mounts 的事务可见性与 pinned view

- 已支持的 mount/remount/move/umount/属性更新通过 namespace 事务入口完成，不让 Mount 属性在
  namespace 锁外任意改变；明确 namespace、mount、dentry/rename 的锁顺序。
- 生成完整已提交的快照后再发送变化通知；失败操作不留部分状态或伪造序号。
- 覆盖 open 时固定的 namespace/root、目标进程退出/换 namespace、chroot、最后租约释放。
- 继续保持 read/EOF/seek 不确认通知，Poll 才消费；dup/fork 共用 cursor，独立 open 分开。

验收：并发变更与读取不产生混合视图/异常；IN/PRI/ERR、events=0、LT/ET/select exceptional-set
与 native 差分一致，`epoll_wait10` 保持 PASS。

## E/F. 逐源适配后才启用 EPOLLEXCLUSIVE

按顺序小步交付：

1. Pipe（补方向化通知与页槽语义）、Unix（peer 写空间、重绑、半关闭、读写方向过滤）。
2. eventfd、timerfd、inotify、TTY、netlink 等源：适配共享的状态/队列，不在每个 waiter 创建独立预算。
3. Netstack PollPump、HostSocket probe：仲裁放在多个 epoll 真正共享的源上，不能各 scheduler
   分别广播后再声称独占；确认前不得把一次 Post 算作成功 wake。
4. nested epoll、所有已支持的可轮询 FD 路径及 ctl 校验。

所有源共同测试：普通+独占混合、重叠掩码、无等待任务、取消/超时/信号、DEL/MOD、最后关闭、
长期 ET、LT 交接、多个 scheduler、真实数据不丢失。ERR/HUP、无方向提示和清理通知单列。

启用门槛：source 台账没有未审计的已支持路径，取消/预算/生命周期测试全过，才移除 syscall
入口拒绝；最后运行未修改的 `epoll_wait16`。不承诺公平，也不将特定测试的 exactly-one 推广为全部 ABI。

## G/H. credentials 安全前置，再启用 NEWUSER

- 引入明确的内部 kernel ID、namespace 可见 ID、credentials 和 UserNamespace 引用，避免沿用
  同一个 int 混用两种身份。接入 get/set*id、文件所有者/创建、stat、信号、proc、Unix credentials、
  fork/exec 以及已经暴露的 IPC/权限入口。
- 每个资源保留 owner userns；逐个替换 `EUID == 0`/`HasEffectiveCapabilityOrRoot` 捷径，
  检查针对目标资源而非一律针对调用者当前 namespace。审计 capset、exec 和全局资源。
- NEWUSER 映射前内部身份不变、外部显示 overflow；子 userns root 不得操作父 namespace 资源。
- proc map 文件固定目标 namespace/opener credentials；完整实现一次性 map、范围/重叠/溢出、
  SETFCAP 条件、setgroups 不可逆状态机及真实 syscall 门禁、配额/深度限制。
- unshare 采用 Prepare/Commit/Rollback，接入真实 fs/fd 解除共享和线程约束；逐步注入失败，
  不出现 credentials、fs/fd、netns 只换了一半的状态。

启用门槛：权限台账中所有已暴露的敏感入口均完成作用域审计和正反例验证，才允许 NEWUSER 成功。
不把“proc 文件已能写”当作支持 userns。

## I/J. 空 NEWNET 与真实网络配置

- task-local netns 引用；只有 down 的 lo，不复制容器 private 的 eth0/IP/路由。
- 统一 task/socket/accepted socket/netlink/后台资源的 namespace 租约；旧 socket 不随调用者
  unshare 切换。覆盖 fork/dup/SCM_RIGHTS 后创建者退出、构造/accept 失败与最终释放。
- abstract Unix 名字与 netlink/ioctl/proc 视图固定到相应 netns；pathname Unix 继续遵循 VFS。
- 原生设备模型支持 UP/DOWN 与可配置 MTU，接入实际路由/传输/分片/PMTU/TCP 行为；
  setter 失败回滚，getter 与 netlink 读取同一已提交状态。不得只更新 snapshot。
- SO_NO_CHECK 按后端 checksum 能力保存/回读并接入需要的发送路径，明确 loopback 能力边界。
- 不新增 netfilter；保持真实不支持路径，不能为测试强行返回 EINVAL。

验收：跨线程隔离、旧 socket 存活、权限拒绝、lo down/up、MTU 边界及 UDP cork/flush 完整载荷
通过 native 对照。完整 setup 后，目标为 setsockopt05 PASS、setsockopt08 自行 TCONF/SKIP；
在实际跑到该路径前不能提前把 BROKEN 改成 SKIP。

## K. guest 资产、验收和全分支二次 review

- `sendmsg01` 的 ip/ifconfig 缺失作为独立 rootfs 资产任务，先确认原程序需要的命令和后端能力，
  用标准可追溯 guest 工具补齐；不替换上游程序、不提供总返回成功的 shim。补齐后可能继续暴露
  其他能力缺口，不能预先保证该项 PASS。
- 每个阶段：目标 native/C# 回归 → 四个 C# 项目与 runner → 当前 CLI 的完整 net-io → Node 24 次矩阵。
  重负载和精度用例串行；保留源码/二进制指纹、逐次退出码、原始日志与基线差异。
- Node 两后端均运行 `6 3000 32768` ×10、`12 3000 32768` ×1、`12 200000 64` ×1；
  必须 exit 0 + 完成标记，无 ExpectCrash。矩阵结果不能沿用旧构建。
- 以 `c321caa` 到届时 HEAD 加全部工作区改动再做一次全分支 review，检查 C#/Rust ABI、权限边界、
  失败原子性、锁顺序、回调/租约引用环和规模性能；保留真实 no-op/能力纠偏记录。

最终验收以逐例结果与完整性证据为准，不承诺一个固定 PASS 总数。EPOLLEXCLUSIVE、05 的目标
和无 netfilter 的 08 SKIP 均是条件性目标，不等于已经实现。P1 未解决前不建议合并；提交/合并/
推送仍需用户另行授权。

## 3. 规模与交付方式

- B/C 是当前最高优先级；D/E/F 是通知与 I/O 语义改造；G–J 是跨权限模型和原生网络的数据面工程。
- 不再给“补几个 handler、1–2 天完成”式估时。A/B/C 的审计和验证结束后，依据具体后端能力，
  对剩余工作重新拆分和估算；每阶段先给可验收范围，再实施。
- 下一实际实施包建议：**A + B**，先交付完整 option 台账/native 用例，再逐项消除成功 no-op；
  同时把 C 的缺口列清，不直接开启剩余 feature gates。
