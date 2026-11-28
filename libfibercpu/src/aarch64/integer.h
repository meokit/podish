#pragma once

#include <bit>
#include <limits>

#include "decoder.h"

#if defined(_MSC_VER)
#define A64_INTEGER_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_INTEGER_INLINE inline __attribute__((always_inline))
#else
#define A64_INTEGER_INLINE inline
#endif

namespace fiberish::aarch64::detail {

constexpr uint32_t Negative = 1u << 31;
constexpr uint32_t Zero = 1u << 30;
constexpr uint32_t Carry = 1u << 29;
constexpr uint32_t Overflow = 1u << 28;
constexpr uint32_t FlagMask = Negative | Zero | Carry | Overflow;

inline uint64_t ReadRegister(const State& state, unsigned reg, unsigned width, bool use_sp = false) {
    const auto value = reg < 31 ? state.x[reg] : use_sp ? state.sp : 0;
    return value & Ones(width);
}

inline void WriteRegister(State& state, unsigned reg, unsigned width, uint64_t value, bool use_sp = false) {
    value &= Ones(width);
    if (reg < 31)
        state.x[reg] = value;
    else if (use_sp)
        state.sp = value;
}

struct ScalarRegisters {
    State& state;

    A64_INTEGER_INLINE uint64_t Read(unsigned reg, unsigned width, bool use_sp = false) const {
        return ReadRegister(state, reg, width, use_sp);
    }

    A64_INTEGER_INLINE void Write(unsigned reg, unsigned width, uint64_t value, bool use_sp = false) const {
        WriteRegister(state, reg, width, value, use_sp);
    }
};

// Hot kernels update the continuation arguments until dispatch publishes them.
struct HotRegisters {
    State& state;
#define A64_PIN_FIELD(reg, slot) uint64_t& pin_##slot;
    FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_FIELD)
#undef A64_PIN_FIELD

    A64_INTEGER_INLINE uint64_t Read(unsigned reg, unsigned width, bool use_sp = false) const {
#define A64_PIN_READ(id, slot) reg == id && (id != 31 || use_sp) ? pin_##slot:
        const auto value = FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_READ) ReadRegister(state, reg, 64, use_sp);
#undef A64_PIN_READ
        return value & Ones(width);
    }

    A64_INTEGER_INLINE void Write(unsigned reg, unsigned width, uint64_t value, bool use_sp = false) const {
#define A64_PIN_WRITE(id, slot)              \
    if (reg == id && (id != 31 || use_sp)) { \
        pin_##slot = value & Ones(width);    \
        return;                              \
    }
        FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_WRITE)
#undef A64_PIN_WRITE
        WriteRegister(state, reg, width, value, use_sp);
    }
};

constexpr int64_t Signed(uint64_t value, unsigned width) {
    return std::bit_cast<int64_t>(SignExtend(value & Ones(width), width));
}

constexpr uint64_t Shift(uint64_t value, unsigned mode, unsigned amount, unsigned width) {
    switch (mode) {
        case 0:
            return (value << amount) & Ones(width);
        case 1:
            return value >> amount;
        case 2:
            return uint64_t(Signed(value, width) >> amount) & Ones(width);
        default:
            return RotateRight(value, amount, width);
    }
}

constexpr uint64_t Extend(uint64_t value, unsigned option, unsigned shift, unsigned width) {
    const auto size = 8u << (option & 3);
    value &= Ones(size);
    if (option & 4) value = SignExtend(value, size);
    return (value << shift) & Ones(width);
}

inline uint32_t NZ(uint64_t result, unsigned width) {
    return (result >> (width - 1) ? Negative : 0) | (result == 0 ? Zero : 0);
}

constexpr bool WritesIntegerNZCV(const DecodedInstruction& op) {
    switch (op.kind) {
        case Kind::AddSubImmediate:
        case Kind::AddSubShifted:
        case Kind::AddSubExtended:
        case Kind::AddSubCarry:
            return (op.operation & 1) != 0;
        case Kind::LogicalImmediate:
        case Kind::LogicalShifted:
            return (op.operation & 3) == 3;
        case Kind::ConditionalCompare:
            return true;
        default:
            return false;
    }
}

