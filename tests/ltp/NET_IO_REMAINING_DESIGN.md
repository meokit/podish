# net-io 剩余三项实现设计（Linux 语义审查修订版）

状态：部分实施、P1 继续修复中（2026-11-15）。代码尚未提交；以下目标语义不等于已实现能力。

剩余工作的执行顺序与阶段验收见 `NET_IO_NEXT_IMPLEMENTATION_PLAN.md`；本文件保留详细 ABI 契约。

### 当前实施边界

- 已加入持久通知队列、Pipe 订阅、epoll 等待票据及最终关闭清理；尚未适配全部
  readiness 源，因此 syscall 入口仍拒绝 `EPOLLEXCLUSIVE`，不能将 `epoll_wait16` 标记为完成。
  Unix socket 适配原型因 peer 写空间源/方向过滤缺陷已撤下，暂保留原 readiness 路径。
- `/proc/mounts`、`/proc/<pid>/mounts`、`mountinfo` 已加入 per-open-file 消费型 Poll 序号、
  namespace/root 引用固定与挂载变更通知；未修改上游的 `epoll_wait10` 已单项 PASS。
- 已修复 select 二次消费、poll 结果掩码，以及分支 review 找到的 fd/SCM_RIGHTS、UDP cork、
  pipe resize 等问题。最新验证和未解决项见 `NET_IO_BRANCH_REVIEW.md`。
- NEWUSER/NEWNET、内部身份映射、namespace-aware capability、真实空 netns/租约/数据面 setter
  **尚未实现**；仍保留真实不支持路径，不能用初始 namespace 的 root 权限替代。
- 上一轮完整 net-io：63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP，无 FAIL/TIMEOUT；Node 矩阵
  tmpfs/silkfs 共 24/24 通过（真实 exit 0 + 完成标记），C# 核心 1882 PASS / 0 FAIL / 1 SKIP。
  下文的最终目标结果不是实测基线。
- foreign-dispatcher 交付取消/积压推进与 epoll 自身的跨 scheduler 最后关闭修复已完成本轮验证：
  C# 核心 1891 PASS / 0 FAIL / 1 SKIP，其他三项目全过，完整 net-io 状态不变，Node 再次 24/24。
  见 `NET_IO_BRANCH_REVIEW.md` 文首。真实源的跨 scheduler Release/Ctl/嵌套图等审计仍需继续，
  不因此开启 EPOLLEXCLUSIVE，也不将整个设计标记完成。

范围：`tests/ltp/cases-net-io.json` 中仍为 BROKEN 的三类能力项

- `epoll_wait16`：`EPOLLEXCLUSIVE` 跨 epoll 实例的独占唤醒
- `epoll_wait10`：`/proc/mounts` 在挂载命名空间变化时上报 `POLLPRI`
- `setsockopt05` / `setsockopt08`：`unshare(CLONE_NEWUSER|CLONE_NEWNET)` 及其前置/后续能力

本文件描述实现契约及当前实施边界；不改动 `tests/ltp/upstream` 的任何测试或断言。
目标是在**不伪造 errno、不伪造能力、不以单个测试 PASS 替代 ABI 正确性**的前提下，
让这三类用例按 Linux 语义运行。依据见第 9 节；原方案中有意偏离 Linux 的折中已撤销。

## 0. 决策摘要

| 项 | 方案 | 关键取舍 |
| --- | --- | --- |
| `EPOLLEXCLUSIVE` | 在源等待队列的一次 wakeup 中按事件匹配扫描，普通监听全部通知，独占监听根据是否真正唤醒合法等待者决定是否消耗预算 | 引入持久监听和可反馈的唤醒契约；不按完整兴趣掩码分组，不承诺轮转公平，不静默退化成广播 |
| `/proc/mounts` `POLLPRI` | per-open-file 序号由 `Poll` 消费；变化返回 `POLLPRI\|POLLERR` | 修复 epoll 的重复消费调用链，而不是改成 read/EOF/lseek 消费 |
| `unshare` | namespace-aware credentials/capabilities 为前置；先准备再提交 userns、fs state、netns | 映射前身份为未映射状态，不强制设为 UID 0；失败不部分安装 |
| `NEWNET` 配套 | 创建只有 down 状态 lo 的空 netns；socket 固定归属并持租约；设备配置接入数据面 | 不复制容器私网地址/eth0；旧 socket 的 ioctl、netlink、abstract Unix socket 也隔离 |
| LTP 预期 | 05 的 PASS、08 的 SKIP 均以完整 setup 可运行及实测为准 | 无 netfilter 时保留 `ENOPROTOOPT`；PASS 不代表覆盖 Linux UFO/netfilter 实现 |

## 1. 不变式与验收标准

所有实现必须满足：

1. 上游 LTP 源文件与断言零改动。
2. ABI 依据是 Linux 文档、内核和 native 差分，不是测试注释。`TCONF` 是 LTP 结果，
   不是 syscall errno；不支持的功能按真实失败路径返回。
3. 调度器拥有的等待票据、epoll ready-list 只在所属 `KernelScheduler` 线程修改。
   外部线程通知先发布事件再投递；跨 scheduler 的独占仲裁必须等到可验证的唤醒确认，
   不能把 `Post()` 成功当成已经唤醒。命名空间共享状态用独立同步保护。
4. 新增行为有 C# 单测与 native Linux 小程序差分覆盖；每个实施阶段都跑：
   - 目标用例的 `--filter` 子集；
   - 完整 net-io 批次；
   - C# 测试套件（至少 `Fiberish.Tests`，并运行相关/可运行的其他测试项目）；
   - `tests/ltp/test_runner.py`；
   - Node.js workloads（含 tmpfs/silkfs，详见第 6 节）。
5. 未实现与已实现功能的边界必须显式记录；不能把缺少隔离、metadata-only setter、
   或退化广播标记成该能力已完成。未取得用户许可，不 commit、merge 或 push。

## 2. `EPOLLEXCLUSIVE`（`epoll_wait16`）

### 2.1 现状与根因

- `SyscallHandlers.Epoll.ValidateAndNormalizeEpollCtlEvents` 对 `EPOLLEXCLUSIVE` 直接返回
  `EINVAL`，用例在 `epoll_ctl(ADD)` 处 `TBROK`。
- 现有 `AsyncWaitQueue.Signal()` 广播并移除一次性回调，缺少 Linux 等待队列的独占预算
  和 callback 返回值协议；只在 epoll 层加一个标志无法解决惊群与漏唤醒。

### 2.2 目标语义

- Linux 对多个独占 epoll 的一般保证是 “one or more”，不是全局 exactly-one，也不保证公平。
  `epoll_wait16` 的特定管道场景要求每次写恰好唤醒一个；这个用例要求不能推广成全部 ABI。
- 普通监听仍收到通知；源的一次 wakeup 使用一个共享独占预算（常见为 1），
  对事件不匹配、已注销、无实际等待者的独占项不消耗成功唤醒预算。
- 没有阻塞等待者的 epoll 可以被加入 ready-list，但源应继续寻找可唤醒的独占等待者。
  独占仲裁不消费管道数据，也不保证所有未选中实例以后任意调用 `epoll_wait` 都有事件。
