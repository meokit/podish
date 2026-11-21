#include "jit/llvm_operand_ir.h"

#include <optional>
#include <vector>

#include <llvm/ADT/SmallBitVector.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/Analysis/SimplifyQuery.h>
#include <llvm/Analysis/ValueTracking.h>
#include <llvm/ExecutionEngine/Orc/JITTargetMachineBuilder.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/PassManager.h>
#include <llvm/IR/PatternMatch.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/KnownBits.h>
#include <llvm/Support/TargetSelect.h>
#include <llvm/Transforms/IPO/GlobalDCE.h>
#include <llvm/Transforms/Scalar/InstSimplifyPass.h>
#include <llvm/Transforms/Scalar/SimplifyCFG.h>
#include <llvm/Transforms/Utils/Cloning.h>
#include <llvm/Transforms/Utils/Local.h>

namespace fiberish {
namespace {

struct KnownPayload {
    uint64_t value = 0;
    uint64_t mask = 0;
};

KnownPayload EncodeKnownPayload(const DecodedOperandShape& operand) {
    const uint8_t metadata = operand.encoding >> 16;
    const auto header =
        DecodedOp::PackOperandHeader(uint8_t(operand.encoding), uint8_t(operand.encoding >> 8), metadata);
    constexpr uint64_t lowered = uint64_t{Meta::operand_lowered_mask} << DecodedOp::kMetaShift;
    const auto kind = ExtKind(metadata >> 6);
    if (kind == ExtKind::Data) {
        const auto value =
            (header & DecodedOp::kHasMemory)
                ? DecodedOp::PackMemoryPayload(header, operand.ea_desc, operand.displacement, operand.immediate)
                : header | operand.immediate;
        return {value, ~lowered};
    }
    if (HasBranchLinkSlot(metadata)) {
        // The absolute target and branch width are not in the key.
        constexpr uint64_t width_bits = uint64_t{3} << DecodedOp::kBranchWidthShift;
        constexpr uint64_t mask =
            ((uint64_t{1} << DecodedOp::kAddressShift) - (uint64_t{1} << 32)) & ~(lowered | width_bits);
        return {header | DecodedOp::kBranchTag, mask};
    }
    return {};
}

std::optional<llvm::KnownBits> KnownOperandUse(const llvm::Value& input, const llvm::User& user,
                                               const llvm::KnownBits& bits) {
    if (auto* cast = llvm::dyn_cast<llvm::CastInst>(&user); cast && cast->getType()->isIntegerTy()) {
        const auto width = cast->getType()->getIntegerBitWidth();
        if (cast->getOpcode() == llvm::Instruction::Trunc) return bits.trunc(width);
        if (cast->getOpcode() == llvm::Instruction::ZExt) return bits.zext(width);
        if (cast->getOpcode() == llvm::Instruction::SExt) return bits.sext(width);
        return {};
    }
    const auto* op = llvm::dyn_cast<llvm::Instruction>(&user);
    if (!op || op->getNumOperands() != 2) return {};
    const bool input_first = op->getOperand(0) == &input;
    const auto* constant = llvm::dyn_cast<llvm::ConstantInt>(op->getOperand(input_first ? 1 : 0));
    if (!constant) return {};
    const auto other = llvm::KnownBits::makeConstant(constant->getValue());
    switch (op->getOpcode()) {
        case llvm::Instruction::And:
            return bits & other;
        case llvm::Instruction::Or:
            return bits | other;
        case llvm::Instruction::Xor:
            return bits ^ other;
        case llvm::Instruction::Shl:
            if (input_first && constant->getValue().ult(bits.getBitWidth())) return llvm::KnownBits::shl(bits, other);
            break;
        case llvm::Instruction::LShr:
            if (input_first && constant->getValue().ult(bits.getBitWidth())) return llvm::KnownBits::lshr(bits, other);
            break;
        case llvm::Instruction::AShr:
            if (input_first && constant->getValue().ult(bits.getBitWidth())) return llvm::KnownBits::ashr(bits, other);
            break;
        case llvm::Instruction::ICmp: {
            const auto predicate = llvm::cast<llvm::ICmpInst>(op)->getPredicate();
            if (predicate != llvm::CmpInst::ICMP_EQ && predicate != llvm::CmpInst::ICMP_NE) break;
            if (auto equal = llvm::KnownBits::eq(bits, other))
                return llvm::KnownBits::makeConstant(llvm::APInt(1, *equal == (predicate == llvm::CmpInst::ICMP_EQ)));
            break;
        }
        default:
            break;
    }
    return {};
}

void FoldKnownOperandBits(llvm::Value& value, const llvm::KnownBits& bits,
                          llvm::SmallPtrSetImpl<llvm::Value*>& visited) {
    if (value.use_empty() || bits.isUnknown() || !visited.insert(&value).second) return;
    if (bits.isConstant()) {
        value.replaceAllUsesWith(llvm::ConstantInt::get(value.getType(), bits.getConstant()));
        return;
    }
    for (auto* user : llvm::make_early_inc_range(value.users())) {
        if (const auto known = KnownOperandUse(value, *user, bits)) FoldKnownOperandBits(*user, *known, visited);
    }
}

void BakeLLVMOperandLoads(llvm::Function& entry, block_recipe::BlockKeyView key) {
    const auto& layout = entry.getParent()->getDataLayout();
    if (!layout.isLittleEndian()) return;

    // Index the physical slots, including each adjacent immediate slot.
    llvm::SmallVector<KnownPayload, 2 * block_recipe::kMaxOps> payloads;
    for (size_t i = 0; i < key.operations.size(); ++i) {
        const auto& operand = key.operands[i];
        payloads.push_back(EncodeKnownPayload(operand));
        if (operand.SlotCount() == 2) {
            const auto metadata = uint8_t(operand.encoding >> 16);
            payloads.push_back(HasBranchLinkSlot(metadata) ? KnownPayload{}
                                                           : KnownPayload{operand.immediate, UINT64_MAX});
        }
    }

    const auto* ops = entry.getArg(1);
    llvm::SmallVector<llvm::LoadInst*, 32> dead_loads;
    for (auto& inst : llvm::instructions(entry)) {
        auto* load = llvm::dyn_cast<llvm::LoadInst>(&inst);
        if (!load || !load->isSimple() || !load->getType()->isIntegerTy()) continue;
        const auto width = load->getType()->getIntegerBitWidth();
        if (width % 8 || width > 64) continue;
        int64_t offset = 0;
        if (llvm::GetPointerBaseWithConstantOffset(load->getPointerOperand(), offset, layout) != ops || offset < 0)
            continue;
        const auto slot = size_t(offset) / sizeof(DecodedOp);
        const auto byte = size_t(offset) % sizeof(DecodedOp);
        constexpr auto payload_offset = offsetof(DecodedOp, payload);
        if (slot >= payloads.size() || byte < payload_offset || byte + width / 8 > payload_offset + sizeof(uint64_t))
            continue;
        const auto& [value, mask] = payloads[slot];
        const auto shift = 8 * (byte - payload_offset);
        llvm::KnownBits bits(width);
        bits.Zero = llvm::APInt(width, (~value & mask) >> shift);
        bits.One = llvm::APInt(width, (value & mask) >> shift);
        llvm::SmallPtrSet<llvm::Value*, 16> visited;
        FoldKnownOperandBits(*load, bits, visited);
        if (load->use_empty()) dead_loads.push_back(load);
    }
    for (auto* load : dead_loads) load->eraseFromParent();
}

void LowerBlockEipReads(llvm::Function& entry, block_recipe::BlockKeyView key, std::span<const size_t> slot_offsets) {
    auto* marker = entry.getParent()->getFunction("FiberLLVMReadEip");
    if (!marker) return;
    const auto& layout = entry.getParent()->getDataLayout();
    const bool bake = !key.eips.empty() && layout.isLittleEndian();
    auto* first_op = entry.getArg(1);
    llvm::Value* base_eip = nullptr;
    llvm::SmallVector<llvm::CallInst*, 8> markers;
    for (auto* user : marker->users()) {
        if (auto* call = llvm::dyn_cast<llvm::CallInst>(user)) markers.push_back(call);
    }
    for (auto* call : markers) {
        auto* value = call->getArgOperand(1);
        const auto field =
            static_cast<LLVMBlockEipField>(llvm::cast<llvm::ConstantInt>(call->getArgOperand(2))->getZExtValue());
        std::optional<uint32_t> delta;
        bool wrap16 = false;
        const bool in_entry = bake && call->getFunction() == &entry;
        if (in_entry && field == LLVMBlockEipField::Fallthrough && key.exit == LLVMBlockExit::Fallthrough) {
            // The sentinel next EIP is copied from the last decoded instruction.
            delta = key.eips.back().next_offset;
        } else if (in_entry && field != LLVMBlockEipField::Fallthrough) {
            int64_t offset = 0;
            if (llvm::GetPointerBaseWithConstantOffset(call->getArgOperand(0), offset, layout) == first_op &&
                offset >= 0) {
                const auto slot = std::ranges::lower_bound(slot_offsets, size_t(offset));
                if (slot != slot_offsets.end() && *slot == size_t(offset)) {
                    const auto i = size_t(slot - slot_offsets.begin());
                    const auto& pc = key.eips[i];
                    if (field == LLVMBlockEipField::Next) {
                        delta = pc.next_offset;
                    } else {
                        const auto imm = key.operands[i].immediate;
                        switch (RelativeBranchWidth(pc.branch_width)) {
                            case RelativeBranchWidth::Byte:
                                delta = pc.next_offset + int8_t(imm);
                                break;
                            case RelativeBranchWidth::Word:
                                delta = pc.next_offset + int16_t(imm);
                                wrap16 = true;
                                break;
                            case RelativeBranchWidth::Dword:
                                delta = pc.next_offset + imm;
                                break;
                            case RelativeBranchWidth::None:
                                break;
                        }
                    }
                }
            }
        }
        if (delta) {
            if (!base_eip) {
                llvm::IRBuilder<> builder(&*entry.getEntryBlock().getFirstInsertionPt());
                // Installed handlers always receive BasicBlock::FirstOp().
                const auto offset = int64_t(offsetof(BasicBlock, chain)) - int64_t(offsetof(BasicBlock, slots));
                auto* header = builder.CreateGEP(builder.getInt8Ty(), first_op, builder.getInt64(offset));
                base_eip = builder.CreateAlignedLoad(builder.getInt32Ty(), header, llvm::Align(alignof(uint32_t)),
                                                     "guest.block.eip");
            }
            llvm::IRBuilder<> builder(call);
            value = builder.CreateAdd(base_eip, builder.getInt32(*delta));
            if (wrap16) value = builder.CreateAnd(value, builder.getInt32(0xffff));
        }
        call->replaceAllUsesWith(value);
        call->eraseFromParent();
    }
}

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
    llvm::PreservedAnalyses run(llvm::Function& entry, llvm::FunctionAnalysisManager&);
};

llvm::PreservedAnalyses ForwardPackedBitTestsPass::run(llvm::Function& entry, llvm::FunctionAnalysisManager&) {
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

bool InlineLLVMKernel(llvm::CallInst& call, llvm::Function& kernel, BoundOperandShape binding,
                      const DecodedOperandShape* shape) {
    if (kernel.isDeclaration() || kernel.getFunctionType() != call.getFunctionType()) return false;
    call.setCalledFunction(&kernel);
    const auto bind = [&](unsigned arg, uint32_t value) {
        call.setArgOperand(arg, llvm::ConstantInt::get(call.getArgOperand(arg)->getType(), value));
    };
    // The placeholder includes the four template operands followed by the
    // logic ABI. ModRM is a constant in both generic and exact blocks.
    bind(8, binding.modrm);
    if (shape) {
        bind(1, shape->encoding);
        bind(2, shape->ea_desc);
        bind(3, shape->displacement);
        bind(7, shape->immediate);
    }
    llvm::InlineFunctionInfo info;
    return llvm::InlineFunction(call, info).isSuccess();
}

bool OptimizeLLVMModule(llvm::Function& entry, bool prune_dead_globals, llvm::StringRef custom_pipeline,
                        bool fold_operand_loads) {
    llvm::LoopAnalysisManager loops;
    llvm::FunctionAnalysisManager functions;
    llvm::CGSCCAnalysisManager call_graph;
    llvm::ModuleAnalysisManager modules;
    llvm::PassBuilder passes;
    passes.registerModuleAnalyses(modules);
    passes.registerCGSCCAnalyses(call_graph);
    passes.registerFunctionAnalyses(functions);
    passes.registerLoopAnalyses(loops);
    passes.crossRegisterProxies(loops, functions, call_graph, modules);
    llvm::ModulePassManager pipeline;
    // The copied kernels have already been inlined into the entry. Prune
    // unreachable definitions before spending optimization time on them.
    if (prune_dead_globals) pipeline.addPass(llvm::GlobalDCEPass());
    if (custom_pipeline.empty()) {
        pipeline.addPass(passes.buildPerModuleDefaultPipeline(llvm::OptimizationLevel::O2));
    } else {
        if (fold_operand_loads) pipeline.addPass(llvm::createModuleToFunctionPassAdaptor(llvm::InstSimplifyPass()));
        // Resolve the constantized step and exit guards even for a minimal
        // user pipeline. Unused template bodies must never reach codegen.
        pipeline.addPass(llvm::createModuleToFunctionPassAdaptor(llvm::SimplifyCFGPass()));
        if (auto error = passes.parsePassPipeline(pipeline, custom_pipeline)) {
            llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "LLVM block pipeline: ");
            return false;
        }
        pipeline.addPass(llvm::GlobalDCEPass());
    }
    pipeline.run(*entry.getParent(), modules);
    // Work on the optimized SSA so the result is independent of which guest
    // kernels supplied or consumed this bit. Packed exit values stay live.
    llvm::FunctionPassManager entry_passes;
    entry_passes.addPass(ForwardPackedBitTestsPass());
    entry_passes.run(entry, functions);
    return true;
}

}  // namespace

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