template <bool write_nzcv = true>
A64_INTEGER_INLINE uint64_t AddWithCarry(uint32_t& nzcv, uint64_t left, uint64_t right, unsigned width, bool carry,
                                         bool set_flags) {
    const auto result = (left + right + carry) & Ones(width);
    if constexpr (write_nzcv) {
        if (set_flags) {
            const auto sign = uint64_t{1} << (width - 1);
            const bool carry_out = result < left || (carry && result == left);
            const bool signed_overflow = ((left ^ result) & (right ^ result) & sign) != 0;
            nzcv = NZ(result, width) | (carry_out ? Carry : 0) | (signed_overflow ? Overflow : 0);
        }
    }
    return result;
}

#if defined(__aarch64__) && (defined(__GNUC__) || defined(__clang__)) && !defined(_MSC_VER)
// W writes zero-extend the machine result. Arithmetic consumes all inputs
// before writing its result, and MRS produces the separate flags output.
#define A64_NATIVE_FLAGS(instruction, width, right_constraint)       \
    asm(instruction " %" width "[result], %" width "[left], %" width \
                    "[right]\n\t"                                    \
                    "mrs %x[flags], nzcv"                            \
        : [result] "=r"(result), [flags] "=r"(flags)                 \
        : [left] "r"(left), [right] right_constraint(right)          \
        : "cc")

#define A64_NATIVE_CARRY_FLAGS(instruction, width)                                             \
    asm("msr nzcv, %x[flags]\n\t" instruction " %" width "[result], %" width "[left], %" width \
        "[right]\n\t"                                                                          \
        "mrs %x[flags], nzcv"                                                                  \
        : [result] "=r"(result), [flags] "+r"(flags)                                           \
        : [left] "r"(left), [right] "r"(right)                                                 \
        : "cc")

template <class Word, bool use_carry>
A64_INTEGER_INLINE Word NativeAddSubFlags(uint32_t& nzcv, Word left, Word right, bool subtract) {
    constexpr bool narrow = sizeof(Word) == sizeof(uint32_t);
    Word result;
    uint32_t flags;
    if constexpr (use_carry) {
        // MSR reads only bits 31:28. The high half of the input is immaterial.
        flags = nzcv;
        if (subtract) {
            if constexpr (narrow)
                A64_NATIVE_CARRY_FLAGS("sbcs", "w");
            else
                A64_NATIVE_CARRY_FLAGS("sbcs", "x");
        } else {
            if constexpr (narrow)
                A64_NATIVE_CARRY_FLAGS("adcs", "w");
            else
                A64_NATIVE_CARRY_FLAGS("adcs", "x");
        }
    } else if (subtract) {
        if constexpr (narrow)
            A64_NATIVE_FLAGS("subs", "w", "rI");
        else
            A64_NATIVE_FLAGS("subs", "x", "rI");
    } else {
        if constexpr (narrow)
            A64_NATIVE_FLAGS("adds", "w", "rI");
        else
            A64_NATIVE_FLAGS("adds", "x", "rI");
    }
    nzcv = flags;
    return result;
}

template <class Word>
A64_INTEGER_INLINE Word NativeAndFlags(uint32_t& nzcv, Word left, Word right) {
    Word result;
    uint32_t flags;
    if constexpr (sizeof(Word) == sizeof(uint32_t))
        A64_NATIVE_FLAGS("ands", "w", "r");
    else
        A64_NATIVE_FLAGS("ands", "x", "r");
    nzcv = flags;
    return result;
}

#undef A64_NATIVE_FLAGS
#undef A64_NATIVE_CARRY_FLAGS
#endif