- 监听持续到 DEL/目标关闭/epoll 关闭；ET 交付不注销源监听，也不要求用户使用 MOD 重武装。
  LT/ET 的区别是交付后的 ready-list 管理，而不是源监听是否独占。

### 2.3 设计

#### 2.3.1 源队列仲裁：事件匹配 + 实际唤醒反馈

在 `AsyncWaitQueue` 的源通知侧新增持久 poll-listener，保留原有普通异步等待者的
一次性契约。监听项包括注册身份/代次、兴趣掩码、Exclusive、存活状态与同步反馈入口。
`SignalPoll(events, wakeMode, exclusiveBudget)` 是概念接口，具体命名在实施时按项目风格确定。

一次通知处理：

1. 发布源状态与通知序号；取得本次可用监听项的稳定快照。
2. 普通监听全部通知；独占监听按注册队列顺序扫描，共享本次 wakeup 的预算。
3. 按位交集匹配本次事件；ERR/HUP、无掩码通知、清理通知按源和 Linux callback 规则处理。
   **不按完整兴趣掩码相等分组**，例如 `IN` 与 `IN|OUT` 不能各得一个 IN 唤醒名额。
4. epoll callback 不调用底层 `Poll`，只去重加入候选 ready-list，并尝试唤醒合法等待票据。
   无等待者时保留 ready 状态，返回未消耗预算并继续扫描。
5. 只有合法票据成功从等待态转为待调度态并被安排执行，才反馈成功、减少独占预算；
   已取消/过期/已被其他原因唤醒的票据不能占名额。
6. 未访问的监听保持注册；已访问的持久监听也不被一次性移除。DEL/close 显式注销。

同一 epoll 内多个线程等待时也要按其等待队列的 Linux 唤醒规则选择票据，不能继续用
`_waitQueue.Signal()` 广播所有 epoll_wait 线程，却向外仅反馈“成功一个”。目标 open-file 的
最后关闭触发清理；有 dup/fork 持有时，关闭某一个 fd 不等于销毁该源，监听租约也不能反过来
阻止最后一次合法关闭与 watch 清理。

反馈必须在所属 scheduler 可验证；禁止把 `Action` 被调用或投递成功作为唤醒成功。
同 scheduler 路径可同步完成；跨 scheduler 使用源侧串行仲裁与确认/取消协议，确认失败继续
扫描，不能从外部线程直接修改另一个 scheduler 的 epoll。该协议是启用对应共享源的前置。
不在队列锁内调用任意 VFS 代码；稳定快照持有注册租约，锁外调用前再次检查存活/代次，
处理回调重入、注销与源销毁。清理通知不得被普通独占预算截断。

#### 2.3.2 注册链路

新增可反馈的持久就绪通知能力，而不是给一次性 `Action` 简单追加 bool：

```csharp
// 概念接口：最终类型名/签名以实施时的 scheduler 所有权约束为准。
internal interface IPollNotificationSource
{
    IDisposable SubscribePoll(
        LinuxFile file, IPollWakeTarget target, short interests, bool exclusive);
}
```

- `IPollWakeTarget` 提供通知入候选列表与实际唤醒结果；`EpollAwaitState` 维护带代次的等待票据。
- Pipe/Unix socket 复用源队列仲裁；netstack/PollPump、host socket probe、timerfd、inotify 等
  使用同一契约或源侧等价适配器，仲裁位置必须在多个 epoll 实例共享的源上。
- 注册与初次状态检查分离。订阅不因 `_isSignaled` 自动重放当前状态；初始 `Poll` 负责检查。
- 公开启用前盘点所有已支持的 epoll 源，逐项验证独占路径。没有适配器的源不能静默广播，
  也不能为通过测试伪造 Linux 不存在的拒绝条件；未完成项是能力上线阻塞项。
- 原有一次性 wait API 可供普通 I/O await 使用；不能让二者互相替代而改变等待语义。

#### 2.3.3 `EpollItem` 与重新武装

- ADD 建立持久订阅并进行初始 `Poll`；通知入候选列表与最终 `Poll` 分离（详见第 3 节）。
- `HarvestEvents` 对候选调用 `Poll` 判断最终掩码。LT 成功交付后将项放入下次扫描的 ready-list；
  ET 不自动放回，但始终保留源订阅，后续源通知仍可再次入队。
- 移除新契约路径中 `CheckAndRegister`/`RearmEdgeTriggeredItem` 的一次性交付后注销重注册。
  迁移期间保留旧源适配路径，不一次性删除其他源的兼容逻辑。
- `RefreshPendingWatches` 不能全量扫描新契约的所有兴趣项，否则会绕过独占选择或提前消费
  procfs 通知；改为处理已发布通知、初次检查和 ready-list 候选。
- 不把 ET 等价为“必须观察到不就绪才允许再次通知”；是否产生新通知遵从源的 Linux 行为。

#### 2.3.4 `epoll_ctl` 校验

校验分为 syscall 参数、独占专用白名单、目标类型和已有 watch 状态：

```text
MOD 的新 events 含 EPOLLEXCLUSIVE                         -> EINVAL
独占 ADD 的目标为 epoll                                  -> EINVAL
独占 events 含 IN|OUT|ERR|HUP|WAKEUP|ET|EXCLUSIVE 以外的位   -> EINVAL
MOD 命中已有独占 watch（即使新 events 没带 EXCLUSIVE）      -> EINVAL
DEL                                                      -> 不读取 events
```

因此 `PRI`/`RDHUP`/`RDNORM`/`ONESHOT` 等不能与独占组合。一般非独占 events 的校验不在此
扩大；不能把“任意未知位必定 EINVAL”当作 Linux 通则。错误优先级用 native 差分确认。
`EPOLLWAKEUP` 在缺少 `CAP_BLOCK_SUSPEND` 时按 Linux 忽略；不能把无条件清除标志宣称为
完整电源管理实现，本方案不新增 suspend 保证。

### 2.4 改动清单

| 文件 | 改动 |
| --- | --- |
| `Fiberish.Core/Core/WaitHandle.cs` | 持久 poll-listener、事件匹配、唤醒预算/反馈；保留一次性 wait 契约 |
| `Fiberish.Core/VFS/Structs.cs` | 通知/状态检查分离接口、适配与租约 |
| `Fiberish.Core/VFS/EpollInode.cs` | ready-list 管理、合法等待票据、持久订阅、移除绕过仲裁的扫描 |
| `Fiberish.Core/Syscalls/SyscallHandlers.Epoll.cs` | 独占白名单、目标及已有 watch 校验 |
| Pipe/Unix socket/PollPump/host probe/timerfd/inotify 等源 | 源侧通知适配；逐项记录测试结果 |
| C# 新增/现有 waitqueue 与 epoll 测试 | 反馈、取消、混合监听、持久 ET/LT、错误矩阵 |

### 2.5 验证

- A 先注册但没有阻塞等待者，B 后注册且阻塞：一次事件必须能唤醒 B，A 可记录 ready 状态。
- 普通 + 独占混合、`IN` + `IN|OUT` 重叠兴趣：普通监听全通知，独占共享源预算。
- 选中项在通知前注销、信号/超时获胜、已排队票据、重复通知与关闭竞争不吞预算或悬挂。
- 长驻多个 epoll，分别验证 LT、ET、多轮写入、仍可读时的新源通知、读空后再次写入；
  不断言 Linux 未保证的轮转顺序或公平性。
