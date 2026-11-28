#include "jit/llvm_arm_ir.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <string>
#include <utility>

#include <llvm/ADT/SetVector.h>
#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/Module.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Support/MemoryBuffer.h>
#include <llvm/Transforms/Utils/Cloning.h>

#include "aarch64/block_operands.h"
#include "aarch64/decoder.h"
#include "aarch64/dispatch.h"
#include "aarch64/flag_liveness.h"
#include "aarch64/operation_binding.h"
#include "aarch64/pin_touch.h"
#include "jit/llvm_ir_common.h"
#include "jit/llvm_kernel_module.h"
#include "mem/page_region.h"

namespace fiberish {
namespace arm_jit {
namespace {
using namespace aarch64;
using namespace aarch64::detail;

#include "llvm_arm_bitcode.inc"

struct Templates {
    llvm::LLVMContext context;
    std::unique_ptr<llvm::Module> module;

    Templates() {
        auto bitcode = llvm::MemoryBufferRef(llvm::StringRef(reinterpret_cast<const char*>(kBitcode), sizeof(kBitcode)),
                                             "A64 interpreter kernels");
        auto parsed = llvm::parseBitcodeFile(bitcode, context);
        if (parsed)
            module = std::move(*parsed);
        else
            llvm::logAllUnhandledErrors(parsed.takeError(), llvm::errs(), "A64 JIT: ");
    }
};

llvm::Constant* ConstantOperands(llvm::Module& module, const DecodedInstruction& op) {
    // A private aggregate lets LLVM fold decoded fields after kernel inlining.
    // Write only the used fields so pointer representation and padding are irrelevant.
    std::array<uint8_t, sizeof(DecodedInstruction)> bytes{};
#define ARM_FIELD(field) std::memcpy(bytes.data() + offsetof(DecodedInstruction, field), &op.field, sizeof(op.field))
    ARM_FIELD(immediate);
    ARM_FIELD(mask);
    ARM_FIELD(word);
    ARM_FIELD(kind);
    ARM_FIELD(rd);
    ARM_FIELD(rn);
    ARM_FIELD(rm);
    ARM_FIELD(ra);
    ARM_FIELD(width);
    ARM_FIELD(operation);
    ARM_FIELD(mode);
    ARM_FIELD(shift);
#undef ARM_FIELD
    auto* value = llvm::ConstantDataArray::get(module.getContext(), bytes);
    auto* global =
        new llvm::GlobalVariable(module, value->getType(), true, llvm::GlobalValue::PrivateLinkage, value, "operands");
    global->setAlignment(llvm::Align(alignof(DecodedInstruction)));
    global->setUnnamedAddr(llvm::GlobalValue::UnnamedAddr::Global);
    return global;
}

unsigned EmittedStepCount(std::span<const Member> members, size_t index) {
    const auto& member = members[index];
    auto count = unsigned(member.steps.size());
    // Capture sorts members by their page offsets. Keep the complete member
    // for its entry budget and slot layout, including the region context.
    if (index + 1 < members.size()) count = std::min(count, unsigned(members[index + 1].offset - member.offset) / 4);
    return count;
}

void PromoteRegionGprs(llvm::Function& entry) {
    constexpr size_t kWordBytes = sizeof(uint64_t);
    constexpr size_t kGprCount = 32;
    static_assert(offsetof(State, x) == 0 && offsetof(State, sp) == (kGprCount - 1) * kWordBytes);
    static constexpr auto kOffsets = [] {
        std::array<size_t, kGprCount - FIBERCPU_AARCH64_PIN_COUNT> result{};
        size_t next = 0;
        for (size_t i = 0; i < kGprCount; ++i) {
            if (!IsPinnedGpr(i, true)) result[next++] = i * kWordBytes;
        }
        return result;
    }();
    PromoteCyclicStateFields(entry, {
                                        .field_bits = 64,
                                        .offsets = kOffsets,
                                        .use_alias_analysis = false,
                                        .clear_aa_metadata = false,
                                        .only_irreducible = true,
                                    });
}

class Builder {
    llvm::Module& module_;
    llvm::IRBuilder<> ir_;
    llvm::Function& entry_;
    llvm::SmallVector<llvm::CallInst*> inline_calls_;
    llvm::Value* state_;
    llvm::Value* current_;
    llvm::Value* limit_;
    llvm::Value* tlb_;
#if FIBERCPU_AARCH64_MICRO_TLB
    llvm::Value* tags_;
    llvm::Value* addend_;
#endif
    llvm::Value* nzcv_;
    llvm::Value* target_;
    llvm::Value* next_op_;
    std::array<llvm::Value*, FIBERCPU_AARCH64_PIN_COUNT> pins_;
    llvm::Value* page_ = nullptr;
    uint64_t single_offset_;
    llvm::Value* region_ops_ = nullptr;
    llvm::Value* remaining_;
    llvm::BasicBlock* leave_;
    llvm::PHINode* leave_pc_;
    llvm::PHINode* leave_op_;

