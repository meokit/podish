#include "jit/llvm_operand_ir.h"
#include "exec_utils.h"
#include "jit/llvm_ir_common.h"
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
#include <llvm/IR/CFG.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/MDBuilder.h>
#include <llvm/IR/PassManager.h>
#include <llvm/IR/PatternMatch.h>
#include <llvm/IR/ValueHandle.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/KnownBits.h>
#include <llvm/Transforms/InstCombine/InstCombine.h>
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

void LowerX86MemoryAccesses(llvm::Function& entry, llvm::Value* region_table = nullptr) {
    auto& module = *entry.getParent();
    auto* guest_address = module.getFunction("FiberLLVMGuestAddress");
    auto* chain_target = module.getFunction("FiberLLVMChainTarget");
    auto& context = entry.getContext();
    llvm::MDBuilder metadata(context);
    auto* domain = metadata.createAnonymousAliasScopeDomain("x86 JIT storage");
    const std::array scopes = {
        metadata.createAnonymousAliasScope(domain, "EmuState"),
        metadata.createAnonymousAliasScope(domain, "PageRegion"),
        metadata.createAnonymousAliasScope(domain, "guest RAM"),
        metadata.createAnonymousAliasScope(domain, "decoded blocks"),
    };
    auto* state = entry.getArg(0);
    const auto classify = [&](const llvm::Value* ptr) {
        llvm::SmallVector<const llvm::Value*, 4> objects;
        llvm::getUnderlyingObjects(ptr, objects);
        unsigned mask = 0;
        for (const auto* object : objects) {
            const auto* call = llvm::dyn_cast<llvm::CallInst>(object);
            if (object == state)
                mask |= 1;
            else if (object == region_table)
                mask |= 2;
            else if (guest_address && call && call->getCalledFunction() == guest_address)
                mask |= 4;
            else if (chain_target && call && call->getCalledFunction() == chain_target)
                mask |= 8;
            else if (!region_table && object == entry.getArg(1))
                mask |= 8;
            else if (const auto* load = llvm::dyn_cast<llvm::LoadInst>(object);
                     region_table && load && load->getMetadata(llvm::LLVMContext::MD_invariant_load) &&
                     llvm::getUnderlyingObject(load->getPointerOperand()) == region_table)
                mask |= 8;  // A pointer loaded from an immutable member slot.
            else
                return 0u;
        }
        return mask;
    };
    // Only annotate accesses whose storage is known. The volatile chain
    // check stays volatile, and calls still observe the published state.
    for (auto& inst : llvm::instructions(entry)) {
        unsigned mask = 0;
        if (auto* load = llvm::dyn_cast<llvm::LoadInst>(&inst); load && !load->isAtomic()) {
            mask = classify(load->getPointerOperand());
        } else if (auto* store = llvm::dyn_cast<llvm::StoreInst>(&inst); store && store->isSimple()) {
            mask = classify(store->getPointerOperand());
        } else if (auto* mem = llvm::dyn_cast<llvm::MemIntrinsic>(&inst); mem && !mem->isVolatile()) {
            mask = classify(mem->getRawDest());
            if (auto* transfer = llvm::dyn_cast<llvm::MemTransferInst>(mem)) {
                const auto source = classify(transfer->getRawSource());
                mask = mask && source ? mask | source : 0;
            }
        }
        if (!mask) continue;
        llvm::SmallVector<llvm::Metadata*, 4> alias, noalias;
        for (auto [i, scope] : llvm::enumerate(scopes)) ((mask & (1u << i)) ? alias : noalias).push_back(scope);
        for (auto [kind, list] : {std::pair{llvm::LLVMContext::MD_alias_scope, &alias},
                                  std::pair{llvm::LLVMContext::MD_noalias, &noalias}}) {
            if (list->empty()) continue;
            inst.setMetadata(kind,
                             llvm::MDNode::concatenate(inst.getMetadata(kind), llvm::MDNode::get(context, *list)));
        }
    }
    for (auto* marker : {guest_address, chain_target}) {
        if (!marker) continue;
        for (auto* user : llvm::make_early_inc_range(marker->users())) {
            auto* call = llvm::cast<llvm::CallInst>(user);
            llvm::IRBuilder<> builder(call);
            auto* value = call->getArgOperand(0);
            if (value->getType()->isIntegerTy()) value = builder.CreateIntToPtr(value, call->getType());
            call->replaceAllUsesWith(value);
            call->eraseFromParent();
        }
        marker->eraseFromParent();
    }
}

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
    // The placeholder includes the three template operands followed by the
    // logic ABI. ModRM is a constant in both generic and exact blocks.
    bind(7, binding.modrm);
    if (shape) {
        bind(0, shape->encoding);
        bind(1, shape->ea_desc);
        bind(2, shape->displacement);
        bind(6, shape->immediate);
    }
    llvm::InlineFunctionInfo info;
    return llvm::InlineFunction(call, info).isSuccess();
}

