#pragma once

#include "decoder.h"

namespace fiberish {

HandlerFunc LowerDynamicOperandHandler(const DecodedOp* op, HandlerFunc original, unsigned remaining);
HandlerFunc CanonicalDynamicOperandHandler(HandlerFunc handler);
size_t DynamicOperandCount();
int32_t DynamicOperandId(HandlerFunc handler);
HandlerFunc DynamicOperandForId(uint32_t id);
const char* DynamicOperandName(uint32_t id);

}  // namespace fiberish