    struct ColdExit {
        llvm::BasicBlock* block = nullptr;
        llvm::PHINode* op = nullptr;
        llvm::PHINode* pc = nullptr;
    };

    ColdExit replay_;
    ColdExit refetch_;
    ColdExit resolve_;
    std::array<ColdExit, 3> links_;

    llvm::BasicBlock* Block(const char* name) { return llvm::BasicBlock::Create(module_.getContext(), name, &entry_); }

    llvm::Value* Saved(llvm::Value* initial, const char* name) {
        auto* slot = ir_.CreateAlloca(initial->getType(), nullptr, name);
        ir_.CreateStore(initial, slot);
        return slot;
    }

    llvm::Value* Load64(llvm::Value* slot) { return ir_.CreateLoad(ir_.getInt64Ty(), slot); }

    llvm::Value* PC(uint64_t offset) {
        if (page_) return ir_.CreateAdd(page_, ir_.getInt64(offset));
        return ir_.CreateAdd(BlockPC(Owner(current_)), ir_.getInt64(offset - single_offset_));
    }

    llvm::Value* Owner(llvm::Value* first) {
        return ir_.CreateConstGEP1_64(ir_.getInt8Ty(), first, -int64_t(sizeof(BasicBlock)));
    }

    llvm::Value* MemberFirst(size_t index) {
        auto* field = ir_.CreateConstGEP1_64(ir_.getPtrTy(), region_ops_, index);
        return ir_.CreateConstGEP1_64(ir_.getInt8Ty(), LoadInvariantRegionField(ir_, ir_.getPtrTy(), field),
                                      sizeof(BasicBlock));
    }

    void PublishMember(llvm::Value* first) {
        if (!region_ops_) return;
        auto* last_block = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), state_, offsetof(State, last_block));
        ir_.CreateStore(Owner(first), last_block);
    }

    llvm::Value* BlockPC(llvm::Value* block) {
        return ir_.CreateAnd(Load64(block), ir_.getInt64(BasicBlockChainPrefix::StartPcMask));
    }

    llvm::FunctionCallee ExitFunction(const char* name) {
        auto target = module_.getOrInsertFunction(name, entry_.getFunctionType());
        llvm::cast<llvm::Function>(target.getCallee())->setCallingConv(entry_.getCallingConv());
        return target;
    }

    void TailExit(llvm::FunctionCallee target, llvm::Value* remaining, llvm::Value* op) {
        llvm::SmallVector<llvm::Value*, 6 + FIBERCPU_AARCH64_PIN_COUNT> args{state_, op, remaining};
#if FIBERCPU_AARCH64_MICRO_TLB
        args.push_back(Load64(tags_));
        args.push_back(Load64(addend_));
#endif
        args.push_back(ir_.CreateLoad(ir_.getInt32Ty(), nzcv_));
        for (auto* pin : pins_) args.push_back(Load64(pin));
        auto* call = ir_.CreateCall(target, args);
        call->setCallingConv(entry_.getCallingConv());
        call->setTailCallKind(llvm::CallInst::TCK_MustTail);
        ir_.CreateRet(call);
    }