constexpr llvm::StringLiteral kWriteArithmeticFlags = "FiberLLVMWriteArithmeticFlags";
constexpr llvm::StringLiteral kWriteLogicalFlags = "FiberLLVMWriteLogicalFlags";
constexpr llvm::StringLiteral kWriteIncDecFlags = "FiberLLVMWriteIncDecFlags";
constexpr llvm::StringLiteral kReadFlag = "FiberLLVMReadFlag";
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
constexpr llvm::StringLiteral kPackRegionFlags = "FiberLLVMPackRegionFlags";
#endif

// Keep the arithmetic flag fields independent across control-flow joins.
// Their positions are the same as in the ABI word, so packing is just an OR.
constexpr uint64_t kFlagsArithmeticMask = uint64_t(ARITHMETIC_FLAGS_MASK) | FLAGS_CACHE_PF_STATE_MASK;
constexpr uint64_t kFlagsBaseMask = ~kFlagsArithmeticMask;

constexpr std::array kFlagFields = {
    kFlagsBaseMask,    uint64_t(CF_MASK), uint64_t(ZF_MASK),         uint64_t(SF_MASK),
    uint64_t(OF_MASK), uint64_t(AF_MASK), FLAGS_CACHE_PF_STATE_MASK,
};

bool IsFlagWriter(llvm::StringRef name) {
    return name == kWriteArithmeticFlags || name == kWriteLogicalFlags || name == kWriteIncDecFlags;
}

template <typename Materialize>
void AddFlagPhiInputs(llvm::PHINode& flags, llvm::PHINode& part, Materialize materialize) {
    for (auto [index, incoming] : llvm::enumerate(flags.incoming_values())) {
        auto* predecessor = flags.getIncomingBlock(index);
        llvm::IRBuilder<> edge(predecessor->getTerminator());
        // Multiple switch edges from the same block must reuse the same SSA value.
        const auto previous = part.getBasicBlockIndex(predecessor);
        auto* value = previous < 0 ? materialize(incoming, edge) : part.getIncomingValue(previous);
        part.addIncoming(value, predecessor);
    }
}

class DeferredFlags {
public:
    void Lower(llvm::Function& entry);

private:
    struct ValueFields {
        llvm::CallInst* producer = nullptr;
        llvm::Value* arithmetic = nullptr;
        llvm::Value* logical_result = nullptr;
        llvm::ConstantInt* logical_width = nullptr;
        std::array<llvm::Value*, kFlagFields.size()> values{};
    };

    llvm::Value* Field(llvm::Value* packed, size_t index, llvm::IRBuilder<>& builder);
    static llvm::Value* ResultField(llvm::Value* result, llvm::Value* width, uint64_t mask, llvm::IRBuilder<>& builder,
                                    llvm::Value* decrement = nullptr);
    static llvm::Value* ResultFlags(llvm::Value* result, llvm::Value* width, llvm::IRBuilder<>& builder,
                                    llvm::Value* decrement = nullptr);
    llvm::Value* Arithmetic(llvm::Value* packed, llvm::IRBuilder<>& builder);
    llvm::Value* MergeLogicalResults(llvm::PHINode& phi, ValueFields& fields);
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
        AddFlagPhiInputs(*phi, *part,
                         [&](llvm::Value* incoming, llvm::IRBuilder<>& edge) { return Field(incoming, index, edge); });
    } else if (auto* choice = llvm::dyn_cast<llvm::SelectInst>(packed)) {
        auto* yes = Field(choice->getTrueValue(), index, builder);
        auto* no = Field(choice->getFalseValue(), index, builder);
        cached = builder.CreateSelect(choice->getCondition(), yes, no);
    } else {
        auto& call = *fields.producer;
        auto* value = call.getArgOperand(1);
        const auto name = call.getCalledFunction()->getName();
        if (mask == kFlagsBaseMask) {
            cached = Field(call.getArgOperand(0), index, builder);
        } else if (name == kWriteArithmeticFlags) {
            if (mask == FLAGS_CACHE_PF_STATE_MASK) {
                cached = builder.CreateShl(builder.CreateZExt(call.getArgOperand(2), builder.getInt64Ty()),
                                           FLAGS_CACHE_PF_STATE_SHIFT);
            } else {
                cached = builder.CreateZExt(builder.CreateAnd(value, uint32_t(mask)), builder.getInt64Ty());
            }
        } else if (name == kWriteIncDecFlags && mask == CF_MASK) {
            cached = Field(call.getArgOperand(0), index, builder);
        } else {
            auto* width = call.getArgOperand(2);
            auto* decrement = name == kWriteIncDecFlags ? call.getArgOperand(3) : nullptr;
            cached = ResultField(value, width, mask, builder, decrement);
        }
    }
    return cached;
}

