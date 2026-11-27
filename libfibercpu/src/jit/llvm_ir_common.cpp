#include "jit/llvm_ir_common.h"

#include <algorithm>
#include <optional>
#include <utility>

#include <llvm/ADT/PostOrderIterator.h>
#include <llvm/ADT/SCCIterator.h>
#include <llvm/ADT/SmallBitVector.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/ADT/SmallVector.h>
#include <llvm/Analysis/AliasAnalysis.h>
#include <llvm/Analysis/CFG.h>
#include <llvm/Analysis/LoopInfo.h>
#include <llvm/Analysis/SimplifyQuery.h>
#include <llvm/Analysis/ValueTracking.h>
#include <llvm/ExecutionEngine/Orc/JITTargetMachineBuilder.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/Dominators.h>
#include <llvm/IR/Function.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/IntrinsicInst.h>
#include <llvm/IR/Module.h>
#include <llvm/IR/PatternMatch.h>
#include <llvm/IR/ValueHandle.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/TargetSelect.h>
#include <llvm/Transforms/IPO/GlobalDCE.h>
#include <llvm/Transforms/Scalar/InstSimplifyPass.h>
#include <llvm/Transforms/Scalar/SimplifyCFG.h>
#include <llvm/Transforms/Utils/Local.h>
#include <llvm/Transforms/Utils/PromoteMemToReg.h>

namespace fiberish {
namespace {

std::optional<bool> KnownBit(llvm::Value* value, unsigned bit, const llvm::SimplifyQuery& query) {
    const auto known = llvm::computeKnownBits(value, query);
    if (known.Zero[bit]) return false;
    if (known.One[bit]) return true;
    return {};
}

struct BitSource {
    llvm::Value* predicate = nullptr;
    bool inverted = false;

