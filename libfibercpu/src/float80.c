#include "float80.h"
#include <math.h>
#include <string.h>

#ifdef X86EMU_USE_SOFTFLOAT
/**
 * Encapsulate SoftFloat 3 internals.
 * We rename the types to avoid naming collisions with system headers (e.g. float32_t on ARM).
 */
#define float16_t soft_float16_t
#define bfloat16_t soft_bfloat16_t
#define float32_t soft_float32_t
#define float64_t soft_float64_t
#define extFloat80_t soft_extFloat80_t

#ifndef SOFTFLOAT_FAST_INT64
#define SOFTFLOAT_FAST_INT64
#endif

#include "3rdparty/softfloat/platform.h"
#include "3rdparty/softfloat/softfloat.h"

#undef float16_t
#undef bfloat16_t
#undef float32_t
#undef float64_t
#undef extFloat80_t
#else
static THREAD_LOCAL int g_RoundingMode = 0;  // round_to_nearest
#endif

#ifdef X86EMU_USE_SOFTFLOAT
// Helper to cast between our float80 and SoftFloat's type.
// Layout is guaranteed to be identical in LITTLEENDIAN mode.
static inline soft_extFloat80_t to_soft(float80 f) {
    soft_extFloat80_t out;
    memcpy(&out, &f, sizeof(out));
    return out;
}

static inline float80 from_soft(soft_extFloat80_t f) {
    float80 out;
    memcpy(&out, &f, sizeof(out));
    return out;
}

static inline uint_fast8_t capture_softfloat_flags_begin(void) {
    uint_fast8_t saved_flags = softfloat_exceptionFlags;
    softfloat_exceptionFlags = 0;
    return saved_flags;
}

static inline uint_fast8_t capture_softfloat_flags_end(uint_fast8_t saved_flags) {
    uint_fast8_t op_flags = softfloat_exceptionFlags;
    softfloat_exceptionFlags = saved_flags | op_flags;
    return op_flags;
}

void f80_set_rounding_mode(enum f80_rounding_mode mode) {
    switch (mode) {
        case round_to_nearest:
            softfloat_roundingMode = softfloat_round_near_even;
            break;
        case round_down:
            softfloat_roundingMode = softfloat_round_min;
            break;
        case round_up:
            softfloat_roundingMode = softfloat_round_max;
            break;
        case round_chop:
            softfloat_roundingMode = softfloat_round_minMag;
            break;
    }
}
#else
void f80_set_rounding_mode(enum f80_rounding_mode mode) {
    g_RoundingMode = (int)mode;
    // We could use fesetround() here but it might affect other things.
    // For now we just store it for f80_to_int / f80_round.
}
#endif

#ifndef X86EMU_USE_SOFTFLOAT
static double round_double_for_int_store(double d, bool truncate) {
    if (truncate) return trunc(d);

    switch (g_RoundingMode) {
        case round_to_nearest:
            return rint(d);
        case round_down:
            return floor(d);
        case round_up:
            return ceil(d);
        case round_chop:
            return trunc(d);
        default:
            return d;
    }
}
#endif

#ifdef X86EMU_USE_SOFTFLOAT
float80 f80_from_int(int64_t i) { return from_soft(i64_to_extF80(i)); }

int64_t f80_to_int(float80 f) { return extF80_to_i64(to_soft(f), softfloat_roundingMode, true); }

float80 f80_from_double(double d) {
    union {
        double d;
        uint64_t u;
    } u;
    u.d = d;
    soft_float64_t f64 = {u.u};
    return from_soft(f64_to_extF80(f64));
}

double f80_to_double(float80 f) {
    soft_float64_t f64 = extF80_to_f64(to_soft(f));
    union {
        double d;
        uint64_t u;
    } u;
    u.u = f64.v;
    return u.d;
}
#else
float80 f80_from_int(int64_t i) { return f80_from_double((double)i); }

int64_t f80_to_int(float80 f) {
    double d = f80_to_double(f);
    switch (g_RoundingMode) {
        case round_to_nearest:
            return (int64_t)rint(d);
        case round_down:
            return (int64_t)floor(d);
        case round_up:
            return (int64_t)ceil(d);
        case round_chop:
            return (int64_t)trunc(d);
        default:
            return (int64_t)d;
    }
}

