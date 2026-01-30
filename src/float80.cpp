#include "float80.h"
#include <string.h>
#include <math.h>
#include <assert.h>

typedef unsigned __int128 uint128_t;
#define CURSED_BIT (1ul << 63)

// Thread-local rounding mode
__thread enum f80_rounding_mode f80_rounding_mode = round_to_nearest;

static unsigned bias(int exp) {
    return exp + BIAS80;
}
static int unbias(unsigned exp) {
    return exp - BIAS80;
}
static int unbias_denormal(unsigned exp) {
    if (exp == 0) // EXP_DENORMAL
        return unbias(EXP_MIN);
    return unbias(exp);
}

static bool round_away_from_zero(int sign) {
    return (f80_rounding_mode == round_up && !sign) ||
        (f80_rounding_mode == round_down && sign);
}

// shift a 128 bit integer right but using the floating point rounding mode
static uint128_t u128_shift_right_round(uint128_t i, int shift, int sign) {
    if (shift == 0) return i;
    if (shift > 127) {
        if (round_away_from_zero(sign) && i != 0) return 1;
        return 0;
    }

    int guard = (i >> (shift - 1)) & 1;
    uint128_t rest = i & ~((uint128_t) -1 << (shift - 1));

    i >>= shift;
    if (guard == 0 && rest == 0) return i;

    if (round_away_from_zero(sign)) {
        i++;
    } else if (f80_rounding_mode == round_to_nearest && guard) {
        if (rest != 0) i++;
        else if (i & 1) i++;
    }
    return i;
}

static float80 f80_shift_left(float80 f, int shift) {
    f.signif <<= shift;
    f.exp -= shift;
    return f;
}

static float80 f80_shift_right(float80 f, int shift) {
    f.signif = (uint64_t)u128_shift_right_round((uint128_t)f.signif, shift, f.sign);
    f.exp += shift;
    return f;
}

bool f80_is_supported(float80 f) {
    if (f.exp == 0) // EXP_DENORMAL
        return (f.signif >> 63) == 0;
    return (f.signif >> 63) == 1;
}

bool f80_isnan(float80 f) {
    return f.exp == EXP_SPECIAL && (f.signif & (-1ul >> 1)) != 0;
}
bool f80_isinf(float80 f) {
    return f.exp == EXP_SPECIAL && (f.signif & (-1ul >> 1)) == 0;
}
bool f80_iszero(float80 f) {
    return f.exp == 0 && f.signif == 0;
}
bool f80_isdenormal(float80 f) {
    return f.exp == 0 && f.signif != 0;
}

static float80 f80_normalize(float80 f) {
    if (f.exp == 0 || f.exp == EXP_SPECIAL)
        assert(f80_is_supported(f));

    if (f.exp == 0) return f;

    int shift;
    if (f.signif != 0) shift = __builtin_clzl(f.signif);
    else shift = 64;

    if (f.exp - shift < EXP_MIN) {
        f = f80_shift_left(f, f.exp - EXP_MIN);
        f.exp = 0;
        return f;
    }
    return f80_shift_left(f, shift);
}

static int u128_clz(uint128_t x) {
    int zeros;
    if (x >> 64 != 0) zeros = __builtin_clzl((uint64_t) (x >> 64));
    else if (x != 0) zeros = 64 + __builtin_clzl((uint64_t) x);
    else zeros = 128;
    return zeros;
}

static float80 u128_normalize_round(uint128_t signif, int exp, int sign) {
    int shift = u128_clz(signif);
    if (exp - shift < EXP_MIN) {
        if (exp > EXP_MIN)
            signif <<= exp - EXP_MIN;
        else
            signif = u128_shift_right_round(signif, EXP_MIN - exp, sign);
        exp = -16383; // unbias(0)
    } else if (exp - shift > EXP_MAX) {
        float80 f;
        if (signif == ((uint128_t) 1 << 127)) f = F80_INF;
        else if ((f80_rounding_mode == round_up && sign) || (f80_rounding_mode == round_down && !sign) || f80_rounding_mode == round_chop)
            f = (float80) {.signif = (uint64_t)-1, .signExp = (uint16_t)(32766 | (sign << 15))}; // EXP_MAX biased (32766)
        else f = F80_INF;
        f.sign = sign;
        return f;
    } else {
        signif <<= shift;
        exp -= shift;
    }

    float80 f;
    f.exp = bias(exp);
    signif = u128_shift_right_round(signif, 64, sign);
    if (signif >> 64 != 0) {
        signif >>= 1;
        f.exp++;
    }
    f.signif = (uint64_t)signif;
    f.sign = sign;
    return f;
}

