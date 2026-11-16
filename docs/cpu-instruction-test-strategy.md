# CPU 指令集测试：调研与落地方案

调研日期：2026-11-16。本文保留**实施前的调研基线与计划**，其中代码现状和风险描述并非修复后的状态。

实施已在独立 worktree/分支 `tests/cpu-instruction-tests` 开始：入口见
[`Fiberish.X86.Tests/README.md`](../Fiberish.X86.Tests/README.md)，上游资产与适配合同见
[`tests/cpu/README.md`](../tests/cpu/README.md)。本文中的上游文件数量不是测试通过数。

## 结论

推荐 **C# / xUnit 驱动的独立 CPU 测试项目 + FEX 32 位回归用例 + 按 Intel SDM 规则生成的定向测试**。

- 开源测试负责提供真实历史缺陷和复杂指令序列；规则生成负责系统性的编码、操作数和边界覆盖。
- SingleStepTests/80386 很有价值，但当前发布主体是实模式，不能当作本项目的即插即用完整测试集。
- 先定义本项目的 ISA 合同，再统计覆盖率；不能把“某助记符跑过一个例子”叫作完整覆盖。
- 不新建共享可变 static 状态；同时审计原生层的全局/TLS 状态。禁用并行或进程重启不能代替隔离正确性。

## 1. 当前代码的约束

| 事实 | 代码依据 | 对测试设计的影响 |
| --- | --- | --- |
| 32 位用户态模型，段主要表达为 base | `libfibercpu/src/common.h` 的 `Context`；`README.md` | 不以 BIOS、实模式、完整 GDT/IDT/页表仿真为目标 |
| CPUID 声明 FPU、TSC、CMPXCHG8B、CMOV、MMX、SSE、SSE2 等 | `libfibercpu/src/ops/ops_control_impl.h` 的 `OpCpuid` | 建立 i686 风格 + 声明扩展的基线，另列额外实现的扩展 |
| C ABI 已提供 Create/Destroy、寄存器、内存、Step/Run | `libfibercpu/src/bindings.h` | C# 可以直接测试核心，不必启动 Linux runtime、rootfs 或 CLI |
| 原生层已有 FCW/FSW/FTW、80 位寄存器、TSC 控制接口 | `libfibercpu/src/bindings.h` | 但 `Fiberish.X86/Native/X86Native.cs` 尚无这些接口的托管声明，需要补齐 |
| MXCSR 在 Context 内，缺少专用读写 ABI | `libfibercpu/src/common.h`、`bindings.h` | 完整 SSE 测试建议增加 MXCSR 访问；初始化不能误算成被测指令 |
| 现有 xUnit 2.6.6 / net10.0 | `Fiberish.Tests/Fiberish.Tests.csproj` | 首期沿用框架，不为本任务迁移全部测试 |
| 已有 SSE move、CVTT、strtod/x87 局部回归 | `Fiberish.Tests/Core/*InstructionTests.cs` | 可以复用测试意图，但缺少统一向量协议与完整性清单 |
| 目前 CI 主要构建测试项目 | `.github/workflows/ci.yml` | 新套件需要明确的 `dotnet test` 执行步骤，而不只是编译 |

### 1.1 需要先验证的基础设施问题

以下为代码审查发现，不等于已经用运行测试证实的指令错误：

1. `X86_Step` 固定预读 16 字节，见 `bindings.cpp`。合法指令位于页尾时，后继未映射页可能使其提前报告 #PF。不能靠给所有测试补映射下一页来掩盖它。
2. `X86_SetEIP` 设置 `eip_dirty`；`state.h` 注释其只由 Run 清除，而 Step 中未见入口清除。先用控制流、异常 EIP 与 Step/Run 对照验证其语义。
3. Create 将 Context 清零，但未见显式将 MXCSR 初始化为架构复位值 `0x1F80`。应区分“测试显式初始状态”与“CPU 默认状态测试”。
4. Step 路径不等同于生产的块构建、特化、flags 死值消除、superopcode 和块链接路径；仅靠单步通过仍不足以证明核心正确。

## 2. 开源候选集

### 2.1 FEX：首选移植来源