    bool operator==(const BitSource&) const = default;
};

BitSource TracePackedBit(llvm::Value* value, unsigned bit, bool inverted, const llvm::SimplifyQuery& query,
                         unsigned& remaining) {
    const auto constant = [&](bool set) -> BitSource {
        return {llvm::ConstantInt::getBool(value->getContext(), set != inverted)};
    };
    // Bound each search, including both arms of a select, for online
    // compilation. Loads and PHIs end the search.
    while (remaining) {
        --remaining;
        if (auto* bits = llvm::dyn_cast<llvm::ConstantInt>(value)) return constant(bits->getValue()[bit]);
        if (value->getType()->isIntegerTy(1)) return {value, inverted};
        auto* inst = llvm::dyn_cast<llvm::Instruction>(value);
        if (!inst || !inst->getNumOperands()) return {};
        auto* input = inst->getOperand(0);
        switch (inst->getOpcode()) {
            case llvm::Instruction::Trunc:
            case llvm::Instruction::ZExt:
            case llvm::Instruction::SExt: {
                const auto width = input->getType()->getIntegerBitWidth();
                if (bit >= width && inst->getOpcode() == llvm::Instruction::ZExt) return constant(false);
                bit = std::min(bit, width - 1);
                break;
            }
            case llvm::Instruction::Shl:
            case llvm::Instruction::LShr:
            case llvm::Instruction::AShr: {
                const auto width = value->getType()->getIntegerBitWidth();
                const auto* shift = llvm::dyn_cast<llvm::ConstantInt>(inst->getOperand(1));
                if (!shift || shift->getValue().uge(width)) return {};
                const auto amount = unsigned(shift->getZExtValue());
                if (inst->getOpcode() == llvm::Instruction::Shl) {
                    if (bit < amount) return constant(false);
                    bit -= amount;
                } else {
                    bit += amount;
                    if (bit >= width && inst->getOpcode() == llvm::Instruction::LShr) return constant(false);
                    bit = std::min(bit, width - 1);
                }
                break;
            }
            case llvm::Instruction::And:
            case llvm::Instruction::Or:
            case llvm::Instruction::Xor: {
                auto* other = inst->getOperand(1);
                auto known = KnownBit(other, bit, query);
                if (!known) {
                    known = KnownBit(input, bit, query);
                    if (!known) return {};
                    input = other;
                }
                if (inst->getOpcode() == llvm::Instruction::Xor) {
                    inverted ^= *known;
                } else if (*known == (inst->getOpcode() == llvm::Instruction::Or)) {
                    return constant(*known);
                }
                break;
            }
            case llvm::Instruction::Select: {
                const auto yes = KnownBit(inst->getOperand(1), bit, query);
                const auto no = KnownBit(inst->getOperand(2), bit, query);
                if (yes && no) {
                    if (*yes == *no) return constant(*yes);
                    return {input, inverted != !*yes};
                }
                // Packing another flag can select between words that
                // both carry the same predicate in the requested bit.
                const auto true_source = TracePackedBit(inst->getOperand(1), bit, inverted, query, remaining);
                if (!true_source.predicate) return {};
                const auto false_source = TracePackedBit(inst->getOperand(2), bit, inverted, query, remaining);
                return true_source == false_source ? true_source : BitSource{};
            }
            default:
                return {};
        }
        value = input;
    }
    return {};
}

class ForwardPackedBitTestsPass : public llvm::PassInfoMixin<ForwardPackedBitTestsPass> {
public:
    llvm::PreservedAnalyses run(llvm::Function& entry, llvm::FunctionAnalysisManager&) {
        using namespace llvm::PatternMatch;
        const auto query = llvm::SimplifyQuery(entry.getParent()->getDataLayout()).getWithoutUndef();
        llvm::SmallVector<llvm::WeakTrackingVH, 16> dead;
        for (auto& inst : llvm::instructions(entry)) {
            if (!inst.getType()->isIntegerTy(1) || inst.use_empty()) continue;
            llvm::Value* packed = nullptr;
            unsigned bit = 0;
            bool inverted = false;
            if (auto* cmp = llvm::dyn_cast<llvm::ICmpInst>(&inst); cmp && cmp->isEquality()) {
                llvm::Value* masked = cmp->getOperand(0);
                auto* expected = llvm::dyn_cast<llvm::ConstantInt>(cmp->getOperand(1));
                if (!expected) {
                    masked = cmp->getOperand(1);
                    expected = llvm::dyn_cast<llvm::ConstantInt>(cmp->getOperand(0));
                }
                const llvm::APInt* mask;
                if (!expected || !match(masked, m_c_And(m_Value(packed), m_APInt(mask))) || !mask->isPowerOf2() ||
                    (!expected->isZero() && expected->getValue() != *mask))
                    continue;
                bit = mask->logBase2();
                inverted = (cmp->getPredicate() == llvm::ICmpInst::ICMP_EQ) == expected->isZero();
            } else if (!match(&inst, m_Trunc(m_Value(packed)))) {
                continue;
            }
            unsigned remaining = 16;
            const auto source = TracePackedBit(packed, bit, inverted, query.getWithInstruction(&inst), remaining);
            if (!source.predicate) continue;
            llvm::IRBuilder<> builder(&inst);
            auto* predicate = source.inverted ? builder.CreateNot(source.predicate) : source.predicate;
            inst.replaceAllUsesWith(predicate);
            dead.emplace_back(&inst);
        }
        if (dead.empty()) return llvm::PreservedAnalyses::all();
        llvm::RecursivelyDeleteTriviallyDeadInstructions(dead);
        llvm::PreservedAnalyses preserved;
        preserved.preserveSet<llvm::CFGAnalyses>();
        return preserved;
    }
};

}  // namespace

bool InitializeLLVMTarget() {
    static const bool initialized = [] {
        if (llvm::InitializeNativeTarget() || llvm::InitializeNativeTargetAsmPrinter()) return false;
#if FIBERCPU_GUEST_AARCH64 && FIBERCPU_LLVM_JIT
        // The A64 interpreter's kernels contain native NZCV instructions.
        if (llvm::InitializeNativeTargetAsmParser()) return false;
#endif
        return true;
    }();
    return initialized;
}

llvm::Expected<std::unique_ptr<llvm::TargetMachine>> CreateLLVMObjectTarget(const llvm::Module& templates) {
    auto target = llvm::orc::JITTargetMachineBuilder::detectHost();
    if (!target) return target.takeError();
    // Host CPU code generation with the deployment target embedded in the interpreter bitcode.
    target->getTargetTriple() = templates.getTargetTriple();
    target->setRelocationModel(llvm::Reloc::PIC_).setOptions(llvm::TargetOptions{});
    return target->createTargetMachine();
}

llvm::Expected<std::string> ParseLLVMBlockPipeline(llvm::StringRef level, llvm::StringRef passes) {
    if (!level.empty() && !passes.empty())
        return llvm::createStringError("select an LLVM optimization level or a pass pipeline");
    if (!passes.empty()) {
        llvm::PassBuilder builder;
        llvm::ModulePassManager pipeline;
        if (auto error = builder.parsePassPipeline(pipeline, passes)) return std::move(error);
        return passes.str();
    }
    level.consume_front("-");
    if (level.empty() || level == "O2") return std::string{};
    if (level != "O0" && level != "O1" && level != "O3" && level != "Os" && level != "Oz")
        return llvm::createStringError("invalid LLVM optimization level: %s", level.str().c_str());
    return "default<" + level.str() + ">";
}

bool OptimizeLLVMFunction(llvm::Function& entry, const LLVMOptimizeOptions& options) {
    const auto cancelled = [&] { return options.should_cancel && options.should_cancel(); };
    if (cancelled()) return false;

    llvm::LoopAnalysisManager loops;
    llvm::FunctionAnalysisManager functions;
    llvm::CGSCCAnalysisManager call_graph;
    llvm::ModuleAnalysisManager modules;
    llvm::PassInstrumentationCallbacks callbacks;
    if (options.should_cancel) {
        callbacks.registerShouldRunOptionalPassCallback([&](llvm::StringRef, llvm::Any) { return !cancelled(); });
    }
    llvm::PassBuilder passes(nullptr, llvm::PipelineTuningOptions(), std::nullopt,
                             options.should_cancel ? &callbacks : nullptr);
    passes.registerModuleAnalyses(modules);
    passes.registerCGSCCAnalyses(call_graph);
    passes.registerFunctionAnalyses(functions);
    passes.registerLoopAnalyses(loops);
    passes.crossRegisterProxies(loops, functions, call_graph, modules);

    if (options.pre_pipeline) {
        options.pre_pipeline(entry, functions);
        if (cancelled()) return false;
        functions.clear(entry, entry.getName());
    }

    llvm::ModulePassManager pipeline;
    // The copied kernels have already been inlined into the entry. Prune
    // unreachable definitions before spending optimization time on them.
    if (options.prune_dead_globals) pipeline.addPass(llvm::GlobalDCEPass());
    if (options.custom_pipeline.empty()) {
        pipeline.addPass(passes.buildPerModuleDefaultPipeline(llvm::OptimizationLevel::O2));
    } else {
        if (options.fold_operand_loads)
            pipeline.addPass(llvm::createModuleToFunctionPassAdaptor(llvm::InstSimplifyPass()));
        // Resolve constantized guards even for a minimal user pipeline.
        pipeline.addPass(llvm::createModuleToFunctionPassAdaptor(llvm::SimplifyCFGPass()));
        if (auto error = passes.parsePassPipeline(pipeline, options.custom_pipeline)) {
            llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "LLVM block pipeline: ");
            return false;
        }
        pipeline.addPass(llvm::GlobalDCEPass());
    }
    pipeline.run(*entry.getParent(), modules);
    if (cancelled()) return false;

