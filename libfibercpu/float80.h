#ifndef FLOAT80_H
#define FLOAT80_H

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

// x87 FPU constants
#define BIAS80 16383
#define EXP_MAX 16383
#define EXP_MIN -16382
#define EXP_SPECIAL 32767

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wgnu-anonymous-struct"
#pragma clang diagnostic ignored "-Wnested-anon-types"
#pragma clang diagnostic ignored "-Wc99-extensions"

/**
 * 80-bit extended precision floating point (x87 type).
 * POD struct compatible with Berkeley SoftFloat 3 extFloat80_t.
 */
typedef struct {
    uint64_t signif;
    uint16_t signExp;
} __attribute__((aligned(16))) float80;

#pragma clang diagnostic pop

// Conversion
float80 f80_from_int(int64_t i);
int64_t f80_to_int(float80 f);
float80 f80_from_double(double d);
double f80_to_double(float80 f);

// Classification
bool f80_isnan(float80 f);
bool f80_isinf(float80 f);
bool f80_iszero(float80 f);
bool f80_isdenormal(float80 f);
bool f80_is_supported(float80 f);

// Unary
float80 f80_neg(float80 f);
float80 f80_abs(float80 f);
float80 f80_sqrt(float80 f);

// Arithmetic
float80 f80_add(float80 a, float80 b);
float80 f80_sub(float80 a, float80 b);
float80 f80_mul(float80 a, float80 b);
float80 f80_div(float80 a, float80 b);
float80 f80_rem(float80 a, float80 b);
float80 f80_mod(float80 a, float80 b);

// Comparison
bool f80_lt(float80 a, float80 b);
bool f80_eq(float80 a, float80 b);
bool f80_uncomparable(float80 a, float80 b);

// Trigonometry and Transcendental
float80 f80_sin(float80 f);
float80 f80_cos(float80 f);
float80 f80_log2(float80 x);
float80 f80_scale(float80 x, int scale);
float80 f80_round(float80 f);

// Instruction helpers
void f80_xtract(float80 f, int *exp, float80 *signif);

enum f80_rounding_mode {
    round_to_nearest = 0,
    round_down = 1,
    round_up = 2,
    round_chop = 3,
};

/**
 * Updates the global FPU rounding mode.
 * Implementation in float80.cpp handles synchronization with SoftFloat state.
 */
void f80_set_rounding_mode(enum f80_rounding_mode mode);
void f80_sync_to_soft(uint16_t cw, uint16_t sw);
void f80_sync_from_soft(uint16_t *cw, uint16_t *sw);

#define F80_NAN ((float80) {.signif = 0xc000000000000000, .signExp = 0x7fff})
#define F80_INF ((float80) {.signif = 0x8000000000000000, .signExp = 0x7fff})
#define F80_ZERO ((float80) {.signif = 0, .signExp = 0})
#define F80_ONE  ((float80) {.signif = 0x8000000000000000, .signExp = 0x3fff})
#define F80_PI   ((float80) {.signif = 0xC90FDAA22168C235, .signExp = 0x4000})
#define F80_L2T  ((float80) {.signif = 0xD49A784BCD1B8AFE, .signExp = 0x4000}) 
#define F80_L2E  ((float80) {.signif = 0xB8AA3B295C17D06D, .signExp = 0x3FFF}) 
#define F80_LG2  ((float80) {.signif = 0x9A209A84FBCFF799, .signExp = 0x3FFD}) 
#define F80_LN2  ((float80) {.signif = 0xB17217F7D1CF79AC, .signExp = 0x3FFE}) 

#ifdef __cplusplus
}

// C++ Constexpr Helpers
constexpr float80 ConstF80_Zero() { return float80{.signif = 0, .signExp = 0}; }
constexpr float80 ConstF80_One()  { return float80{.signif = 0x8000000000000000, .signExp = 0x3fff}; }
constexpr float80 ConstF80_Pi()   { return float80{.signif = 0xC90FDAA22168C235, .signExp = 0x4000}; }
constexpr float80 ConstF80_L2T()  { return float80{.signif = 0xD49A784BCD1B8AFE, .signExp = 0x4000}; }
constexpr float80 ConstF80_L2E()  { return float80{.signif = 0xB8AA3B295C17D06D, .signExp = 0x3FFF}; }
constexpr float80 ConstF80_LG2()  { return float80{.signif = 0x9A209A84FBCFF799, .signExp = 0x3FFD}; }
constexpr float80 ConstF80_LN2()  { return float80{.signif = 0xB17217F7D1CF79AC, .signExp = 0x3FFE}; }

#endif

#endif