- 关闭管道端、ERR/HUP、源销毁与跨 scheduler 确认失败的继续扫描。
- 全部独占 ctl 错误组合与每类已支持 epoll 源的适配测试。
- `epoll_wait16` 期望 PASS；它的子进程每次交付后退出，**不覆盖长期 ET 重用**，不能代替上述测试。

### 2.6 风险

- 这不是简单加 flag：等待反馈、持久订阅和 ready-list 必须联动；跨 scheduler 协议需独立测试。
- 保留不相关一次性队列路径，避免热路径新增每 watch 定时器或每次通知的大量分配。
- 现有 Unix socket 重复可读、网络 idle-watch 分配/`HasTimers` 测试是迁移门槛。
  禁止用 `HarvestEvents` 开头全量 `_readyList.RemoveAll` 粗暴裁剪修复。

## 3. `/proc/mounts` 的 `POLLPRI`（`epoll_wait10`）

### 3.1 现状与根因

- `/proc/mounts` 由 `ProcDynamicFileInode` 提供，打开时把内容快照进
  `ProcOpenData.Content`，不实现任何 poll/注册能力。
- `SyscallHandlers.Epoll.IsEpollSupportedInode` 只接受
  `IDispatcherWaitSource`/`ITaskWaitSource`/`InotifyInode`/`TimerFdInode`，
  因此 `epoll_ctl(ADD, /proc/mounts)` 返回 `EPERM`，用例在 setup 处 `TBROK`。
- `MountNamespace` 只有挂载列表，没有“命名空间已变化”的事件序号或通知通道。

### 3.2 目标语义

- 打开文件时固定目标进程当时的 mount namespace 与文件视图所需的 root，并持有引用。
  `/proc/mounts` 对应 `/proc/self/mounts`；不能在每次操作时重新跟随调用者或目标进程的新 namespace。
- per-open-file 的 `SeenEvent` 初始化为当前 namespace 的事件序号；`dup/fork` 共享，独立 open 不共享。
- `Poll` 基础结果为 `POLLIN|POLLRDNORM`。序号变化时更新 `SeenEvent`，并返回 `POLLPRI|POLLERR`。
  没有新变化的下一次 `Poll` 不再报告这两位；read、读到 EOF、`lseek` 都不更新该序号。
- `POLLERR` 不要求显式请求，所以 events 为 0 或未请求 PRI 的监听也要观察挂载变化。
- LTP 的重读注释不是消费规则的规范来源；应让未修改的用例在以上语义下运行。

### 3.3 设计

#### 3.3.1 `MountNamespace`：事件序号 + 订阅

- 增加 namespace 事件序号与可注销的持久订阅；完成可见的挂载变化后递增并发布通知。
- mount/umount 是首批路径；已有 remount、move、bind、mount 属性更改等路径也要逐项对照
  Linux 的 `touch_mnt_namespace` 调用点接线，不把已支持操作的通知留作“可选扩展”。
  不支持的挂载操作仍按现有能力边界失败；失败操作不发布成功变更事件。
- 订阅与初始检查采用 subscribe-before-check；临界区发布状态/序号，锁外通知稳定快照。
  并发操作产生的变化不得落在检查与订阅的空窗；允许多次变化合并，不要求每次 mount 一个用户事件。
- 回调只将候选通知投递给目标 scheduler，不调用消费型 `Poll`。退订处理已排队回调，
  通过注册代次/存活检查避免关闭后访问已释放状态。
- namespace 引用跟随打开文件和订阅租约；不能仅靠清空 listener 列表掩盖生命周期问题。

#### 3.3.2 `/proc/mounts` 专用 inode

新增专用 inode 和 per-open-file 状态，包含 namespace/root 租约、`SeenEvent`、seq-reader 内容与位置。
同一 open-file 上的序号比较/更新必须序列化；read 内容缓存与 poll 序号互不驱动。

```text
Poll(file):
  res = IN | RDNORM
  event = pinnedNamespace.Event
  若 SeenEvent != event:
      SeenEvent = event
      res |= PRI | ERR
  返回 res
```

- 实现第 2 节的通知源契约，以及 poll/select 所需的任务适配；订阅本身不消费序号。
  PRI 以外的 ERR 路径同样接线，不能被 `watchEvents == 0` 的提前返回截断。
- 使用独立的非消费序号检查仅用于防止丢失订阅期间的通知；不能以它代替对用户可见的 Poll。
- 内容从打开时固定的 namespace/root 生成；重新从头读取反映新的挂载状态，不永久保存 open 时快照。
  分段 read/seek 遵循 seq-reader 语义，零长度 read 不刷新或确认事件，不能靠普通文件 Size 推断 EOF。

#### 3.3.3 epoll/poll 调用契约：消除多余的消费型 Poll

这部分是本阶段必做，不允许在 procfs 中加入特殊的“read 清通知”绕过：

1. ADD/需要重检的 MOD：建立订阅后进行 Linux 对应的初始 `Poll`，有结果则加入候选列表。
   初始 Poll 也可能消费事件，不能把它的旧掩码永久缓存后强行交付；与 Linux 一样，交付仍要重检。
2. 源变化：回调只入候选列表和唤醒，不走当前 `RePoll -> CheckAndRegister -> Poll` 链。
3. Harvest：每次取出候选时执行一次最终 `Poll`，本次返回值直接用于组装返回事件，
   不再先以 `RefreshPendingWatches` 全量采样再 Poll 一次。
4. LT 成功交付：候选保留到下一轮，但本轮末尾不再次 `CheckAndRegister` 消费序号；
   ET 不自动重新入队。持久订阅均保留；下次最终 Poll 无事件时正常移出候选。
5. `poll/ppoll/select/pselect` 同样分离订阅与取样；一次有效扫描的结果用于本次 copy-out，
   不为判断是否返回而再次取样吞掉同一通知。select exceptional set 与 poll ERR 的映射对照 native。
6. 不对普通 pipe/socket 缓存旧就绪结果绕过最终 Poll：其数据被其他线程取走后必须允许返回空，
   保持 stale-ready、maxevents 和取消处理的现有约束。

#### 3.3.4 创建点与关联文件

- `ProcRootInode.Lookup("mounts")`
- `ProcPidDirectoryInode.Lookup("mounts")`（`/proc/<pid>/mounts`、`/proc/self/mounts`）

两处解析到同一种打开文件行为，目标 pid 在 open 时用于取得并固定 namespace/root。
Linux 的 `/proc/<pid>/mountinfo` 也使用 `mounts_poll`；共享通知/seq-reader 基础设施一起覆盖，
避免同一 procfs 能力中留下不一致。`mountstats` 不因本改动自动变成同样的 poll 源。

### 3.4 改动清单

| 文件 | 改动 |
| --- | --- |
| `Fiberish.Core/VFS/MountNamespace.cs`、挂载 syscall 路径 | 事件与事务可见性、订阅/退订、已支持变化操作接线 |
| `Fiberish.Core/VFS/ProcFs.cs` | mounts/mountinfo 打开状态、namespace/root 租约、消费型 Poll 与 seq-reader |
| `Fiberish.Core/Syscalls/SyscallManager.cs` | 显式获取/持有目标 mount namespace 的接口 |
| `Fiberish.Core/VFS/EpollInode.cs`、poll/select 路径 | 通知不 Poll、最终 Poll 不重复消费、LT 不立即重检 |
| C# mount/procfs/epoll 测试与 native 探针 | 序号消费、ERR、共享 open-file、持有视图、竞争与订阅清理 |