float80 f80_from_int(int64_t i) {
    float80 f = {
        .signif = (uint64_t)i,
        .exp = bias(63),
        .sign = 0,
    };
    if (i == 0) f.exp = 0;
    if (i < 0) {
        f.sign = 1;
        f.signif = (uint64_t)-i;
    }
    return f80_normalize(f);
}

int64_t f80_to_int(float80 f) {
    if (!f80_is_supported(f)) return (int64_t)0x8000000000000000; // INT64_MIN
    if (f.exp > bias(63)) return (int64_t)0x8000000000000000;

    f = f80_shift_right(f, bias(63) - f.exp);
    return !f.sign ? (int64_t)f.signif : -(int64_t)f.signif;
}

struct double_bits {
    unsigned long signif:52;
    unsigned exp:11;
    unsigned sign:1;
};

float80 f80_from_double(double d) {
    struct double_bits db;
    memcpy(&db, &d, sizeof(db));
    float80 f;

    if (db.exp == EXP64_SPECIAL) f.exp = EXP_SPECIAL;
    else if (db.exp == 0) // EXP64_DENORMAL
        f.exp = db.signif == 0 ? 0 : bias(1 - 0x3ff);
    else f.exp = bias((int) db.exp - 0x3ff);

    f.signif = (uint64_t) db.signif << 11;
    if (db.exp != 0) f.signif |= CURSED_BIT;
    f.sign = db.sign;
    return f80_normalize(f);
}

double f80_to_double(float80 f) {
    if (!f80_is_supported(f)) return NAN;
    struct double_bits db;
    db.sign = f.sign;
    int new_exp = unbias(f.exp) + 0x3ff;
    
    if (f.exp == EXP_SPECIAL) new_exp = EXP64_SPECIAL;
    else if (new_exp >= EXP64_SPECIAL) return !f.sign ? INFINITY : -INFINITY;
    
    if (new_exp <= 0) {
        f.signif >>= 1;
        f = f80_shift_right(f, -new_exp);
        new_exp = unbias(f.exp) + 0x3ff;
    }
    db.exp = new_exp;
    uint64_t db_signif = (uint64_t)u128_shift_right_round(f.signif, 11, f.sign);
    if (db_signif & (1ul << 53)) {
        db_signif >>= 1;
        db.exp++;
    }
    db.signif = db_signif;
    double d;
    memcpy(&d, &db, sizeof(db));
    return d;
}

float80 f80_round(float80 f) {
    if (!f80_is_supported(f)) return F80_NAN;
    int bits_to_clear = 63 - unbias(f.exp);
    if (bits_to_clear <= 0) return f;

    f = f80_shift_right(f, bits_to_clear);
    if (f.signif == 0) f.exp = 0;
    else f = f80_normalize(f);
    return f;
}

float80 f80_neg(float80 f) {
    f.sign = ~f.sign;
    return f;
}

float80 f80_abs(float80 f) {
    f.sign = 0;
    return f;
}

#define handle_nans(a, b) do { \
    if (!f80_is_supported(a) || !f80_is_supported(b)) return F80_NAN; \
    if (f80_isnan(a) && f80_isnan(b) && a.sign && !b.sign) return b; \
    if (f80_isnan(a)) return a; \
    if (f80_isnan(b)) return b; \
} while(0)

