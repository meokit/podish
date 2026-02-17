#include "logger.h"
#include <cstdarg>
#include <cstdio>
#include "state.h"

static LogCallback g_LogCallback = nullptr;
static void* g_LogUserdata = nullptr;

void SetGlobalLogCallback(LogCallback callback, void* userdata) {
    g_LogCallback = callback;
    g_LogUserdata = userdata;
}

void LogMsg(int level, const char* fmt, ...) {
    if (!g_LogCallback) return;

    char buffer[2048];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buffer, sizeof(buffer), fmt, args);
    va_end(args);

    g_LogCallback(level, buffer, g_LogUserdata);
}

void LogMsgState(EmuState* state, int level, const char* fmt, ...) {
    if (state && state->log_callback) {
        char buffer[2048];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buffer, sizeof(buffer), fmt, args);
        va_end(args);
        state->log_callback(level, buffer, state->log_userdata);
        return;
    }
    // Fallback to global if instance callback not set?
    // Or just check global?
    if (g_LogCallback) {
        char buffer[2048];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buffer, sizeof(buffer), fmt, args);
        va_end(args);
        g_LogCallback(level, buffer, g_LogUserdata);
    }
}