float80 f80_from_double(double d) {
    // 64-bit double to 80-bit float80
    // Sign bit
    uint16_t sign = signbit(d) ? 0x8000 : 0x0000;
    if (isnan(d)) return F80_NAN;
    if (isinf(d)) return sign ? f80_neg(F80_INF) : F80_INF;
    if (d == 0.0) return sign ? f80_neg(F80_ZERO) : F80_ZERO;

    int exp;
    double mantissa = frexp(fabs(d), &exp);

    float80 f;
    f.signif = (uint64_t)(mantissa * 2.0 * 9223372036854775808.0);  // manual scale
    // significand should have bit 63 set for normal numbers (except 0)
    f.signif = (uint64_t)(mantissa * 18446744073709551616.0);  // 2^64

    // Actually, frexp returns [0.5, 1). x87 normal is [1, 2).
    // So if mantissa is 0.5, signif is 0x8000... and exp is correct.
    f.signif = (uint64_t)(mantissa * 2.0 * 0x8000000000000000ULL);
    f.signExp = sign | (uint16_t)(exp + 16383 - 1);
    return f;
}

double f80_to_double(float80 f) {
    if (f80_isnan(f)) return NAN;
    if (f80_isinf(f)) return (f.signExp & 0x8000) ? -INFINITY : INFINITY;
    if (f80_iszero(f)) return (f.signExp & 0x8000) ? -0.0 : 0.0;

    double d = (double)f.signif / 18446744073709551616.0;  // signif / 2^64
    d *= 2.0;                                              // Now [1, 2)
    int exp = (int)(f.signExp & 0x7FFF) - 16383;
    d = ldexp(d, exp);
    if (f.signExp & 0x8000) d = -d;
    return d;
}
#endif

int32_t f80_to_int32_checked(float80 f, bool truncate, bool* invalid) {
#ifdef X86EMU_USE_SOFTFLOAT
    uint_fast8_t saved_flags = capture_softfloat_flags_begin();
    int_fast32_t value =
        truncate ? extF80_to_i32_r_minMag(to_soft(f), true) : extF80_to_i32(to_soft(f), softfloat_roundingMode, true);
    uint_fast8_t op_flags = capture_softfloat_flags_end(saved_flags);

    if (invalid) *invalid = (op_flags & softfloat_flag_invalid) != 0;
    return (int32_t)value;
#else
    double d = f80_to_double(f);
    double rounded = round_double_for_int_store(d, truncate);

    bool is_invalid =
        !isfinite(d) || !isfinite(rounded) || (rounded < (double)INT32_MIN) || (rounded > (double)INT32_MAX);
    if (invalid) *invalid = is_invalid;
    return is_invalid ? INT32_MIN : (int32_t)rounded;
#endif
}

int64_t f80_to_int64_checked(float80 f, bool truncate, bool* invalid) {
#ifdef X86EMU_USE_SOFTFLOAT
    uint_fast8_t saved_flags = capture_softfloat_flags_begin();
    int_fast64_t value =
        truncate ? extF80_to_i64_r_minMag(to_soft(f), true) : extF80_to_i64(to_soft(f), softfloat_roundingMode, true);
    uint_fast8_t op_flags = capture_softfloat_flags_end(saved_flags);

    if (invalid) *invalid = (op_flags & softfloat_flag_invalid) != 0;
    return (int64_t)value;
#else
    double d = f80_to_double(f);
    double rounded = round_double_for_int_store(d, truncate);

    bool is_invalid =
        !isfinite(d) || !isfinite(rounded) || (rounded < (double)INT64_MIN) || (rounded > (double)INT64_MAX);
    if (invalid) *invalid = is_invalid;
    return is_invalid ? INT64_MIN : (int64_t)rounded;
#endif
}