    // Work on the optimized SSA so the result is independent of which guest
    // kernels supplied or consumed a packed bit. Packed exit values stay live.
    llvm::FunctionPassManager entry_passes;
    entry_passes.addPass(ForwardPackedBitTestsPass());
    entry_passes.run(entry, functions);
    return !cancelled();
}

llvm::LoadInst* LoadInvariantRegionField(llvm::IRBuilder<>& builder, llvm::Type* type, llvm::Value* field) {
    // Publication fixes the member pointers and page for the table's lifetime.
    auto* load = builder.CreateLoad(type, field);
    load->setMetadata(llvm::LLVMContext::MD_invariant_load, llvm::MDNode::get(builder.getContext(), {}));
    return load;
}

DecodedRegionSelector DecodeRegionSelector(llvm::IRBuilder<>& builder, llvm::Value* tagged_arg, uint64_t member_mask,
                                           size_t guest_page_offset, llvm::Type* index_type, llvm::Type* page_type) {
    const auto& layout = builder.GetInsertBlock()->getModule()->getDataLayout();
    auto* ptr_int_ty = builder.getIntPtrTy(layout);
    auto* tagged = builder.CreatePtrToInt(tagged_arg, ptr_int_ty);
    auto* index_src = index_type == ptr_int_ty ? tagged : builder.CreateTruncOrBitCast(tagged, index_type);
    auto* index = builder.CreateAnd(index_src, llvm::ConstantInt::get(index_type, member_mask), "member.index");
    auto* table = builder.CreateIntToPtr(builder.CreateAnd(tagged, llvm::ConstantInt::get(ptr_int_ty, ~member_mask)),
                                         builder.getPtrTy());
    auto* page_field = builder.CreateConstGEP1_64(builder.getInt8Ty(), table, guest_page_offset);
    auto* raw_page = LoadInvariantRegionField(builder, builder.getInt64Ty(), page_field);
    auto* page = page_type == builder.getInt64Ty() ? static_cast<llvm::Value*>(raw_page)
                                                   : builder.CreateTrunc(raw_page, page_type, "guest.page");
    return {index, table, page};
}