template <bool use_carry, bool write_nzcv = true>
A64_INTEGER_INLINE uint64_t AddSub(uint32_t& nzcv, uint64_t left, uint64_t right, unsigned width, bool subtract,
                                   bool set_flags) {
#if defined(__aarch64__) && (defined(__GNUC__) || defined(__clang__)) && !defined(_MSC_VER)
    if constexpr (write_nzcv) {
        if (set_flags)
            return width == 32 ? NativeAddSubFlags<uint32_t, use_carry>(nzcv, left, right, subtract)
                               : NativeAddSubFlags<uint64_t, use_carry>(nzcv, left, right, subtract);
    }
#endif
    if (subtract) right ^= Ones(width);
    const bool carry = use_carry ? (nzcv & Carry) != 0 : subtract;
    return AddWithCarry<write_nzcv>(nzcv, left, right, width, carry, set_flags);
}

template <bool write_nzcv>
A64_INTEGER_INLINE uint64_t AndWithFlags(uint32_t& nzcv, uint64_t left, uint64_t right, unsigned width) {
    if constexpr (write_nzcv) {
#if defined(__aarch64__) && (defined(__GNUC__) || defined(__clang__)) && !defined(_MSC_VER)
        return width == 32 ? NativeAndFlags<uint32_t>(nzcv, left, right) : NativeAndFlags<uint64_t>(nzcv, left, right);
#else
        nzcv = NZ(left & right, width);
#endif
    }
    return left & right;
}

constexpr bool ConditionHolds(uint32_t flags, unsigned condition) {
    const bool negative = flags & Negative;
    const bool zero = flags & Zero;
    const bool carry = flags & Carry;
    const bool overflow = flags & Overflow;
    bool holds;
    switch (condition >> 1) {
        case 0:
            holds = zero;
            break;
        case 1:
            holds = carry;
            break;
        case 2:
            holds = negative;
            break;
        case 3:
            holds = overflow;
            break;
        case 4:
            holds = carry && !zero;
            break;
        case 5:
            holds = negative == overflow;
            break;
        case 6:
            holds = negative == overflow && !zero;
            break;
        default:
            return true;
    }
    return holds != bool(condition & 1);
}

inline uint64_t ReverseBits(uint64_t value, unsigned width) {
    value = ((value & 0x5555555555555555ULL) << 1) | ((value >> 1) & 0x5555555555555555ULL);
    value = ((value & 0x3333333333333333ULL) << 2) | ((value >> 2) & 0x3333333333333333ULL);
    value = ((value & 0x0f0f0f0f0f0f0f0fULL) << 4) | ((value >> 4) & 0x0f0f0f0f0f0f0f0fULL);
    return width == 32 ? std::byteswap(uint32_t(value)) : std::byteswap(value);
}

inline uint64_t DataOneSource(uint64_t value, unsigned operation, unsigned width) {
    switch (operation) {
        case 0:
            return ReverseBits(value, width);
        case 1:
            return ((value & 0x00ff00ff00ff00ffULL) << 8) | ((value >> 8) & 0x00ff00ff00ff00ffULL);
        case 2:
            return uint64_t(std::byteswap(uint32_t(value))) | (uint64_t(std::byteswap(uint32_t(value >> 32))) << 32);
        case 3:
            return std::byteswap(value);
        case 4:
            return width == 32 ? std::countl_zero(uint32_t(value)) : std::countl_zero(value);
        default:
            if (value >> (width - 1)) value ^= Ones(width);
            return (width == 32 ? std::countl_zero(uint32_t(value)) : std::countl_zero(value)) - 1;
    }
}

A64_INTEGER_INLINE uint32_t Crc32(uint32_t accum, uint64_t value, unsigned operation) {
    const auto polynomial = (operation & 4) ? 0x82f63b78u : 0xedb88320u;
    const unsigned size = 8u << (operation & 3);
    for (unsigned bit = 0; bit < size; ++bit) {
        const bool reduce = (accum ^ value) & 1;
        accum = (accum >> 1) ^ (reduce ? polynomial : 0);
        value >>= 1;
    }
    return accum;
}

