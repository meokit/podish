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
enum SpecializedOp : uint16_t {};

// Diagnostics / tooling helper: map a wrapper handler pointer back to its logical opcode id.
int FindOpcodeIndexForHandler(HandlerFunc handler);

}  // namespace fiberish
