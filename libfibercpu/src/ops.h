#pragma once

#include "common.h"
#include "decoder.h"
#include "dispatch.h"
#include "instruction_families.h"
#include "specialization.h"
#include "state.h"

#include <array>
#include <limits>
#include <utility>

namespace fiberish {

#ifndef FIBERCPU_EXIT_HANDLER_REPLICA_COUNT
#define FIBERCPU_EXIT_HANDLER_REPLICA_COUNT 1
#endif

static_assert(FIBERCPU_EXIT_HANDLER_REPLICA_COUNT > 0);
static_assert(FIBERCPU_EXIT_HANDLER_REPLICA_COUNT <= 64);
inline constexpr size_t kExitHandlerReplicaCount = FIBERCPU_EXIT_HANDLER_REPLICA_COUNT;

// Handler Base Anchor (Used for calculating relative offsets)
extern void* g_HandlerBase;

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];
extern std::array<HandlerFunc, kExitHandlerReplicaCount> g_ExitHandlersFallthrough;
extern std::array<HandlerFunc, kExitHandlerReplicaCount> g_ExitHandlersBranch;

// Specialized Opcode Indices
enum SpecializedOp : uint16_t {
    OP_MOV_RR_STORE = 0x200,  // MOV r32, r32 (from 0x89)
    OP_MOV_RM_STORE = 0x201,  // MOV [mem], r32 (from 0x89)
    OP_MOV_RR_LOAD = 0x202,   // MOV r32, r32 (from 0x8B)
    OP_MOV_MR_LOAD = 0x203,   // MOV r32, [mem] (from 0x8B)
};

// Diagnostics / tooling helper: map a wrapper handler pointer back to its logical opcode id.
int FindOpcodeIndexForHandler(HandlerFunc handler);

}  // namespace fiberish