- 仓库：[FEX-Emu/FEX](https://github.com/FEX-Emu/FEX)。根许可证 MIT；引入时仍逐文件确认版权及例外。
- 调研固定版本：`8e95a994a014b0e68fa38b7bf17d84987102e374`。
- [测试说明](https://github.com/FEX-Emu/FEX/blob/8e95a994a014b0e68fa38b7bf17d84987102e374/unittests/Readme.md)；[32Bit_ASM](https://github.com/FEX-Emu/FEX/tree/8e95a994a014b0e68fa38b7bf17d84987102e374/unittests/32Bit_ASM)。

该版本目录清点：

| 位置 | `.asm` 文件数 |
| --- | ---: |
| `Primary` | 50 |
| `PrimaryGroup` | 12 |
| `Secondary` | 4 |
| `SecondaryModRM` | 2 |
| `TwoByte` | 2 |
| `VEX` | 32 |
| `X87` | 138 |
| `FEX_bugs` | 18 |
| 根目录 | 2 |
| **总计** | **260** |

这些是文件数，不是独立指令数，更不是全部可直接接入的用例数。32 个 VEX 文件不属于当前 SSE2 基线；其他目录也含 syscall、段选择器及特殊环境依赖。

已检查的代表性样本：

- `Primary/Primary_00.asm`：ADD 的 byte/word/dword 内存操作和高位保留。
- `Primary/Primary_A6_REP_down.asm`：DF=1 的 REPE CMPS、剩余计数和 LAHF。
- `FEX_bugs/InvertedCarrySet.asm`：逻辑运算清 CF 后，跨栈操作和 INC 的 CF 保留。
- `FEX_bugs/adc.asm`：连续 ADC/SBB。
- `X87/D8_00.asm`：内存 FLD/FADD，80 位预期结果。

**适配方式：只借用用例，不依赖 FEX 的执行器。**

1. 保留原始 `.asm`、许可证与源文件 hash；C# 工具提取 `%ifdef CONFIG` 中的 JSON。
2. NASM 在资产生成阶段编译 32 位平坦二进制。普通 `dotnet test` 消费锁定的二进制和 manifest，不联网、不现场汇编。
3. 严格解释 `Mode`、`RegData`、`MemoryRegions`、`MemoryData`、`HostFeatures` 等配置；未知字段或不支持模式必须报告，不能静默忽略。
4. `RegData` 是预期输出，不是初始寄存器值；初始化必须按执行器合同单独实现。
5. 上游 32 位 CMake 加了 `BITS 32`、`org 10000h`、段初始化前导和末尾 RET。必须审计这些包装与默认映射，不能只复制裸文件。
6. 上游大量用例使用 `0xE0000000` 等固定 guest 地址。独立 MMU 可以让并行用例使用相同 guest 地址，不应据此分配全局地址编号。
7. `MM0..MM7` 在 x87 测试中可能表示物理 FPU 槽位的 80 位值；需结合 TOP 转换，不能直接当作逻辑 ST(i) 或仅比较 64 位 MMX 值。
8. HLT 是上游的结束哨兵，不应在 ring 3 被测核心中执行后把 #GP 当成功。终止标记必须来自正常控制流到达的可信哨兵位置；不能全文搜索 `F4`，更不能接受任意 #GP。
9. 不继承 FEX 的环境变量到当前测试进程；需要特殊环境的用例显式分类。未适配用例进入清单，不能伪装成已覆盖。

主要不足：专用 32 位目录不是完整 ISA 测试；通用 `ASM` 目录还有大量 64 位测试，不能靠替换寄存器名字机械转换。

### 2.2 SingleStepTests/80386：可选硬件补充，不作主线

- [仓库及方法说明](https://github.com/SingleStepTests/80386)。软件 MIT；数据按仓库声明通常为公有领域，存在例外时以具体说明为准。
- 386EX 实机生成，每个有效 opcode/prefix 组合约 100–2,500 个测试，使用偏重边界的分布。
- 当前 README 明确：现有主体 `v1_ex_real_mode`；protected/unreal/virtual-mode 数据仍标注未来提供；不含 x87，也不可能覆盖后来的 MMX/SSE。
- MOO 为二进制容器；[dbalsom/moo](https://github.com/dbalsom/moo) 提供格式资料和 JSON 转换工具。
- [现成 NuGet 包装](https://github.com/OpenRakis/single-step-tests-nuget) 解决分发，不解决 CPU 模式适配。

不直接接入的原因：默认 operand/address size 与本项目相反；涉及段限长、16 位栈、实模式异常入栈、HLT 后 EIP 和 386 特有未定义行为。

仅考虑语义可证明一致的寄存器子集，且重新核实编码、长度、EIP、flags mask 与 CPU 代际差异。不能全局增删 `66/67` 后保留原预期结果。必须支持撤销列表，并将 final 的增量状态合并到 initial 后比较，避免漏掉本应保持不变的状态。

### 2.3 QEMU TCG i386：第二阶段参考/集成回归

- [说明](https://github.com/qemu/qemu/blob/master/tests/tcg/i386/README)；[test-i386.c](https://github.com/qemu/qemu/blob/master/tests/tcg/i386/test-i386.c)。该文件许可证 GPL-2.0-or-later，其他文件逐个审计。
- 覆盖大量整数、移位、跳转、字符串、x87、异常等情形；可以借鉴输入组合和比较掩码。
- 原生程序输出与 reference 对比，部分依赖 Linux 的信号、`modify_ldt`、vm86 等，直接执行会混入 runtime 缺陷。
- 可后续由 C# 启动隔离的 guest/参考进程；不作为主 CPU 单元测试执行器。测试依赖也不应被生产项目引用。

### 2.4 test386.asm：不建议整套移植

[barotto/test386.asm](https://github.com/barotto/test386.asm)，GPL-3.0-or-later。它是 BIOS 替代 ROM，涉及模式切换、分页、TSS、端口输出；与用户态核心范围不匹配。可以参考边界设计，但不要为运行测试而扩展成全系统模拟器。

### 2.5 新的实机 x86-64 向量：保留为研究支线

[BinPrey/x86Tester](https://github.com/BinPrey/x86Tester) 与 [向量数据集](https://huggingface.co/datasets/BinPrey/x86-instruction-test-vectors) 提供寄存器、内存、异常和多 CPU 的实机结果；数据集标注 MIT。

但它针对 64 位执行模式，记录了未定义 flags，且内存编码可能采用 64 位寻址。只允许经过 32/64 位语义等价审计的子集，例如部分纯寄存器算术或 SSE；不能截断 RAX/RIP 就宣称可直接运行。尚未做数据量与有效子集清点，不列入首期交付。

## 3. 按文档生成：生成规则，不盲目把 PDF 翻译成测试

规范来源：[Intel SDM](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)。调研时官方页面标注 SDM version 093、更新日期 2026-10-09；落地时固定实际 PDF 版本和 SHA-256，记录具体指令章节，不依赖漂移的 “latest”。

推荐 C# 使用 [Iced](https://github.com/icedland/iced)（MIT）提供编码/解码、CPUID、读写集和 flags 元数据。它不是指令执行 oracle；尤其输入相关的 flags 定义性仍需 SDM 规则补充。

### 3.1 建立 ISA 合同与清单

一个形式由 `opcode map + opcode + group/subopcode + mandatory prefix + operand/address size + reg/mem form` 标识，而不是只有 mnemonic。

- 第一层：IA-32 用户态目标的指令形式、特权指令应有的异常、CPUID 声明能力。
- 第二层：已实现但未声明的扩展、项目特定行为，单独列出，不默认为基线。
- 第三层：明确不支持的模式/扩展，记录排除理由。
- 依据规范/Iced 建立分母，与 decoder 和 handler 注册交叉检查；不能仅从实现反推测试清单，否则漏实现也会从分母消失。
- 每项记录 implemented、tested、边界维度、oracle 来源、已知偏差。不允许“没有数据便 pass”。

### 3.2 预期结果来源

1. **整数**：独立 C# 位向量/`BigInteger` 模型，显式截断到 8/16/32 位；不拷贝 C++ handler。
2. **x87/SSE 浮点**：优先使用经验证的上游向量；缺口由独立参考执行离线生成 golden corpus。不能调用核心正在使用的 SoftFloat/SIMDe 来给自身出答案。
3. **寻址/异常**：规范规则、可控页布局及精确状态断言；异常优先级用独立参考交叉核实。
4. **差分**：同一向量可离线在真实 i386 执行环境与 QEMU/其他独立实现对照；有分歧先定位规范，不以多数票决定正确性。
5. **性质测试**：验证可逆/等价关系、寄存器高位保留、未写集合不变等，作为补充，不能代替固定预期结果。

真实硬件 oracle 需要独立的 32 位 helper/进程；不能在 .NET x64 进程中把同一串字节直接按 long mode 执行。危险/异常指令必须在受控子进程捕获；Windows 可考虑 WOW64 helper，Linux 可用原生 i386 helper。macOS ARM 上普通测试只回放 golden 数据，不依赖本机 x86 硬件。

`undefined`、`preserved`、`defined` 状态必须区分：

- 未定义位不比较；保留位比较输入；定义位比较 oracle。
- masks 依赖输入，例如 shift count 为 0、1 或更大时不同，不能只给每个 opcode 一个常量 mask。
- BSF/BSR 的零输入等情况，可能连目标寄存器也未定义。
- 当 flags 被 PUSHF/LAHF 等传播到内存/寄存器时，同步传播有效位掩码。
- 近似倒数、倒数平方根和部分 x87 超越函数按规范允许误差判定，不对所有浮点指令强制 bit-exact，也不统一用宽松浮点相等。

### 3.3 优先覆盖的 edge cases

| 类别 | 必须定向生成的情形 |
| --- | --- |
| ALU/flags | 0、1、全 1、signed min/max、每个 `2^k` 及其邻居；carry/borrow 输入；AF 半字节进借位；PF 只看低字节；INC/DEC 保留 CF |
| 部分寄存器 | AL/AH/AX 写入后其他位保留；源/目标同寄存器；ESP/EBP 参与操作；立即数符号扩展 |
| shift/rotate | 原始 count 0..255；重点 0/1/width−1/width/width+1/31/32/33/255；RCL/RCR 带 CF；SHLD/SHRD 的定义域 |
| MUL/DIV | 隐式高低半寄存器；除零、商刚好能容纳/刚好溢出、signed min/−1；#DE 不应错误提交目标结果 |
| 地址/解码 | 所有合法 ModRM/SIB 形状；no-base/no-index、ESP/EBP 默认段、负 disp8/disp32、FS/GS、地址回绕、`66/67`、最长合法编码与超长编码、非法 LOCK |
| 控制流/栈 | Jcc 双路径与各条件组合；rel8/16/32 边界；CALL/RET 返回地址；PUSH/POP ESP 和 `[ESP]`；16 位操作数的特殊行为 |
| 字符串 | DF 双方向；REP count 0/1/2；比较首个/末个元素终止；源目标重叠；页故障后的计数、指针和重启位置 |
| 内存/异常 | 页尾 1/2/4/8/10/16 字节读写与跨页；后半页未映射/只读；取指恰好页尾；对齐限制；访问权限；异常优先级与部分提交按逐指令规范处理 |
| SIMD 整数 | lane 宽度、signed/unsigned 饱和、全零/全 1、跨 lane shuffle/pack/unpack；scalar 高 lane 保留；寄存器与内存来源差异 |
| x87/SSE 浮点 | ±0、normal/subnormal 边界、±∞、qNaN/sNaN、整数转换边界；四种 RC；x87 三种有效 PC；TOP 环绕、empty/full tag、MMX/EMMS；MXCSR masks、FTZ/DAZ（按目标支持合同） |
| 状态/确定性 | 多实例用相同 guest 地址；不同 FCW/FSW/MXCSR 交错；固定 TSC；创建销毁后重跑；同一线程与不同线程执行一致 |

8 位二元算术可做全部 `256 × 256`，ADC/SBB 再乘 carry 输入；16/32 位使用定向边界 + 固定种子随机 + 编码形状组合。不能把所有维度全笛卡尔积，避免规模失控；用风险优先的 pairwise/定向组合，并输出维度覆盖率。

## 4. C# 驱动与状态隔离

建议新增 `Fiberish.X86.Tests`，只引用 `Fiberish.X86`，不引用 `Fiberish.Core`、Podish、Rust 网络栈或文件系统。

```text
Fiberish.X86.Tests/
  Harness/       CpuSandbox、SafeHandle、状态快照、向量比较
  Generated/     按指令族/分片的测试入口
  Regression/    定向缺陷、隔离与并发验证
  Assets/        锁定的 manifest、向量和二进制
tests/cpu/
  sources.lock.json   来源 commit、许可证、hash、转换规则版本
  specs/             ISA 清单、边界规则、规范引用
  tools/             C# 资产导入/生成工具
```

这些是拟议目录，本轮不创建测试项目或引入依赖。

### 4.1 生命周期合同

- 每个向量创建独立的 `CpuSandbox` 与 `X86_Create()` 状态，用 SafeHandle/Dispose 保证失败路径也能 Destroy。
- MMU、映射页、回调 userdata、日志缓冲区、随机生成器和输出文件均属于当前测试；不复用可变 fixture 或 CPU 池。
- 内存逐页明确填充，不能依赖 native allocator 的历史内容；代码先写入后设 RX，数据按用例授权，其他页保持未映射。
- 状态快照包括 GPR、EIP、EFLAGS、XMM、FPU 80 位值、TOP/tag/CW/SW、MXCSR、段 base、预期内存与异常状态。
- 同时断言无关寄存器和被保护内存未变化。只检查目的寄存器会漏掉高位损坏和越界写。
- callback 可以是无状态 static thunk，但通过 userdata/GCHandle 找到当前实例；禁止用 `static CurrentCpu` 或全局 callback 字典路由测试。
- static 方法、常量与不可变编码表不等于“残留测试状态”；禁止的是跨测试持有的可变 CPU/内存/预期数组/随机数状态。`static readonly byte[]` 并不自动不可变。

### 4.2 原生状态审计与处置

| 当前状态 | 判断与建议 |
| --- | --- |
| `Engine.MmuAttachmentRegistry` | 位于 `Fiberish.Core/Core/Engine.cs`。主 CPU 测试绕过 Engine，避免把静态注册表纳入生命周期 |
| SoftFloat `THREAD_LOCAL` 舍入、精度、异常标志 | `3rdparty/softfloat/platform.h` 已定义 TLS，因此不能误报为普通全局竞态。但 TLS 在线程池线程上会保留，Run/Step 的输入/输出同步不等于恢复宿主 TLS |
| `float80.c` 非 SoftFloat 路径的 TLS rounding | 同样需要保存/恢复或实例化状态，不能因为替代构建较少使用而忽略 |
| `ops.cpp` 的 TLS `g_SpecCache` | 会跨 CPU 销毁保留。若严格要求无可变跨测试缓存，移入实例/MMU，或改成初始化后不可变查表；不能仅证明它碰巧不影响结果 |
| `bindings.cpp` 的 `g_SignalRegistered` | Create 入口未见同步，且默认注册进程级 signal handler。测试不应安装全局 signal handler；优先提供显式配置/测试构建选项。生产初始化另行保证线程安全 |
| 全局日志 callback/userdata | `logger.cpp` 尚存在。不要由测试设置，优先使用每实例日志；需确认未来测试路径不会重新使用全局接口 |
| handler/特化注册表、superopcode 查表 | 初始化后仅含代码元数据，可作为明确允许的只读共享数据，但要验证初始化顺序、线程安全、不可运行期修改和资源所有权 |
| native resolver 与 MMU identity 计数器 | 属于库装载/身份管理，不应用于生成 case 预期值；若要求整个库绝无任何 static 可变存储，需要另列更大范围重构，不能靠测试 harness 声称已满足 |

浮点首选将状态显式挂在 EmuState；若为兼容 SoftFloat 保留 TLS 实现，需在每次进入 Run/Step/状态设置入口时完整保存、装载、写回、恢复，并覆盖提前返回与嵌套回调。SSE 经宿主浮点执行时，宿主 fenv/MXCSR 也不能外泄。

严格程度需要在落地时作为验收合同固定：**每个用例结束后不残留其可变状态，所有可变测试资源可归属并释放**；不以“TLS 不是 static”钻空子，也不为禁止 static 关键字而重写 P/Invoke。

### 4.3 真正并行，而不是只写了 Theory

xUnit v2 默认不同 collection 可并行，同一类/collection 中的 Theory 数据行通常串行。大 corpus 放进一个 Theory 不能自动得到用例并行。

- 首期按指令族/资产分片生成独立测试类，避免统一的共享 collection。
- 超大穷举在明确的分片内做有界并发，每个执行项仍创建独立 sandbox；失败必须带 case ID，不能只得到一个无定位信息的 AggregateException。
- 限制并发 CPU 数和映射内存，不用无限 Task.WhenAll。
- 不在测试中修改进程级环境变量、当前目录、文化设置、Console 输出目标或 signal handler。
- 资产在构建/生成阶段准备；测试只读，不在并行 discovery/execution 中共享解压目录或更新缓存。
- 固定算法与 per-case seed；不要使用 `Random.Shared` 或依赖运行顺序。

必须有隔离回归：同线程 A/B/A 交错不同 RC/PC/异常标志、跨线程并发、同地址不同内存、Dispose 后重建、失败后的清理。并发结果应与串行结果逐 case 一致。

## 5. 执行模式、诊断和覆盖率

### 5.1 至少两条执行路径

1. **Step**：单条指令的解码、结果和异常，便于定位。
2. **Run**：相同初始状态的序列，在新实例上运行；覆盖 flags 保留/消除、特化、热缓存、块链接、SMC 和 superopcode。

不要假设 `Run(maxInsts: 1)` 就是精确的单条指令边界，先验证块级 budget 语义。序列必须有可靠结束协议；对无限循环/REP 设置预算和外部超时。无法中断的 native hang 应由隔离 worker 的进程超时兜底，而不是把所有 CPU 测试改成串行。

superopcode ON/OFF 是不同原生构建；当前 P/Invoke 同库名和装载模型不适合在同一进程反复切换库。用独立构建输出与独立 testhost 运行同一 corpus，比较各自结果。

### 5.2 失败可复现

每条记录输出：稳定 case ID、来源 commit/path/hash、规范条款、指令字节、mode、初始状态、内存布局、预期/实际差异、有效位掩码、执行模式和随机 seed。尽量落盘为一个可单独重放的向量，不依赖完整上游 runner。

支持按指令族、case ID、来源、Step/Run 和 seed 过滤；已知失败必须绑定理由/issue，不允许全族跳过或默认通过。

### 5.3 覆盖率不是一个百分数

- ISA 形式覆盖：计划内形式中，实际有运行且带断言的形式比例。
- edge 覆盖：进借位、溢出、各 flags 结果、计数分区、地址形状、跨页/异常等维度。
- 执行路径覆盖：Step、Run、冷/热缓存、superopcode 开/关。
- 原生代码分支覆盖：用 Clang coverage 或其他 C++ 工具收集；现有 coverlet 只覆盖 C#，不能反映 C++ handler 覆盖率。
- 未实现、未适配、oracle 缺失和已知失败分别统计。不能把 skipped 从报告中隐去，虚报完整性。

## 6. 建议实施顺序与验收

### P0：合同与隔离探针

- ISA 清单、静态/TLS 清单、向量 schema 与 mask 规则。
- 最小 C# sandbox；补 FPU/MXCSR/TSC 托管接口。
- 先测 Step 页尾、EIP、默认状态及 A/B/A 浮点隔离；确认问题后再做最小修复，不把修复与大规模移植混在一起。
- 验收：多实例可并发、状态可释放、同线程执行顺序不影响结果；原生 TLS/宿主状态不携带前一个用例的状态。

### P1：有独立答案的整数与 flags

- ALU、ADC/SBB、shift/rotate、mul/div、位操作、条件分支、部分寄存器、栈和寻址。
- 引入 8 位穷举和 16/32 位定向生成，建立 Step/Run 对照。
- 验收：所有基线形式有状态记录；每个已支持形式有正常、边界和适用异常用例；缺失项可在 CI 报告中定位。

### P2：移植 FEX 可用子集与浮点/SIMD

- 从上述代表性文件打通导入，再逐项分类 260 个 32 位文件；每个文件都有 imported/excluded/blocked 状态与理由。
- 完善 x87、MMX、SSE/SSE2 的定向生成与离线 golden 向量。
- 验收：许可证/来源可追溯，未知配置不静默丢失，无全局环境配置，x87 物理槽位/TOP 转换有专门验证。

### P3：优化路径和持续扩充

- superopcode、flags 活性、SMC、fault/restart、热冷缓存、跨页和随机差分。
- PR 跑定向边界与关键回归；nightly 跑全穷举、更多固定种子和 ON/OFF 构建矩阵。
- 至少 Linux x64 / Windows x64 / macOS ARM64 回放确定性 corpus；各平台实际测试，不止 build。

当前环境已找到 .NET 与 CMake，未在 PATH 找到 NASM；汇编资产生成前需补齐或配置 NASM 路径。本轮未下载大 corpus、未修改生产代码，也未运行或宣称通过任何新测试。
