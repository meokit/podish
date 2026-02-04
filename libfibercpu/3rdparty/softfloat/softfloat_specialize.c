#include "platform.h"
#include "internals.h"
#include "specialize.h"
#include "softfloat.h"

/*----------------------------------------------------------------------------
| assuming uiA has the bit pattern of a 32-bit floating-point NaN, converts
| this NaN to the common NaN form, and stores the resulting common NaN at the
| location pointed to by zPtr.
*----------------------------------------------------------------------------*/
void softfloat_f32UIToCommonNaN(uint_fast32_t uiA, struct commonNaN *zPtr)
{
    if (softfloat_isSigNaNF32UI(uiA)) softfloat_raiseFlags(softfloat_flag_invalid);
    zPtr->sign = uiA >> 31;
    zPtr->v64  = (uint_fast64_t)uiA << 32;
    zPtr->v0   = 0;
}

/*----------------------------------------------------------------------------
| converts the common NaN pointed to by aPtr into a 32-bit floating-point
| NaN, and returns the bit pattern of this value as an unsigned integer.
*----------------------------------------------------------------------------*/
uint_fast32_t softfloat_commonNaNToF32UI(const struct commonNaN *aPtr)
{
    return (uint_fast32_t)aPtr->sign << 31 | 0x7FC00000 | (uint_fast32_t)(aPtr->v64 >> 32);
}

/*----------------------------------------------------------------------------
| returns the bit pattern of the combined NaN result of two 32-bit floating-
| point values.
*----------------------------------------------------------------------------*/
uint_fast32_t softfloat_propagateNaNF32UI(uint_fast32_t uiA, uint_fast32_t uiB)
{
    if (softfloat_isSigNaNF32UI(uiA) || softfloat_isSigNaNF32UI(uiB)) {
        softfloat_raiseFlags(softfloat_flag_invalid);
    }
    return defaultNaNF32UI;
}

/*----------------------------------------------------------------------------
| assuming uiA has the bit pattern of a 64-bit floating-point NaN, converts
| this NaN to the common NaN form, and stores the resulting common NaN at the
| location pointed to by zPtr.
*----------------------------------------------------------------------------*/
void softfloat_f64UIToCommonNaN(uint_fast64_t uiA, struct commonNaN *zPtr)
{
    if (softfloat_isSigNaNF64UI(uiA)) softfloat_raiseFlags(softfloat_flag_invalid);
    zPtr->sign = uiA >> 63;
    zPtr->v64  = uiA << 12;
    zPtr->v0   = 0;
}

/*----------------------------------------------------------------------------
| converts the common NaN pointed to by aPtr into a 64-bit floating-point
| NaN, and returns the bit pattern of this value as an unsigned integer.
*----------------------------------------------------------------------------*/
uint_fast64_t softfloat_commonNaNToF64UI(const struct commonNaN *aPtr)
{
    return (uint_fast64_t)aPtr->sign << 63 | UINT64_C(0x7FF8000000000000) | (aPtr->v64 >> 12);
}

/*----------------------------------------------------------------------------
| returns the bit pattern of the combined NaN result of two 64-bit floating-
| point values.
*----------------------------------------------------------------------------*/
uint_fast64_t softfloat_propagateNaNF64UI(uint_fast64_t uiA, uint_fast64_t uiB)
{
    if (softfloat_isSigNaNF64UI(uiA) || softfloat_isSigNaNF64UI(uiB)) {
        softfloat_raiseFlags(softfloat_flag_invalid);
    }
    return defaultNaNF64UI;
}

/*----------------------------------------------------------------------------
| assuming the 80-bit extended floating-point value formed from concatenating
| uiA64 and uiA0 is a NaN, converts this NaN to the common NaN form, and
| stores the resulting common NaN at the location pointed to by zPtr.
*----------------------------------------------------------------------------*/
void softfloat_extF80UIToCommonNaN(uint_fast16_t uiA64, uint_fast64_t uiA0, struct commonNaN *zPtr)
{
    if (softfloat_isSigNaNExtF80UI(uiA64, uiA0)) {
        softfloat_raiseFlags(softfloat_flag_invalid);
    }
    zPtr->sign = uiA64 >> 15;
    zPtr->v64  = uiA0;
    zPtr->v0   = 0;
}

/*----------------------------------------------------------------------------
| converts the common NaN pointed to by aPtr into an 80-bit extended
| floating-point NaN, and returns the bit pattern of this value as a 128-bit
| unsigned integer.
*----------------------------------------------------------------------------*/
struct uint128 softfloat_commonNaNToExtF80UI(const struct commonNaN *aPtr)
{
    struct uint128 z;
    z.v64 = (uint_fast16_t)aPtr->sign << 15 | 0x7FFF;
    z.v0  = aPtr->v64 | UINT64_C(0x8000000000000000);
    return z;
}

/*----------------------------------------------------------------------------
| returns the bit pattern of the combined NaN result of two 80-bit extended
| floating-point values.
*----------------------------------------------------------------------------*/
struct uint128 softfloat_propagateNaNExtF80UI(uint_fast16_t uiA64, uint_fast64_t uiA0, uint_fast16_t uiB64, uint_fast64_t uiB0)
{
    if (softfloat_isSigNaNExtF80UI(uiA64, uiA0) || softfloat_isSigNaNExtF80UI(uiB64, uiB0)) {
        softfloat_raiseFlags(softfloat_flag_invalid);
    }
    struct uint128 z;
    z.v64 = defaultNaNExtF80UI64;
    z.v0  = defaultNaNExtF80UI0;
    return z;
}