### 3.5 测试与风险

- 无变化 Poll 不含 PRI/ERR；变化后第一次含二者，第二次消失，不需要 read。
- 变化后先 read 至 EOF 或先 seek 再 Poll，通知仍在；单独读取不能“确认”序号。
- `events=0` 的 poll/epoll、只请求 PRI、只请求 IN、LT/ET 与 select exceptional set 的 native 差分。
- dup/fork 共用消费状态，独立 open 分别消费；另一个 poll/epoll 消费同一 open-file 后的行为也要差分。
- open 后目标进程退出/换 namespace，已打开文件仍持有原视图；namespace/root 最后引用才释放。
- mount/umount 与已支持 remount/move 等操作通知；失败操作、多个变化合并、订阅时竞争与 close。
- 未修改的 `epoll_wait10` 两轮都收到 PRI，且不新增 pipe/socket 重复唤醒或 stale-ready 回归。
- 与第 2 节共同依赖通知/取样分离，实施时不能再把这两个阶段视为架构上完全独立。

## 4. `unshare` 与网络命名空间（`setsockopt05` / `setsockopt08`）

### 4.1 现状

- `unshare`（i386 syscall 310）没有 handler，落到默认 `ENOSYS`。
- 缺少 `CLONE_NEWUSER` / `CLONE_NEWNET` 常量。
- `/proc/self/setgroups`、`uid_map`、`gid_map` 不存在，`tst_setup_netns()` 的
  `SAFE_FILE_PRINTF` 会失败。
- `SIOCSIFMTU`、`SIOCSIFFLAGS` 只实现了 getter，没有 setter。
- `SO_NO_CHECK`（SOL_SOCKET, optname 11）未实现，`setsockopt05` 会 `ENOPROTOOPT`。
- `setsockopt08` 依赖 netfilter 的 `IPT_SO_SET_REPLACE`；Podish 没有 netfilter。

### 4.2 范围界定

必须一起完成：

- namespace-aware credentials/capabilities 与必要的 VFS/资源权限接线；这是 NEWUSER 的安全前置，
  **不能退化为新 userns 中 EUID 0 具有全局权限**。
- `unshare(NEWUSER)`、映射 proc 文件、真实 `setgroups()` 门禁、隐含 `CLONE_FS` 与校验/回滚。
- `unshare(NEWNET)`：调用任务切换到空网络 namespace，拥有独立端口、设备、路由、netlink 视图、
  AF_UNIX abstract 名字空间；pathname Unix socket 仍通过共享 VFS 定位，不错误地隔离其文件路径。
- socket/open-file 持有 namespace 租约，原进程退出后也能由其他持有者继续使用旧 socket。
- MTU/flags 配置影响数据面；UDP `SO_NO_CHECK` 保存/回读，并明确后端的校验和能力边界。

仍不实现 netfilter；`IPT_SO_SET_REPLACE` 继续 `ENOPROTOOPT`。不在本方案新增
NEWNS/NEWPID/NEWUTS/NEWIPC/NEWCGROUP/NEWTIME 等 namespace；确无对应支持的类型按 Linux
配置缺失路径返回 `EINVAL`，已有类型但缺少 unshare 的情况要单列为兼容性缺口，不能伪称类型不存在。
AF_INET6、RAW、packet 等现有不支持能力不会因 NEWNET 自动获得支持。

不要求一次完成所有 Linux 安全子系统，但在 NEWUSER 成功可用前，必须审计所有已暴露的
root/capability 敏感入口，保证子 userns 不取得父 namespace 或全局资源权限；未完成审计不启用该能力。

### 4.3 设计

#### 4.3.1 syscall 入口

- 常量：`CLONE_NEWUSER = 0x10000000`、`CLONE_NEWNET = 0x40000000`。
- i386 syscall 310 在 `SyscallHandlers.cs` 分发到新增 namespace handler。
- flags 先做已知位识别与支持情况检查，不能把合法但未实现的位称为“未知位”。
  `unshare(0)` 为 no-op；`CLONE_FS` 与 NEWUSER 隐含的 FS 复用同一真实解除共享路径。
  `CLONE_FILES` 复用 fd-table 克隆实现；单线程下 THREAD/VM/SIGHAND 按 Linux 允许的 no-op
  及隐含位规则处理。其他合法 flag 的缺口需列明，不扩大本方案的 namespace 类型范围。

```text
SysUnshare(flags):
  验证 flags、隐含位及共享/线程限制；不改变当前状态
  NEWUSER：校验有效 UID/GID 的父映射、chroot、嵌套/数量限制
  准备独立 fs/fd 状态（若需要），准备新 credentials/userns（若需要）
  NEWNET：以待安装 credentials 的 userns 检查 CAP_SYS_ADMIN，准备空 netns
  所有准备成功 -> 在所属 scheduler 的不让出执行提交段安装所需新状态
  失败 -> 释放全部临时引用/配额，返回原始 errno，保留所有旧状态
```

NEWUSER 隐含 THREAD 与 FS；多线程/共享地址空间、sighand 等限制按 Linux `check_unshare_flags`
实际规则检查。NEWNET 本身可用于多线程程序，只改变调用任务的 namespace；不能写共享
`Process` 字段导致其他线程一起切换。若当前 `SyscallManager` 的线程共享关系无法满足此语义，
先引入 task-local namespace 引用再实现 handler。

NEWUSER 会先于同次调用的 NEWNET 准备，使非特权调用者可以合法创建自己的 userns+netns；
不能用“创建者在父 namespace 中没有 CAP_SYS_ADMIN”拒绝这种组合。
两次分开的 unshare 调用各自提交，不要求第一步回滚第二步的失败；同一次组合调用必须原子安装。
资源分配失败用 `ENOMEM`；namespace 深度/数量超限按现代 Linux 用 `ENOSPC`，不无限创建。

#### 4.3.2 `UserNamespace` 模型

新增 `UserNamespace` 与显式 credentials 模型（下列是必要数据，不要求照抄为可变 public 属性）：

```csharp
sealed class UserNamespace
{
    // Parent、Level、OwnerKernelUid/Group、ParentCouldSetFcap、配额/租约
    // UidMap/GidMap（建立后不可变），继承且不可逆的 SetgroupsAllowed
}
```

- credentials 保存内部 kernel UID/GID（real/effective/saved/fs 与附加组）及所属 userns。
  NEWUSER 不改内部 ID；新映射初始为空，`get*id` 的未映射值返回 overflow ID（默认 65534），
  写入 `0 <原ID> 1` 后才显示 0。Owner 是创建者有效 ID，不是自动建立的 inside-0 映射。
- fork 共享 userns 引用，复制 credentials；NEWUSER 仅在通过单线程限制后替换调用者 credentials。
  现有 `Process` 字段需明确区分内部 ID 和可见 ID，禁止同一个 int 在各调用点混用两种含义。
