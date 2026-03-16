# SuperOpcode 设计文档

## 文档状态

- Owner: Fiberish Core Maintainers
- Last Updated: 2026-03-16
- Status: Draft v1
- Scope: `libfibercpu` decode/dispatch 路径 + profile/生成脚本

## 1. 背景

当前解释执行热路径已经有两类明显成本：

- 单个 op 的 `DispatchWrapper` 框架成本
- 相邻 op 之间的 handler 跳转、`NextOp()` 前进、分支预测与 I-cache 干扰

已有 profile 表明，CoreMark 这类 workload 中会反复出现稳定的局部 op 序列。  
如果两个相邻 op 经常一起出现，并且中间没有必须暴露给通用 dispatch 的边界，那么把它们合并成一个更宽的 handler，通常可以减少：

- 一次 handler 跳转
- 一次 `DispatchWrapper` 框架
- 一部分重复的 decode 元信息读取
- 一部分不必要的 branch predictor / BTB 噪声

这里把这种“由多个连续 op 合并出的单个执行单元”称为 `SuperOpcode`。

## 2. 目标

`SuperOpcode` 的第一阶段目标很克制：

- 只做 block 内连续 2 个 op 的合并
- 只合并 profile 中高频的 2-gram
- 只在 decode 完成后重写第一个 op 的 handler
- 不改变 `DecodedOp` 大小
- 不引入新的动态编译器

非目标：

- 不在第一阶段支持 3 个及以上 op 合并
- 不在第一阶段支持跨 block 合并
- 不在第一阶段改变 guest 可观察语义
- 不在第一阶段做基于 SSA/IR 的 peephole 优化

## 3. 核心思路

### 3.1 数据来源

通过脚本统计 block 内 `handler[i] -> handler[i + 1]` 的出现频次，得到常见 2-gram。

输入可来自：

- handler profile block dump
- block trace dump
- 直接遍历 `blocks.bin` 导出的 op 序列

输出应至少包含：

- `first_handler`
- `second_handler`
- 频次
- 覆盖率
- 可选的 guest opcode / ModRM / 前缀分布

### 3.2 生成物

对选中的 2-gram，脚本生成一个新的 handler：

- 名字形如 `SuperOpcode_<A>__<B>`
- 内部顺序执行两个原始 `LogicFunc`
- 在成功路径上直接跳到第三个 op

解码后，如果发现：

- 当前 op 的 handler 是 `A`
- 下一 op 的 handler 是 `B`
- 且满足该 superopcode 的附加约束

则把第一个 op 的 handler 改写成 `SuperOpcode_<A>__<B>`。

第二个 op 仍然保留在 block 中，但正常执行时会被第一个 op 跨过去。

## 4. 执行模型

假设原始执行链是：

`op0(A) -> op1(B) -> op2(C)`

重写后变成：

- `op0.handler = SuperOpcode_A_B`
- `op1.handler = B`，但正常路径不会直接落到这里
- `op2.handler = C`

`SuperOpcode_A_B` 的成功路径相当于：

1. 调用 `A` 对应的 `LogicFunc`
2. 若 `A` 返回非 `Continue`，按原语义退出
3. 调用 `B` 对应的 `LogicFunc`
4. 若 `B` 返回非 `Continue`，按原语义退出
5. 直接跳到 `op2->handler`

关键点是：

- `A` 和 `B` 仍然使用原来的逻辑实现
- 不改变单 op 语义
- 只改变“两个 op 之间的调度框架”

## 5. 推荐实现形态

### 5.1 生成脚本

建议新增一个脚本，输入 2-gram 统计结果，输出：

- 一个 `.inc` 或 `.generated.h`
- 可选一个 `.generated.cpp`
- 一份 manifest，记录生成了哪些 superopcode

推荐输出内容：

- superopcode 的枚举表
- `HandlerFunc` 声明
- `DispatchWrapper` 风格的包装函数
- `(handler_a, handler_b) -> super_handler` 的查找表初始化代码

### 5.2 组合方式

建议复用现有 `LogicFunc` 模型，而不是直接拼两个 `HandlerFunc`。

原因：