llvm::Value* DeferredFlags::ResultField(llvm::Value* result, llvm::Value* width, uint64_t mask,
                                        llvm::IRBuilder<>& builder, llvm::Value* decrement) {
    if (mask == ZF_MASK) {
        auto* zero = builder.CreateICmpEQ(result, builder.getInt32(0));
        return builder.CreateSelect(zero, builder.getInt64(ZF_MASK), builder.getInt64(0));
    }
    if (mask == SF_MASK) {
        auto* shift = builder.CreateSub(width, builder.getInt32(8));
        auto* sign = builder.CreateAnd(builder.CreateLShr(result, shift), SF_MASK);
        return builder.CreateZExt(sign, builder.getInt64Ty());
    }
    if (mask == FLAGS_CACHE_PF_STATE_MASK) {
        auto* byte = builder.CreateTrunc(result, builder.getInt8Ty());
        return builder.CreateShl(builder.CreateZExt(byte, builder.getInt64Ty()), FLAGS_CACHE_PF_STATE_SHIFT);
    }
    if (decrement && mask == OF_MASK) {
        // INC overflows to the signed minimum and DEC to the signed maximum.
        auto* top_bit = builder.CreateSub(width, builder.getInt32(1));
        auto* sign = builder.CreateShl(builder.getInt32(1), top_bit);
        auto* limit = builder.CreateSelect(decrement, builder.CreateSub(sign, builder.getInt32(1)), sign);
        return builder.CreateSelect(builder.CreateICmpEQ(result, limit), builder.getInt64(OF_MASK),
                                    builder.getInt64(0));
    }
    if (decrement && mask == AF_MASK) {
        // The result's low nibble is 0 after a carry and 15 after a borrow.
        auto* nibble = builder.CreateAnd(result, 15);
        auto* limit = builder.CreateSelect(decrement, builder.getInt32(15), builder.getInt32(0));
        return builder.CreateSelect(builder.CreateICmpEQ(nibble, limit), builder.getInt64(AF_MASK),
                                    builder.getInt64(0));
    }
    return builder.getInt64(0);
}

llvm::Value* DeferredFlags::ResultFlags(llvm::Value* result, llvm::Value* width, llvm::IRBuilder<>& builder,
                                        llvm::Value* decrement) {
    llvm::Value* word = builder.getInt64(0);
    for (auto mask : {uint64_t(ZF_MASK), uint64_t(SF_MASK), FLAGS_CACHE_PF_STATE_MASK})
        word = builder.CreateOr(word, ResultField(result, width, mask, builder));
    if (decrement) {
        for (auto mask : {uint64_t(OF_MASK), uint64_t(AF_MASK)})
            word = builder.CreateOr(word, ResultField(result, width, mask, builder, decrement));
    }
    return word;
}

// A join between a logical writer and an incoming ABI word can carry the
// result of the guest operation across the hot edge. Tag the incoming
// arithmetic bits in bit 63, which is outside their mask.
llvm::Value* DeferredFlags::MergeLogicalResults(llvm::PHINode& phi, ValueFields& fields) {
    if (fields.logical_result) return fields.logical_result;
    llvm::ConstantInt* width = nullptr;
    for (llvm::Value* incoming : phi.incoming_values()) {
        if (incoming == &phi) continue;
        const auto found = fields_.find(incoming);
        if (found == fields_.end()) continue;
        const auto* call = found->second.producer;
        if (!call || call->getCalledFunction()->getName() != kWriteLogicalFlags) return nullptr;
        auto* bits = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(2));
        if (!bits || (width && width != bits)) return nullptr;
        width = bits;
    }
    if (!width) return nullptr;

    constexpr uint64_t tag = uint64_t{1} << 63;
    llvm::IRBuilder<> builder(&phi);
    auto* result = builder.CreatePHI(builder.getInt64Ty(), phi.getNumIncomingValues(), "flag.result");
    AddFlagPhiInputs(phi, *result, [&](llvm::Value* incoming, llvm::IRBuilder<>& edge) -> llvm::Value* {
        if (incoming == &phi) return result;
        if (const auto writer = fields_.find(incoming); writer != fields_.end())
            return edge.CreateZExt(writer->second.producer->getArgOperand(1), edge.getInt64Ty());
        auto* bits = edge.CreateAnd(incoming, edge.getInt64(kFlagsArithmeticMask));
        return edge.CreateOr(bits, edge.getInt64(tag));
    });
    fields.logical_width = width;
    return fields.logical_result = result;
}