- `ToKernelId/ToNamespaceId` **本阶段接入**：get/set*id、stat/文件所有者与创建权限、
  proc 身份展示、信号/IPC 权限、Unix socket credentials 等已支持身份接口。
  未映射 ID 不得当作 overflow 用户的真实内部身份；设置无有效映射的 ID 按 Linux 失败。
- 新 credentials 在新 userns 中取得完整 permitted/effective 集合，inheritable/securebits 等
  按 Linux `set_cred_user_ns` 初始化；不因此获得父 userns 权限。exec 的能力重算同样遵守
  userns 映射和已支持的文件权限语义，不能在 exec 后凭 namespace 内 UID 0恢复全局权限。
- 引入 `HasCapability(credentials, targetUserNamespace, capability)`：检查有效集合、
  namespace 祖先关系与父 namespace 创建者的 owner 规则，而非 `EUID == 0` 快捷判断。
  mount/net/UTS 等资源记录不可变 `OwnerUserNamespace`，NEWUSER 不改变已有资源的归属。
- 修改 netdev 检查 `netns.OwnerUserNamespace` 中的 `CAP_NET_ADMIN`；mount/UTS 等操作
  检查其 owner userns；全局资源检查 initial userns。文件 capability bypass 还受文件 UID/GID
  是否映射等规则约束，不能统一改成“对当前 userns 有 capability 就允许”。
- 审计 `HasEffectiveCapabilityOrRoot`、直接 `EUID == 0`、capset/身份设置、文件权限、mount、
  UTS、kill、socket force/bind 选项及全局系统调用。权限对象不同的调用点不能机械替换。
  初始 root 的 capability mask 与测试环境显式对齐；目前只初始化 SYS_ADMIN 的简化模型
  不足以满足现代 UID-0 映射规则，不能在 `uid_map` handler 临时补权或绕过 `CAP_SETFCAP`。

#### 4.3.3 procfs 控制文件

在 pid proc 目录提供 `setgroups`、`uid_map`、`gid_map`。open 时固定目标 userns 并持租约，
记录 opener credentials；read/write 不按 pid 重新找到另一个 namespace，也不能绕过 open 时权限。
复用动态 proc 文件的 write handler 前必须检查其是否支持这些打开上下文、偏移和完整写入约束。

**映射格式与提交：**

- 每次 write 必须在 offset 0，长度小于 guest PAGE_SIZE，1–340 条 extent；5 条是旧 Linux 限制。
  解析整次写入后一次提交，不能分块累加或在失败时留下部分映射。
- 每行 inside/outside/count 均为合法无符号 ID/count；count > 0，inside 与 outside 范围分别不能
  重叠，计算不得溢出，`(uid_t)-1`/`(gid_t)-1` 不得成为有效映射 ID；末行无换行也应接受。
- namespace 的每种 map 只能成功建立一次（不是每个 fd 一次）；再次建立返回 `EPERM`。
  格式/范围/偏移/长度问题与权限问题的 errno、优先级对照 Linux，测试覆盖现代 340 条上限。
- 初始 userns 提供 Linux 的初始映射视图；其他 namespace 读回第二列按 opener userns 解释，
  同 userns 打开时对应其父 namespace，不把所有读者都硬编码成同一视角。

**映射权限（UID/GID 分别对应 SETUID/SETGID）：**

- 写者处于目标 userns 或直接父 userns，并在目标 userns 有相应 capability；映射范围在父中有效。
- 在父 userns 具有相应 capability 时可写合法的多段范围；否则仅允许一行映射写者在父中的
  effective ID，且写者 effective UID 必须匹配创建者。无父 SETGID 的 gid_map 还要求先 deny。
- 映射父 UID 0 时增加 Linux 5.12 起的 `CAP_SETFCAP` 条件：父中的写者具备该能力，
  或同 userns 写入时创建者在创建时具备它；将该事实保存在 namespace 创建元数据中。
- 同时检查 opener/current credentials 的 Linux 对应限制，防止转交 proc fd 绕过权限；
  不能仅以写者可见 UID 是否为 0 判断。

**setgroups 状态机：**

- 继承父 namespace 的 allowed/denied 状态；deny 不可逆，子 namespace 不能恢复 allow。
- `allow` 仅在尚未被 deny 时成功；`deny` 仅在 gid_map 尚未建立时成功。
  即使当前已 deny，gid_map 建立后再次写 deny 也不能笼统按幂等返回成功。
- 需要目标 userns 的 `CAP_SYS_ADMIN`，并校验 offset、短输入/尾部垃圾等格式。
- `setgroups()` syscall 同时要求相应 SETGID 权限、gid_map 已建立且 allowed；
  gid_map 之前或 deny 之后均返回 `EPERM`，不是只更新 proc 文本。

namespace 配额/深度限制必须实际实施。若暴露 `/proc/sys/user/max_*_namespaces`，读写必须
对应真实配额和权限；不放只为绕过 setup 的假值。暂不暴露该 sysctl 时也不能省略内部限制。

#### 4.3.4 `unshare(CLONE_NEWNET)`

**空 netns 与容器 private 网络必须分开创建：**

- 新增概念上的 `PrepareEmptyNetworkNamespace(ownerUserns)`；不能复用当前会添加私网地址的
  `LoopbackNetNamespace.Create(ipv4, prefix)` 初始化策略。
- 初始只有 `lo`（ifindex 1），flags 为 LOOPBACK、未 UP/RUNNING，MTU 按 Linux lo 默认 65536；
  不继承 eth0、私网 IP、外部路由、已有端口/连接或到宿主网络的出口。
- 将 lo 设 UP 后，按所支持的 IPv4 行为建立 loopback 地址/路由并启用传输。
  `NetDeviceSnapshotProvider`/netlink/proc 网络视图读取同一设备与路由模型，不自行合成 eth0。
- socket 创建、bind/connect/发送、lazy UDP socket 创建均固定使用创建 socket 时的 namespace；
  unshare 仅改变后续新 socket 的归属，不重绑旧 socket 或已存在连接。
- abstract Unix socket 名字表归属 netns，旧/新 socket 都使用自己的表；pathname 地址仍是 VFS 对象。
  需改造当前 `_sharedUnixSocketNamespace` 的共享方式，不能只换 INET netstack。

**统一租约与旧 socket：**

- 统一一个引用计数 owner（可演进现有 `SharedLoopbackNetNamespace` 为更完整的 network namespace）。
  task namespace 引用、socket inode/端点、accepted socket、netlink、存活的后台资源持有明确租约；
  不能再叠加两个互不知情的计数，或让 `Dispose` 绕过引用计数提前销毁句柄。
- socket 租约在最后实际所有者释放时归还，不是每个 fd close 就销毁。fork/dup/SCM_RIGHTS 后
  即使创建者退出，剩余进程仍能使用；构造失败/accept 失败/取消路径也必须正确归还。
- 不采用 `_retiredNetNamespaces` 保留到原进程退出的方案：旧 fd 可能比该进程活得更久。
- netlink socket 捕获创建时的 namespace 租约，替换当前动态读取 `SyscallManager` 的闭包。
  网络设备 ioctl 同样从 socket 固定的 namespace 查询，而非调用者当前 namespace。
- 源通知/PollPump 在有效租约期间运行，销毁顺序应停止通知、撤销回调后销毁原生句柄。
  namespace 内部对象和回调不能形成永不释放的强引用环；最后引用销毁与异步注销要有测试。

