# CPU 指令测试

`Fiberish.X86.Tests` 通过 C ABI 测试 IA-32 用户态核心。每个测试向量创建独立的
`CpuSandbox`，由它持有 CPU、MMU 和映射页。测试覆盖 CPUID 宣告的用户态基线与扩展，
包括 x87、MMX、SSE 和 SSE2。仓库用定向用例检查整数结果、操作数和页边界，并回放
锁定版本的 FEX 用例。

## 运行测试

在仓库根目录运行以下命令。第一条跑定向测试和 FEX 回归，第二条复用测试构建，
穷举 8 位加法和减法。

```sh
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release \
  --filter "Category!=Exhaustive" --blame-hang-timeout 3m
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --no-build \
  --filter "Category=Exhaustive" --blame-hang-timeout 5m
```

用独立的原生构建目录关闭融合。每个构建使用新的 testhost，避免复用已加载的库。

```sh
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release \
  -p:FibercpuBakingPreset=custom -p:NativeBuildDir=build_native/cpu-nosuper -p:EnableSuperOpcodes=false \
  --filter "Category!=Exhaustive"
```

[CI 工作流](../.github/workflows/cpu-tests.yml)在 Linux 上分别开关融合，
在 Windows 和 macOS 上开启融合。它先验证 FEX 资产，再跑定向和导入测试。
定时运行和手动触发还会跑字节算术穷举。2026-11-23 的
[四组运行记录](https://github.com/meokit/podish/actions/runs/45107890009)
均通过。[RESULTS.md](../tests/cpu/RESULTS.md) 记录 2026-11-16 集成前的测量。

用 `--filter FullyQualifiedName~类名` 筛选测试。需要串行复验时，传入
`--settings Fiberish.X86.Tests/validation.serial.runsettings`，再逐项对照并行结果。

## 执行合同

- `Step` 检查单条指令的结果、异常向量和重启 EIP。`Run` 检查块解码、特化、
  flags 活性、融合、链接和 SMC。Run 按块处理预算，单条指令测试使用 Step。
- 每个向量通过 `SafeHandle` 释放 CPU，独立初始化页和寄存器。不同实例可以使用
  相同的 guest 地址。测试通过 `X86_CreateWithOptions` 禁用进程级 fatal signal handler，
  并用实例 userdata 调用回调。
- xUnit 按测试类并行，配置上限是四个 worker。FEX 用例分在八个类中，同一个
  Theory 的数据行不作为并行保证。隔离回归还覆盖跨线程、同线程交错、同地址
  不同映射和销毁后复用。
- FPU 环境测试验证 Run、Step 和嵌套回调前后的宿主状态。测试可以经托管 ABI
  读写 FCW、FSW、FTW、80 位寄存器和 MXCSR，并固定 TSC。

[测试沙箱](../Fiberish.X86.Tests/Harness/CpuSandbox.cs)和
[隔离回归](../Fiberish.X86.Tests/Isolation/)提供实现。取指页尾、复位状态和
条件跳转用例分别位于
[PageBoundaryTests](../Fiberish.X86.Tests/Addressing/PageBoundaryTests.cs)、
[CpuIsolationTests](../Fiberish.X86.Tests/Isolation/CpuIsolationTests.cs)和
[ConditionalBranchTests](../Fiberish.X86.Tests/Addressing/ConditionalBranchTests.cs)。

## FEX 语料

仓库锁定 [FEX](https://github.com/FEX-Emu/FEX) 提交 `8e95a994a014b0e68fa38b7bf17d84987102e374`，
保留源文件和 [MIT 许可证](../tests/cpu/upstream/fex/LICENSE)。
其中 260 份 32 位汇编源文件有 204 份可以回放，共含 137 份 x87 用例和
14 份 FEX 回归。其余 56 份在清单中记录了原因，例如 AVX、依赖 Linux 段描述符
或没有可移植的预期结果。[来源和资产清单](../tests/cpu/README.md)

导入工具用 NASM 生成平坦二进制，CPU 测试读取仓库内锁定的文件和清单。
导入时保留上游测试的 EAX 初始化和代码地址，用等长 NOP 代替外层包装中的
DS/ES 选择器加载。`RegData` 描述结果。测试通过 FSW.TOP 把上游物理 x87 槽号
换算成逻辑 ST(i)。测试先确认执行到指令边界，再按 manifest 中的 HLT 地址
结束单步。两份回绕测试会生成 HLT，runner 在这个例外中核对完整生成字节和偏移。
[导入工具](../tests/cpu/tools/FexImport/Program.cs)和
[FEX runner](../Fiberish.X86.Tests/Fex/FexRunner.cs)定义包装细节。

## 新增用例

用 opcode map、opcode、group/subopcode、mandatory prefix、操作数宽度、地址宽度
和寄存器或内存形态标识测试覆盖。用 [Intel SDM](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)
确定语义，记录引用的版本和指令章节。`Iced` 提供编码、解码和元数据。
整数用例用独立的 C# 定宽模型计算预期，把结果截断到 8、16 或 32 位。8 位 ADD/SUB
各跑 `256 × 256` 对输入，16/32 位用定向边界和固定种子补充。x87 和 SIMD 用例
优先使用锁定的上游结果。缺少预期时可以从独立参考实现或 32 位硬件辅助进程
生成离线向量。

按输入区分定义、保留与未定义的位。移位计数为 0 时断言保留的 flags，
计数为 1 和大于 1 时按各指令规则比较。BSF 和 BSR 的零输入还要处理目标寄存器
的未定义结果。浮点输入用精确位模式表达，近似指令按规范允许的误差比较。
[x87 预期说明](../tests/cpu/X87-NOTES.md)

| 指令或状态 | 定向边界 |
| --- | --- |
| 算术、位运算 | carry/borrow、半字节进借位、符号边界、部分寄存器高位保留、移位计数掩码、除零与商溢出 |
| 寻址和内存 | ModRM/SIB、默认 DS/SS、FS/GS、16 位地址回绕、最长编码、非法 LOCK、页尾取指、跨页权限和异常优先级 |
| 分支、栈、字符串 | Jcc 双路径、相对位移边界、PUSH/POP ESP、REP 计数和 DF 方向、故障后的指针和计数 |
| SIMD 和 x87 | lane 饱和及保留、舍入模式、NaN、FTZ/DAZ、TOP 回绕、MMX 与 x87 槽位切换 |
| 优化路径 | 冷热块缓存、SMC、拼接、融合、fault/restart EIP、不同宿主上的结果一致性 |

报告中分开记录指令形态覆盖、边界覆盖、Step/Run 路径和原生分支覆盖。xUnit
结果按测试行计数，一个测试行可以包含多个操作数组合。定位失败时保留 case ID、
输入字节、初始状态、预期与实际差异、flags 掩码、执行路径和随机种子。

## 2026-11-16 调研中的备选语料

以下备选均来自实施前调研。当前 CI 回放 FEX 锁定语料。

- [SingleStepTests/80386](https://github.com/SingleStepTests/80386) 的实模式向量来自
  386EX，使用 [MOO](https://github.com/dbalsom/moo) 容器。
  [NuGet 包](https://github.com/OpenRakis/single-step-tests-nuget)提供分发。
  移植前要核对操作数和地址宽度、段限长、异常模型、EIP 和 flags mask，并按
  原仓库说明区分 MIT 工具代码和标为公有领域的向量。
- [QEMU TCG i386](https://github.com/qemu/qemu/blob/master/tests/tcg/i386/README) 中的
  [test-i386.c](https://github.com/qemu/qemu/blob/master/tests/tcg/i386/test-i386.c)
  可为运算和异常组合提供参考。依赖 Linux 信号、`modify_ldt` 或 vm86 的用例属于
  进程级集成测试。调研时 `test-i386.c` 使用 GPL-2.0-or-later，引入其他文件前
  也要查各自许可证。
- [test386.asm](https://github.com/barotto/test386.asm)（GPL-3.0-or-later）涉及
  BIOS、模式切换、分页和端口 I/O。用户态核心可以参考边界设计。
- [x86Tester](https://github.com/BinPrey/x86Tester) 的
  [实机向量](https://huggingface.co/datasets/BinPrey/x86-instruction-test-vectors)
  使用 64 位执行模式。引入纯寄存器或 SSE 子集前，先审计 32/64 位语义等价性。