llvm::Value* DeferredFlags::Arithmetic(llvm::Value* packed, llvm::IRBuilder<>& builder) {
    auto found = fields_.find(packed);
    if (found == fields_.end()) return builder.CreateAnd(packed, builder.getInt64(kFlagsArithmeticMask));
    auto& fields = found->second;
    if (fields.arithmetic) return fields.arithmetic;

    if (auto* phi = llvm::dyn_cast<llvm::PHINode>(packed)) {
        if (auto* encoded = MergeLogicalResults(*phi, fields)) {
            auto* result = builder.CreateTrunc(encoded, builder.getInt32Ty());
            auto* word = ResultFlags(result, fields.logical_width, builder);
            auto* raw = builder.CreateAnd(encoded, builder.getInt64(kFlagsArithmeticMask));
            return builder.CreateSelect(builder.CreateICmpSLT(encoded, builder.getInt64(0)), raw, word);
        }
        llvm::IRBuilder<> join(phi);
        auto* part = join.CreatePHI(join.getInt64Ty(), phi->getNumIncomingValues(), "arithmetic.flags");
        fields.arithmetic = part;
        AddFlagPhiInputs(*phi, *part,
                         [&](llvm::Value* incoming, llvm::IRBuilder<>& edge) { return Arithmetic(incoming, edge); });
        return part;
    }
    if (auto* choice = llvm::dyn_cast<llvm::SelectInst>(packed)) {
        // Both input words are already available before the select.
        llvm::IRBuilder<> select(choice);
        auto* yes = Arithmetic(choice->getTrueValue(), select);
        auto* no = Arithmetic(choice->getFalseValue(), select);
        return fields.arithmetic = select.CreateSelect(choice->getCondition(), yes, no);
    }

    auto& call = *fields.producer;
    const auto name = call.getCalledFunction()->getName();
    auto* result = call.getArgOperand(1);
    if (name == kWriteArithmeticFlags) {
        // The kernel supplied a packed set of low bits. Keep it together at a
        // full-word use, even if a separate consumer also requested one bit.
        auto* parity = builder.CreateZExt(call.getArgOperand(2), builder.getInt64Ty());
        return builder.CreateOr(builder.CreateZExt(result, builder.getInt64Ty()),
                                builder.CreateShl(parity, FLAGS_CACHE_PF_STATE_SHIFT));
    }
    auto* width = call.getArgOperand(2);
    if (name == kWriteLogicalFlags) return ResultFlags(result, width, builder);

    constexpr auto carry_index = std::ranges::find(kFlagFields, CF_MASK) - kFlagFields.begin();
    auto* carry = Field(call.getArgOperand(0), carry_index, builder);
    return builder.CreateOr(carry, ResultFlags(result, width, builder, call.getArgOperand(3)));
}

llvm::Value* DeferredFlags::Pack(llvm::Value* packed, llvm::IRBuilder<>& builder) {
    // Keep sticky bits and arithmetic bits independent through region joins.
    auto* base = Field(packed, 0, builder);
    return builder.CreateOr(base, Arithmetic(packed, builder));
}