void EmitRegionEntryWrappers(llvm::Module& module, llvm::Function& body, llvm::StringRef name,
                             std::span<const size_t> context_offsets) {
    auto& context = module.getContext();
    llvm::IRBuilder<> builder(context);
    auto* ptr_int_ty = builder.getIntPtrTy(module.getDataLayout());
    for (size_t i = 0; i < context_offsets.size(); ++i) {
        auto* wrapper = llvm::Function::Create(body.getFunctionType(), llvm::GlobalValue::ExternalLinkage,
                                               name + "_" + llvm::Twine(i), module);
        wrapper->setCallingConv(body.getCallingConv());
        wrapper->addFnAttr(llvm::Attribute::NoUnwind);
        builder.SetInsertPoint(llvm::BasicBlock::Create(context, "entry", wrapper));
        llvm::SmallVector<llvm::Value*, 9> args;
        for (auto& arg : wrapper->args()) args.push_back(&arg);
        auto* slot = builder.CreateConstGEP1_64(builder.getInt8Ty(), args[1], context_offsets[i]);
        auto* table = builder.CreateAlignedLoad(builder.getPtrTy(), slot, llvm::Align(alignof(void*)));
        auto* address = builder.CreatePtrToInt(table, ptr_int_ty);
        auto* tagged = builder.CreateOr(address, llvm::ConstantInt::get(ptr_int_ty, i));
        args[1] = builder.CreateIntToPtr(tagged, builder.getPtrTy());
        auto* call = builder.CreateCall(&body, args);
        call->setCallingConv(body.getCallingConv());
        call->setTailCallKind(llvm::CallInst::TCK_MustTail);
        builder.CreateRet(call);
    }
}