- `HandlerFunc` 已经带了一层 dispatch 外壳，直接嵌套会重复框架
- `LogicFunc` 的输入输出更接近“可组合的解释器 primitive”
- 现有 `LogicFlow` 已经表达了 continue/exit/restart/branch 等结果

理想生成形态类似：

```cpp
template <LogicFunc A, LogicFunc B>
ATTR_PRESERVE_NONE int64_t SuperOpcodeDispatch(EmuState* state, DecodedOp* op, int64_t instr_limit,
                                               mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    auto* op1 = NextOp(op);
    auto flow0 = A(state, op, &utlb, GetImm(op), &branch, flags_cache);
    // 处理 A 的 flow
    auto flow1 = B(state, op1, &utlb, GetImm(op1), &branch, flags_cache);
    // 处理 B 的 flow
    auto* op2 = NextOp(op1);
    ATTR_MUSTTAIL return op2->handler(state, op2, instr_limit, utlb, branch, flags_cache);
}
```

实际生成代码不一定长这样，但语义应尽量接近。

### 5.3 查找表

运行时不应做昂贵查找。  
推荐在进程初始化期构建一个静态表：

- key: `(handler_a, handler_b)`
- value: `super_handler`

decode 阶段只做一次查询，然后写回 `op->handler`。

查找结构可选：

- 小规模静态数组 + 线性扫
- 排序数组 + 二分
- `unordered_dense` 静态 map

第一阶段条目不会很多，优先简单实现。

## 6. Decode 接线

### 6.1 时机

推荐在 block decode 完成、sentinel 追加之前或之后统一跑一遍 block-local pass。

输入：

- `temp_ops`
- op 数量
- 每个 op 当前已经解析好的 handler

遍历方式：

- 对每个 `i` 看 `temp_ops[i]` 和 `temp_ops[i+1]`
- 如果命中 superopcode 表，则尝试应用

### 6.2 应用规则

应用 superopcode 时，第一个 op 至少需要满足：

- `op[i]` 不是 block 最后一个真实 op
- `op[i+1]` 不是 sentinel
- `(handler_i, handler_{i+1})` 命中表
- 两个 op 都满足对应的 legality check

应用后：

- `op[i].handler = super_handler`
- `op[i]` 可选标记一个 debug bit，表示已被 superopcode 覆盖

不建议第一阶段直接删除 `op[i+1]`，因为这会改变 block 布局、`NextOp` 偏移和大量现有假设。  
“保留第二个 op 但跳过它”更稳。

## 7. 正确性约束

不是所有相邻的两个 op 都适合合并。第一阶段必须保守。

### 7.1 必须拒绝的组合

- 第一个 op 可能 `ContinueSkipOne`
- 第一个 op 或第二个 op 可能依赖“当前 op 必须等于 dispatch 入口 op”的特殊协议
- 第一个 op 成功后可能改写 `op` 指针语义
- 第二个 op 对 block profiler / debug hook 有特殊边界要求
- 任一 op 是 sentinel / exit handler
- 任一 op 是需要精确单步可见边界的特殊控制流 op

### 7.2 内存重启语义

如果第一个 op 返回：

- `RestartMemoryOp`
- `RetryMemoryOp`
- `ExitOnCurrentEIP`
- `ExitOnNextEIP`
- `ExitToBranch`

则 superopcode 必须立刻按原语义退出，不能继续执行第二个 op。

第二个 op 同理。

### 7.3 分支语义

第一个 op 如果写了 `branch` 并返回分支型 flow：

- superopcode 必须把该 `branch` 原样传给原来的 resolver 路径

只有在第一个 op 返回普通 `Continue` 时，才能进入第二个 op。

### 7.4 EIP 可见性

如果中途退出：

- 必须保证与“单独执行第一个或第二个 op”完全一致的 `eip` 同步行为

这里最容易出错的是：

- 第一个 op 成功，第二个 op fault/restart
- 第一个 op 已经修改状态，第二个 op 需要把可见 `eip` 设为自己的起始/结束位置

所以 superopcode 里的 flow 处理不能偷懒，必须按“当前正在处理哪个 op”分别走原规则。

## 8. 推荐的第一批候选

候选应优先满足：