void DeferredFlags::Lower(llvm::Function& entry) {
    llvm::SmallVector<llvm::Value*, 16> deferred;
    llvm::SmallVector<llvm::CallInst*, 16> reads;
    for (auto& instruction : llvm::instructions(entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&instruction);
        const auto* fn = call ? call->getCalledFunction() : nullptr;
        if (!fn) continue;
        if (IsFlagWriter(fn->getName())) {
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

bool OptimizeX86Module(llvm::Function& entry, bool prune_dead_globals, llvm::StringRef custom_pipeline,
                       bool fold_operand_loads, llvm::function_ref<bool()> should_cancel = {}) {
    const auto pre = [](llvm::Function& fn, llvm::FunctionAnalysisManager& functions) {
        // Promote the C++ flags reference before lowering its symbolic producers.
        llvm::FunctionPassManager promote;
        promote.addPass(llvm::SROAPass(llvm::SROAOptions::ModifyCFG));
        promote.addPass(llvm::SimplifyCFGPass());
        promote.run(fn, functions);
        DeferredFlags().Lower(fn);
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
        if (auto* pack = fn.getParent()->getFunction(kPackRegionFlags)) {
            for (auto* user : llvm::make_early_inc_range(pack->users())) {
                auto* call = llvm::cast<llvm::CallInst>(user);
                call->replaceAllUsesWith(call->getArgOperand(0));
                call->eraseFromParent();
            }
            pack->eraseFromParent();
        }
#endif
    };
    const auto post = [](llvm::Function& fn, llvm::FunctionAnalysisManager& functions) {
        // Work on the optimized SSA so the result is independent of which guest
        // kernels supplied or consumed this bit. Packed exit values stay live.
        llvm::FunctionPassManager entry_passes;
        entry_passes.addPass(ForwardPackedBitTestsPass());
        entry_passes.run(fn, functions);
    };
    return OptimizeLLVMFunction(entry, {
                                           .prune_dead_globals = prune_dead_globals,
                                           .custom_pipeline = custom_pipeline,
                                           .fold_operand_loads = fold_operand_loads,
                                           .should_cancel = should_cancel,
                                           .pre_pipeline = pre,
                                           .post_pipeline = post,
                                       });
}

}  // namespace

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

bool KernelAlwaysContinues(const llvm::Function& kernel) {
    bool saw_return = false;
    for (const auto& block : kernel) {
        const auto* ret = llvm::dyn_cast<llvm::ReturnInst>(block.getTerminator());
        if (!ret) continue;
        saw_return = true;
        const auto* value = llvm::dyn_cast_or_null<llvm::ConstantInt>(ret->getReturnValue());
        if (!value || !value->isZero()) return false;
    }
    return saw_return;
}

std::unique_ptr<llvm::Module> CloneBlockModule(const LLVMOperandTemplates& templates,
                                               std::span<const BlockKeyView> keys) {
    if (!templates.IsValid()) return {};
    const auto& source = templates.Module();
    const auto* step_full = source.getFunction("FiberLLVMStep");
    const auto* step_continue = source.getFunction("FiberLLVMStepContinue");
    const auto* tail = source.getFunction("FiberLLVMTail");
    if (!step_full || !step_continue || !tail) return {};
    llvm::SmallSetVector<const llvm::GlobalValue*, 32> roots;
    roots.insert(tail);
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    // The region builder reads this table directly when grouping memory exits.
    if (keys.size() > 1)
        if (const auto* memory_exits = source.getNamedGlobal("FiberLLVMRegionMemoryExits")) roots.insert(memory_exits);
#endif
    bool need_full_step = false;
    bool need_continue_step = false;
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
            if (KernelAlwaysContinues(*kernel))
                need_continue_step = true;
            else
                need_full_step = true;
        }
    }
    if (need_full_step) roots.insert(step_full);
    if (need_continue_step) roots.insert(step_continue);
    auto module = KernelModule().Clone(source, roots.getArrayRef());
    // The catalog normally stays in the source module. Let DCE remove it
    // if LLVM had to clone the entire module for module-level constructs.
    if (auto* table = module->getNamedGlobal("FiberLLVMOperationTable"))
        table->setLinkage(llvm::GlobalValue::InternalLinkage);
    return module;
}