    void CommitPc(llvm::Value* pc) {
        auto* field = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), state_, offsetof(State, pc));
        ir_.CreateStore(pc, field);
    }

    void Leave(llvm::Value* pc, llvm::Value* op) {
        PublishMember(op);
        leave_pc_->addIncoming(pc, ir_.GetInsertBlock());
        leave_op_->addIncoming(op, ir_.GetInsertBlock());
        ir_.CreateBr(leave_);
    }

    llvm::Value* Slot(llvm::Value* first, unsigned units) {
        return ir_.CreateConstGEP1_64(ir_.getInt8Ty(), first, units * sizeof(DecodedOp));
    }

    void RegionWrappers(llvm::StringRef name, std::span<const Member> members) {
        llvm::SmallVector<size_t, 16> offsets;
        offsets.reserve(members.size());
        for (const auto& member : members)
            offsets.push_back(BasicBlock::RegionContextOffset(member.SlotCount(), member.has_sentinel));
        EmitRegionEntryWrappers(module_, entry_, name, offsets);
    }

    void ExitMember(const char* name, llvm::Value* slot, llvm::Value* pc) {
        CommitPc(pc);
        TailExit(ExitFunction(name), Load64(remaining_), slot);
    }

    void TailToColdExit(ColdExit& exit, const char* name, llvm::Value* op, llvm::Value* pc = nullptr) {
        if (!FIBERCPU_LLVM_SHARED_REGION_EXITS || !region_ops_) {
            if (pc) CommitPc(pc);
            TailExit(ExitFunction(name), Load64(remaining_), op);
            return;
        }

        // Every predecessor has saved its instruction budget and live ABI
        // state. Only the decoded slot and optional PC need explicit PHIs.
        auto* source = ir_.GetInsertBlock();
        if (!exit.block) {
            exit.block = Block("region.cold.exit");
            ir_.SetInsertPoint(exit.block);
            exit.op = ir_.CreatePHI(ir_.getPtrTy(), 2, "exit.op");
            if (pc) {
                exit.pc = ir_.CreatePHI(ir_.getInt64Ty(), 2, "exit.pc");
                CommitPc(exit.pc);
            }
            TailExit(ExitFunction(name), Load64(remaining_), exit.op);
            ir_.SetInsertPoint(source);
        }

        exit.op->addIncoming(op, source);
        if (pc) exit.pc->addIncoming(pc, source);
        ir_.CreateBr(exit.block);
    }

    void ChainBlock(llvm::Value* first, llvm::Value* slot, BlockExitLink form, uint64_t end_offset) {
        // On a link hit, TryChainBlock publishes the target. On a miss the
        // dispatcher needs the source to resolve or cache the link.
        PublishMember(first);
        auto* fallthrough_pc = form == BlockExitLink::Direct ? ir_.getInt64(0) : PC(end_offset);
        if (!FIBERCPU_LLVM_SHARED_REGION_EXITS || !region_ops_) {
            EmitChainBlock(slot, form, fallthrough_pc);
            return;
        }

        // Share the link lookup across members while keeping its form constant.
        auto* source = ir_.GetInsertBlock();
        auto& link = links_[static_cast<size_t>(form)];
        if (!link.block) {
            link.block = Block("region.link");
            ir_.SetInsertPoint(link.block);
            link.op = ir_.CreatePHI(ir_.getPtrTy(), 2, "branch.op");
            if (form != BlockExitLink::Direct) link.pc = ir_.CreatePHI(ir_.getInt64Ty(), 2, "fallthrough.pc");
            EmitChainBlock(link.op, form, link.pc ? link.pc : fallthrough_pc);
            ir_.SetInsertPoint(source);
        }
        link.op->addIncoming(slot, source);
        if (link.pc) link.pc->addIncoming(fallthrough_pc, source);
        ir_.CreateBr(link.block);
    }

    void EmitChainBlock(llvm::Value* slot, BlockExitLink form, llvm::Value* fallthrough_pc) {
        auto* kind = ir_.getInt8(static_cast<uint8_t>(form));
        auto* indirect_pc = form == BlockExitLink::Indirect ? Load64(target_) : ir_.getInt64(0);
        auto* entry = ir_.CreateCall(module_.getFunction("FiberArmJitTryLink"),
                                     {state_, slot, remaining_, next_op_, indirect_pc, fallthrough_pc, kind});
        inline_calls_.push_back(entry);
        auto* next = ir_.CreateLoad(ir_.getPtrTy(), next_op_);
        auto* linked = Block("link.hit");
        auto* miss = Block("link.miss");
        ir_.CreateCondBr(ir_.CreateIsNotNull(entry), linked, miss);

        ir_.SetInsertPoint(linked);
        TailExit(llvm::FunctionCallee(entry_.getFunctionType(), entry), Load64(remaining_), next);
        ir_.SetInsertPoint(miss);
        TailToColdExit(resolve_, "FiberArmResolveBranchTargetSlow", next);
    }