- 高频
- 都是普通 `Continue` 型 ALU/compare/data-move op
- 没有复杂控制流边界

典型可能包括：

- `cmp/test + jcc` 之前的纯 flags producer
- `mov/load + cmp/test`
- `add/sub/and no-flags + cmp/test`
- 常见 register-only ALU 串

但要注意：

- `cmp + jcc` 虽然直觉上很香，第二个 op 是 control-flow，合法性检查要更严
- 比起一上来碰 `jcc`，第一阶段也许先做纯非控制流二元组更稳

## 9. 统计脚本输出建议

N-gram 统计脚本建议输出三层视图：

1. handler 级频次
2. guest opcode 级频次
3. “可合并候选”频次

示例字段：

- `handler_a`
- `handler_b`
- `count`
- `count_pct`
- `opcode_a`
- `opcode_b`
- `modrm_shape_a`
- `modrm_shape_b`
- `is_candidate`
- `reject_reason`

这样可以避免只看 handler 热度就错误选择一些实际上不适合合并的组合。

## 9.1 现有 Runner / Block Dump 能力评估

当前 [runner.py](/Users/jiangyiheng/repos/x86emu/benchmark/podish_perf/runner.py) 已经提供了一个可复用的基础，但还不能直接满足 `SuperOpcode` 候选筛选。

### 现有能力

- 支持固定 workload 批量跑样本
- 支持 `--jit-handler-profile-block-dump`
- 支持自动调用 [analyze_blocks.py](/Users/jiangyiheng/repos/x86emu/scripts/analyze_blocks.py)
- 每个 sample 都会保存 transcript、summary 和可选 guest stats 目录

### 现有数据里已经够用的部分

如果 `blocks.bin` 导出完整可读，那么理论上已经足够恢复：

- block 内 op 顺序
- 每个 op 对应的 handler symbol
- block 级执行次数

这意味着：

- handler 2-gram 统计不一定需要新增 VM 内埋点
- 可以先离线遍历 block dump，按 `block.exec_count` 对 block 内相邻 handler 做加权

### 当前不足

以现在的 runner 形态，还存在几处不足：

1. `runner.py` 只把 block dump 当“附带产物”  
   当前主目标还是 benchmark 计时，不是构建稳定的 op-sequence 语料。

2. `run_block_analysis()` 没有启用 `--n-gram`  
   当前自动分析只输出 block/op 列表，不会直接产出 2-gram 报告。

3. 目前一次只跑单个 workload，样本覆盖窄  
   如果只基于 CoreMark，很容易把 superopcode 过拟合到单一 workload。

4. 结果目录中的 `blocks_analysis.json` 目前可能没有有效 `blocks` 列表  
   这说明当前 block dump 导出/消费链路至少还需要验证，不能直接假设它已经能稳定提供完整 block-op 序列。

5. 缺少跨 sample 聚合  
   当前每次 sample 独立分析，没有统一聚合脚本来输出“全样本 top 2-gram 候选”。

### 结论

结论是：

- `runner.py` 作为采样入口已经基本够了
- 但“当前 runner 输出的数据管线”还不够直接支持 `SuperOpcode` 选型
- 下一步更合适的是扩展数据后处理，而不是立刻改 VM 内部新增复杂 runtime 统计

## 9.2 基于现有 Runner 的实施方案

推荐按下面顺序推进。

### Step 1: 先打通 block dump 可读性

先确认 `blocks.bin -> analyze_blocks.py -> blocks_analysis.json` 这条链路稳定输出非空 block/op 列表。

需要检查：

- `blocks.bin` 导出格式是否与脚本读取格式一致
- runtime base / handler 指针解析是否正确
- 是否有 schema 漂移导致脚本读空

这是 `SuperOpcode` 的前置条件。  
如果这里没打通，后续 2-gram 全是空谈。

### Step 2: 扩展 `analyze_blocks.py`

建议把 [analyze_blocks.py](/Users/jiangyiheng/repos/x86emu/scripts/analyze_blocks.py) 变成第一版 N-gram 数据源，而不是新增一套完全平行的脚本。

建议新增能力：

