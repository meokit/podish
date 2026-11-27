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
#include "jit/llvm_kernel_module.h"
#include "mem/page_region.h"

namespace fiberish {

bool OptimizeLLVMArmModule(llvm::Function& entry, llvm::StringRef pipeline, bool fold_operand_loads);

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

class Builder {
    llvm::Module& module_;
    llvm::IRBuilder<> ir_;
    llvm::Function& entry_;
    llvm::SmallVector<llvm::CallInst*> inline_calls_;
    llvm::Value* state_;
    llvm::Value* current_;
    llvm::Value* limit_;
    llvm::Value* tlb_;
    llvm::Value* tags_;
    llvm::Value* addend_;
    llvm::Value* nzcv_;
    llvm::Value* target_;
    llvm::Value* next_op_;
    std::array<llvm::Value*, FIBERCPU_AARCH64_PIN_COUNT> pins_;
    llvm::Value* page_;
    llvm::Value* region_ops_ = nullptr;
    llvm::Value* remaining_;
    llvm::BasicBlock* leave_;
    llvm::PHINode* leave_pc_;
    llvm::PHINode* leave_op_;

    llvm::BasicBlock* Block(const char* name) { return llvm::BasicBlock::Create(module_.getContext(), name, &entry_); }

    llvm::Value* Saved(llvm::Value* initial, const char* name) {
        auto* slot = ir_.CreateAlloca(initial->getType(), nullptr, name);
        ir_.CreateStore(initial, slot);
        return slot;
    }

    llvm::Value* Load64(llvm::Value* slot) { return ir_.CreateLoad(ir_.getInt64Ty(), slot); }

    llvm::Value* PC(uint64_t offset) { return ir_.CreateAdd(page_, ir_.getInt64(offset)); }

    llvm::Value* BlockPC(llvm::Value* block) {
        return ir_.CreateAnd(Load64(block), ir_.getInt64(BasicBlockChainPrefix::StartPcMask));
    }

    llvm::FunctionCallee ExitFunction(const char* name) {
        auto target = module_.getOrInsertFunction(name, entry_.getFunctionType());
        llvm::cast<llvm::Function>(target.getCallee())->setCallingConv(entry_.getCallingConv());
        return target;
    }

    void TailExit(llvm::FunctionCallee target, llvm::Value* remaining, llvm::Value* op) {
        llvm::SmallVector<llvm::Value*, 6 + FIBERCPU_AARCH64_PIN_COUNT> args{
            state_, op, remaining, Load64(tags_), Load64(addend_), ir_.CreateLoad(ir_.getInt32Ty(), nzcv_)};
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
        leave_pc_->addIncoming(pc, ir_.GetInsertBlock());
        leave_op_->addIncoming(op, ir_.GetInsertBlock());
        ir_.CreateBr(leave_);
    }

    llvm::Value* Slot(llvm::Value* first, unsigned units) {
        return ir_.CreateConstGEP1_64(ir_.getInt8Ty(), first, units * sizeof(DecodedOp));
    }

