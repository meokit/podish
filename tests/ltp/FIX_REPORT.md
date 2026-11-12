# 首批 LTP syscall 兼容性修复报告

日期：2026-11-12

修复分支：`fix/ltp-syscall-compatibility`

测试接入提交（保留在 main）：`13737b2`

LTP 固定版本：`e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`

## 结果

| 测试 | 修复前 | 修复后 |
| --- | --- | --- |
| 原生 Linux，50 个 LTP 二进制 | 50 PASS | 50 PASS |
| Windows Podish，同一批二进制 | 33 PASS / 12 FAIL / 4 BROKEN / 1 TIMEOUT | **50 PASS** |
| Podish 内部 LTP 断言 | 226 通过 / 26 失败 / 4 broken / 4 skipped | **364 通过，其他均为 0** |
| 全部 C# 测试 | 1739 通过 / 1 个原有跳过 | **1770 通过 / 0 失败 / 1 个原有跳过** |
| 新增 C# 回归测试 | 无 | **31 通过** |
| Python 测试运行器单测 | 11 通过 | 11 通过 |

修复没有删除测试、改变断言、调整预期错误码、增加 XFAIL，也没有修改
LTP submodule。原生 Linux 与 Podish 使用相同 SHA-256 的静态 i386 glibc
二进制；最终 Podish 运行默认 30 秒/二进制宿主超时，未通过增加超时跑绿。

## 逐项问题原因与修复

### 1. read02：目录读取错误码

公共 `DoReadV` 入口没有排除目录，目录被交给普通文件/后端读路径，最终返回
`EIO`，而 Linux 应返回 `EISDIR`。在公共读入口进行类型检查，使 read、readv
以及 positional read 的目录错误码一致，不依赖宿主平台异常转换。

### 2. dup201：newfd 范围与 RLIMIT_NOFILE

`SysDup2` 直接把目标 fd 写入描述符表，没有验证负数或进程 fd 软上限。
当 newfd=-1 时返回值 -1 又被 guest 解释成 `EPERM`；newfd=1024 则能绕过
默认软上限成功创建。现在在改变 fd 表、关闭目标或增加引用计数之前检查
newfd 范围并返回 `EBADF`。dup3 复用此校验。

### 3. pread02 / pwrite02：位置参数与不可定位描述符

这两个接口直接复用通用 IO：通用层用 offset=-1 表示“采用当前文件位置”，
但 pread/pwrite 的负 offset 必须是 `EINVAL`，不能作为内部哨兵值。
同时，pipe/socket 的 host-buffer 路径会忽略位置参数，导致 positional IO
错误地成功。新增位置校验，对负位置返回 `EINVAL`、对 pipe/socket 返回
`ESPIPE`；preadv/pwritev 同样处理，v2 接口保留其合法的 offset=-1 语义。

### 4. readv02 / writev01：iovec 校验与错误传播

原先非法 iovcnt 和读取 iovec 表失败都返回 null，调用者统一转换成 `EFAULT`；
也没有在访问数据缓冲前检查 32 位 ssize_t 的长度范围，错误流入底层后产生
`EPERM/EFAULT` 等不正确结果。

现在分别验证计数、iovec 表地址和单个向量长度，明确传播 `EINVAL/EFAULT`；
总传输量按 Linux `MAX_RW_COUNT` 截断，而不是错误拒绝多个合法长度的合计。
这个细节用额外的原生 i386 小程序确认，并加入空文件向量读取回归检查。
同时修复解析失败时已租用的 Iovec 数组没有归还 ArrayPool 的问题。

### 5. ftruncate03：文件类型和有符号长度

原先仅排除目录，socket 会进入基类未实现的 Truncate，返回 `ENOSYS`，而不是
`EINVAL`。旧版 32 位 ftruncate 还把 uint 参数直接转为 long，把 -1 变成
4294967295，可能错误扩展文件。

现在按 signed off_t 解码 32 位长度，对两种 ABI 都检查负长度，并在调用后端前
拒绝非普通文件；路径版 truncate 同步修复 signed 长度校验。

### 6. uname02 / gettimeofday01：输出指针“假成功”

- uname 的局部字符串写入函数在 CopyToUser 失败后仅退出局部函数，外层继续
  返回 0。现在先构造完整 utsname，再检查写入结果并返回 `EFAULT`。
- gettimeofday 处理了 timeval，但完全忽略旧式 timezone 输出参数，因此坏
  timezone 指针仍返回成功。现在写入两个为零的 timezone 字段并检查地址。

### 7. epoll_create1_02：忽略未知 flags

原先只提取 O_CLOEXEC，其他位静默忽略，`-1` 或 `EPOLL_CLOEXEC|1` 都能创建 fd。
现在只允许 O_CLOEXEC，其他位在分配 inode/fd 之前返回 `EINVAL`。

### 8. nanosleep04：glibc 使用未接入的旧版 clock_nanosleep

问题不在现有 nanosleep 参数校验。Syscall trace 证明静态 i386 glibc 的
nanosleep 包装实际调用 **clock_nanosleep（编号 267）**，而分发器只接入了
clock_nanosleep_time64。因此三组非法 timespec 都首先得到 `ENOSYS`。

新增旧版分发，共享时钟睡眠实现但按不同 ABI 读取 8 字节 timespec32 或
16 字节 timespec64，正确写回相应格式的剩余时间。保留绝对截止时间语义；
clock_nanosleep 被信号打断返回 `EINTR`，不错误地按 SA_RESTART 自动重启。

### 9. clock_getres01：time32/time64 分支行为不一致

