# SuperOpcodes

The interpreter can run two, three, or four adjacent guest instructions in one
native handler. A SuperOpcode calls the constituent instruction kernels in order
and makes one tail dispatch on the success path. Decode keeps each constituent's
slots so execution can resume at the instruction that faulted.

## Build inputs

CMake uses the trained `3.5m` preset by default. Each preset supplies a set of
fusion recipes and their exact operand shapes. Select
`FIBERCPU_BAKING_PRESET=custom` to change the individual baking options.
In a custom build, `FIBERCPU_FUSION_STRATEGY=BASE` selects the legacy
32-sequence table, `FIBERCPU_ENABLE_SUPEROPCODES=OFF` runs scalar handlers, and
`FIBERCPU_STATIC_SPECIALIZATION=OFF` keeps only the mandatory scalar slot
bindings.

| Default input | Role |
| --- | --- |
| [Sequence manifest](../libfibercpu/presets/3.5m/recipes.json) | Orders candidates by their numeric, catalog-assigned operation IDs. |
| [Fusion operand plan](../libfibercpu/presets/3.5m/fusion-operands.json) | Lists the exact operand shapes to compile for those sequences. |
| [Instruction catalog](../libfibercpu/src/instruction_catalog.json) | Defines instruction identities, kernel bindings, and eligible family aliases. |

`Podish.PerfTools gen-superopcodes` reads the version 3 manifest and emits
C++ handlers and lookup code. CMake includes the selected generated source and
runs [gen_operand_specializations.py](../scripts/gen_operand_specializations.py)
to generate the exact operand clones. To test a candidate in a custom build,
set `FIBERCPU_GENERATED_SUPEROPCODES_FILE` to its generated source and
`FIBERCPU_OPERAND_PLAN_FILE` to the corresponding plan.

## Decode and lookup

`DecodeBlock` calls `ApplySuperOpcodesToBlockOps` on the decoded instruction
array before packing the runtime slots. The matcher sees a canonical view of
the scalar handlers, so a previously bound scalar handler can match its
catalog identity. The generated code hashes the first two canonical handler
addresses. It then checks the longer sequences first and tests each candidate
with `LowerFusionOperandHandler`. That last step checks the decoded shape
of each constituent, including slot width, ModRM, prefix, flags and operands.

A match replaces the current entry handler. The matcher steps to the next
instruction in the decoded array, so another entry can also start a fusion.
The compiled handler advances by the baked slot widths, including the data slot
for any two-slot instruction. An entry with no matching operand clone uses the
scalar binding. Block concat can restore the canonical identities and run
matching again on the joined instruction array. See
[superopcodes.cpp](../libfibercpu/src/superopcodes.cpp),
[the sequence generator](../Podish.PerfTools/Program.SuperopcodeSequences.cs)
and [bindings.cpp](../libfibercpu/src/bindings.cpp).

The decoder's reverse lookup relies on distinct handler addresses. For example,
opposite SUB encodings can produce identical machine code for one pair of
registers while still requiring different ModRM interpretations. Windows links
libfibercpu and the NativeAOT CLI with `/OPT:NOICF` to preserve those identities.
The [block concat regression](../Fiberish.X86.Tests/Regression/BlockConcatTests.cs)
checks the operand direction after a Jcc fallthrough concat.

## Execution and replay

The generated code passes the bound operands to the same kernels used by
scalar handlers. It carries the micro-TLB state, branch target and flags through
the sequence. For selected CMP, TEST and register SHR shapes followed by JZ
or JNZ, the generator passes the producer's zero result to the branch and
packs the flags for the exit.
See [the operand clone generator](../scripts/gen_operand_specializations.py)
and [the flow dispatch macros](../libfibercpu/src/superopcodes.h).

Each constituent checks its `LogicFlow`. `RestartMemoryOp` and
`RetryMemoryOp` use the faulting constituent's slot to enter the corresponding
memory path. Other early exits use that slot for guest EIP recovery. A branch
exit passes the branch target to the normal resolver. Kernels that complete
before a later fault keep their effects.

## Profiles and validation

Run `fibercpu_train_presets` to capture unfused block profiles and measure
candidate tables. The candidate collector ranks sequence and operand shapes
by estimated dispatch savings across workloads and replays longest-first
matching to score overlap. The trainer measures subsets of those candidates
together with scalar variants. See the [baking guide](baked-instruction-variants.md).

For block-dump diagnostics, use `Podish.PerfTools analyze-blocks` and aggregate
the analysis files with `analyze-superopcode-candidates`. That output helps
inspect adjacency and coverage. The baking commands in the
[training guide](baked-instruction-variants.md) produce the manifests that CMake
uses.

```sh
dotnet run --project Podish.PerfTools/Podish.PerfTools.csproj -- \
  analyze-superopcode-candidates /path/to/guest-stats \
  --n-gram 2 --top 100 \
  --output-json /path/to/superopcode_candidates.json \
  --output-md /path/to/superopcode_candidates.md
```

Run the CPU suite with separate fusion-enabled and fusion-disabled builds.
Check final guest state, memory effects, branch targets and fault/restart EIP.
[OperandFusionGuardTests](../Fiberish.X86.Tests/Regression/OperandFusionGuardTests.cs)
checks the exact operand guard. The
[training workflow](../.github/workflows/superopcode-training.yml)
also exercises sequence counters and three- and four-instruction probes.
To reproduce a replay failure, keep the guest bytes and initial state for the
whole sequence. Compare the faulting EIP and completed predecessors' effects
against a scalar run. On a conditional branch, test both branch directions.
When reviewing a new plan, compare workload output, wall time, and native code
size. A handler count cannot show whether a workload ran faster.