    void RegionWrappers(llvm::StringRef name, std::span<const Member> members) {
        for (size_t i = 0; i < members.size(); ++i) {
            auto* wrapper = llvm::Function::Create(entry_.getFunctionType(), llvm::GlobalValue::ExternalLinkage,
                                                   name + "_" + llvm::Twine(i), module_);
            wrapper->setCallingConv(entry_.getCallingConv());
            wrapper->addFnAttr(llvm::Attribute::NoUnwind);
            ir_.SetInsertPoint(llvm::BasicBlock::Create(module_.getContext(), "entry", wrapper));
            llvm::SmallVector<llvm::Value*, 8> args;
            for (auto& arg : wrapper->args()) args.push_back(&arg);
            auto* context = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), args[1],
                                                   BasicBlock::RegionContextOffset(members[i].SlotCount()));
            auto* table = ir_.CreateLoad(ir_.getPtrTy(), context);
            // The threaded ABI has no branch argument. The internal body
            // receives the table and member number in the same op argument.
            auto* selector = ir_.CreateOr(ir_.CreatePtrToInt(table, ir_.getInt64Ty()), ir_.getInt64(i));
            args[1] = ir_.CreateIntToPtr(selector, ir_.getPtrTy());
            auto* call = ir_.CreateCall(&entry_, args);
            call->setCallingConv(entry_.getCallingConv());
            call->setTailCallKind(llvm::CallInst::TCK_MustTail);
            ir_.CreateRet(call);
        }
    }

    void ExitMember(const char* name, llvm::Value* slot, llvm::Value* pc) {
        CommitPc(pc);
        TailExit(ExitFunction(name), Load64(remaining_), slot);
    }

    void ChainBlock(llvm::Value* sentinel, BranchForm branch, llvm::Value* taken = nullptr) {
        auto* form = ir_.getInt8(static_cast<uint8_t>(BlockExitLink::Fallthrough));
        llvm::Value* kind = form;
        llvm::Value* indirect_pc = ir_.getInt64(0);
        if (branch == BranchForm::Indirect) {
            kind = ir_.getInt8(static_cast<uint8_t>(BlockExitLink::Indirect));
            indirect_pc = Load64(target_);
        } else if (branch == BranchForm::Direct || branch == BranchForm::Conditional) {
            auto* direct = ir_.getInt8(static_cast<uint8_t>(BlockExitLink::Direct));
            kind = branch == BranchForm::Direct ? direct : ir_.CreateSelect(taken, direct, form);
        }
        auto* entry = ir_.CreateCall(module_.getFunction("FiberArmJitTryLink"),
                                     {state_, sentinel, remaining_, next_op_, indirect_pc, kind});
        inline_calls_.push_back(entry);
        auto* next = ir_.CreateLoad(ir_.getPtrTy(), next_op_);
        auto* linked = Block("link.hit");
        auto* miss = Block("link.miss");
        ir_.CreateCondBr(ir_.CreateIsNotNull(entry), linked, miss);

        ir_.SetInsertPoint(linked);
        TailExit(llvm::FunctionCallee(entry_.getFunctionType(), entry), Load64(remaining_), next);
        ir_.SetInsertPoint(miss);
        TailExit(ExitFunction("FiberArmResolveBranchTargetSlow"), Load64(remaining_), next);
    }