llvm::Function* BuildBlockEntry(const LLVMOperandTemplates& templates, llvm::Module& module, llvm::StringRef entry_name,
                                llvm::GlobalValue::LinkageTypes linkage, BlockKeyView key, bool fold_operand_loads,
                                std::optional<uint32_t> member_offset = {}) {
    auto* tail_fn = module.getFunction("FiberLLVMTail");
    if (!tail_fn) return nullptr;
    auto* step_full = module.getFunction("FiberLLVMStep");
    auto* step_continue = module.getFunction("FiberLLVMStepContinue");
    auto* next_step_fn = module.getFunction("FiberLLVMNextStep");
    auto* placeholder_fn = module.getFunction("FiberLLVMPlaceholder");
    auto* operand_layout_fn = module.getFunction("FiberLLVMOperandLayout");
    auto* branch_suffix_fn = module.getFunction("FiberLLVMBranchSuffix");
    auto* exit_kind_fn = module.getFunction("FiberLLVMExit");
    if (!exit_kind_fn) return nullptr;
    if (!key.operations.empty() && (!next_step_fn || !placeholder_fn || !operand_layout_fn)) return nullptr;

    auto* entry = llvm::Function::Create(tail_fn->getFunctionType(), linkage, entry_name, module);
    entry->copyAttributesFrom(tail_fn);
    entry->setLinkage(linkage);
    entry->setCallingConv(tail_fn->getCallingConv());

    auto& context = module.getContext();
    auto* start = llvm::BasicBlock::Create(context, "entry", entry);
    llvm::IRBuilder<> builder(start);
    llvm::SmallVector<llvm::Value*, 9> entry_args;
    for (auto& arg : entry->args()) entry_args.push_back(&arg);
    llvm::Function* initial_target = key.operations.empty() ? tail_fn : next_step_fn;
    auto* continuation = builder.CreateCall(initial_target, entry_args);
    continuation->setCallingConv(entry->getCallingConv());
    continuation->setTailCallKind(llvm::CallInst::TCK_MustTail);
    builder.CreateRet(continuation);

    const bool exact = key.IsExact();
    fold_operand_loads &= exact;
    std::array<size_t, block_recipe::kMaxOps> slot_offsets;
    size_t next_offset = 0;
    for (size_t i = 0; i < key.operations.size(); ++i) {
        slot_offsets[i] = next_offset;
        next_offset += key.operations[i].shape.slots * sizeof(DecodedOp);
    }

    const auto replace_in_entry = [&](llvm::Function* fn, auto&& callback) {
        if (!fn) return;
        for (auto* user : llvm::make_early_inc_range(fn->users())) {
            auto* call = llvm::dyn_cast<llvm::CallInst>(user);
            if (call && call->getFunction() == entry) callback(*call);
        }
    };

    for (size_t slot = 0; slot < key.operations.size(); ++slot) {
        const auto& operation = key.operations[slot];
        const auto* source = templates.Get(operation.id, exact);
        auto* target = source ? module.getFunction(source->getName()) : nullptr;
        if (!target) return nullptr;
        const bool always_continues = KernelAlwaysContinues(*target);
        auto* step_template = always_continues ? step_continue : step_full;
        if (!step_template) return nullptr;

        if (slot > 0) {
            llvm::IRBuilder<> pos(continuation);
            continuation->setArgOperand(
                1, pos.CreateInBoundsGEP(pos.getInt8Ty(), entry->getArg(1), pos.getInt64(slot_offsets[slot])));
        }
        continuation->setCalledFunction(step_template);
        llvm::InlineFunctionInfo step_info;
        if (!llvm::InlineFunction(*continuation, step_info).isSuccess()) return nullptr;

        const auto id = static_cast<size_t>(operation.id);
        const auto layout = block_recipe::kHasBranchLinkSlot[id] ? kLLVMBranchLinkSlot : operation.shape.slots;
        assert((layout & 3) == operation.shape.slots);
        replace_in_entry(operand_layout_fn, [&](llvm::CallInst& call) {
            call.replaceAllUsesWith(llvm::ConstantInt::get(call.getType(), layout));
            call.eraseFromParent();
        });
        const auto inst_count = key.operations.size() + key.uncompiled_ops;
        const auto remaining =
            block_recipe::kHasBranchLinkSlot[id] ? DecodedOp::BranchSuffixAfter(inst_count, slot) : 0;
        replace_in_entry(branch_suffix_fn, [&](llvm::CallInst& call) {
            call.replaceAllUsesWith(llvm::ConstantInt::get(call.getType(), remaining));
            call.eraseFromParent();
        });

        llvm::SmallVector<llvm::CallInst*, 2> sites;
        replace_in_entry(placeholder_fn, [&](llvm::CallInst& call) { sites.push_back(&call); });
        if (sites.size() != 1) return nullptr;
        if (fold_operand_loads || !key.eips.empty()) {
            llvm::IRBuilder<> pos(sites[0]);
            auto* op = pos.CreateInBoundsGEP(pos.getInt8Ty(), entry->getArg(1), pos.getInt64(slot_offsets[slot]));
            sites[0]->setArgOperand(4, op);  // DecodedOp* in LogicFuncParams.
        }
        if (!InlineLLVMKernel(*sites[0], *target, operation.shape, exact ? &key.operands[slot] : nullptr))
            return nullptr;

        llvm::SmallVector<llvm::CallInst*, 2> next_calls;
        replace_in_entry(next_step_fn, [&](llvm::CallInst& call) { next_calls.push_back(&call); });
        if (next_calls.size() != 1) return nullptr;
        continuation = next_calls[0];
    }

    if (!key.operations.empty()) {
        llvm::IRBuilder<> pos(continuation);
        continuation->setArgOperand(
            1, pos.CreateInBoundsGEP(pos.getInt8Ty(), entry->getArg(1), pos.getInt64(next_offset)));
        continuation->setCalledFunction(tail_fn);
    }
    llvm::InlineFunctionInfo tail_info;
    if (!llvm::InlineFunction(*continuation, tail_info).isSuccess()) return nullptr;
    replace_in_entry(exit_kind_fn, [&](llvm::CallInst& call) {
        call.replaceAllUsesWith(llvm::ConstantInt::get(call.getType(), static_cast<uint8_t>(key.exit)));
        call.eraseFromParent();
    });

#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    replace_in_entry(module.getFunction("FiberLLVMShareRegionExits"), [&](llvm::CallInst& call) {
        call.replaceAllUsesWith(llvm::ConstantInt::getBool(context, member_offset.has_value()));
        call.eraseFromParent();
    });
