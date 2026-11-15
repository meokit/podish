# net-io 审查修复与 main 整合验证

日期：2026-11-15。本文是本轮合并验收记录；`NET_IO_BRANCH_REVIEW.md` 中的进行中结论属于
之前的检查点，不代表本轮四项审查缺陷仍未修复。后续完整网络设计不在本轮交付范围内。

## 范围与代码版本

- 原审查分支：`37b0eda`；待合入的 main：`8274fc0`（已经包含 fs-fd 修复）。
- 四项修复：`def333c`。
- 经冲突整合并用于下述完整回归的运行时代码：`ed077af49fc048ae8d21f8d0240ea7dc2b62cace`。
- 后续验收文档修改不改变已验证的运行时代码。未修改上游 LTP 二进制、测试断言或 manifest。

## 四项审查缺陷的修复

1. **Host SO_ERROR 的符号**：`MapSocketError` 返回负的 syscall errno；写入 SO_ERROR 的值
   必须取反，得到 Linux ABI 的正 errno。新增原生 nonblocking connect 回归绕过 managed connect
   错误缓存，实际核验 ECONNREFUSED 为 `+111`。
2. **UDP bind 丢失缓冲区配置**：所有 UDP 创建入口使用同一个带容量的工厂。分别回归
   SO_SNDBUF/SO_RCVBUF 在 bind 前设置后，bind 后 getter 和 native 实际容量均保持 `131072`。
3. **监听端 TCP 选项没有真实继承**：NODELAY/KEEPALIVE 同时应用到真实 listener 和 stream；
   Rust 端 promote 后的新 listener 保留这些配置。accepted 端从已建立后端读取实际快照，
   而不是在 accept 时复制监听端当前 flags。回归覆盖 listen 前/后设置、连续两次 accept、
   已进入 accept queue 后关闭 listener 选项而不影响旧连接；直接核验 Nagle/keepalive 后端状态。
4. **UDP MSG_MORE 错投最后目的端点**：cork 独立保存首次目的端点，不能仅用非零 pending payload
   长度判断是否已 cork。后续 sendto 只能追加或 flush，不能重定向已确认的数据；溢出和 flush
   清除目的端点，下一个 datagram 使用新的目标。回归覆盖普通与零字节首次发送，以及下一个包。

原生 WSL 对照确认：A(MSG_MORE) + B(flush) 的合并包交付 A；首次 payload 为空也一样。
`probes/udp_cork.c` 已扩展并通过 i386 static 原生编译执行，保留原 overflow/EMSGSIZE 回归。
另以 select 等待 Linux listener 的 accept queue 就绪，再改变 listener 选项，确认 accepted
保留 NODELAY/KEEPALIVE/KEEPIDLE 的 `1/1/100` 快照，避免把握手竞态误当继承语义。

## main 冲突的语义选择

- 保留 main 的任务级 fd 表、显式 task 参数及 fd 级 CLOEXEC。统一 `AllocFD` 消费一份 owned
  引用（失败也释放）；dup 必须先 Get 自己的引用，创建入口不能在失败后再 Close 一次。
  SCM_RIGHTS 安装失败保持截断/引用清理，copyout 失败按接收 task 的表回滚，而不是 ambient
  sibling 的表。新增 EMFILE、dup、open 和独立 fd 表的 SCM_RIGHTS copyout 失败回归。
- 管道容量只保留一套实际存储/resize 实现，继续使用 main 的 pipe-max-size/CAP_SYS_RESOURCE
  权限限制，并保留 net-io 的通知适配与 readiness 检查。大请求先检查权限：无能力返回 EPERM；
  2 GiB 超出 managed 连续数组能力时明确 ENOMEM，不把舍入溢出当成 EINVAL，也不静默缩小。
  Linux 原生对无 CAP_SYS_RESOURCE 的 INT_MAX 请求返回 EPERM；旧测试的无条件 ENOMEM 断言
  已改为该原生语义，另保留 privileged/managed allocation limit 的独立断言。
- mountinfo 同时保留 main 的真实 dev major/minor 与 net-io 的 pinned root/chroot 相对路径，
  不在冲突解决时退回 `0:0 /`。
- 保留 main 的真实文件锁、dnotify、zero-copy、O_PATH 以及 task fd-table 修复，而不是整体选择
  任一侧。除了显式冲突，还检查了自动合并处的重复容量字段、open/accept 双重关闭和设备字段。

## 验证

宿主为 Windows、.NET 11 runtime，以 `DOTNET_ROLL_FORWARD=Major` 运行 net10.0 测试；
没有更改项目 TFM。所有 guest 批次串行运行，使用本 worktree 新构建的 CLI，超时 40 秒。

| 验证项 | 结果 |
| --- | --- |
| Fiberish.Tests | 1948 PASS / 0 FAIL / 1 SKIP |
| Fiberish.SilkFS.Tests | 13/13 PASS |
| Podish.Pulse.Tests | 49/49 PASS |
| Podish.Wayland.Tests | 19/19 PASS |
| Python LTP runner | 22/22 PASS |
| Node host harness（fake CLI，不计 guest 结果） | 6/6 PASS |
| Podish.Cli build | 0 errors / 0 warnings |
| net-io，73 例，tmpfs | 63 PASS / 5 PARTIAL / 4 BROKEN / 1 SKIP；0 FAIL / 0 TIMEOUT |
| fs-fd，115 例，tmpfs | 106 PASS / 1 PARTIAL / 8 SKIP；0 FAIL / 0 BROKEN / 0 TIMEOUT |
| 基本 LTP，50 例，tmpfs | 49 PASS / 1 PARTIAL；0 FAIL / 0 BROKEN / 0 TIMEOUT |
| Node tmpfs/SilkFS 矩阵 | 24/24 PASS，真实 exit 0 + 完成标记 |

net-io 与先前检查点、fs-fd 与 main 的逐例状态完全一致。基本 LTP 的 read02 两个 O_DIRECT
变体被 tmpfs 的 TCONF 跳过，因此 runner 如实标为 PARTIAL，不能冒充 50/50 PASS。

报告：

- `artifacts/verification/net-io-merge-final.json`
- `artifacts/verification/fs-fd-merge-final.json`
- `artifacts/verification/core-ltp-merge-final.json`
- `artifacts/verification/node-merge-final.json`（24 次逐次报告路径保存在 Rows 中）
- TRX：`C:/Users/Jiang/AppData/Local/Temp/opencode/net-io-review/net-io-merged-*.trx`
- 原生对照日志：同一临时目录的 `native-review-oracle.log`，以及 `udp_cork` i386 可执行文件。

这三份 guest JSON 的 source revision 均为 `ed077af`、dirty=false。
runner 对 PARTIAL/BROKEN/SKIP 返回非零；此处没有把 exit 1 解释成整批断言失败或全通过。

## 保持明确的能力边界

本轮放行仅针对上述审查修复和 main 整合，不声称实现了 `NET_IO_REMAINING_DESIGN.md` 的全部
设计。net-io 仍有原有的四个 BROKEN：EPOLLEXCLUSIVE、依赖 userns/netns 的两个 setsockopt
用例，以及依赖 guest 网络工具的 sendmsg01。live buffer resize、完整 page-slot pipe 布局、
全部 readiness 源的跨 scheduler 支持等仍按已有能力边界处理；没有为提升 PASS 数接受假成功、
开启独占标志或移除上游断言。