#### 4.3.5 设备 ioctl setter

- `SIOCSIFMTU (0x8922)`、`SIOCSIFFLAGS (0x8914)` 对 socket 所属 netns 的实际设备操作；
  查设备存在性、参数与 `CAP_NET_ADMIN` 的顺序对照 Linux，所有失败都不得部分更新设备。
- MTU 合法范围由设备约束与协议规则决定，不写死统一的 `68..65536`。
  Linux lo 的边界与常规以太网不同，需 native 探针记录本次目标内核/设备实际结果。
- 修改 flags 保留 LOOPBACK 等不可写/动态位，处理 Linux 支持的可写位及派生 UP/RUNNING；
  不只保存 `IFF_UP|IFF_DEBUG`，不允许用户直接伪造只读状态位。
- **先实现数据面再宣称 setter 完成**：down/up 控制路由/传输行为；MTU 影响原生设备的发送
  上限、IP 分片/PMTU、UDP 错误和相关 TCP 行为，不能只改 snapshot。
  smoltcp 当前 `Loopback` 使用固定能力，需要可配置 device/等价数据面适配，必要时补原生 ABI。
- 05 的 4000-byte MSG_MORE + 1-byte flush 路径在 MTU 1500 下必须对照 Linux：
  成功的 flush 不能丢载荷或把 UDP 分片变成多个用户 datagram；若 Linux 本身返回错误则匹配其错误。
  不为躲开分片实现而一律返回 `EMSGSIZE` 或仅因测试未检查末次 send 就宣布数据面正确。
- getter、netlink 和 proc 视图从同一已提交状态读回；原生配置失败要回滚，不能读回假成功值。
- host 模式不能因为没有 private 对象就断言真实设备不存在。其配置能力要明确区分权限、
  设备不存在和未实现后端操作，不以伪造 `ENODEV` 替代；也不自动修改宿主网络。

#### 4.3.6 UDP `SO_NO_CHECK`

- `LinuxConstants.SO_NO_CHECK = 11`。
- `SOL_SOCKET` 分支按 Linux int optlen 校验（至少 sizeof(int)）、非零归一化 bool，保存并回读；
  无效用户地址/短 optlen、getsockopt 截断等遵从已有 socket-option 复制契约。
- 当前 smoltcp 0.12 `Loopback` 明确使用 `ChecksumCapabilities::ignored()`，纯内部 loopback
  后端不计算/验证校验和，因此保存此属性在当前传输模型中没有线上的校验和差异。
  若后续 device 适配引入真实报文校验和，必须把属性接入 UDP 发送，不能继续只存字段。
- 测试属性回读与 MSG_MORE flush 数据完整性；不宣称实现 Linux UFO/GSO，
  `setsockopt05` PASS 只说明该程序在支持的后端模型中完成，并不证明覆盖原 Linux CVE 触发路径。

### 4.4 错误码矩阵

| 场景 | errno |
| --- | --- |
| `unshare` 未知位、Linux 对应配置确无支持的 namespace 类型 | `EINVAL` |
| NEWUSER 多线程或 THREAD/VM/SIGHAND 的共享约束不满足 | `EINVAL` |
| NEWUSER 创建者有效 ID 无父映射，或处于 Linux 禁止的 chroot 状态 | `EPERM` |
| NEWNET 缺少创建 userns 中的 `CAP_SYS_ADMIN` | `EPERM` |
| namespace 数量/嵌套限制触发（现代 Linux） | `ENOSPC` |
| 准备新状态资源分配失败 | `ENOMEM`，无部分安装 |
| uid_map/gid_map 已建立、权限不足、父映射/SETFCAP 条件不满足 | `EPERM` |
| map 格式/偏移/写入大小/范围/重叠校验失败 | `EINVAL` |
| deny 后请求 allow；gid_map 建立后请求 deny | `EPERM` |
| gid_map 未建立或已 deny 时调用 setgroups | `EPERM` |
| 控制文件或 ioctl 的无效用户指针 | `EFAULT` |
| ioctl setter 缺少所属 netns owner userns 的 NET_ADMIN | `EPERM` |
| ioctl setter 设备不存在 | `ENODEV` |
| MTU 违反对应设备/协议的合法约束 | `EINVAL`（具体边界及优先级对照 native） |
| `IPT_SO_SET_REPLACE` | `ENOPROTOOPT`（保持） |

表中是独立场景；同次输入同时违反多项条件时按目标 Linux 的检查顺序验证，不能从本表
行序推导 errno 优先级。合法但尚未实现的其他 unshare 操作需另列缺口，不用“未知位”掩盖。

### 4.5 `setsockopt05` / `setsockopt08` 预期

- `setsockopt05`：`unshare` + proc 映射 + `SIOCSIFMTU`/`SIOCSIFFLAGS` + `SO_NO_CHECK`
  全部就位并通过数据面差分后，期望程序完成 1000 轮并报告 **PASS**。
  它的最后一次 send 未断言返回值，必须另加发送结果/载荷测试；不等价于覆盖 Linux UFO CVE。
- `setsockopt08`：`tst_setup_netns()` 成功后，`setsockopt(IPT_SO_SET_REPLACE, len=1)`
  在 Podish 上仍是 `ENOPROTOOPT` → 测试自行 `tst_brk(TCONF)` → 结果为 **SKIP**。
  这是“没有 netfilter”的诚实结果；只有实现 netfilter 兼容层后才会变成 PASS。
  若为了通过而返回 `EINVAL`，就是伪造内核能力，违反本批次的不变式。
- 两者 setup 都依次调用 NEWUSER、NEWNET，然后写 setgroups/uid_map/gid_map。
  **只有 NEWUSER 不足以把 08 从 BROKEN 变为 SKIP**；必须等到完整 namespace setup 完成。

### 4.6 改动清单

| 文件 | 改动 |
| --- | --- |
| `Fiberish.Core/Native/LinuxConstants.cs` | `CLONE_NEWUSER`、`CLONE_NEWNET`、`SIOCSIFMTU`、`SIOCSIFFLAGS`、`SO_NO_CHECK` |
| `Fiberish.Core/Native/X86SyscallNumbers.cs` | 已有 `unshare`，无需改 |
| `Fiberish.Core/Syscalls/SyscallHandlers.cs` | 分发 `unshare` |
| `Fiberish.Core/Syscalls/SyscallHandlers.Namespace.cs`（新增） | `SysUnshare` |
| `Fiberish.Core/Core/UserNamespace.cs`、credentials/capability 支持 | 内部 ID、映射、owner/祖先权限、配额与租约 |
| `Fiberish.Core/Core/Process.cs`、`FiberTask.cs`、创建/exec 路径 | credential 继承、单线程校验、task-local netns、exec 权限重算 |
| syscall 的身份/信号/IPC/全局资源入口与 VFS 权限代码 | ID 转换、setgroups 门禁、root/capability 敏感路径审计 |
| `Fiberish.Core/Syscalls/SyscallManager.cs` | 准备/提交新状态、解除 fs/fd 共享、namespace 租约 |
| mount/net/UTS 等 namespace 资源 | 不可变 OwnerUserNamespace 与对应权限检查 |
| `Fiberish.Core/VFS/ProcFs.cs` | 控制文件 opener/目标 namespace 固定、映射权限/格式/状态机 |
| `Fiberish.Core/Net/LoopbackNetNamespace.cs`、`SharedLoopbackNetNamespace.cs` | 统一生命周期、空 netns 工厂、设备/路由模型 |
| `Fiberish.Core/Net/NetDeviceIoctlHelper.cs`、`NetDeviceSnapshotProvider.cs` | socket 所属 namespace 配置与真实状态读取 |
| `Fiberish.Core/VFS/NetstackSocketInode.cs`、`HostSocketInode.cs` | socket 租约、ioctl 路由、SO_NO_CHECK 与后端边界 |
| `SyscallHandlers.Socket.cs`、Unix/netlink socket 及名字表 | 创建时固定 netns；abstract 名字与 netlink 视图隔离 |
| `Fiberish.Netstack/src/lib.rs`、必要的 native binding/device 代码 | 空网络初始化、设备启停、可配置 MTU 与真实分片/PMTU 行为 |
| C# 测试、独立 native 差分探针 | 失败原子性、权限边界、生命周期、隔离与数据面 |