public:
    Builder(llvm::Module& module, llvm::StringRef name, std::span<const Member> members)
        : module_(module),
          ir_(module.getContext()),
          entry_(*llvm::Function::Create(
              module.getFunction("FiberArmJitABI")->getFunctionType(),
              members.size() == 1 ? llvm::GlobalValue::ExternalLinkage : llvm::GlobalValue::InternalLinkage,
              members.size() == 1 ? llvm::Twine(name) : name + "_body", module)),
          single_offset_(members.front().offset) {
        entry_.addFnAttr(llvm::Attribute::NoUnwind);
        entry_.setCallingConv(module.getFunction("FiberArmJitABI")->getCallingConv());
        auto arg = entry_.arg_begin();
        // The host State is separate from decoded blocks, the TLB and guest
        // RAM. External exits still receive State and observe its stores.
        arg->addAttr(llvm::Attribute::NoAlias);
        arg->addAttr(llvm::Attribute::getWithDereferenceableBytes(module.getContext(), sizeof(State)));
        state_ = &*arg++;
        current_ = &*arg++;
        limit_ = &*arg++;
        ir_.SetInsertPoint(Block("entry"));
        auto* tlb_field = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), state_, offsetof(State, data_tlb));
        tlb_ = ir_.CreateLoad(ir_.getPtrTy(), tlb_field);
#if FIBERCPU_AARCH64_MICRO_TLB
        tags_ = Saved(&*arg++, "tags");
        addend_ = Saved(&*arg++, "addend");