- `--n-gram 2`
- `--group-by handler`
- `--group-by guest-opcode`
- `--weighted-by exec-count`
- 输出每个 n-gram 的：
  - weighted count
  - unique block count
  - sample count
  - 前后文示例

### Step 3: 新增聚合脚本

建议新增一个聚合脚本，例如：

- `benchmark/podish_perf/analyze_superopcode_candidates.py`

输入：

- 一个 `results/` 目录
- 多个 `guest-stats/*/blocks_analysis.json`

输出：

- `superopcode_candidates.json`
- `superopcode_candidates.md`

职责：

- 跨 sample 聚合 2-gram
- 去重 runtime address 差异
- 按 weighted count / coverage 排序
- 标记候选与 reject reason

### Step 4: 扩展 runner 选项

在 [runner.py](/Users/jiangyiheng/repos/x86emu/benchmark/podish_perf/runner.py) 中建议新增一组更明确的 superopcode 采样参数。

推荐选项：

- `--export-block-dump`
- `--analyze-blocks`
- `--block-n-gram 2`
- `--aggregate-superopcode-candidates`
- `--candidate-output <path>`

其中：

- `--jit-handler-profile-block-dump` 可以保留兼容
- 但长期建议收敛为更通用的 block stats / n-gram 语义，而不是把功能绑死在“handler profile build”

### Step 5: 扩大 workload 覆盖

第一批候选不应只来自 CoreMark。

建议至少覆盖：

- `coremark run`
- `compress`
- `compile`
- 未来可加：
  - 小型 shell workload
  - libc-heavy workload
  - branch-heavy workload

这样得到的候选更不容易被单一程序绑架。

## 9.3 对 Runner 的具体扩展建议

如果直接在现有 [runner.py](/Users/jiangyiheng/repos/x86emu/benchmark/podish_perf/runner.py) 上扩展，建议如下。

### A. 保留现有 SampleResult，但新增字段

建议新增：

- `block_dump_dir`
- `ngram_analysis_json`
- `candidate_manifest_json`

这样 summary.json 可以直接串起整条数据链。

### B. 将 block dump 与 benchmark 计时解耦

当前 `run_sample()` 中，导出 block dump 与 benchmark 样本是绑在一起的。  
对 `SuperOpcode` 来说，更好的模式是：

- benchmark 模式：追求时间稳定
- block-dump 模式：追求语料覆盖

建议后续允许：

- 单独跑“语料采集模式”
- 调低 `repeat`
- 提高 workload 多样性

### C. 支持后处理聚合

建议 runner 在全部 sample 完成后，可选自动调用聚合脚本，产出：

- 全局 top 2-gram
- 每个 workload 的 top 2-gram
- 候选 superopcode 清单

这会比手工翻多个 `blocks_analysis.json` 高效很多。

现在这一步已经可以直接跑，建议流程是：

```bash
python3 benchmark/podish_perf/runner.py \
  --engine jit \
  --case run \
  --repeat 3 \
  --jit-handler-profile-block-dump \
  --block-n-gram 2 \
  --aggregate-superopcode-candidates
```

或者对已有结果目录单独聚合：

```bash
python3 benchmark/podish_perf/analyze_superopcode_candidates.py \
  benchmark/podish_perf/results/<timestamp>/guest-stats \
  --n-gram 2 \
  --top 100 \
  --output-json benchmark/podish_perf/results/<timestamp>/superopcode_candidates.json \
  --output-md benchmark/podish_perf/results/<timestamp>/superopcode_candidates.md
```

当前聚合脚本的职责是：

- 递归发现 `blocks_analysis.json`
- 跳过 `blocks` 为空或存在明显 schema/dump 漂移告警的样本
- 直接从 `blocks` 重建 N-gram，而不是依赖 `top_ngrams`
  这样不会被单样本截断影响总榜
- 输出：
  - `superopcode_candidates.json`
  - `superopcode_candidates.md`

### D. 记录 build identity

因为 superopcode 候选依赖 handler symbol 名和布局，建议 summary 里额外记录：

- git commit
- build flavor
- `EnableHandlerProfile`
- `FIBERCPU_EXIT_HANDLER_REPLICA_COUNT`
- 是否启用 superopcode