void PromoteCyclicStateFields(llvm::Function& body, const CyclicStatePromotionSpec& spec,
                              llvm::FunctionAnalysisManager* functions) {
    if (spec.offsets.empty()) return;
    std::optional<llvm::DominatorTree> local_dom;
    if (spec.only_irreducible) {
        llvm::DominatorTree* dom = nullptr;
        if (functions) {
            dom = &functions->getResult<llvm::DominatorTreeAnalysis>(body);
        } else {
            local_dom.emplace(body);
            dom = &*local_dom;
        }
        llvm::LoopInfo loops(*dom);
        llvm::ReversePostOrderTraversal<llvm::Function*> order(&body);
        if (!llvm::containsIrreducibleCFG<llvm::BasicBlock*>(order, loops)) return;
    }
    llvm::SmallPtrSet<const llvm::BasicBlock*, 32> cyclic;
    for (auto scc = llvm::scc_begin(&body); !scc.isAtEnd(); ++scc)
        if (scc.hasCycle()) cyclic.insert(scc->begin(), scc->end());
    if (cyclic.empty()) return;

    const auto& layout = body.getParent()->getDataLayout();
    auto* state = body.getArg(0);
    const size_t field_bytes = spec.field_bits / 8;
    auto* field_ty = llvm::IntegerType::get(body.getContext(), spec.field_bits);
    const size_t count = spec.offsets.size();

    std::vector<llvm::SmallVector<llvm::Instruction*, 8>> accesses(count);
    llvm::SmallBitVector eligible(count, true);
    llvm::SmallBitVector in_cycle(count, false);
    llvm::SmallBitVector written(count, false);
    llvm::SmallVector<llvm::Instruction*, 8> exits;

    if (spec.use_alias_analysis) {
        llvm::SmallVector<llvm::Instruction*, 32> memory;
        for (auto& inst : llvm::instructions(body)) {
            if (auto* ret = llvm::dyn_cast<llvm::ReturnInst>(&inst)) {
                auto* call = llvm::dyn_cast_or_null<llvm::CallInst>(ret->getPrevNonDebugInstruction());
                exits.push_back(call && call->isMustTailCall() ? static_cast<llvm::Instruction*>(call) : ret);
            }
            if (auto* call = llvm::dyn_cast<llvm::CallInst>(&inst); call && call->isMustTailCall()) continue;
            // The external chain header read is modeled as a memory barrier.
            // Publish registers before it as well as before the continuation.
            if (auto* load = llvm::dyn_cast<llvm::LoadInst>(&inst); load && load->isVolatile() &&
                                                                    load->getType()->isIntegerTy(128) &&
                                                                    !cyclic.contains(load->getParent())) {
                exits.push_back(load);
                continue;
            }
            if (!inst.mayReadOrWriteMemory()) continue;
            memory.push_back(&inst);

            llvm::Value* ptr = nullptr;
            bool is_store = false;
            if (auto* load = llvm::dyn_cast<llvm::LoadInst>(&inst);
                load && load->isSimple() && load->getType() == field_ty) {
                ptr = load->getPointerOperand();
            } else if (auto* store = llvm::dyn_cast<llvm::StoreInst>(&inst);
                       store && store->isSimple() && store->getValueOperand()->getType() == field_ty) {
                ptr = store->getPointerOperand();
                is_store = true;
            }
            int64_t offset = 0;
            if (!ptr || llvm::GetPointerBaseWithConstantOffset(ptr, offset, layout) != state || offset < 0) continue;
            auto field = std::ranges::find(spec.offsets, static_cast<size_t>(offset));
            if (field != spec.offsets.end()) {
                const size_t slot = static_cast<size_t>(field - spec.offsets.begin());
                accesses[slot].push_back(&inst);
                if (cyclic.contains(inst.getParent())) in_cycle.set(slot);
                if (is_store) written.set(slot);
            }
        }

        std::optional<llvm::FunctionAnalysisManager> local_fam;
        llvm::FunctionAnalysisManager* fam = functions;
        if (!fam) {
            local_fam.emplace();
            llvm::PassBuilder pb;
            pb.registerFunctionAnalyses(*local_fam);
            fam = &*local_fam;
        }
        auto& aa = fam->getResult<llvm::AAManager>(body);
        for (size_t slot = 0; slot < count; ++slot) {
            if (!in_cycle.test(slot)) {
                eligible.reset(slot);
                continue;
            }
            const auto& uses = accesses[slot];
            const auto location = *llvm::MemoryLocation::getOrNone(uses.front());
            // Other accesses, including narrow or merged accesses to these fields,
            // can only remain in IR if they are independent of the promoted value.
            const bool clean = std::ranges::none_of(memory, [&](auto* inst) {
                if (llvm::is_contained(uses, inst)) return false;
                if (auto other = llvm::MemoryLocation::getOrNone(inst); other && aa.isNoAlias(location, *other))
                    return false;
                const auto effect = aa.getModRefInfo(inst, location);
                return llvm::isModSet(effect) || (written.test(slot) && llvm::isRefSet(effect));
            });
            if (!clean) eligible.reset(slot);
        }
    } else {
        // Direct use-walk over `state`: proves no unrecognized use escapes the
        // register file and detects any overlapping narrow/wide accesses.
        size_t max_end = 0;
        for (size_t offset : spec.offsets) max_end = std::max(max_end, offset + field_bytes);
        llvm::SmallVector<std::pair<llvm::Value*, uint64_t>, 32> addresses{{state, 0}};
        for (size_t i = 0; i < addresses.size(); ++i) {
            const auto [address, offset] = addresses[i];
            for (auto& use : address->uses()) {
                auto* inst = llvm::cast<llvm::Instruction>(use.getUser());
                if (auto* gep = llvm::dyn_cast<llvm::GetElementPtrInst>(inst)) {
                    llvm::APInt delta(layout.getIndexTypeSizeInBits(address->getType()), 0);
                    if (!gep->accumulateConstantOffset(layout, delta)) return;
                    addresses.emplace_back(gep, offset + delta.getZExtValue());
                    continue;
                }
                auto* load = llvm::dyn_cast<llvm::LoadInst>(inst);
                auto* store = llvm::dyn_cast<llvm::StoreInst>(inst);
                llvm::Type* type = nullptr;
                if (load) {
                    type = load->getType();
                } else if (store && &use == &store->getOperandUse(1)) {
                    type = store->getValueOperand()->getType();
                } else if (llvm::isa<llvm::AssumeInst>(inst) ||
                           (llvm::isa<llvm::CallInst>(inst) && llvm::cast<llvm::CallInst>(inst)->isMustTailCall() &&
                            &use == &inst->getOperandUse(0) && address == state)) {
                    continue;
                } else {
                    return;
                }
                if (offset >= max_end) continue;
                const auto field = std::ranges::find(spec.offsets, static_cast<size_t>(offset));
                if (field != spec.offsets.end() && type == field_ty && (load ? load->isSimple() : store->isSimple())) {
                    const size_t slot = static_cast<size_t>(field - spec.offsets.begin());
                    accesses[slot].push_back(inst);
                    if (cyclic.contains(inst->getParent())) in_cycle.set(slot);
                    if (store) written.set(slot);
                } else {
                    const auto size = layout.getTypeStoreSize(type);
                    const uint64_t access_end = size.isScalable() ? max_end : offset + size.getFixedValue();
                    for (size_t slot = 0; slot < count; ++slot) {
                        const uint64_t slot_start = spec.offsets[slot];
                        const uint64_t slot_end = slot_start + field_bytes;
                        if (offset < slot_end && access_end > slot_start) eligible.reset(slot);
                    }
                }
            }
        }
        eligible &= in_cycle;
        for (auto& inst : llvm::instructions(body)) {
            if (auto* call = llvm::dyn_cast<llvm::CallInst>(&inst); call && call->isMustTailCall())
                exits.push_back(&inst);
            else if (auto* load = llvm::dyn_cast<llvm::LoadInst>(&inst); load && load->isVolatile() &&
                                                                         load->getType()->isIntegerTy(128) &&
                                                                         !cyclic.contains(load->getParent()))
                exits.push_back(&inst);
        }
    }

    llvm::IRBuilder<> entry(&*body.getEntryBlock().getFirstInsertionPt());
    llvm::SmallVector<llvm::AllocaInst*, 16> shadows;
    for (auto slot : eligible.set_bits()) {
        auto* field = entry.CreateConstGEP1_64(entry.getInt8Ty(), state, spec.offsets[slot]);
        auto* shadow = entry.CreateAlloca(field_ty);
        entry.CreateStore(entry.CreateLoad(field_ty, field), shadow);
        shadows.push_back(shadow);
        for (auto* inst : accesses[slot]) {
            if (auto* load = llvm::dyn_cast<llvm::LoadInst>(inst)) {
                load->setOperand(0, shadow);
                load->setAlignment(llvm::Align(field_bytes));
            } else {
                auto* store = llvm::cast<llvm::StoreInst>(inst);
                store->setOperand(1, shadow);
                store->setAlignment(llvm::Align(field_bytes));
            }
            if (spec.clear_aa_metadata) inst->setAAMetadata(llvm::AAMDNodes());
        }
        if (!written.test(slot)) continue;
        for (auto* exit : exits) {
            llvm::IRBuilder<> out(exit);
            out.CreateStore(out.CreateLoad(field_ty, shadow), field);
        }
    }
    if (shadows.empty()) return;
    if (functions) {
        auto& dom = functions->getResult<llvm::DominatorTreeAnalysis>(body);
        llvm::PromoteMemToReg(shadows, dom);
        functions->invalidate(body, llvm::PreservedAnalyses::none());
    } else {
        if (!local_dom) local_dom.emplace(body);
        llvm::PromoteMemToReg(shadows, *local_dom);
    }
}

}  // namespace fiberish