#endif
    LowerBlockEipReads(*entry, key, std::span(slot_offsets).first(key.operations.size()), member_offset);
    if (fold_operand_loads) BakeLLVMOperandLoads(*entry, key);
    return entry;
}

void RemoveBlockMarkers(llvm::Module& module) {
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
    if (auto* memory_exits = module.getNamedGlobal("FiberLLVMRegionMemoryExits")) memory_exits->eraseFromParent();
#endif
    for (const auto name :
         {"FiberLLVMStep", "FiberLLVMStepContinue", "FiberLLVMTail", "FiberLLVMNextStep", "FiberLLVMOperandLayout",
          "FiberLLVMBranchSuffix", "FiberLLVMPlaceholder", "FiberLLVMExit", "FiberLLVMShareRegionExits"}) {
        if (auto* fn = module.getFunction(name); fn && fn->use_empty()) fn->eraseFromParent();
    }
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
                                               llvm::StringRef custom_pipeline, bool fold_operand_loads,
                                               llvm::function_ref<bool()> should_cancel) {
    if (should_cancel && should_cancel()) return {};
    auto module = CloneBlockModule(templates, std::span(&key, 1));
    if (!module) return {};
    auto* entry =
        BuildBlockEntry(templates, *module, entry_name, llvm::GlobalValue::ExternalLinkage, key, fold_operand_loads);
    if (!entry) return {};
    RemoveBlockMarkers(*module);
    LowerX86MemoryAccesses(*entry);
    for (auto& function : *module) {
        if (&function != entry && !function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    if (!OptimizeX86Module(*entry, prune_dead_globals, custom_pipeline, fold_operand_loads, should_cancel) ||
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
        // GetNextEip receives the block itself, which is bound before inlining.
        if (!callee->isIntrinsic() && !IsFlagWriter(name) && name != kReadFlag && name != "FiberLLVMRegionEdge" &&
            name != "FiberLLVMRegionExit" && name != "FiberLLVMRegionBase" &&
            name != "_ZNK8fiberish10BasicBlock10GetNextEipEm" && name != "FiberLLVMGuestAddress" &&
            name != "FiberLLVMChainTarget" && name != "memcpy" && name != "memmove" && name != "memset" &&
            name != "memcmp")
            return false;
    }
    return true;
}

llvm::Value* BlockPointerSlot(llvm::IRBuilder<>& builder, llvm::Value* state) {
    return builder.CreateGEP(builder.getInt8Ty(), state, builder.getInt64(offsetof(EmuState, last_block)));
}

// A region can enter a cycle at any member. Promote independent context
// fields across these edges even when the CFG has no single loop header.
void PromoteRegionRegisters(llvm::Function& body, llvm::FunctionAnalysisManager& functions) {
    constexpr size_t kRegisterCount = sizeof(Context::regs) / sizeof(uint32_t);
    constexpr size_t kSegmentCount = sizeof(Context::seg_base) / sizeof(uint32_t);
    constexpr size_t kRegisterOffset = offsetof(EmuState, ctx) + offsetof(Context, regs);
    constexpr size_t kSegmentOffset = offsetof(EmuState, ctx) + offsetof(Context, seg_base);
    static constexpr auto kOffsets = [] {
        std::array<size_t, kRegisterCount + kSegmentCount> result{};
        for (size_t i = 0; i < kRegisterCount; ++i) result[i] = kRegisterOffset + i * sizeof(uint32_t);
        for (size_t i = 0; i < kSegmentCount; ++i) result[kRegisterCount + i] = kSegmentOffset + i * sizeof(uint32_t);
        return result;
    }();
    PromoteCyclicStateFields(body,
                             {
                                 .field_bits = 32,
                                 .offsets = kOffsets,
                                 .use_alias_analysis = true,
                                 .clear_aa_metadata = true,
                             },
                             &functions);
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

llvm::Value* RegionEdgeTarget(llvm::CallInst& edge) {
    auto* target = edge.getArgOperand(5);
    auto* choice = llvm::dyn_cast<llvm::SelectInst>(target);
    if (!choice) return target;
    // A conditional branch may select an unused EIP for the other edge.
    // Resolve the select while building the region so dead predecessors
    // cannot keep flag state live through the resulting PHIs.
    auto* block = edge.getParent();
    for (unsigned depth = 0; depth < 4; ++depth) {
        auto* predecessor = block->getSinglePredecessor();
        if (!predecessor) break;
        auto* branch = llvm::dyn_cast<llvm::BranchInst>(predecessor->getTerminator());
        if (!branch) break;
        if (branch->isConditional() && branch->getCondition() == choice->getCondition() &&
            branch->getSuccessor(0) != branch->getSuccessor(1))
            return branch->getSuccessor(0) == block ? choice->getTrueValue() : choice->getFalseValue();
        block = predecessor;
    }
    return target;
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
            auto* block = LoadInvariantRegionField(builder, builder.getPtrTy(), record);
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
                                                    std::vector<size_t>& included,
                                                    llvm::function_ref<bool()> should_cancel) {
    if (should_cancel && should_cancel()) return {};
    using Region = mem::PageRegion<BasicBlock>;
    if (candidates.size() < 2 || candidates.size() > Region::MaxMembers) return {};

    llvm::SmallVector<BlockKeyView, 16> keys;
    for (const auto& candidate : candidates) keys.push_back(candidate.key);
    auto module = CloneBlockModule(templates, keys);
    if (!module || !module->getDataLayout().isLittleEndian()) return {};

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
        if (should_cancel && should_cancel()) return {};
        auto* member = BuildBlockEntry(templates, *module, "region.member." + std::to_string(index),
                                       llvm::GlobalValue::InternalLinkage, candidate.key, fold_operand_loads,
                                       candidate.page_offset);
        if (!member) return {};
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
    if (members.size() < 2) return {};

    auto* body = llvm::Function::Create(members[0]->getFunctionType(), llvm::GlobalValue::InternalLinkage,
                                        name + "_body", *module);
    body->setCallingConv(members[0]->getCallingConv());
    body->addFnAttr(llvm::Attribute::NoInline);
    body->getArg(0)->addAttr(llvm::Attribute::NoAlias);
    body->addDereferenceableParamAttr(0, sizeof(Context));
    body->getArg(1)->addAttr(llvm::Attribute::NoAlias);
    auto& context = module->getContext();
    auto* start = llvm::BasicBlock::Create(context, "entry", body);
    auto* bad_selector = llvm::BasicBlock::Create(context, "invalid.selector", body);
    llvm::IRBuilder<>(bad_selector).CreateUnreachable();
    llvm::IRBuilder<> builder(start);
    const auto [index, table, page] =
        DecodeRegionSelector(builder, body->getArg(1), Region::MemberMask, offsetof(Region, guest_page),
                             builder.getInt32Ty(), builder.getInt32Ty());
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
        auto* block = LoadInvariantRegionField(builder, builder.getPtrTy(), label.member_record);
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
        auto* block = LoadInvariantRegionField(builder, builder.getPtrTy(), record);
        builder.CreateStore(block, BlockPointerSlot(builder, state));
        call->eraseFromParent();
    }
    for (auto* call : edges) {
        const auto known_offset = ConstantPageOffset(RegionEdgeTarget(*call), page);
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

    llvm::SmallVector<size_t, 16> wrapper_offsets;
    wrapper_offsets.reserve(members.size());
    for (auto [i, member] : llvm::enumerate(members)) {
        functions.clear(*member, member->getName());
        member->eraseFromParent();
        const auto& key = candidates[included[i]].key;
        size_t physical_count = 1;  // Include the sentinel.
        for (const auto& step : key.operations) physical_count += step.shape.slots;
        wrapper_offsets.push_back(
            BasicBlock::SlotStorageSize(physical_count, physical_count > key.operations.size() + 1));
    }
    EmitRegionEntryWrappers(*module, *body, name, wrapper_offsets);

    RemoveBlockMarkers(*module);
    LowerX86MemoryAccesses(*body, table);
    for (auto& function : *module) {
        if (function.isDeclaration()) continue;
        function.removeFnAttr(llvm::Attribute::UWTable);
        function.setDoesNotThrow();
        if (!function.getName().starts_with(name)) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    const llvm::WeakTrackingVH page_base(page);
    if (llvm::verifyModule(*module) || !OptimizeX86Module(*body, true, pipeline, fold_operand_loads, should_cancel))
        return {};
    if (should_cancel && should_cancel()) return {};
    // CFG simplification can expose a constant target only after the region's edges have been built.
    if (page_base && FoldRegionPageOffsets(*body, page_base)) {
        llvm::FunctionPassManager cleanup;
        cleanup.addPass(llvm::InstCombinePass());
        cleanup.addPass(llvm::SimplifyCFGPass());
        cleanup.run(*body, functions);
    }
    PromoteRegionRegisters(*body, functions);
    if ((should_cancel && should_cancel()) || llvm::verifyModule(*module)) return {};
    return module;
}

}  // namespace fiberish
