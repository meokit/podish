#include "jit/llvm_operand_ir.h"
#include "exec_utils.h"
#include "jit/llvm_kernel_module.h"
#include "state.h"

#include <array>
#include <optional>
#include <vector>

#include <llvm/ADT/DenseMap.h>
#include <llvm/ADT/SetVector.h>
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
#include <llvm/IR/ValueHandle.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/KnownBits.h>
#include <llvm/Support/TargetSelect.h>
#include <llvm/Transforms/IPO/GlobalDCE.h>
#include <llvm/Transforms/InstCombine/InstCombine.h>
#include <llvm/Transforms/Scalar/InstSimplifyPass.h>
#include <llvm/Transforms/Scalar/SROA.h>
#include <llvm/Transforms/Scalar/SimplifyCFG.h>
#include <llvm/Transforms/Utils/Cloning.h>
#include <llvm/Transforms/Utils/Local.h>

namespace fiberish {
namespace {

using block_recipe::BlockKeyView;

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

constexpr int64_t kFirstOpOffset = offsetof(BasicBlock, slots);

llvm::Value* LoadBlockEip(llvm::IRBuilder<>& builder, llvm::Value* first_op) {
    constexpr auto offset = int64_t(offsetof(BasicBlock, chain)) - kFirstOpOffset;
    auto* header = builder.CreateGEP(builder.getInt8Ty(), first_op, builder.getInt64(offset));
    return builder.CreateAlignedLoad(builder.getInt32Ty(), header, llvm::Align(alignof(uint32_t)), "guest.block.eip");
}

void LowerBlockEipReads(llvm::Function& entry, block_recipe::BlockKeyView key, std::span<const size_t> slot_offsets,
                        std::optional<uint32_t> member_offset) {
    auto* marker = entry.getParent()->getFunction("FiberLLVMReadEip");
    if (!marker) return;
    const auto& layout = entry.getParent()->getDataLayout();
    const bool bake = !key.eips.empty() && layout.isLittleEndian();
    auto* first_op = entry.getArg(1);
    llvm::Value* base_eip = nullptr;
    llvm::SmallVector<llvm::CallInst*, 8> markers;
    for (auto* user : marker->users()) {
        if (auto* call = llvm::dyn_cast<llvm::CallInst>(user); call && call->getFunction() == &entry)
            markers.push_back(call);
    }
    for (auto* call : markers) {
        auto* value = call->getArgOperand(1);
        const auto field =
            static_cast<LLVMBlockEipField>(llvm::cast<llvm::ConstantInt>(call->getArgOperand(2))->getZExtValue());
        std::optional<uint32_t> delta;
        bool wrap16 = false;
        if (bake && field == LLVMBlockEipField::Fallthrough && key.exit == LLVMBlockExit::Fallthrough) {
            // The sentinel next EIP is copied from the last decoded instruction.
            delta = key.eips.back().next_offset;
        } else if (bake && field != LLVMBlockEipField::Fallthrough) {
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
                if (member_offset) {
                    auto placeholder =
                        entry.getParent()->getOrInsertFunction("FiberLLVMRegionBase", builder.getInt32Ty());
                    base_eip = builder.CreateAdd(builder.CreateCall(placeholder), builder.getInt32(*member_offset));
                } else {
                    base_eip = LoadBlockEip(builder, first_op);
                }
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

constexpr llvm::StringLiteral kWriteArithmeticFlags = "FiberLLVMWriteArithmeticFlags";
constexpr llvm::StringLiteral kWriteLogicalFlags = "FiberLLVMWriteLogicalFlags";
constexpr llvm::StringLiteral kReadFlag = "FiberLLVMReadFlag";
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
constexpr llvm::StringLiteral kPackRegionFlags = "FiberLLVMPackRegionFlags";
#endif

// Keep the arithmetic flag fields independent across control-flow joins.
// Their positions are the same as in the ABI word, so packing is just an OR.
constexpr uint64_t kFlagsBaseMask = ~(uint64_t(ARITHMETIC_FLAGS_MASK & ~PF_MASK) | FLAGS_CACHE_PF_STATE_MASK);
constexpr std::array kFlagFields = {
    kFlagsBaseMask,    uint64_t(CF_MASK), uint64_t(ZF_MASK),         uint64_t(SF_MASK),
    uint64_t(OF_MASK), uint64_t(AF_MASK), FLAGS_CACHE_PF_STATE_MASK,
};

class DeferredFlags {
public:
    void Lower(llvm::Function& entry);

private:
    struct ValueFields {
        llvm::CallInst* producer = nullptr;
        std::array<llvm::Value*, kFlagFields.size()> values{};
    };

    llvm::Value* Field(llvm::Value* packed, size_t index, llvm::IRBuilder<>& builder);
    llvm::Value* Pack(llvm::Value* packed, llvm::IRBuilder<>& builder);
    llvm::DenseMap<llvm::Value*, ValueFields> fields_;
};

llvm::Value* DeferredFlags::Field(llvm::Value* packed, size_t index, llvm::IRBuilder<>& at_use) {
    const uint64_t mask = kFlagFields[index];
    auto found = fields_.find(packed);
    if (found == fields_.end()) return at_use.CreateAnd(packed, at_use.getInt64(mask));
    auto& fields = found->second;
    auto*& cached = fields.values[index];
    if (cached) return cached;

    llvm::IRBuilder<> builder(llvm::cast<llvm::Instruction>(packed));
    if (auto* phi = llvm::dyn_cast<llvm::PHINode>(packed)) {
        auto* part = builder.CreatePHI(builder.getInt64Ty(), phi->getNumIncomingValues(), "flag");
        cached = part;  // A loop may use this field on one of its own incoming edges.
        for (auto [i, incoming] : llvm::enumerate(phi->incoming_values())) {
            auto* predecessor = phi->getIncomingBlock(i);
            llvm::IRBuilder<> edge(predecessor->getTerminator());
            part->addIncoming(Field(incoming, index, edge), predecessor);
        }
    } else if (auto* choice = llvm::dyn_cast<llvm::SelectInst>(packed)) {
        auto* yes = Field(choice->getTrueValue(), index, builder);
        auto* no = Field(choice->getFalseValue(), index, builder);
        cached = builder.CreateSelect(choice->getCondition(), yes, no);
    } else {
        auto& call = *fields.producer;
        auto* value = call.getArgOperand(1);
        const bool logical = call.getCalledFunction()->getName() == kWriteLogicalFlags;
        if (mask == kFlagsBaseMask) {
            auto* base = Field(call.getArgOperand(0), index, builder);
            cached = builder.CreateAnd(base, builder.getInt64(~uint64_t(PF_MASK)));
        } else if (mask == FLAGS_CACHE_PF_STATE_MASK) {
            auto* parity = logical ? builder.CreateTrunc(value, builder.getInt8Ty()) : call.getArgOperand(2);
            cached = builder.CreateShl(builder.CreateZExt(parity, builder.getInt64Ty()), FLAGS_CACHE_PF_STATE_SHIFT);
        } else if (!logical) {
            cached = builder.CreateZExt(builder.CreateAnd(value, uint32_t(mask)), builder.getInt64Ty());
        } else if (mask == ZF_MASK) {
            auto* zero = builder.CreateICmpEQ(value, builder.getInt32(0));
            cached = builder.CreateSelect(zero, builder.getInt64(ZF_MASK), builder.getInt64(0));
        } else if (mask == SF_MASK) {
            // The bit width is a constant supplied by the C++ kernel.
            auto* top = builder.CreateSub(call.getArgOperand(2), builder.getInt32(8));
            auto* sign = builder.CreateAnd(builder.CreateLShr(value, top), SF_MASK);
            cached = builder.CreateZExt(sign, builder.getInt64Ty());
        } else {
            cached = builder.getInt64(0);
        }
    }
    return cached;
}

llvm::Value* DeferredFlags::Pack(llvm::Value* packed, llvm::IRBuilder<>& builder) {
    llvm::Value* word = builder.getInt64(0);
    for (size_t index = 0; index < kFlagFields.size(); ++index)
        word = builder.CreateOr(word, Field(packed, index, builder));
    return word;
}

void DeferredFlags::Lower(llvm::Function& entry) {
    llvm::SmallVector<llvm::Value*, 16> deferred;
    llvm::SmallVector<llvm::CallInst*, 16> reads;
    for (auto& instruction : llvm::instructions(entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&instruction);
        const auto* fn = call ? call->getCalledFunction() : nullptr;
        if (!fn) continue;
        if (fn->getName() == kWriteLogicalFlags || fn->getName() == kWriteArithmeticFlags) {
            fields_[call].producer = call;
            deferred.push_back(call);
        } else if (fn->getName() == kReadFlag) {
            reads.push_back(call);
        }
    }
    for (size_t i = 0; i < deferred.size(); ++i) {
        for (auto* user : deferred[i]->users()) {
            if (llvm::isa<llvm::PHINode, llvm::SelectInst>(user) && user->getType()->isIntegerTy(64) &&
                fields_.try_emplace(user).second)
                deferred.push_back(user);
        }
    }

    for (auto* call : reads) {
        llvm::IRBuilder<> builder(call);
        auto* packed = call->getArgOperand(0);
        const auto mask = llvm::cast<llvm::ConstantInt>(call->getArgOperand(1))->getZExtValue();
        const auto field = llvm::find(kFlagFields, mask);
        assert(field != kFlagFields.end());
        auto* part = Field(packed, field - kFlagFields.begin(), builder);
        call->replaceAllUsesWith(builder.CreateICmpNE(part, builder.getInt64(0)));
        call->eraseFromParent();
    }

    for (auto* value : deferred) {
        for (auto& use : llvm::make_early_inc_range(value->uses())) {
            auto* user = llvm::cast<llvm::Instruction>(use.getUser());
            if (fields_.contains(user)) continue;
            llvm::IRBuilder<> builder(user);
            use.set(Pack(value, builder));
        }
    }
    // Removing the original packed graph together handles cyclic PHIs.
    for (auto* value : deferred) llvm::cast<llvm::Instruction>(value)->dropAllReferences();
    for (auto* value : deferred) llvm::cast<llvm::Instruction>(value)->eraseFromParent();
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
    // Promote the C++ flags reference before lowering its symbolic producers.
    llvm::FunctionPassManager promote;
    promote.addPass(llvm::SROAPass(llvm::SROAOptions::ModifyCFG));
    promote.addPass(llvm::SimplifyCFGPass());
    promote.run(entry, functions);
    DeferredFlags().Lower(entry);
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    if (auto* pack = entry.getParent()->getFunction(kPackRegionFlags)) {
        for (auto* user : llvm::make_early_inc_range(pack->users())) {
            auto* call = llvm::cast<llvm::CallInst>(user);
            call->replaceAllUsesWith(call->getArgOperand(0));
            call->eraseFromParent();
        }
        pack->eraseFromParent();
    }
#endif
    functions.clear(entry, entry.getName());
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

#if FIBERCPU_GUEST_AARCH64 && FIBERCPU_LLVM_JIT
bool OptimizeLLVMArmModule(llvm::Function& entry, llvm::StringRef pipeline, bool fold_operand_loads) {
    return OptimizeLLVMModule(entry, true, pipeline, fold_operand_loads);
}
#endif

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
    }
    valid_ = true;
}

const llvm::Function* LLVMOperandTemplates::Get(block_recipe::OpId id, bool exact) const {
    const auto index = static_cast<size_t>(id);
    return index < block_recipe::kOpIdCount ? functions_[2 * index + exact] : nullptr;
}

namespace {

std::unique_ptr<llvm::Module> CloneBlockModule(const LLVMOperandTemplates& templates,
                                               std::span<const BlockKeyView> keys) {
    if (!templates.IsValid()) return {};
    const auto& source = templates.Module();
    const auto* entry = source.getFunction("FiberLLVMEntry");
    if (!entry) return {};
    llvm::SmallSetVector<const llvm::GlobalValue*, 32> roots;
    roots.insert(entry);
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    // The region builder reads this table directly when grouping memory exits.
    if (keys.size() > 1)
        if (const auto* memory_exits = source.getNamedGlobal("FiberLLVMRegionMemoryExits")) roots.insert(memory_exits);
#endif
    for (const auto key : keys) {
        if (key.operations.size() > block_recipe::kMaxOps ||
            (key.IsExact() && key.operands.size() != key.operations.size()) ||
            (!key.eips.empty() && (!key.IsExact() || key.eips.size() != key.operations.size())))
            return {};
        for (const auto& step : key.operations) {
            if (step.shape.slots < 1 || step.shape.slots > 2) return {};
            const auto* kernel = templates.Get(step.id, key.IsExact());
            if (!kernel) return {};
            roots.insert(kernel);
        }
    }
    auto module = KernelModule().Clone(source, roots.getArrayRef());
    // The catalog normally stays in the source module. Let DCE remove it
    // if LLVM had to clone the entire module for module-level constructs.
    if (auto* table = module->getNamedGlobal("FiberLLVMOperationTable"))
        table->setLinkage(llvm::GlobalValue::InternalLinkage);
    return module;
}

bool SpecializeBlockEntry(const LLVMOperandTemplates& templates, llvm::Function& entry, BlockKeyView key,
                          bool fold_operand_loads, std::optional<uint32_t> member_offset = {}) {
    auto& module = *entry.getParent();
    const bool exact = key.IsExact();
    fold_operand_loads &= exact;
    std::array<size_t, block_recipe::kMaxOps> slot_offsets;
    size_t next_offset = 0;
    size_t step_index = 0;
    for (const auto& step : key.operations) {
        slot_offsets[step_index++] = next_offset;
        next_offset += step.shape.slots * sizeof(DecodedOp);
    }
    const auto* placeholder = module.getFunction("FiberLLVMPlaceholder");
    const auto* use_step = module.getFunction("FiberLLVMUseStep");
    const auto* exit_kind = module.getFunction("FiberLLVMExit");
    const auto* operand_layout = module.getFunction("FiberLLVMOperandLayout");
    if (!placeholder || !use_step || !exit_kind || !operand_layout) return false;

    std::vector<llvm::CallInst*> guards;
    llvm::CallInst* exit_site = nullptr;
    for (auto& inst : llvm::instructions(entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (call && call->getCalledFunction() == use_step) guards.push_back(call);
        if (call && call->getCalledFunction() == exit_kind) exit_site = call;
    }
    if (guards.size() != block_recipe::kMaxOps || !exit_site) return false;
    exit_site->replaceAllUsesWith(llvm::ConstantInt::get(exit_site->getType(), static_cast<uint8_t>(key.exit)));
    exit_site->eraseFromParent();
    for (auto* guard : guards) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(guard->getArgOperand(0));
        if (!index) return false;
        guard->replaceAllUsesWith(
            llvm::ConstantInt::get(guard->getType(), index->getZExtValue() < key.operations.size()));
        guard->eraseFromParent();
    }
    llvm::removeUnreachableBlocks(entry);

    std::vector<llvm::CallInst*> sites;
    std::vector<llvm::CallInst*> advances;
    for (auto& inst : llvm::instructions(entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (!call) continue;
        if (call->getCalledFunction() == placeholder) sites.push_back(call);
        if (call->getCalledFunction() == operand_layout) advances.push_back(call);
    }
    for (auto* advance : advances) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(advance->getArgOperand(0));
        if (!index || index->getZExtValue() >= key.operations.size()) return false;
        const auto& operation = key.operations[index->getZExtValue()];
        const auto id = static_cast<size_t>(operation.id);
        const auto layout = block_recipe::kHasBranchLinkSlot[id] ? kLLVMBranchLinkSlot : operation.shape.slots;
        assert((layout & 3) == operation.shape.slots);
        advance->replaceAllUsesWith(llvm::ConstantInt::get(advance->getType(), layout));
        advance->eraseFromParent();
    }
    if (sites.size() != key.operations.size()) return false;
    llvm::SmallBitVector slots(key.operations.size());
    for (auto* call : sites) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(0));
        if (!index || index->getZExtValue() >= key.operations.size() || slots.test(index->getZExtValue())) return false;
        slots.set(index->getZExtValue());
        const auto slot = index->getZExtValue();
        const auto& operation = key.operations[slot];
        const auto* source = templates.Get(operation.id, exact);
        auto* target = source ? module.getFunction(source->getName()) : nullptr;
        if (fold_operand_loads || !key.eips.empty()) {
            // A kernel runs only at its step. The running cursor also flows
            // into exits, so its incoming PHIs need not name that same slot.
            // Give the kernel a direct pointer before inlining its loads.
            llvm::IRBuilder<> builder(call);
            auto* op =
                builder.CreateInBoundsGEP(builder.getInt8Ty(), entry.getArg(1), builder.getInt64(slot_offsets[slot]));
            call->setArgOperand(5, op);  // DecodedOp* in LogicFuncParams.
        }
        if (!target || !InlineLLVMKernel(*call, *target, operation.shape, exact ? &key.operands[slot] : nullptr))
            return false;
    }
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    auto* share_exits = module.getFunction("FiberLLVMShareRegionExits");
    if (share_exits) {
        for (auto* user : llvm::make_early_inc_range(share_exits->users())) {
            auto* call = llvm::cast<llvm::CallInst>(user);
            if (call->getFunction() != &entry) continue;
            call->replaceAllUsesWith(llvm::ConstantInt::getBool(entry.getContext(), member_offset.has_value()));
            call->eraseFromParent();
        }
    }
#endif
    LowerBlockEipReads(entry, key, std::span(slot_offsets).first(step_index), member_offset);
    if (fold_operand_loads) BakeLLVMOperandLoads(entry, key);

    return true;
}

void RemoveBlockMarkers(llvm::Module& module) {
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    if (auto* memory_exits = module.getNamedGlobal("FiberLLVMRegionMemoryExits")) memory_exits->eraseFromParent();
#endif
    for (const auto name : {"FiberLLVMReadEip", "FiberLLVMRegionEdge", "FiberLLVMRegionExit"}) {
        auto* function = module.getFunction(name);
        if (!function) continue;
        for (auto* user : llvm::make_early_inc_range(function->users())) {
            auto* call = llvm::cast<llvm::CallInst>(user);
            if (!call->getType()->isVoidTy()) {
                call->replaceAllUsesWith(call->getArgOperand(1));
            } else if (function->getName() == "FiberLLVMRegionEdge") {
                // Standalone compiled blocks retain the interpreter's chain cleanup.
                llvm::IRBuilder<> builder(call);
                auto* tag = builder.CreateGEP(builder.getInt8Ty(), call->getArgOperand(0),
                                              builder.getInt64(offsetof(EmuState, mem_op_type)));
                builder.CreateAlignedStore(builder.getInt8(static_cast<uint8_t>(PendingMemOp::None)), tag,
                                           llvm::Align(alignof(PendingMemOp)));
            }
            call->eraseFromParent();
        }
        function->eraseFromParent();
    }
}

}  // namespace

std::unique_ptr<llvm::Module> PrepareLLVMBlock(const LLVMOperandTemplates& templates, block_recipe::BlockKeyView key,
                                               llvm::StringRef entry_name, bool prune_dead_globals,
                                               llvm::StringRef custom_pipeline, bool fold_operand_loads) {
    auto module = CloneBlockModule(templates, std::span(&key, 1));
    if (!module) return {};
    auto* entry = module->getFunction("FiberLLVMEntry");
    if (!entry || !SpecializeBlockEntry(templates, *entry, key, fold_operand_loads)) return {};
    RemoveBlockMarkers(*module);
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

namespace {

bool CanInlinePageMember(const llvm::Function& entry) {
    for (const auto& instruction : llvm::instructions(entry)) {
        const auto* call = llvm::dyn_cast<llvm::CallBase>(&instruction);
        if (!call || call->isMustTailCall()) continue;
        const auto* callee = call->getCalledFunction();
        if (!callee) return false;
        const auto name = callee->getName();
        // These calls cannot run a guest observer or invalidate the page.
        // GetNextEip and ExecCountStorage receive the block itself, which is bound before inlining.
        if (!callee->isIntrinsic() && name != kWriteArithmeticFlags && name != kWriteLogicalFlags &&
            name != kReadFlag && name != "FiberLLVMRegionEdge" && name != "FiberLLVMRegionExit" &&
            name != "FiberLLVMRegionBase" && name != "_ZNK8fiberish10BasicBlock10GetNextEipEm" &&
            name != "_ZN8fiberish10BasicBlock16ExecCountStorageEv" && name != "memcpy" && name != "memmove" &&
            name != "memset" && name != "memcmp")
            return false;
    }
    return true;
}

llvm::Value* BlockPointerSlot(llvm::IRBuilder<>& builder, llvm::Value* state) {
    return builder.CreateGEP(builder.getInt8Ty(), state, builder.getInt64(offsetof(EmuState, last_block)));
}

void BindCurrentBlock(llvm::Function& entry) {
    const auto& layout = entry.getParent()->getDataLayout();
    auto* state = entry.getArg(0);
    // Template compilation may outline these EIP stores. Inline them first so
    // their last_block loads can be rebound to the current region member.
    llvm::SmallVector<llvm::CallInst*, 4> eip_stores;
    for (auto& instruction : llvm::instructions(entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&instruction);
        const auto* callee = call ? call->getCalledFunction() : nullptr;
        if (callee && (callee->getName() == "_ZN8fiberish8EmuState20sync_eip_to_op_startEPKNS_9DecodedOpE" ||
                       callee->getName() == "_ZN8fiberish8EmuState18sync_eip_to_op_endEPKNS_9DecodedOpE"))
            eip_stores.push_back(call);
    }
    for (auto* call : eip_stores) {
        llvm::InlineFunctionInfo info;
        llvm::InlineFunction(*call, info);
    }
    llvm::IRBuilder<> builder(&*entry.getEntryBlock().getFirstInsertionPt());
    auto* current = builder.CreateGEP(builder.getInt8Ty(), entry.getArg(1), builder.getInt64(-kFirstOpOffset));
    for (auto& instruction : llvm::instructions(entry)) {
        auto* load = llvm::dyn_cast<llvm::LoadInst>(&instruction);
        if (!load || !load->isSimple() || !load->getType()->isPointerTy()) continue;
        int64_t offset = 0;
        if (llvm::GetPointerBaseWithConstantOffset(load->getPointerOperand(), offset, layout) == state &&
            offset == offsetof(EmuState, last_block))
            load->replaceAllUsesWith(current);
    }
}

std::optional<uint32_t> ConstantPageOffset(llvm::Value* target, llvm::Value* page, unsigned depth = 0) {
    using namespace llvm::PatternMatch;
    if (target == page) return 0;
    if (depth == 4) return {};
    llvm::Value* base = nullptr;
    llvm::ConstantInt* constant = nullptr;
    if (match(target, m_c_Add(m_Value(base), m_ConstantInt(constant)))) {
        if (const auto offset = ConstantPageOffset(base, page, depth + 1))
            return *offset + uint32_t(constant->getZExtValue());
    } else if (match(target, m_c_Or(m_Value(base), m_ConstantInt(constant)))) {
        if (const auto offset = ConstantPageOffset(base, page, depth + 1)) {
            const auto bits = uint32_t(constant->getZExtValue());
            // The page and any known in-page offset occupy disjoint bits.
            if ((*offset | bits) <= mem::PAGE_MASK) return *offset | bits;
        }
    }
    return std::nullopt;
}

bool FoldRegionPageOffsets(llvm::Function& body, llvm::Value* page) {
    using namespace llvm::PatternMatch;
    bool changed = false;
    for (auto& instruction : llvm::instructions(body)) {
        llvm::Value* target = nullptr;
        if (!match(&instruction, m_Sub(m_Value(target), m_Specific(page)))) continue;
        if (const auto offset = ConstantPageOffset(target, page)) {
            instruction.replaceAllUsesWith(llvm::ConstantInt::get(instruction.getType(), *offset));
            changed = true;
        }
    }
    return changed;
}

struct RegionLabel {
    llvm::BasicBlock* label;
    llvm::SmallVector<llvm::PHINode*, 9> arguments;
    llvm::Value* member_record;
    uint32_t page_offset;
    uint32_t budget;
};

#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
bool IsRegionChainExit(const llvm::CallInst& call) {
    const auto* callee = call.getCalledFunction();
    return callee &&
           (callee->getName() == "FiberLLVMRegionChainSingle" || callee->getName() == "FiberLLVMRegionChainDouble");
}

void ShareRegionTailExits(llvm::Function& body, const llvm::DenseMap<llvm::CallInst*, llvm::Value*>& sources) {
    auto& module = *body.getParent();
    const auto* memory_exits = module.getNamedGlobal("FiberLLVMRegionMemoryExits");
    const auto* memory = memory_exits ? memory_exits->getInitializer() : nullptr;
    const std::array<const llvm::Value*, 5> targets = {
        module.getFunction("FiberLLVMRegionChainSingle"),   module.getFunction("FiberLLVMRegionChainDouble"),
        module.getFunction("FiberLLVMRegionReturn"),        memory ? memory->getAggregateElement(0u) : nullptr,
        memory ? memory->getAggregateElement(1u) : nullptr,
    };
    std::array<llvm::SmallVector<llvm::CallInst*, 8>, targets.size()> groups;
    for (auto& instruction : llvm::instructions(body)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&instruction);
        const auto* callee = call ? call->getCalledFunction() : nullptr;
        if (!callee || !call->isMustTailCall()) continue;
        const auto target = llvm::find(targets, callee);
        if (target != targets.end()) groups[target - targets.begin()].push_back(call);
    }
    for (auto& group : groups) {
        if (group.empty()) continue;
        auto* first = group.front();
        const bool chain = IsRegionChainExit(*first);
        const bool inline_chain = chain && llvm::all_of(group, [&](auto* call) { return sources.contains(call); });
        auto* state = body.getArg(0);
        const auto restore_source = [&](llvm::IRBuilder<>& builder, llvm::Value* record) {
            auto* block = builder.CreateLoad(builder.getPtrTy(), record);
            builder.CreateStore(block, BlockPointerSlot(builder, state));
        };
        if (chain && !inline_chain) {
            for (auto* call : group) {
                if (auto* record = sources.lookup(call)) {
                    llvm::IRBuilder<> builder(call);
                    restore_source(builder, record);
                }
            }
        }
        if (group.size() == 1 && !inline_chain) continue;
        auto* callee = first->getCalledFunction();
        auto* exit = llvm::BasicBlock::Create(body.getContext(), callee->getName() + ".shared", &body);
        llvm::IRBuilder<> builder(exit);
        llvm::SmallVector<llvm::PHINode*, 9> inputs;
        for (auto& arg : first->args()) inputs.push_back(builder.CreatePHI(arg->getType(), group.size()));
        auto* source = inline_chain ? builder.CreatePHI(builder.getPtrTy(), group.size()) : nullptr;
        llvm::SmallVector<llvm::Value*, 9> arguments(inputs.begin(), inputs.end());
        if (inline_chain) {
            // Keep a single use of the symbolic flags even after expanding the
            // resolver's link-hit and link-miss paths into this exit.
            auto pack = module.getOrInsertFunction(kPackRegionFlags, builder.getInt64Ty(), builder.getInt64Ty());
            arguments[6] = builder.CreateCall(pack, arguments[6]);
        }
        auto* tail = builder.CreateCall(first->getFunctionType(), callee, arguments);
        tail->setCallingConv(first->getCallingConv());
        tail->setTailCallKind(llvm::CallInst::TCK_MustTail);
        builder.CreateRet(tail);

        // The flags argument is still symbolic. DeferredFlags will split the
        // PHI and pack the selected fields once at the shared ABI boundary.
        for (auto* call : group) {
            auto* predecessor = call->getParent();
            for (auto [index, input] : llvm::enumerate(inputs))
                input->addIncoming(call->getArgOperand(index), predecessor);
            if (source) source->addIncoming(sources.lookup(call), predecessor);
            auto* ret = llvm::cast<llvm::ReturnInst>(predecessor->getTerminator());
            ret->eraseFromParent();
            call->eraseFromParent();
            builder.SetInsertPoint(predecessor);
            builder.CreateBr(exit);
        }

        if (!inline_chain) continue;
        llvm::InlineFunctionInfo info;
        if (!llvm::InlineFunction(*tail, info).isSuccess()) {
            builder.SetInsertPoint(tail);
            restore_source(builder, source);
            continue;
        }
        if (auto* restore = module.getFunction("FiberLLVMRegionExit")) {
            for (auto* user : llvm::make_early_inc_range(restore->users())) {
                auto* call = llvm::cast<llvm::CallInst>(user);
                if (call->getFunction() != &body) continue;
                builder.SetInsertPoint(call);
                restore_source(builder, source);
                call->eraseFromParent();
            }
        }
    }
}
#endif

}  // namespace

std::unique_ptr<llvm::Module> PrepareLLVMPageRegion(const LLVMOperandTemplates& templates,
                                                    std::span<const LLVMPageMember> candidates, llvm::StringRef name,
                                                    llvm::StringRef pipeline, bool fold_operand_loads,
                                                    std::vector<size_t>& included) {
    using Region = mem::PageRegion<BasicBlock>;
    if (candidates.size() < 2 || candidates.size() > Region::MaxMembers) return {};

    llvm::SmallVector<BlockKeyView, 16> keys;
    for (const auto& candidate : candidates) keys.push_back(candidate.key);
    auto module = CloneBlockModule(templates, keys);
    if (!module || !module->getDataLayout().isLittleEndian()) return {};
    auto* pattern = module->getFunction("FiberLLVMEntry");
    if (!pattern) return {};

    llvm::LoopAnalysisManager loops;
    llvm::FunctionAnalysisManager functions;
    llvm::CGSCCAnalysisManager calls;
    llvm::ModuleAnalysisManager modules;
    llvm::PassBuilder passes;
    passes.registerModuleAnalyses(modules);
    passes.registerCGSCCAnalyses(calls);
    passes.registerFunctionAnalyses(functions);
    passes.registerLoopAnalyses(loops);
    passes.crossRegisterProxies(loops, functions, calls, modules);
    llvm::FunctionPassManager prepare;
    prepare.addPass(llvm::SROAPass(llvm::SROAOptions::ModifyCFG));
    prepare.addPass(llvm::InstCombinePass());
    prepare.addPass(llvm::SimplifyCFGPass());

    llvm::SmallVector<llvm::Function*, 16> members;
    included.clear();
    for (auto [index, candidate] : llvm::enumerate(candidates)) {
        llvm::ValueToValueMapTy values;
        auto* member = llvm::CloneFunction(pattern, values);
        member->setLinkage(llvm::GlobalValue::InternalLinkage);
        if (!SpecializeBlockEntry(templates, *member, candidate.key, fold_operand_loads, candidate.page_offset))
            return {};
        BindCurrentBlock(*member);
        prepare.run(*member, functions);
        if (!CanInlinePageMember(*member)) {
            functions.clear(*member, member->getName());
            member->eraseFromParent();
            continue;
        }
        included.push_back(index);
        members.push_back(member);
    }
    pattern->eraseFromParent();
    if (members.size() < 2) return {};

    auto* body = llvm::Function::Create(members[0]->getFunctionType(), llvm::GlobalValue::InternalLinkage,
                                        name + "_body", *module);
    body->setCallingConv(members[0]->getCallingConv());
    body->addFnAttr(llvm::Attribute::NoInline);
    auto& context = module->getContext();
    auto* start = llvm::BasicBlock::Create(context, "entry", body);
    auto* bad_selector = llvm::BasicBlock::Create(context, "invalid.selector", body);
    llvm::IRBuilder<>(bad_selector).CreateUnreachable();
    llvm::IRBuilder<> builder(start);
    auto* pointer_type = builder.getIntPtrTy(module->getDataLayout());
    auto* tagged_table = builder.CreatePtrToInt(body->getArg(1), pointer_type);
    auto* index = builder.CreateAnd(builder.CreateTruncOrBitCast(tagged_table, builder.getInt32Ty()),
                                    builder.getInt32(Region::MemberMask), "member.index");
    auto* table = builder.CreateIntToPtr(
        builder.CreateAnd(tagged_table, llvm::ConstantInt::get(pointer_type, ~uint64_t{Region::MemberMask})),
        builder.getPtrTy());
    auto* page_field = builder.CreateConstGEP1_64(builder.getInt8Ty(), table, offsetof(Region, guest_page));
    auto* page =
        builder.CreateTrunc(builder.CreateLoad(builder.getInt64Ty(), page_field), builder.getInt32Ty(), "guest.page");
    auto* state = body->getArg(0);
    auto* start_switch = builder.CreateSwitch(index, bad_selector, members.size());

    llvm::SmallVector<RegionLabel, 16> labels;
    llvm::SmallVector<llvm::CallInst*, 32> edges;
    llvm::DenseMap<llvm::CallInst*, size_t> exits;
    auto* edge_marker = module->getFunction("FiberLLVMRegionEdge");
    auto* exit_marker = module->getFunction("FiberLLVMRegionExit");

    for (auto [i, member] : llvm::enumerate(members)) {
        const auto& candidate = candidates[included[i]];
        auto* landing = llvm::BasicBlock::Create(context, "member", body);
        start_switch->addCase(builder.getInt32(i), landing);
        builder.SetInsertPoint(landing);
        RegionLabel label{landing, {}, nullptr, candidate.page_offset, uint32_t(candidate.key.operations.size())};
        llvm::SmallVector<llvm::Value*, 9> args;
        for (auto& arg : body->args()) {
            const auto number = arg.getArgNo();
            llvm::Value* supplied = &arg;
            llvm::PHINode* phi = nullptr;
            if (number == 1) {
                supplied = nullptr;
            } else if (number == 5) {
                supplied = builder.getInt32(UINT32_MAX);
            } else if (number != 0) {
                phi = builder.CreatePHI(arg.getType(), 4);
                phi->addIncoming(&arg, start);
                supplied = phi;
            }
            label.arguments.push_back(phi);
            args.push_back(supplied);
        }
        label.member_record = builder.CreateConstGEP1_64(builder.getPtrTy(), table, i);
        auto* block = builder.CreateLoad(builder.getPtrTy(), label.member_record);
        args[1] = builder.CreateGEP(builder.getInt8Ty(), block, builder.getInt64(kFirstOpOffset));
        auto* call = builder.CreateCall(member, args);
        call->setCallingConv(member->getCallingConv());
        call->setTailCallKind(llvm::CallInst::TCK_MustTail);
        builder.CreateRet(call);
        llvm::InlineFunctionInfo info;
        if (!llvm::InlineFunction(*call, info).isSuccess()) return {};
        labels.push_back(std::move(label));
        if (exit_marker) {
            for (auto* user : exit_marker->users()) {
                auto* exit = llvm::cast<llvm::CallInst>(user);
                if (exit->getFunction() == body) exits.try_emplace(exit, i);
            }
        }
    }
    if (edge_marker) {
        for (auto* user : edge_marker->users()) {
            auto* edge = llvm::cast<llvm::CallInst>(user);
            if (edge->getFunction() == body) edges.push_back(edge);
        }
    }

    if (auto* base_marker = module->getFunction("FiberLLVMRegionBase")) {
        for (auto* user : llvm::make_early_inc_range(base_marker->users())) {
            auto* call = llvm::cast<llvm::CallInst>(user);
            if (call->getFunction() != body) continue;
            call->replaceAllUsesWith(page);
            call->eraseFromParent();
        }
    }
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    llvm::DenseMap<llvm::CallInst*, llvm::Value*> chain_sources;
#endif
    for (auto [call, index] : exits) {
        builder.SetInsertPoint(call);
        auto* record = labels[index].member_record;
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
        auto* ret = llvm::dyn_cast<llvm::ReturnInst>(call->getParent()->getTerminator());
        auto* tail = ret ? llvm::dyn_cast_or_null<llvm::CallInst>(ret->getReturnValue()) : nullptr;
        if (tail && tail->isMustTailCall() && IsRegionChainExit(*tail)) {
            chain_sources.try_emplace(tail, record);
            call->eraseFromParent();
            continue;
        }
#endif
        auto* block = builder.CreateLoad(builder.getPtrTy(), record);
        builder.CreateStore(block, BlockPointerSlot(builder, state));
        call->eraseFromParent();
    }
    for (auto* call : edges) {
        const auto known_offset = ConstantPageOffset(call->getArgOperand(5), page);
        const auto known =
            known_offset ? std::ranges::find(labels, *known_offset, &RegionLabel::page_offset) : labels.end();
        if (!known_offset || known != labels.end()) {
            auto* predecessor = call->getParent();
            auto* external = predecessor->splitBasicBlock(call->getIterator(), "chain.external");
            auto* local = llvm::BasicBlock::Create(context, "chain.local", body, external);
            predecessor->getTerminator()->eraseFromParent();
            builder.SetInsertPoint(predecessor);
            auto* has_budget = builder.CreateICmpSGT(call->getArgOperand(2), builder.getInt64(0));
            builder.CreateCondBr(has_budget, local, external);

            const auto branch_to = [&](llvm::BasicBlock* edge, const RegionLabel& target) {
                builder.SetInsertPoint(edge);
                for (auto [arg, phi] : llvm::enumerate(target.arguments)) {
                    if (!phi) continue;
                    auto* value = call->getArgOperand(arg);
                    if (arg == 2) value = builder.CreateSub(value, builder.getInt64(target.budget));
                    phi->addIncoming(value, edge);
                }
                builder.CreateBr(target.label);
            };
            if (known != labels.end()) {
                branch_to(local, *known);
            } else {
                builder.SetInsertPoint(local);
                auto* offset = builder.CreateSub(call->getArgOperand(5), page);
                auto* select = builder.CreateSwitch(offset, external, labels.size());
                for (const auto& target : labels) {
                    auto* edge = llvm::BasicBlock::Create(context, "chain.hit", body, external);
                    select->addCase(builder.getInt32(target.page_offset), edge);
                    branch_to(edge, target);
                }
            }
        }
        // Region entry arrives with no pending replay. A memory miss terminates the
        // region and replays through the scalar handler before chaining again.
        call->eraseFromParent();
    }
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    ShareRegionTailExits(*body, chain_sources);
#endif

    for (auto [i, member] : llvm::enumerate(members)) {
        functions.clear(*member, member->getName());
        member->eraseFromParent();
        auto* wrapper = llvm::Function::Create(body->getFunctionType(), llvm::GlobalValue::ExternalLinkage,
                                               name + "_" + llvm::Twine(i), *module);
        wrapper->setCallingConv(body->getCallingConv());
        builder.SetInsertPoint(llvm::BasicBlock::Create(context, "entry", wrapper));
        llvm::SmallVector<llvm::Value*, 9> args;
        for (auto& arg : wrapper->args()) args.push_back(&arg);
        const auto& key = candidates[included[i]].key;
        size_t physical_count = 1;  // Include the sentinel.
        for (const auto& step : key.operations) physical_count += step.shape.slots;
        const auto context_offset =
            BasicBlock::SlotStorageSize(physical_count, physical_count > key.operations.size() + 1);
        auto* slot = builder.CreateGEP(builder.getInt8Ty(), args[1], builder.getInt64(context_offset));
        auto* table = builder.CreateAlignedLoad(builder.getPtrTy(), slot, llvm::Align(alignof(void*)));
        auto* address = builder.CreatePtrToInt(table, pointer_type);
        auto* tagged = builder.CreateOr(address, llvm::ConstantInt::get(pointer_type, i));
        args[1] = builder.CreateIntToPtr(tagged, builder.getPtrTy());
        auto* call = builder.CreateCall(body, args);
        call->setCallingConv(body->getCallingConv());
        call->setTailCallKind(llvm::CallInst::TCK_MustTail);
        builder.CreateRet(call);
    }

    RemoveBlockMarkers(*module);
    for (auto& function : *module) {
        if (function.isDeclaration()) continue;
        function.removeFnAttr(llvm::Attribute::UWTable);
        function.setDoesNotThrow();
        if (!function.getName().starts_with(name)) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    const llvm::WeakTrackingVH page_base(page);
    if (llvm::verifyModule(*module) || !OptimizeLLVMModule(*body, true, pipeline, fold_operand_loads)) return {};
    // CFG simplification can expose a constant target only after the region's edges have been built.
    if (page_base && FoldRegionPageOffsets(*body, page_base)) {
        llvm::FunctionPassManager cleanup;
        cleanup.addPass(llvm::InstCombinePass());
        cleanup.addPass(llvm::SimplifyCFGPass());
        cleanup.run(*body, functions);
    }
    if (llvm::verifyModule(*module)) return {};
    return module;
}

}  // namespace fiberish