int16_t f80_to_int16_checked(float80 f, bool truncate, bool* invalid) {
#ifdef X86EMU_USE_SOFTFLOAT
    bool wide_invalid = false;
    int32_t wide = f80_to_int32_checked(f, truncate, &wide_invalid);
    bool is_invalid = wide_invalid || (wide < INT16_MIN) || (wide > INT16_MAX);

    if (!wide_invalid && is_invalid) {
        softfloat_exceptionFlags |= softfloat_flag_invalid;
    }

    if (invalid) *invalid = is_invalid;
    return is_invalid ? INT16_MIN : (int16_t)wide;
#else
    double d = f80_to_double(f);
    double rounded = round_double_for_int_store(d, truncate);

    bool is_invalid =
        !isfinite(d) || !isfinite(rounded) || (rounded < (double)INT16_MIN) || (rounded > (double)INT16_MAX);
    if (invalid) *invalid = is_invalid;
    return is_invalid ? INT16_MIN : (int16_t)rounded;
#endif
}

bool f80_isnan(float80 f) { return (f.signExp & 0x7FFF) == 0x7FFF && (f.signif & 0x7FFFFFFFFFFFFFFFUL) != 0; }

bool f80_is_signaling_nan(float80 f) {
#ifdef X86EMU_USE_SOFTFLOAT
    return extF80_isSignalingNaN(to_soft(f));
#else
    return ((f.signExp & 0x7FFF) == 0x7FFF) && ((f.signif & UINT64_C(0x4000000000000000)) == 0) &&
           ((f.signif & UINT64_C(0x3FFFFFFFFFFFFFFF)) != 0);
#endif
}

bool f80_isinf(float80 f) { return (f.signExp & 0x7FFF) == 0x7FFF && (f.signif & 0x7FFFFFFFFFFFFFFFUL) == 0; }

bool f80_iszero(float80 f) { return (f.signExp & 0x7FFF) == 0 && f.signif == 0; }

bool f80_isdenormal(float80 f) { return (f.signExp & 0x7FFF) == 0 && f.signif != 0; }

bool f80_is_supported(float80 f) {
    uint16_t exp = f.signExp & 0x7FFF;
    if (exp == 0) return true;
    return (f.signif >> 63) == 1;
}

float80 f80_neg(float80 f) {
    f.signExp ^= 0x8000;
    return f;
}

float80 f80_abs(float80 f) {
    f.signExp &= 0x7FFF;
    return f;
}

#ifdef X86EMU_USE_SOFTFLOAT
float80 f80_sqrt(float80 f) { return from_soft(extF80_sqrt(to_soft(f))); }

float80 f80_add(float80 a, float80 b) { return from_soft(extF80_add(to_soft(a), to_soft(b))); }

float80 f80_sub(float80 a, float80 b) { return from_soft(extF80_sub(to_soft(a), to_soft(b))); }

float80 f80_mul(float80 a, float80 b) { return from_soft(extF80_mul(to_soft(a), to_soft(b))); }

float80 f80_div(float80 a, float80 b) { return from_soft(extF80_div(to_soft(a), to_soft(b))); }

float80 f80_rem(float80 a, float80 b) { return from_soft(extF80_rem(to_soft(a), to_soft(b))); }

float80 f80_mod(float80 a, float80 b) {
    // x87 FPREM uses truncation, SoftFloat rem uses IEEE 754 (rounding).
    // For now we use IEEE rem, but a more accurate x87 FPREM might be needed.
    return from_soft(extF80_rem(to_soft(a), to_soft(b)));
}
#else
float80 f80_sqrt(float80 f) { return f80_from_double(sqrt(f80_to_double(f))); }
float80 f80_add(float80 a, float80 b) { return f80_from_double(f80_to_double(a) + f80_to_double(b)); }
float80 f80_sub(float80 a, float80 b) { return f80_from_double(f80_to_double(a) - f80_to_double(b)); }
float80 f80_mul(float80 a, float80 b) { return f80_from_double(f80_to_double(a) * f80_to_double(b)); }
float80 f80_div(float80 a, float80 b) { return f80_from_double(f80_to_double(a) / f80_to_double(b)); }
float80 f80_rem(float80 a, float80 b) { return f80_from_double(fmod(f80_to_double(a), f80_to_double(b))); }
float80 f80_mod(float80 a, float80 b) { return f80_from_double(fmod(f80_to_double(a), f80_to_double(b))); }
#endif

#ifdef X86EMU_USE_SOFTFLOAT
bool f80_lt(float80 a, float80 b) { return extF80_lt(to_soft(a), to_soft(b)); }

