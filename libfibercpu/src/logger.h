#pragma once

#ifdef __cplusplus
extern "C" {
#endif

enum LogLevel { LogTrace = 0, LogDebug = 1, LogInfo = 2, LogWarning = 3, LogError = 4, LogCritical = 5, LogNone = 6 };

// Function pointer type for the callback
// Message string is null-terminated UTF-8
typedef void (*LogCallback)(int level, const char* message, void* userdata);

#ifdef __cplusplus
namespace fiberish {
struct EmuState;
}
using EmuState = fiberish::EmuState;
#else
typedef struct EmuState EmuState;
#endif

// Internal function to log formatted messages
// Note: This function will use a stack buffer, so keep messages reasonably sized (< 2KB)
void LogMsg(int level, const char* fmt, ...);
void LogMsgState(EmuState* state, int level, const char* fmt, ...);

// Setter for the callback (exposed internally)
void SetGlobalLogCallback(LogCallback callback, void* userdata);

#ifdef __cplusplus
}
#endif