bool InitializeLLVMTarget() {
    static const bool initialized = !llvm::InitializeNativeTarget() && !llvm::InitializeNativeTargetAsmPrinter();
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

LLVMOperandTemplates::LLVMOperandTemplates(const llvm::Module* module) : module_(module) {
    if (!module_) return;
    const auto* table = module_->getNamedGlobal("FiberLLVMOperationTable");
    if (!table || !table->hasInitializer()) return;
    const auto* contents = llvm::dyn_cast<llvm::ConstantStruct>(table->getInitializer());
    if (!contents || contents->getNumOperands() != 2) return;
    const auto* fingerprint = llvm::dyn_cast<llvm::ConstantDataArray>(contents->getOperand(0));
    if (!fingerprint || !fingerprint->isCString() ||
        fingerprint->getAsCString() != llvm::StringRef(block_recipe::kOpIdFingerprint))
        return;
    const auto* array = llvm::dyn_cast<llvm::ConstantArray>(contents->getOperand(1));
    if (!array || array->getNumOperands() != functions_.size()) return;
    for (size_t i = 0; i < functions_.size(); ++i) {
        const auto* fn = llvm::dyn_cast<llvm::Function>(array->getOperand(i)->stripPointerCasts());
        if (!fn || fn->isDeclaration()) return;
        functions_[i] = fn;
        kernels_.insert(fn);
    }
    table_ = table;
}

const llvm::Function* LLVMOperandTemplates::Get(block_recipe::OpId id, bool exact) const {
    const auto index = static_cast<size_t>(id);
    return index < block_recipe::kOpIdCount ? functions_[2 * index + exact] : nullptr;
}

bool LLVMOperandTemplates::IsTemplateGlobal(const llvm::GlobalValue* value) const {
    return value == table_ || kernels_.contains(value);
}

std::unique_ptr<llvm::Module> PrepareLLVMBlock(const LLVMOperandTemplates& templates, block_recipe::BlockKeyView key,
                                               llvm::StringRef entry_name, bool prune_dead_globals,
                                               llvm::StringRef custom_pipeline, bool fold_operand_loads) {
    if (!templates.IsValid() || key.operations.size() > block_recipe::kMaxOps) return {};
    const bool exact = key.IsExact();
    if (exact && key.operands.size() != key.operations.size()) return {};
    if (!key.eips.empty() && (!exact || key.eips.size() != key.operations.size())) return {};
    fold_operand_loads &= exact;
    llvm::SmallPtrSet<const llvm::GlobalValue*, 32> kernels;
    std::array<size_t, block_recipe::kMaxOps> slot_offsets;
    size_t next_offset = 0;
    size_t step_index = 0;
    for (const auto& operation : key.operations) {
        if (operation.shape.slots < 1 || operation.shape.slots > 2) return {};
        slot_offsets[step_index++] = next_offset;
        next_offset += operation.shape.slots * sizeof(DecodedOp);
        const auto* kernel = templates.Get(operation.id, exact);
        if (!kernel) return {};
        kernels.insert(kernel);
    }
    llvm::ValueToValueMapTy values;
    auto module = llvm::CloneModule(templates.Module(), values, [&](const llvm::GlobalValue* def) {
        return kernels.contains(def) || !templates.IsTemplateGlobal(def);
    });
    auto* entry = module->getFunction("FiberLLVMEntry");
    const auto* placeholder = module->getFunction("FiberLLVMPlaceholder");
    const auto* use_step = module->getFunction("FiberLLVMUseStep");
    const auto* exit_kind = module->getFunction("FiberLLVMExit");
    const auto* operand_layout = module->getFunction("FiberLLVMOperandLayout");
    if (!entry || !placeholder || !use_step || !exit_kind || !operand_layout) return {};

    std::vector<llvm::CallInst*> guards;
    llvm::CallInst* exit_site = nullptr;
    for (auto& inst : llvm::instructions(*entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (call && call->getCalledFunction() == use_step) guards.push_back(call);
        if (call && call->getCalledFunction() == exit_kind) exit_site = call;
    }
    if (guards.size() != block_recipe::kMaxOps || !exit_site) return {};
    exit_site->replaceAllUsesWith(llvm::ConstantInt::get(exit_site->getType(), static_cast<uint8_t>(key.exit)));
    exit_site->eraseFromParent();
    for (auto* guard : guards) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(guard->getArgOperand(0));
        if (!index) return {};
        guard->replaceAllUsesWith(
            llvm::ConstantInt::get(guard->getType(), index->getZExtValue() < key.operations.size()));
        guard->eraseFromParent();
    }
    llvm::removeUnreachableBlocks(*entry);

    std::vector<llvm::CallInst*> sites;
    std::vector<llvm::CallInst*> advances;
    for (auto& inst : llvm::instructions(*entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (!call) continue;
        if (call->getCalledFunction() == placeholder) sites.push_back(call);
        if (call->getCalledFunction() == operand_layout) advances.push_back(call);
    }
    for (auto* advance : advances) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(advance->getArgOperand(0));
        if (!index || index->getZExtValue() >= key.operations.size()) return {};
        const auto& operation = key.operations[index->getZExtValue()];
        const auto id = static_cast<size_t>(operation.id);
        const auto layout = block_recipe::kHasBranchLinkSlot[id] ? kLLVMBranchLinkSlot : operation.shape.slots;
        assert((layout & 3) == operation.shape.slots);
        advance->replaceAllUsesWith(llvm::ConstantInt::get(advance->getType(), layout));
        advance->eraseFromParent();
    }
    if (sites.size() != key.operations.size()) return {};
    llvm::SmallBitVector slots(key.operations.size());
    for (auto* call : sites) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(0));
        if (!index || index->getZExtValue() >= key.operations.size() || slots.test(index->getZExtValue())) return {};
        slots.set(index->getZExtValue());
        const auto slot = index->getZExtValue();
        const auto& operation = key.operations[slot];
        const auto* source = templates.Get(operation.id, exact);
        auto* target = llvm::dyn_cast_or_null<llvm::Function>(values.lookup(source));
        if (fold_operand_loads || !key.eips.empty()) {
            // A kernel runs only at its step. The running cursor also flows
            // into exits, so its incoming PHIs need not name that same slot.
            // Give the kernel a direct pointer before inlining its loads.
            llvm::IRBuilder<> builder(call);
            auto* op =
                builder.CreateInBoundsGEP(builder.getInt8Ty(), entry->getArg(1), builder.getInt64(slot_offsets[slot]));
            call->setArgOperand(5, op);  // DecodedOp* in LogicFuncParams.
        }
        if (!target || !InlineLLVMKernel(*call, *target, operation.shape, exact ? &key.operands[slot] : nullptr))
            return {};
    }
    LowerBlockEipReads(*entry, key, std::span(slot_offsets).first(step_index));
    if (fold_operand_loads) BakeLLVMOperandLoads(*entry, key);

    entry->setName(entry_name);
    for (auto& function : *module) {
        if (&function != entry && !function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    if (!OptimizeLLVMModule(*entry, prune_dead_globals, custom_pipeline, fold_operand_loads) ||
        llvm::verifyModule(*module))
        return {};
    for (auto& inst : llvm::instructions(*entry)) {
        const auto* call = llvm::dyn_cast<llvm::CallBase>(&inst);
        const auto* fn = call ? call->getCalledFunction() : nullptr;
        if (fn && fn->getName().starts_with("FiberLLVM")) return {};
    }
    return module;
}

}  // namespace fiberish