A64_INTEGER_INLINE uint64_t DataTwoSource(uint64_t left, uint64_t right, unsigned operation, unsigned width) {
    switch (operation) {
        case 2:
            return right ? left / right : 0;
        case 3: {
            const auto dividend = Signed(left, width);
            const auto divisor = Signed(right, width);
            if (!divisor) return 0;
            if (dividend == std::numeric_limits<int64_t>::min() && divisor == -1) return left;
            return uint64_t(dividend / divisor);
        }
        case 8:
        case 9:
        case 10:
        case 11:
            return Shift(left, operation - 8, right & (width - 1), width);
        default:
            return Crc32(uint32_t(left), right, operation);
    }
}

template <class Registers>
A64_INTEGER_INLINE uint64_t Multiply(const Registers& registers, const DecodedInstruction& op) {
    const auto left = registers.Read(op.rn, op.width);
    const auto right = registers.Read(op.rm, op.width);
    if (op.operation == 2) {
        const auto product = __int128_t(Signed(left, 64)) * Signed(right, 64);
        return __uint128_t(product) >> 64;
    }
    if (op.operation == 6) return (__uint128_t(left) * right) >> 64;
    uint64_t product;
    if (op.operation == 1)
        product = uint64_t(Signed(left, 32) * Signed(right, 32));
    else if (op.operation == 5)
        product = uint64_t(uint32_t(left)) * uint32_t(right);
    else
        product = left * right;
    const auto accum = registers.Read(op.ra, op.width);
    return op.mode ? accum - product : accum + product;
}

constexpr StepResult Branch(uint64_t address) { return {Stop::Branch, Access::Execute, 0, address}; }

constexpr bool IsConditionalBranch(Kind kind) { return kind >= Kind::BranchCondition && kind <= Kind::TestBranch; }

constexpr bool HasBlockTarget(Kind kind) {
    switch (kind) {
        case Kind::Address:
        case Kind::BranchImmediate:
        case Kind::BranchCondition:
        case Kind::CompareBranch:
        case Kind::TestBranch:
        case Kind::LoadLiteral:
            return true;
        default:
            return false;
    }
}

inline void BindBlockOperands(DecodedInstruction& op, uint64_t pc) {
    if (!HasBlockTarget(op.kind)) return;
    const auto base = op.kind == Kind::Address && op.operation ? pc & ~uint64_t{4095} : pc;
    op.target_pc = base + op.immediate;
}

template <Kind kind>
A64_INTEGER_INLINE uint64_t BlockTarget(const DecodedInstruction& op) {
    if constexpr (HasBlockTarget(kind)) return op.target_pc;
    return 0;
}

template <bool bound_pc>
A64_INTEGER_INLINE uint64_t RelativeTarget(uint64_t base, uint64_t immediate, uint64_t bound_target) {
    if constexpr (bound_pc) return bound_target;
    return base + immediate;
}

enum class IntegerForm { Default, Uxtb, Uxtw, RotateRight };

constexpr bool IsRotateImmediate(const DecodedInstruction& op) { return op.kind == Kind::Extract && op.rn == op.rm; }

template <Kind kind, unsigned fixed_width = 0, int fixed_operation = -1, int fixed_shift = -1, bool write_nzcv = true,
          bool bound_pc = false, IntegerForm form = IntegerForm::Default, class Registers>