bool f80_eq(float80 a, float80 b) { return extF80_eq(to_soft(a), to_soft(b)); }
#else
bool f80_lt(float80 a, float80 b) { return f80_to_double(a) < f80_to_double(b); }
bool f80_eq(float80 a, float80 b) { return f80_to_double(a) == f80_to_double(b); }
#endif

bool f80_uncomparable(float80 a, float80 b) { return f80_isnan(a) || f80_isnan(b); }

float80 f80_round(float80 f) {
#ifdef X86EMU_USE_SOFTFLOAT
    return from_soft(extF80_roundToInt(to_soft(f), softfloat_roundingMode, true));
#else
    double d = f80_to_double(f);
    float80 out;
    switch (g_RoundingMode) {
        case round_to_nearest:
            out = f80_from_double(rint(d));
            break;
        case round_down:
            out = f80_from_double(floor(d));
            break;
        case round_up:
            out = f80_from_double(ceil(d));
            break;
        case round_chop:
            out = f80_from_double(trunc(d));
            break;
        default:
            out = f80_from_double(d);
            break;
    }
    return out;
#endif
}

float80 f80_scale(float80 x, int scale) {
    // x * 2^scale.
    // Naive implementation for now.
    if (scale > 16383) {
        return (x.signExp & 0x8000) ? f80_neg(F80_INF) : F80_INF;
    }
    if (scale < -16382) {
        return (x.signExp & 0x8000) ? f80_neg(F80_ZERO) : F80_ZERO;
    }

    float80 s;
    s.signif = 0x8000000000000000ULL;
    s.signExp = (uint16_t)(16383 + scale);
    return f80_mul(x, s);
}

void f80_xtract(float80 f, int* exp, float80* signif) {
    uint16_t bi_exp = f.signExp & 0x7FFF;
    *exp = (int)bi_exp - 16383;
    *signif = f;
    signif->signExp = (f.signExp & 0x8000) | 16383;
}

// transcendental/special via double
float80 f80_sin(float80 x) { return f80_from_double(sin(f80_to_double(x))); }

float80 f80_cos(float80 x) { return f80_from_double(cos(f80_to_double(x))); }

float80 f80_log2(float80 x) { return f80_from_double(log2(f80_to_double(x))); }

void f80_sync_to_soft(uint16_t cw, uint16_t sw) {
    uint16_t rc = (cw >> 10) & 3;
    f80_set_rounding_mode((enum f80_rounding_mode)rc);
#ifdef X86EMU_USE_SOFTFLOAT
    uint16_t pc = (cw >> 8) & 3;
    switch (pc) {
        case 0:
            extF80_roundingPrecision = 32;
            break;
        case 2:
            extF80_roundingPrecision = 64;
            break;
        case 3:
            extF80_roundingPrecision = 80;
            break;
        default:
            extF80_roundingPrecision = 80;
            break;
    }
    softfloat_exceptionFlags = 0;
    if (sw & (1 << 0)) softfloat_exceptionFlags |= 16;
    if (sw & (1 << 2)) softfloat_exceptionFlags |= 8;
    if (sw & (1 << 3)) softfloat_exceptionFlags |= 4;
    if (sw & (1 << 4)) softfloat_exceptionFlags |= 2;
    if (sw & (1 << 5)) softfloat_exceptionFlags |= 1;
#endif
}

void f80_sync_from_soft(uint16_t* cw, uint16_t* sw) {
#ifdef X86EMU_USE_SOFTFLOAT
    // Map softfloat_exceptionFlags back to sw (IE:0, ZE:2, OE:3, UE:4, PE:5)
    if (softfloat_exceptionFlags & 16) *sw |= (1 << 0);
    if (softfloat_exceptionFlags & 8) *sw |= (1 << 2);
    if (softfloat_exceptionFlags & 4) *sw |= (1 << 3);
    if (softfloat_exceptionFlags & 2) *sw |= (1 << 4);
    if (softfloat_exceptionFlags & 1) *sw |= (1 << 5);
#else
    // For double path, we don't easily have sticky flags from <math.h>
    // without using <fenv.h>.
#endif
}