public:
    Builder(llvm::Module& module, llvm::StringRef name, std::span<const Member> members)
        : module_(module),
          ir_(module.getContext()),
          entry_(*llvm::Function::Create(
              module.getFunction("FiberArmJitABI")->getFunctionType(),
              members.size() == 1 ? llvm::GlobalValue::ExternalLinkage : llvm::GlobalValue::InternalLinkage,
              members.size() == 1 ? llvm::Twine(name) : name + "_body", module)) {
        entry_.addFnAttr(llvm::Attribute::NoUnwind);
        entry_.setCallingConv(module.getFunction("FiberArmJitABI")->getCallingConv());
        auto arg = entry_.arg_begin();
        state_ = &*arg++;
        current_ = &*arg++;
        limit_ = &*arg++;
        ir_.SetInsertPoint(Block("entry"));
        auto* tlb_field = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), state_, offsetof(State, data_tlb));
        tlb_ = ir_.CreateLoad(ir_.getPtrTy(), tlb_field);
        tags_ = Saved(&*arg++, "tags");
        addend_ = Saved(&*arg++, "addend");
        nzcv_ = Saved(&*arg++, "nzcv");
        target_ = Saved(ir_.getInt64(0), "branch.target");
        next_op_ = ir_.CreateAlloca(ir_.getPtrTy(), nullptr, "next.op");
        for (auto& pin : pins_) pin = Saved(&*arg++, "pinned");
        llvm::Value* selector = nullptr;
        if (members.size() == 1) {
            auto* block = ir_.CreateGEP(ir_.getInt8Ty(), current_, ir_.getInt64(-int64_t(sizeof(BasicBlock))));
            page_ = ir_.CreateSub(BlockPC(block), ir_.getInt64(members.front().offset));
        } else {
            entry_.addFnAttr(llvm::Attribute::NoInline);
            using Region = mem::PageRegion<BasicBlock>;
            constexpr uint64_t member_mask = Region::MemberMask;
            auto* input = ir_.CreatePtrToInt(current_, ir_.getInt64Ty());
            selector = ir_.CreateAnd(input, ir_.getInt64(member_mask));
            region_ops_ = ir_.CreateIntToPtr(ir_.CreateAnd(input, ir_.getInt64(~member_mask)), ir_.getPtrTy());
            auto* page_field = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), region_ops_, offsetof(Region, guest_page));
            page_ = Load64(page_field);
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
                auto* exit_block =
                    ir_.CreateLoad(ir_.getPtrTy(), ir_.CreateConstGEP1_64(ir_.getPtrTy(), region_ops_, i));
                Leave(PC(members[i].offset), ir_.CreateConstGEP1_64(ir_.getInt8Ty(), exit_block, sizeof(BasicBlock)));

                ir_.SetInsertPoint(run_bodies[i]);
                auto* record = ir_.CreateConstGEP1_64(ir_.getPtrTy(), region_ops_, i);
                auto* block = ir_.CreateLoad(ir_.getPtrTy(), record);
                auto* last_block = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), state_, offsetof(State, last_block));
                ir_.CreateStore(block, last_block);
                first = ir_.CreateConstGEP1_64(ir_.getInt8Ty(), block, sizeof(BasicBlock));
            } else {
                ir_.SetInsertPoint(run_bodies[i]);
            }
            EmitMember(members, bodies, members[i], first);
        }

        ir_.SetInsertPoint(leave_);
        if (leave_pc_->getNumIncomingValues())
            ExitMember("FiberArmJitLeave", leave_op_, leave_pc_);
        else
            leave_->eraseFromParent();
        if (region_ops_) RegionWrappers(name, members);
    }

    llvm::ConstantInt* Flow(OperationFlow flow) { return ir_.getInt32(static_cast<uint32_t>(flow)); }

    void ExitOnFlow(llvm::Value* flow, llvm::Value* slot, llvm::Value* pc, llvm::Value* remaining,
                    bool mandatory_refetch) {
        llvm::BasicBlock* next = nullptr;
        if (!mandatory_refetch) {
            next = Block("step.next");
            auto* exit = Block("step.exit");
            ir_.CreateCondBr(ir_.CreateICmpEQ(flow, Flow(OperationFlow::Continue)), next, exit);
            ir_.SetInsertPoint(exit);
        }
        ir_.CreateStore(remaining, remaining_);
        auto* replay = Block("step.replay");
        auto* refetch = Block("step.refetch");
        ir_.CreateCondBr(ir_.CreateICmpEQ(flow, Flow(OperationFlow::Refetch)), refetch, replay);

        ir_.SetInsertPoint(replay);
        ExitMember("FiberArmJitRetry", slot, pc);
        ir_.SetInsertPoint(refetch);
        ExitMember("FiberArmJitRefetch", slot, pc);
        if (next) ir_.SetInsertPoint(next);
    }

    void EmitMember(std::span<const Member> members, std::span<llvm::BasicBlock* const> bodies, const Member& member,
                    llvm::Value* first) {
        auto* budget = Load64(remaining_);
        const auto retire = [&](unsigned count) {
            ir_.CreateStore(ir_.CreateSub(budget, ir_.getInt64(count)), remaining_);
        };
        uint32_t dead_flags = 0;
#if FIBERCPU_AARCH64_DFE
        uint32_t live = FlagMask;
        for (unsigned i = unsigned(member.steps.size()); i-- > 0;) {
            const auto word = member.steps[i].word;
            if (word != NopWord && OmitNZCV(DecodeFields(word), live)) dead_flags |= uint32_t{1} << i;
        }
#endif
        auto offset = uint64_t(member.offset);
        BranchForm branch = BranchForm::None;
        llvm::Value* taken = nullptr;
        uint64_t target_offset = 0;
        unsigned slot_offset = 0;
        for (unsigned i = 0; i < member.steps.size(); ++i, offset += 4) {
            const auto& step = member.steps[i];
            auto* slot = Slot(first, slot_offset);
            slot_offset += step.slots;
            const auto op = DecodeFields(step.word);
            const auto binding = BindOperation(op);
            if (binding.id == OperationId::Nop) continue;
            const auto* name = KernelName(binding.id);
            if (!name) {
                retire(i);
                ExitMember("FiberArmJitRetry", slot, PC(offset));
                return;
            }

            auto* pc = PC(offset);
            llvm::SmallVector<llvm::Value*, 10 + FIBERCPU_AARCH64_PIN_COUNT> args{
                state_,  slot,  ConstantOperands(module_, op),      pc,     tlb_, tags_,
                addend_, nzcv_, ir_.getInt1((dead_flags >> i) & 1), target_};
            for (auto* pin : pins_) args.push_back(pin);
            auto* flow = ir_.CreateCall(module_.getFunction(name), args);
            inline_calls_.push_back(flow);
            if (binding.may_exit || binding.refetch) {
                // Publish the prefix only for an exit. Refetch retires the
                // current instruction in the handler; replay has not run it.
                ExitOnFlow(flow, slot, pc, ir_.CreateSub(budget, ir_.getInt64(i)), binding.refetch);
                if (binding.refetch) return;
            }
            if (binding.branch != BranchForm::None) {
                // The decoder ends the member at every branch.
                branch = binding.branch;
                target_offset = offset + op.immediate;
                if (branch == BranchForm::Conditional) taken = ir_.CreateICmpEQ(flow, Flow(OperationFlow::Branch));
            }
        }
        retire(unsigned(member.steps.size()));
        auto* sentinel = Slot(first, slot_offset);

        if (members.size() == 1) {
            ChainBlock(sentinel, branch, taken);
            return;
        }

        const auto branch_to = [&](uint64_t target, BranchForm form) {
            const auto it = std::ranges::find(members, target, &Member::offset);
            if (it != members.end())
                ir_.CreateBr(bodies[size_t(it - members.begin())]);
            else
                ChainBlock(sentinel, form);
        };
        if (branch == BranchForm::Indirect) {
            auto* outside = Block("region.outside");
            auto* lookup = ir_.CreateSwitch(ir_.CreateSub(Load64(target_), page_), outside, unsigned(members.size()));
            for (size_t i = 0; i < members.size(); ++i) lookup->addCase(ir_.getInt64(members[i].offset), bodies[i]);
            ir_.SetInsertPoint(outside);
            ChainBlock(sentinel, branch);
        } else if (branch == BranchForm::Direct) {
            branch_to(target_offset, BranchForm::Direct);
        } else if (branch == BranchForm::Conditional) {
            auto* target = Block("region.taken");
            auto* fallthrough = Block("region.fallthrough");
            ir_.CreateCondBr(taken, target, fallthrough);
            ir_.SetInsertPoint(target);
            branch_to(target_offset, BranchForm::Direct);
            ir_.SetInsertPoint(fallthrough);
            branch_to(offset, BranchForm::None);
        } else {
            branch_to(offset, BranchForm::None);
        }
    }

    bool Optimize(llvm::StringRef pipeline, bool fold_operand_loads) {
        // Native FP kernels use constrained arithmetic for guest FPCR.
        // Retain that contract when their bodies become part of this function.
        for (auto* call : inline_calls_) {
            if (call->getCalledFunction()->hasFnAttribute(llvm::Attribute::StrictFP)) {
                entry_.addFnAttr(llvm::Attribute::StrictFP);
                break;
            }
        }
        for (auto* call : inline_calls_) {
            llvm::InlineFunctionInfo info;
            if (!llvm::InlineFunction(*call, info).isSuccess()) return false;
        }
        return !llvm::verifyModule(module_, &llvm::errs()) &&
               OptimizeLLVMArmModule(entry_, pipeline, fold_operand_loads) &&
               !llvm::verifyModule(module_, &llvm::errs());
    }
};
}  // namespace

bool CanLower(uint32_t word) { return BindOperation(DecodeFields(word)).id != OperationId::Unsupported; }

std::unique_ptr<llvm::Module> Prepare(std::span<const Member> members, llvm::StringRef name, llvm::StringRef pipeline,
                                      bool fold_operand_loads) {
    if (members.empty() || members.size() > mem::PageRegion<BasicBlock>::MaxMembers) return {};
    thread_local Templates templates;
    if (!templates.module) return {};
    const auto& source = *templates.module;
    llvm::SmallSetVector<const llvm::GlobalValue*, 32> kernels;
    kernels.insert(source.getFunction("FiberArmJitABI"));
    kernels.insert(source.getFunction("FiberArmJitTryLink"));
    for (const auto& member : members) {
        for (const auto& step : member.steps) {
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
    auto module = KernelModule().Clone(source, kernels.getArrayRef());
    for (auto& function : *module)
        if (!function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    Builder builder(*module, name, members);
    if (!builder.Optimize(pipeline, fold_operand_loads)) return {};
    return module;
}

}  // namespace arm_jit
}  // namespace fiberish
