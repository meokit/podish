#pragma once

// All threaded continuations use one signature per native build. The public C ABI is unchanged.
// 0=off, 1=ABI-only control, 2=conservative write-through cache, 3=static-write cache.
#ifndef FIBERCPU_GPR_PIN_MODE
#define FIBERCPU_GPR_PIN_MODE 0
#endif
#if FIBERCPU_GPR_PIN_MODE
#define FIBERCPU_PIN_PARAMS , uint32_t pin_eax, uint32_t pin_esp
#define FIBERCPU_PIN_ARGS , pin_eax, pin_esp
#define FIBERCPU_PIN_INIT , state->ctx.regs[0], state->ctx.regs[4]
#else
#define FIBERCPU_PIN_PARAMS
#define FIBERCPU_PIN_ARGS
#define FIBERCPU_PIN_INIT
#endif
#if FIBERCPU_GPR_PIN_MODE >= 2
#define FIBERCPU_PIN_ASSUME                          \
    __builtin_assume(state->ctx.regs[0] == pin_eax); \
    __builtin_assume(state->ctx.regs[4] == pin_esp)
#define FIBERCPU_PIN_RELOAD , state->ctx.regs[0], state->ctx.regs[4]
#if FIBERCPU_GPR_PIN_MODE == 3
#define FIBERCPU_PIN_FORWARD(Policy) \
    , ((Policy::keep_mask & 1) ? pin_eax : state->ctx.regs[0]), ((Policy::keep_mask & 2) ? pin_esp : state->ctx.regs[4])
#else
#define FIBERCPU_PIN_FORWARD(Policy) FIBERCPU_PIN_RELOAD
#endif
#else
#define FIBERCPU_PIN_ASSUME
#define FIBERCPU_PIN_RELOAD FIBERCPU_PIN_ARGS
#define FIBERCPU_PIN_FORWARD(Policy) FIBERCPU_PIN_ARGS
#endif