这样后续比较不同数据集时不会混淆来源。

## 9.4 推荐的数据成熟度标准

在开始生成第一批 superopcode 之前，建议至少满足：

- `blocks_analysis.json` 中能稳定看到非空 `blocks`
- 至少 3 个 workload
- 至少 3 次 sample 聚合
- top 2-gram 排名在样本间基本稳定
- 候选 pair 的 coverage 足够高

如果这些条件达不到，就更适合先修数据管线，而不是急着生成 superopcode。

## 10. 代码生成建议

推荐生成代码而不是手写大表，原因是：

- 候选组合会不断调整
- 命名规则可以统一
- 容易同步生成 legality metadata
- 可以自动产出注册表和测试清单

生成器输入建议：

- `superopcode_candidates.json`

生成器输出建议：

- `libfibercpu/generated/superopcodes.generated.h`
- `libfibercpu/generated/superopcodes.generated.cpp`

每条 superopcode 元数据建议包含：

- 名称
- `LogicFunc A`
- `LogicFunc B`
- 需要的 legality predicate
- 是否启用

## 11. 调试与可观测性

建议保留以下调试能力：

- 环境变量总开关：`FIBERCPU_ENABLE_SUPEROPCODE`
- 日志/计数器：命中多少次 superopcode
- block dump 中可见“哪个 op 被 superopcode 覆盖”
- 可打印 `(A,B) -> SuperOpcode_A_B` 映射

这样当出现错误时，可以很快：

- 全局关闭 superopcode 验证问题是否消失
- 定位到具体哪一对组合有问题

## 12. 测试策略

### 12.1 单元测试

对每个生成的 superopcode，至少覆盖：

- 正常 `Continue -> Continue`
- 第一个 op 提前退出
- 第二个 op 提前退出
- branch 写回
- memory restart / retry
- fault 时的 `eip` 同步

### 12.2 差分测试

同一 guest 输入，比较：

- superopcode 关闭
- superopcode 开启

对比：

- GPR
- flags
- memory
- `eip`
- 异常/信号行为

### 12.3 Profile 回归

每次扩充 superopcode 集合，都至少看：

- 总体 wall time
- top-25 self time
- 命中 superopcode 的次数
- 热门原始 handler 是否真的下降

避免只因为热点重排就误判优化有效。

## 13. 风险

主要风险有四类：

1. 语义风险  
   flow 处理不完整，导致 restart/exit/eip 同步错误。

2. 代码体积风险  
   生成过多 superopcode，造成 I-cache 和指令布局恶化。

3. 维护风险  
   hand-written 组合过多后难以管理，所以第一阶段必须脚本化生成。

4. profile 过拟合  
   只对单一 workload 有效，换程序后收益消失甚至回退。

## 14. 分阶段落地建议

### Phase 1: 基础设施

- 增加 N-gram 统计脚本
- 打通 `blocks.bin -> analyze_blocks.py` 可读链路
- 让 runner 能稳定导出 block-op 序列
- 定义 superopcode manifest 格式
- 增加代码生成脚本
- 引入全局开关和 debug 计数

### Phase 2: 最小可用版本

- 基于聚合数据选出少量稳定 2-gram
- 只启用少量纯 `Continue` 二元组
- decode 后按 handler pair 重写第一个 op
- 保留第二个 op，不改 block 布局

### Phase 3: 扩展集合

- 按 profile 扩大候选
- 加入更复杂但收益更高的组合
- 引入 legality predicate 细分

### Phase 4: 更激进优化

- 研究 3-op superopcode
- 研究 block 内 op 物理压缩
- 研究与 specialized handler / modreg fast path 的协同生成

## 15. 当前建议

当前最合理的实现路径是：

- 先把 N-gram 统计做出来
- 先只支持 2-op
- 先只做“decode 后 handler 重写”
- 先只合并最稳的普通 `Continue` 组合

等这一版跑通并验证收益后，再决定要不要把 `cmp/test + jcc` 这类控制流组合纳入第一批。

这条路线的优点是：

- 对现有解释器结构侵入小
- 回滚简单
- 容易逐步扩容
- 更容易把错误限制在单个 superopcode 上