旧 time64 分支不检查 clock ID，直接写固定分辨率并返回 0，即使 ID=-1 或
输出为空也是如此。旧 time32 校验函数则不识别 alarm clock，导致部分变体
被 LTP 标记为 TCONF。

现在两个 ABI 共用 clock ID 与分辨率校验，并允许查询 REALTIME_ALARM 和
BOOTTIME_ALARM 的分辨率。**查询分辨率不代表实现了唤醒挂起宿主的 alarm timer**，
也不放行创建该类定时器的权限。

### 10. getpid01 / getppid01 / wait402：缺少 procfs 前置接口

三个测试都在 setup 阶段读取 `/proc/sys/kernel/pid_max` 时中断，不是 getpid、
getppid 或 wait4 的断言失败。procfs 补充只读 pid_max，报告当前 signed-int
标识符空间的上限，并加入目录枚举。数据由真正的 procfs inode 提供，而不是
在测试 rootfs 里塞固定文件。补齐后这些用例进入断言并全部通过。

### 11. wait401：进程状态可观测性导致的死等

测试子进程先等父进程在 `/proc/<pid>/stat` 中显示 `S`，然后退出；父进程则
阻塞在 wait4 等子进程退出。原实现的 Process.State 主要记录生命周期，
阻塞系统调用只更新 FiberTask.Status，procfs 却仅使用 Process.State，始终
显示父进程为 `R`。

所以这不是“wait4 等待不够久”，而是子进程永远看不到所需状态。procfs 现在
同时参考任务等待状态，对全部线程都等待的活进程报告 `S`，并继续让
Stopped/Zombie/Dead 生命周期状态优先。最终 wait401 约 0.6 秒完成。

### 12. pipe2_01：缺少 packet-mode pipe

原先 pipe2 显式拒绝 O_DIRECT。不能只取消标志校验，因为 O_DIRECT 在 pipe
上表示 packet mode，而不是普通文件的 direct IO。

新增创建时 packet mode 及包长度队列：

- 写入按 PIPE_BUF（4096）拆包；零长度写不产生包。
- 一次读取最多取一个包；短读丢弃该包尾部，但不影响下个包。
- iovec 边界与 guest 内存页边界不会意外拆包/丢包。
- 读指针错误不消费已排队的包。
- 保留非阻塞 EAGAIN、包写入原子性与 SIGPIPE 行为。
- 只在写端暴露 O_DIRECT 状态位。

原 C# `Pipe2_RejectsUnsupportedFlags` 把 O_DIRECT 固定当成不支持功能；现在
将其负面测试输入换成真正非法的 O_APPEND，仍断言 EINVAL，并新增正面语义测试。
LTP 原始用例本身没有任何修改。

### 附：全量 C# 复跑暴露的既有测试竞态

`BlockingHostOperationAwaitable_SignalInterruptsWait_AndLateCompletionDoesNotDoubleResume`
用 1 ms 定时器打断线程池操作，却无同步地断言 worker 已经启动。在某次全量
复跑中，信号早于线程池启动，该断言偶发失败，与 syscall 生产逻辑无关。
将信号改为由 worker 启动后投递回 scheduler，保留原有结果/恢复次数断言，
去掉对线程池必须在 1 ms 内启动的假设；没有通过重试或跳过该测试掩盖竞态。
修正后连续三次完整运行 Fiberish.Tests，每次均为 1689 通过、0 失败、1 个原有跳过。

## 回归验证与环境

新增 `Fiberish.Tests/Syscalls/LtpRegressionTests.cs`：31 个参数展开后的用例，
覆盖坏输出指针、fd 软上限、vector IO 参数、目录 IO、positional IO、signed
truncate、时钟 ABI、procfs 状态、packet pipe 跨页/跨向量/短读/背压语义。

重新构建 Fiberish.Tests、Fiberish.SilkFS.Tests、Podish.Wayland.Tests、
Podish.Pulse.Tests 和 Podish.Cli；全部构建成功。使用本机 .NET 11.0.100、
VS2026 clang-cl/Ninja 和 Rust 工具链，保留 net10.0 TFM，并将测试运行时向
.NET 11 roll-forward。SilkFS 测试仍使用之前准备的测试输出目录 sqlite3.dll。

| C# 项目 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Fiberish.Tests | 1689 | 0 | 1 |
| Fiberish.SilkFS.Tests | 13 | 0 | 0 |
| Podish.Wayland.Tests | 19 | 0 | 0 |
| Podish.Pulse.Tests | 49 | 0 | 0 |

唯一显式跳过仍为既有的 NativeContainer StartStopStart 测试（缺 test_futex 资产）。
此前指出的无 Zig 时直接 return 的 SilkFsLifetime 测试也仍受原有条件限制，
不应把本轮汇总当成那个场景已经获得额外验证。

最终详细日志（不入 Git）：

- `artifacts/ltp/results/fixed-native.json`
- `artifacts/ltp/results/fixed-podish.json`
- `artifacts/ltp/csharp/*.trx`
- 各 JSON 中记录的逐用例 `output.log`

## 分支和范围

测试先单独提交在 main。修复只在修复分支，未合并或推送。
之前运行 C# 测试时产生的 blocking host connect 修复，也单独提交为 `4822546`，
没有混进 main 的测试提交。

这份结论仅针对当前固定的 50 个 LTP 用例与新增回归测试，不是整个 LTP 或
整个 Linux ABI 已经兼容。当前 LTP 测试后端是 Windows hostfs；packet mode
支持本轮 pipe2 创建路径，尚未扩展 F_SETFL 在运行时切换 packet/stream 模式。