A64_INTEGER_INLINE StepResult IntegerKernel(const Registers& registers, const DecodedInstruction& op, uint64_t pc,
                                            uint32_t& nzcv, uint64_t bound_target = 0) {
    const unsigned width = fixed_width == 0 ? op.width : fixed_width;
    const unsigned operation = fixed_operation < 0 ? op.operation : fixed_operation;
    const unsigned shift = fixed_shift < 0 ? op.shift : fixed_shift;
    if constexpr (kind == Kind::Address) {
        const auto base = operation ? pc & ~uint64_t{4095} : pc;
        registers.Write(op.rd, 64, RelativeTarget<bound_pc>(base, op.immediate, bound_target));
    } else if constexpr (kind == Kind::AddSubImmediate || kind == Kind::AddSubShifted || kind == Kind::AddSubExtended ||
                         kind == Kind::AddSubCarry) {
        constexpr bool use_sp = kind == Kind::AddSubImmediate || kind == Kind::AddSubExtended;
        const auto left = registers.Read(op.rn, width, use_sp);
        uint64_t right;
        if constexpr (kind == Kind::AddSubImmediate) {
            right = op.immediate;
        } else {
            right = registers.Read(op.rm, width);
            if constexpr (kind == Kind::AddSubShifted) {
                if (shift) right = Shift(right, op.mode, shift, width);
            }
            if constexpr (kind == Kind::AddSubExtended) {
                if constexpr (form == IntegerForm::Uxtb || form == IntegerForm::Uxtw) {
                    static_assert(fixed_shift == 0);
                    right = form == IntegerForm::Uxtb ? uint8_t(right) : uint32_t(right);
                } else {
                    right = Extend(right, op.mode, shift, width);
                }
            }
        }
        const bool subtract = operation & 2;
        const bool set_flags = operation & 1;
        const auto result =
            AddSub<kind == Kind::AddSubCarry, write_nzcv>(nzcv, left, right, width, subtract, set_flags);
        // The encoded S bit still selects XZR/WZR as destination for flag setters.
        registers.Write(op.rd, width, result, use_sp && !set_flags);
    } else if constexpr (kind == Kind::LogicalImmediate || kind == Kind::LogicalShifted) {
        const auto left = registers.Read(op.rn, width);
        auto right = op.immediate;
        if constexpr (kind == Kind::LogicalShifted) {
            right = registers.Read(op.rm, width);
            if (shift) right = Shift(right, op.mode, shift, width);
            if (operation & 4) right ^= Ones(width);
        }
        const auto logical = operation & 3;
        uint64_t result;
        switch (logical) {
            case 1:
                result = left | right;
                break;
            case 2:
                result = left ^ right;
                break;
            case 3:
                result = AndWithFlags<write_nzcv>(nzcv, left, right, width);
                break;
            default:
                result = left & right;
                break;
        }
        registers.Write(op.rd, width, result, kind == Kind::LogicalImmediate && logical != 3);
    } else if constexpr (kind == Kind::MoveWide) {
        auto value = op.immediate;
        if (operation == 0) value = ~value;
        if (operation == 3) {
            value |= registers.Read(op.rd, width) & ~(uint64_t{0xffff} << shift);
        }
        registers.Write(op.rd, width, value);
    } else if constexpr (kind == Kind::Bitfield) {
        const auto source = registers.Read(op.rn, width);
        const auto bottom = RotateRight(source, shift, width) & op.immediate;
        uint64_t result = bottom & op.mask;
        if (operation == 0 && ((source >> op.rm) & 1)) result |= ~op.mask;
        if (operation == 1) {
            const auto dest = registers.Read(op.rd, width);
            result = (dest & ~op.mask) | (((dest & ~op.immediate) | bottom) & op.mask);
        }
        registers.Write(op.rd, width, result);
    } else if constexpr (kind == Kind::Extract) {
        const auto low = registers.Read(op.rm, width);
        if constexpr (form == IntegerForm::RotateRight) {
            static_assert(fixed_width == 32 || fixed_width == 64);
            const auto result = fixed_width == 32 ? std::rotr(uint32_t(low), int(shift)) : std::rotr(low, int(shift));
            registers.Write(op.rd, width, result);
        } else {
            const auto high = registers.Read(op.rn, width);
            const auto result = shift ? (low >> shift) | (high << (width - shift)) : low;
            registers.Write(op.rd, width, result);
        }
    } else if constexpr (kind == Kind::ConditionalCompare) {
        if constexpr (write_nzcv) {
            if (ConditionHolds(nzcv, shift)) {
                const auto left = registers.Read(op.rn, width);
                const auto right = op.mode ? op.rm : registers.Read(op.rm, width);
                AddSub<false>(nzcv, left, right, width, operation != 0, true);
            } else {
                nzcv = uint32_t(op.immediate) << 28;
            }
        }
    } else if constexpr (kind == Kind::ConditionalSelect) {
        uint64_t result;
        if (ConditionHolds(nzcv, shift)) {
            result = registers.Read(op.rn, width);
        } else {
            result = registers.Read(op.rm, width);
            if (operation & 2) result = ~result;
            if (operation & 1) ++result;
        }
        registers.Write(op.rd, width, result);
    } else if constexpr (kind == Kind::DataOneSource) {
        const auto value = registers.Read(op.rn, width);
        registers.Write(op.rd, width, DataOneSource(value, operation, width));
    } else if constexpr (kind == Kind::DataTwoSource) {
        const auto left = registers.Read(op.rn, width);
        const auto right = registers.Read(op.rm, width);
        registers.Write(op.rd, operation >= 16 ? 32 : width, DataTwoSource(left, right, operation, width));
    } else if constexpr (kind == Kind::Multiply) {
        registers.Write(op.rd, width, Multiply(registers, op));
    } else if constexpr (kind == Kind::BranchImmediate || kind == Kind::BranchRegister) {
        const auto target = kind == Kind::BranchImmediate ? RelativeTarget<bound_pc>(pc, op.immediate, bound_target)
                                                          : registers.Read(op.rn, 64);
        if (operation) registers.Write(30, 64, pc + 4);
        return Branch(target);
    } else if constexpr (kind == Kind::BranchCondition) {
        if (ConditionHolds(nzcv, shift)) return Branch(RelativeTarget<bound_pc>(pc, op.immediate, bound_target));
    } else if constexpr (kind == Kind::CompareBranch) {
        const bool nonzero = registers.Read(op.rd, width) != 0;
        if (nonzero == bool(operation)) return Branch(RelativeTarget<bound_pc>(pc, op.immediate, bound_target));
    } else if constexpr (kind == Kind::TestBranch) {
        const bool bit = (registers.Read(op.rd, width) >> shift) & 1;
        if (bit == bool(operation)) return Branch(RelativeTarget<bound_pc>(pc, op.immediate, bound_target));
    }
    return {};
}