float80 f80_add(float80 a, float80 b) {
    handle_nans(a, b);
    if (a.exp < b.exp) { float80 t = a; a = b; b = t; }

    bool flipped = false;
    if (a.sign) { a.sign = ~a.sign; b.sign = ~b.sign; flipped = true; }

    uint128_t a_signif = (uint128_t) a.signif << 64;
    uint128_t b_signif = (uint128_t) b.signif << 64;
    b_signif = u128_shift_right_round(b_signif, a.exp - b.exp, b.sign ^ flipped);

    int sign = a.sign;
    int exp = unbias_denormal(a.exp);
    uint128_t signif = a_signif;

    if (!b.sign) {
        if (!f80_isinf(a)) {
            if (__builtin_add_overflow(a_signif, b_signif, &signif)) {
                signif = u128_shift_right_round(signif, 1, sign);
                signif |= (uint128_t) 1 << 127;
                exp++;
            }
        }
    } else {
        if (f80_isinf(a) && f80_isinf(b)) return F80_NAN;
        if (f80_rounding_mode == round_chop && b_signif == 0 && b.signif != 0) b_signif = 1;
        if (f80_isinf(a)) b_signif = 0;

        if (a_signif >= b_signif) signif = a_signif - b_signif;
        else { signif = b_signif - a_signif; sign = 1; }

        if (signif == 0 && a_signif != 0 && f80_rounding_mode == round_down) return (float80) {.signif = 0, .signExp = (uint16_t)(1 << 15)};
        if (signif == 0) return F80_ZERO;
    }

    if (flipped) sign = !sign;
    float80 f = u128_normalize_round(signif, exp, sign);
    return f;
}

float80 f80_sub(float80 a, float80 b) {
    return f80_add(a, f80_neg(b));
}

float80 f80_mul(float80 a, float80 b) {
    handle_nans(a, b);
    if (f80_isinf(a) || f80_isinf(b)) {
        if (f80_iszero(a) || f80_iszero(b)) return F80_NAN;
        float80 f = F80_INF;
        f.sign = a.sign ^ b.sign;
        return f;
    }

    int f_exp = unbias_denormal(a.exp) + unbias_denormal(b.exp) + 1;
    uint128_t f_signif = (uint128_t) a.signif * b.signif;
    float80 f = u128_normalize_round(f_signif, f_exp, a.sign ^ b.sign);
    f.sign = a.sign ^ b.sign;
    return f;
}

float80 f80_div(float80 a, float80 b) {
    handle_nans(a, b);
    float80 f;
    if (f80_isinf(a)) {
        f = F80_INF;
        if (f80_isinf(b)) return F80_NAN;
    } else if (f80_isinf(b)) {
        f = F80_ZERO;
    } else if (f80_iszero(b)) {
        f = F80_INF;
        if (f80_iszero(a)) f = F80_NAN;
    } else {
        int b_trailing = __builtin_ctzl(b.signif);
        b.signif >>= b_trailing;
        uint128_t signif = ((uint128_t) a.signif << 64) / b.signif;
        uint128_t remainder = ((uint128_t) a.signif << 64) % b.signif;
        int extra_bits = 0;
        if (signif != 0) {
            extra_bits = u128_clz(signif);
            signif <<= extra_bits;
            signif |= (remainder << extra_bits) / b.signif;
        }
        int exp = unbias_denormal(a.exp) - unbias_denormal(b.exp) + 63 - b_trailing - extra_bits;
        f = u128_normalize_round(signif, exp, a.sign ^ b.sign);
    }
    f.sign = a.sign ^ b.sign;
    return f;
}

float80 f80_mod(float80 x, float80 y) {
    float80 quotient = f80_div(x, y);
    enum f80_rounding_mode old_mode = f80_rounding_mode;
    f80_rounding_mode = round_chop;
    quotient = f80_round(quotient);
    f80_rounding_mode = old_mode;
    return f80_sub(x, f80_mul(quotient, y));
}

bool f80_uncomparable(float80 a, float80 b) {
    if (!f80_is_supported(a) || !f80_is_supported(b)) return true;
    if (f80_isnan(a) || f80_isnan(b)) return true;
    return false;
}

bool f80_lt(float80 a, float80 b) {
    if (f80_uncomparable(a, b)) return false;
    if (f80_isinf(a) && f80_isinf(b) && a.sign == b.sign) return false;
    if (f80_iszero(a) && f80_iszero(b)) return false;
    float80 diff = f80_sub(a, b);
    return diff.sign == 1 && !f80_iszero(diff);
}
bool f80_eq(float80 a, float80 b) {
    if (f80_uncomparable(a, b)) return false;
    if (f80_iszero(a)) a.sign = 0;
    if (f80_iszero(b)) b.sign = 0;
    return a.sign == b.sign && a.exp == b.exp && a.signif == b.signif;
}
bool f80_lte(float80 a, float80 b) { return f80_lt(a, b) || f80_eq(a, b); }
bool f80_gt(float80 a, float80 b) { return !f80_lte(a, b); }

void f80_xtract(float80 f, int *exp, float80 *signif) {
    *exp = unbias(f.exp);
    *signif = f;
    signif->exp = bias(0);
}