### 4.7 测试

- 身份：非 root 创建 NEWUSER，映射前 overflow、映射后 inside UID 0；内部所有者不变，
  stat/创建文件/信号/Unix credentials 使用正确视角，不把 overflow ID 当真实身份。
- 权限：子 userns 的完整 capabilities 不允许修改父 netns/UTS/mount 或全局资源；
  丢弃 NET_ADMIN/SYS_ADMIN 后 inside UID 0 也不能绕过检查；父 owner/祖先授权的正向测试。
- 映射：初始视图、1/6/340/341 条记录（合法记录总长需满足 PAGE_SIZE）、重复写、非零偏移、
  双向重叠、溢出/保留 ID、无父映射、不同 opener 视角、转交 fd 和 SETFCAP 拒绝/允许路径。
- setgroups：映射前拒绝；allow+gid_map 后允许合法组；deny 后不可恢复、对子 namespace 继承；
  gid_map 后 deny 拒绝；缺少父 SETGID 时须先 deny，不能凭 root 模型跳过。
- unshare：0、组合调用、分开调用、多线程/共享 FS、chroot、无映射的嵌套创建、配额/深度限制；
  对每个准备步骤注入失败，核对 credentials、fs/fd、netns、引用计数与配额全部保持原状态。
- task 隔离：一个线程 NEWNET 不切换其他线程；NEWUSER 隐含 FS 后更改 cwd/umask 不影响原共享者。
- 空网络：初始仅 down 的 lo、无 eth0/私网路由；UP 后 loopback 工作，down 再次影响传输。
  端口、abstract Unix 名字可在两个 netns 独立绑定，pathname Unix socket 仍通过 VFS 访问。
- 旧 socket：unshare 后数据、ioctl、netlink 仍用旧 namespace；fork/SCM_RIGHTS 后创建者退出仍可用；
  accepted socket、构造失败和最后引用释放时无 UAF、重复销毁、回调悬挂或泄漏。
- 设备：MTU 原生应用失败回滚、flags 只读位、缺少权限、lo 边界差分；MTU 1500 的 UDP
  cork/flush 数据完整性或匹配的失败结果，以及已支持 TCP 的回归，不只断言 setter/getter 一致。
- SO_NO_CHECK：0/非零规范化、长度/指针、回读与 cork 中途切换；完整 setup 后运行 05/08。

## 5. 实施顺序与依赖门槛

撤销原先 0.5–1 天/1–2 天的粗估：修正后包含共享唤醒契约、权限边界和真实数据面，
不能按“补几个返回值”估时。阶段 0 结束后依据代码审计与差分结果重新估算。

| 阶段 | 内容与前置 | 交付验收（不是当前完成状态） |
| --- | --- | --- |
| 0 | native 小探针、源/权限入口清单、租约与 task 归属审计 | 固定 Linux 版本/配置/credentials，明确差分断言与资源失败注入点 |
| 1a | 通知与 Poll 分离、持久订阅/合法等待票据 | 旧 epoll LT/ET、stale-ready、idle-watch 与全部回归通过 |
| 1b | 依赖 1a：共享源独占仲裁、ctl 校验、各源适配 | `epoll_wait16` PASS；未等待者/取消不吞名额，长期 ET 与混合监听通过 |
| 2 | 依赖 1a：mounts/mountinfo 消费型 Poll、namespace 事件与租约 | `epoll_wait10` PASS；ERR、read 不清通知与共享 open-file 差分通过 |
| 3a | namespace-aware credentials、capability 审计、NEWUSER/proc map、事务准备 | 身份/权限/映射/原子性单测通过；不宣称 08 已 SKIP |
| 3b | 依赖 3a：空 NEWNET、task-local 归属、socket 租约、abstract Unix/netlink 隔离 | 完整 `tst_setup_netns()` 可运行，08 实测 SKIP（无 netfilter） |
| 3c | 依赖 3b：设备数据面启停/MTU、分片/PMTU 差分、SO_NO_CHECK | 05 实测 PASS，额外发送结果/载荷测试通过 |

后续实施按阶段顺序推进；每阶段完成均跑第 6 节回归。阶段 1b 与 2 共用 1a 基础，
不可再说两者无需协调。上文阶段表是原始依赖契约，当前已部分实施；实际状态见文首及
`NET_IO_BRANCH_REVIEW.md`，下一步按 `NET_IO_NEXT_IMPLEMENTATION_PLAN.md` 推进。没有提交或合并授权。

## 6. 回归门槛

每个阶段完成时必须同时满足：

- 阶段承诺的目标 LTP 状态达到预期；完整 `cases-net-io.json` 不新增退步，
  不以更多 SKIP/PARTIAL 或减少断言掩盖回归。runner 非零退出不自动等于新增 FAIL，需核对逐例报告。
- C#：`Fiberish.Tests` 以及可运行的 `Fiberish.SilkFS.Tests`、`Podish.Pulse.Tests`、
  `Podish.Wayland.Tests`，0 failed；平台/依赖限制要逐项记为未运行，不写成已通过。
  现有 1860 passed / 1 skipped 是旧实测基线，不是修订方案的测试结果。
- runner 单测：`python -m unittest discover -s tests/ltp -p "test_runner.py" -t tests/ltp`；
  历史为 20 OK，后续新增测试按实际数量记录。
- Node.js：使用**当前 worktree 构建的 CLI**，运行 `tests/node-crash/repro.js` 的
  `6 3000 32768`（10 次）、`12 3000 32768`、`12 200000 64`，覆盖 tmpfs 与 silkfs，
  均须 guest exit 0、输出完成标记、无超时/崩溃；保留退出码与日志。
- 注意现有 `tests/node-crash/run-repro.ps1` 硬编码主仓库路径，且以“复现崩溃”成功退出。
  不得原样用其 exit 0 当回归通过；实施时使用显式当前 CLI 路径的正常成功判据 harness，
  如需修脚本须作为对应实施阶段的显式改动并验证。
- native 差分探针独立存放于 tests 辅助目录，不修改 `tests/ltp/upstream`。记录 kernel、
  root/非 root、capability mask、配置与 guest ABI；不能把 WSL 配置限制当成 Linux 通则。