#endif
        nzcv_ = Saved(&*arg++, "nzcv");
        target_ = Saved(ir_.getInt64(0), "branch.target");
        next_op_ = ir_.CreateAlloca(ir_.getPtrTy(), nullptr, "next.op");
        for (auto& pin : pins_) pin = Saved(&*arg++, "pinned");
        llvm::Value* selector = nullptr;
        if (members.size() > 1) {
            entry_.addFnAttr(llvm::Attribute::NoInline);
            using Region = mem::PageRegion<BasicBlock>;
            const auto decoded = DecodeRegionSelector(ir_, current_, Region::MemberMask, offsetof(Region, guest_page),
                                                      ir_.getInt64Ty(), ir_.getInt64Ty());
            selector = decoded.index;
            region_ops_ = decoded.table;
            page_ = decoded.page;
        }
        leave_ = Block("leave");

        // The interpreter already deducted the entry block's instruction count.
        // Recover the budget before sharing it with other region members.
        auto* entry_dispatch = ir_.GetInsertBlock();
        llvm::Value* budget = limit_;
        if (members.size() == 1) budget = ir_.CreateAdd(limit_, ir_.getInt64(members.front().steps.size()));
        remaining_ = Saved(budget, "remaining");

        ir_.SetInsertPoint(leave_);
        leave_pc_ = ir_.CreatePHI(ir_.getInt64Ty(), unsigned(members.size()), "continuation");
        leave_op_ = ir_.CreatePHI(ir_.getPtrTy(), unsigned(members.size()), "resume.op");

        std::vector<llvm::BasicBlock*> bodies;
        std::vector<llvm::BasicBlock*> run_bodies;
        bodies.reserve(members.size());
        run_bodies.reserve(members.size());
        for (size_t i = 0; i < members.size(); ++i) {
            bodies.push_back(Block("member"));
            run_bodies.push_back(members.size() > 1 ? Block("region.run") : bodies.back());
        }

        if (region_ops_) {
            auto* unreachable = Block("invalid.entry");
            ir_.SetInsertPoint(unreachable);
            ir_.CreateUnreachable();
            ir_.SetInsertPoint(entry_dispatch);
            auto* entry_switch = ir_.CreateSwitch(selector, unreachable, unsigned(members.size()));
            for (size_t i = 0; i < members.size(); ++i) {
                const auto& member = members[i];
                auto* target = Block("region.entry");
                entry_switch->addCase(ir_.getInt64(i), target);
                ir_.SetInsertPoint(target);
                ir_.CreateStore(ir_.CreateAdd(limit_, ir_.getInt64(member.steps.size())), remaining_);
                // The caller already verified and charged the entry member.
                ir_.CreateBr(run_bodies[i]);
            }
        } else {
            ir_.SetInsertPoint(entry_dispatch);
            ir_.CreateBr(run_bodies.front());
        }

        for (size_t i = 0; i < members.size(); ++i) {
            auto* first = current_;
            if (members.size() > 1) {
                ir_.SetInsertPoint(bodies[i]);
                auto* exhausted = Block("region.budget");
                ir_.CreateCondBr(ir_.CreateICmpSGE(Load64(remaining_), ir_.getInt64(0)), run_bodies[i], exhausted);
                ir_.SetInsertPoint(exhausted);
                Leave(PC(members[i].offset), MemberFirst(i));

                ir_.SetInsertPoint(run_bodies[i]);
                first = MemberFirst(i);
            } else {
                ir_.SetInsertPoint(run_bodies[i]);
            }
            EmitMember(members, bodies, i, first);
        }

        ir_.SetInsertPoint(leave_);
        if (leave_pc_->getNumIncomingValues())
            ExitMember("FiberArmJitLeave", leave_op_, leave_pc_);
        else
            leave_->eraseFromParent();
        if (region_ops_) RegionWrappers(name, members);
    }

    llvm::ConstantInt* Flow(OperationFlow flow) { return ir_.getInt32(static_cast<uint32_t>(flow)); }

    void ExitOnFlow(llvm::Value* flow, llvm::Value* first, llvm::Value* slot, uint64_t offset, llvm::Value* remaining,
                    bool mandatory_refetch) {
        llvm::BasicBlock* next = nullptr;
        if (!mandatory_refetch) {
            next = Block("step.next");
            auto* exit = Block("step.exit");
            ir_.CreateCondBr(ir_.CreateICmpEQ(flow, Flow(OperationFlow::Continue)), next, exit);
            ir_.SetInsertPoint(exit);
        }
        ir_.CreateStore(remaining, remaining_);
        PublishMember(first);
        auto* pc = PC(offset);
        auto* replay = Block("step.replay");
        auto* refetch = Block("step.refetch");
        ir_.CreateCondBr(ir_.CreateICmpEQ(flow, Flow(OperationFlow::Refetch)), refetch, replay);

        ir_.SetInsertPoint(replay);
        TailToColdExit(replay_, "FiberArmJitRetry", slot, pc);
        ir_.SetInsertPoint(refetch);
        TailToColdExit(refetch_, "FiberArmJitRefetch", slot, pc);
        if (next) ir_.SetInsertPoint(next);
    }

    void EmitMember(std::span<const Member> members, std::span<llvm::BasicBlock* const> bodies, size_t index,
                    llvm::Value* first) {
        const auto& member = members[index];
        const auto count = EmittedStepCount(members, index);
        auto* budget = Load64(remaining_);
        const auto retire = [&](unsigned count) {
            ir_.CreateStore(ir_.CreateSub(budget, ir_.getInt64(count)), remaining_);
        };
        const auto branch_to = [&](uint64_t target, llvm::Value* slot, BlockExitLink form) {
            const auto it = std::ranges::find(members, target, &Member::offset);
            if (members.size() > 1 && it != members.end())
                ir_.CreateBr(bodies[size_t(it - members.begin())]);
            else
                ChainBlock(first, slot, form, target);
        };
        uint32_t dead_flags = 0;
#if FIBERCPU_AARCH64_DFE
        uint32_t live = FlagMask;
        for (unsigned i = count; i-- > 0;) {
            const auto word = member.steps[i].word;
            if (word != NopWord && OmitNZCV(DecodeFields(word), live)) dead_flags |= uint32_t{1} << i;
        }
#endif
        auto offset = uint64_t(member.offset);
        unsigned slot_offset = 0;
        for (unsigned i = 0; i < count; ++i, offset += 4) {
            const auto& step = member.steps[i];
            auto* slot = Slot(first, slot_offset);
            slot_offset += step.slots;
            const auto op = DecodeFields(step.word);
            const auto binding = BindOperation(op);
            if (binding.id == OperationId::Nop) continue;
            const auto* name = KernelName(binding.id);
            if (!name) {
                retire(i);
                PublishMember(first);
                TailToColdExit(replay_, "FiberArmJitRetry", slot, PC(offset));
                return;
            }

            auto* pc = PC(offset);
            llvm::SmallVector<llvm::Value*, 10 + FIBERCPU_AARCH64_PIN_COUNT> args{
                state_, slot, ConstantOperands(module_, op), pc, tlb_};
#if FIBERCPU_AARCH64_MICRO_TLB
            args.push_back(tags_);
            args.push_back(addend_);
#endif
            args.push_back(nzcv_);
            args.push_back(ir_.getInt1((dead_flags >> i) & 1));
            args.push_back(target_);
            for (auto* pin : pins_) args.push_back(pin);
            auto* flow = ir_.CreateCall(module_.getFunction(name), args);
            inline_calls_.push_back(flow);
            if (binding.may_exit || binding.refetch) {
                // Publish the prefix only for an exit. Refetch retires the
                // current instruction in the handler; replay has not run it.
                ExitOnFlow(flow, first, slot, offset, ir_.CreateSub(budget, ir_.getInt64(i)), binding.refetch);
                if (binding.refetch) return;
            }
            if (binding.branch == BranchForm::Conditional) {
                auto* taken = Block("branch.taken");
                auto* fallthrough = Block("branch.fallthrough");
                ir_.CreateCondBr(ir_.CreateICmpEQ(flow, Flow(OperationFlow::Branch)), taken, fallthrough);
                ir_.SetInsertPoint(taken);
                retire(i + 1);
                branch_to(offset + op.immediate, slot, BlockExitLink::Direct);
                ir_.SetInsertPoint(fallthrough);
            } else if (binding.branch == BranchForm::Direct) {
                retire(i + 1);
                branch_to(offset + op.immediate, slot, BlockExitLink::Direct);
                return;
            } else if (binding.branch == BranchForm::Indirect) {
                retire(i + 1);
                if (members.size() > 1) {
                    auto* outside = Block("region.outside");
                    auto* lookup =
                        ir_.CreateSwitch(ir_.CreateSub(Load64(target_), page_), outside, unsigned(members.size()));
                    for (size_t j = 0; j < members.size(); ++j)
                        lookup->addCase(ir_.getInt64(members[j].offset), bodies[j]);
                    ir_.SetInsertPoint(outside);
                }
                ChainBlock(first, slot, BlockExitLink::Indirect, member.offset + member.steps.size() * 4);
                return;
            }
        }
        retire(count);
        branch_to(offset, Slot(first, slot_offset), BlockExitLink::Fallthrough);
    }

    bool Optimize(llvm::StringRef pipeline, bool fold_operand_loads, llvm::function_ref<bool()> should_cancel) {
        if (should_cancel && should_cancel()) return false;
        // Native FP kernels use constrained arithmetic for guest FPCR.
        // Retain that contract when their bodies become part of this function.
        for (auto* call : inline_calls_) {
            if (call->getCalledFunction()->hasFnAttribute(llvm::Attribute::StrictFP)) {
                entry_.addFnAttr(llvm::Attribute::StrictFP);
                break;
            }
        }
        for (auto* call : inline_calls_) {
            if (should_cancel && should_cancel()) return false;
            llvm::InlineFunctionInfo info;
            if (!llvm::InlineFunction(*call, info).isSuccess()) return false;
        }
        if (should_cancel && should_cancel()) return false;
        if (llvm::verifyModule(module_, &llvm::errs()) ||
            !OptimizeLLVMFunction(entry_,
                                  {
                                      .prune_dead_globals = true,
                                      .custom_pipeline = pipeline,
                                      .fold_operand_loads = fold_operand_loads,
                                      .should_cancel = should_cancel,
                                  }) ||
            (should_cancel && should_cancel()))
            return false;
        if (region_ops_ && pipeline.empty()) PromoteRegionGprs(entry_);
        return (!should_cancel || !should_cancel()) && !llvm::verifyModule(module_, &llvm::errs());
    }
};
}  // namespace