template <Kind kind, unsigned fixed_width = 0, int fixed_operation = -1, int fixed_shift = -1, bool write_nzcv = true,
          IntegerForm form = IntegerForm::Default>
inline StepResult Integer(State& state, const Memory&, const DecodedInstruction& op) {
    return IntegerKernel<kind, fixed_width, fixed_operation, fixed_shift, write_nzcv, false, form>(
        ScalarRegisters{state}, op, state.pc, state.nzcv);
}

template <Kind kind, unsigned fixed_width = 0, int fixed_operation = -1, int fixed_shift = -1, bool write_nzcv = true,
          IntegerForm form = IntegerForm::Default, bool TouchesPin = true>
A64_INTEGER_INLINE StepResult HotInteger(State& state, const DecodedInstruction& op, uint64_t pc, uint64_t bound_target,
                                         uint32_t& nzcv A64_HOT_PIN_REF) {
    if constexpr (TouchesPin) {
        const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
        return IntegerKernel<kind, fixed_width, fixed_operation, fixed_shift, write_nzcv, true, form>(
            registers, op, pc, nzcv, bound_target);
    } else {
        return IntegerKernel<kind, fixed_width, fixed_operation, fixed_shift, write_nzcv, true, form>(
            ScalarRegisters{state}, op, pc, nzcv, bound_target);
    }
}

}  // namespace fiberish::aarch64::detail

#undef A64_INTEGER_INLINE