- 从当前 worktree 构建，按环境需要设置 `CARGO_NET_OFFLINE=true`、
  `DOTNET_ROLL_FORWARD=LatestMajor`，准备 sqlite3 依赖；记录无法完成的前置条件。
- 仅在实测后更新 `tests/ltp/NET_IO_BASELINE.md` 和报告。设计编辑本身不刷新历史 PASS 数。

## 7. 预期最终结果

以下是全部前置与回归通过后的目标，**不是修订后已获得的结果**：

| 用例 | 现状 | 待实测目标 |
| --- | --- | --- |
| `epoll_wait16` | BROKEN（`EINVAL`） | PASS |
| `epoll_wait10` | BROKEN（`EPERM`） | PASS |
| `setsockopt05` | BROKEN（`unshare ENOSYS`） | PASS |
| `setsockopt08` | BROKEN（`unshare ENOSYS`） | SKIP（`TCONF: ENOPROTOOPT`，无 netfilter） |
| `sendmsg01` | BROKEN（guest 无 `ip`/`ifconfig`） | 仍 BROKEN（不在本设计范围） |

若以上四例恰好达到目标且其余不变，批次将从 **62 PASS / 5 PARTIAL / 5 BROKEN / 1 SKIP**
变为 **65 PASS / 5 PARTIAL / 1 BROKEN / 2 SKIP**（总数 73）。在数据面与权限审计完成并实测前，
不得保证该汇总或以目标倒推放宽实现要求。

## 8. 明确不做

- 不为 `setsockopt08` 伪造 netfilter 返回值。
- 不为 `/proc/mounts` 返回“永远 PRI”，不改成 read/EOF/lseek 确认序号，也不缓存旧掩码伪造最终就绪。
- 不把 EPOLLEXCLUSIVE 简化成按掩码各挑一个或无条件广播，不承诺 Linux 没保证的公平性。
- 不强制把新 userns 中的身份设为 0，不把子 namespace capability 当全局 root 权限。
- 不复制容器 private 网络配置充当空 NEWNET，不让设备 setter 只修改可读回的 metadata。
- 不用原进程生命周期替代 socket namespace 租约，不绕过原生配置失败或载荷检查。
- 不实现 `CLONE_NEWNS`/`CLONE_NEWPID` 等本批次不需要的命名空间；需要时另立设计。

## 9. Linux 依据与差分基准

优先级：目标 Linux 的可见行为与内核路径 > 对应 man-pages > LTP 注释；版本差异必须显式记录。
当前 native 基线是 WSL Linux 5.15.153.1，i386 static guest ABI；审查还使用 Linux v6.12 源码
与当前 man-pages。不能把 v6.12 的内部结构硬编码成所有版本 ABI；已知差异以目标版本测试确认。

| 主题 | 可核查依据 | 本方案约束 |
| --- | --- | --- |
| 独占 ctl 与一般保证 | [epoll_ctl(2)](https://man7.org/linux/man-pages/man2/epoll_ctl.2.html) | 白名单、已有独占 watch 禁止 MOD、禁止目标 epoll、one-or-more |
| 源等待队列与 callback | [eventpoll.c](https://github.com/torvalds/linux/blob/v6.12/fs/eventpoll.c)、[sched/wait.c](https://github.com/torvalds/linux/blob/v6.12/kernel/sched/wait.c) | `ep_poll_callback`、`ep_ptable_queue_proc`、`__wake_up_common` 的反馈与预算 |
| mounts/mountinfo | [proc_namespace.c](https://github.com/torvalds/linux/blob/v6.12/fs/proc_namespace.c)、[namespace.c](https://github.com/torvalds/linux/blob/v6.12/fs/namespace.c) | `mounts_poll` 消费 poll_event，PRI+ERR，open 固定 namespace/root，真实变化通知 |
| userns 身份与权限 | [user_namespaces(7)](https://man7.org/linux/man-pages/man7/user_namespaces.7.html)、[user_namespace.c](https://github.com/torvalds/linux/blob/v6.12/kernel/user_namespace.c) | 未映射身份、capability 范围、map 权限/SETFCAP/340 extents、setgroups 不可逆 |
| unshare 事务 | [unshare(2)](https://man7.org/linux/man-pages/man2/unshare.2.html)、[fork.c](https://github.com/torvalds/linux/blob/v6.12/kernel/fork.c)、[nsproxy.c](https://github.com/torvalds/linux/blob/v6.12/kernel/nsproxy.c) | 隐含位、共享限制、先准备后安装、以新 credentials 创建其他 namespace |
| netns 隔离 | [network_namespaces(7)](https://man7.org/linux/man-pages/man7/network_namespaces.7.html)、[loopback.c](https://github.com/torvalds/linux/blob/v6.12/drivers/net/loopback.c) | 端口/设备/路由/abstract Unix 隔离，新 lo 未 UP |
| 设备 ioctl | [socket.c](https://github.com/torvalds/linux/blob/v6.12/net/socket.c)、[dev_ioctl.c](https://github.com/torvalds/linux/blob/v6.12/net/core/dev_ioctl.c)、[dev.c](https://github.com/torvalds/linux/blob/v6.12/net/core/dev.c) | `sock_net(sk)`、所属 userns 的 NET_ADMIN、MTU/flags 数据面 |
| SO_NO_CHECK 与后端 | [sock.c](https://github.com/torvalds/linux/blob/v6.12/net/core/sock.c)、[smoltcp 0.12 loopback](https://github.com/smoltcp-rs/smoltcp/blob/v0.12.0/src/phy/loopback.rs) | bool socket 属性、当前后端忽略校验和，不声称 UFO 覆盖 |

本地上游依据：`epoll_wait10.c`、`epoll_wait16.c`、`setsockopt05.c`、`setsockopt08.c` 与
`tests/ltp/upstream/lib/tst_net.c`。16 的子进程只交付一次即退出；05 不断言最后一次 send；
`tst_setup_netns` 先 NEWUSER 再 NEWNET，然后才建立映射，三者均需补独立探针覆盖。

## 10. Review 问题落实位置（设计层，均未代表代码已实现）

| 问题 | 修订位置 |
| --- | --- |
| 子 userns 全局 root/capability 越权 | 4.2、4.3.2、3a 上线门槛 |
| 映射前错误强制 UID/GID 0 | 4.3.2、4.7 身份差分 |
| mounts 在 EOF/lseek 消费且漏 ERR | 3.2–3.5、共享通知/取样阶段 1a |
| 独占无实际等待者吞预算/按掩码分组/广播折中 | 2.2–2.3.3、2.5 |
| 独占 ctl 校验遗漏 | 2.3.4 |
| unshare 前置检查、task 归属、失败部分安装 | 4.3.1、4.7 故障注入 |
| setgroups 状态机、map 权限与现代上限 | 4.3.3、4.4、4.7 |
| 新 netns 继承 eth0/私网、lo 错误 UP | 4.3.4 空 netns、4.7 空网络 |
| socket 跨进程租约、旧 ioctl/netlink/abstract 视图 | 4.3.4 统一租约、4.6–4.7 |
| MTU/flags 只读回不影响数据面 | 4.3.5、阶段 3c |
| 3a 错误预期 SKIP、ET 覆盖、Node 缺失 | 2.5、4.5、第 5–7 节 |
