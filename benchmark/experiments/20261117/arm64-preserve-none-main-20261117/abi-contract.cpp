#include "libfibercpu/src/decoder.h"
#include <type_traits>
using ExpectedHandler = int64_t(__attribute__((preserve_none)) *)(fiberish::EmuState*, fiberish::DecodedOp*, int64_t,
                                                                  fiberish::mem::MicroTlbAbiWord,
                                                                  fiberish::mem::MicroTlbAbiWord, uint32_t, uint64_t);
static_assert(std::is_same_v<fiberish::HandlerFunc, ExpectedHandler>,
              "ARM64 handler graph must actually use preserve_none");