bool CanLower(uint32_t word) { return BindOperation(DecodeFields(word)).id != OperationId::Unsupported; }

std::unique_ptr<llvm::Module> Prepare(std::span<const Member> members, llvm::StringRef name, llvm::StringRef pipeline,
                                      bool fold_operand_loads, llvm::function_ref<bool()> should_cancel) {
    if ((should_cancel && should_cancel()) || members.empty() ||
        members.size() > mem::PageRegion<BasicBlock>::MaxMembers)
        return {};
    thread_local Templates templates;
    if (!templates.module) return {};
    const auto& source = *templates.module;
    llvm::SmallSetVector<const llvm::GlobalValue*, 32> kernels;
    kernels.insert(source.getFunction("FiberArmJitABI"));
    kernels.insert(source.getFunction("FiberArmJitTryLink"));
    for (size_t i = 0; i < members.size(); ++i) {
        for (const auto& step : std::span(members[i].steps).first(EmittedStepCount(members, i))) {
            const auto binding = BindOperation(DecodeFields(step.word));
            if (binding.id == OperationId::Unsupported) break;
            if (const auto* name = KernelName(binding.id)) {
                auto* kernel = source.getFunction(name);
                if (!kernel) return {};
                kernels.insert(kernel);
            }
            if (binding.refetch) break;
        }
    }
    if (should_cancel && should_cancel()) return {};
    auto module = KernelModule().Clone(source, kernels.getArrayRef());
    for (auto& function : *module)
        if (!function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    Builder builder(*module, name, members);
    if (!builder.Optimize(pipeline, fold_operand_loads, should_cancel)) return {};
    return module;
}

}  // namespace arm_jit
}  // namespace fiberish
