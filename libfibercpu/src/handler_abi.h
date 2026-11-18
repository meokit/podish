#pragma once

// All threaded continuations use one signature per native build. The public C ABI is unchanged.
// 0=off, 1=ABI-only control, 2=conservative write-through cache, 3=static-write cache, 4=write-back.
#ifndef FIBERCPU_GPR_PIN_MODE
#define FIBERCPU_GPR_PIN_MODE 0
#endif
#if FIBERCPU_GPR_PIN_MODE
#include "gpr_pins.generated.h"
#else
#define FIBERCPU_PIN_COUNT 0
#define FIBERCPU_PIN_KEEP_MASK 0u
#define FIBERCPU_FOR_EACH_PIN(X, ...)
#endif

#define FIBERCPU_PIN_PARAM_ITEM(id, reg, slot, ...) , uint32_t pin_##reg
#define FIBERCPU_PIN_ARG_ITEM(id, reg, slot, ...) , pin_##reg
#define FIBERCPU_PIN_INIT_ITEM(id, reg, slot, ...) , state->ctx.regs[id]
#define FIBERCPU_PIN_PARAMS FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_PARAM_ITEM)
#define FIBERCPU_PIN_ARGS FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_ARG_ITEM)
#define FIBERCPU_PIN_INIT FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_INIT_ITEM)

#if FIBERCPU_GPR_PIN_MODE == 2 || FIBERCPU_GPR_PIN_MODE == 3
#define FIBERCPU_PIN_ASSUME_ITEM(id, reg, slot, ...) __builtin_assume(state->ctx.regs[id] == pin_##reg);
#define FIBERCPU_PIN_ASSUME FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_ASSUME_ITEM)
#define FIBERCPU_PIN_RELOAD FIBERCPU_PIN_INIT
#if FIBERCPU_GPR_PIN_MODE == 3
#define FIBERCPU_PIN_FORWARD_ITEM(id, reg, slot, Policy) \
    , ((Policy::keep_mask & (1u << slot)) ? pin_##reg : state->ctx.regs[id])
#define FIBERCPU_PIN_FORWARD(Policy) FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_FORWARD_ITEM, Policy)
#else
#define FIBERCPU_PIN_FORWARD(Policy) FIBERCPU_PIN_RELOAD
#endif
#else
#define FIBERCPU_PIN_ASSUME
#define FIBERCPU_PIN_RELOAD FIBERCPU_PIN_ARGS
#define FIBERCPU_PIN_FORWARD(Policy) FIBERCPU_PIN_ARGS
#endif

// In write-back mode the threaded arguments are authoritative until an observer
// boundary. Logic kernels use references to the current handler's arguments.
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_PIN_REFERENCE_ITEM(id, reg, slot, ...) pin_##reg,
#define FIBERCPU_GPR_CACHE \
    GprCache { FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_REFERENCE_ITEM) }
#define FIBERCPU_PIN_COMMIT(state) FIBERCPU_GPR_CACHE.Commit(state)
#define FIBERCPU_PIN_RESUME FIBERCPU_PIN_INIT
#else
#define FIBERCPU_GPR_CACHE \
    GprCache {}
#define FIBERCPU_PIN_COMMIT(state) ((void)0)
#define FIBERCPU_PIN_RESUME FIBERCPU_PIN_RELOAD
#endif
