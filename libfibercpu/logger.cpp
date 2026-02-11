#include "logger.h"
#include <cstdarg>
#include <cstdio>

static LogCallback g_LogCallback = nullptr;

void SetGlobalLogCallback(LogCallback callback) { g_LogCallback = callback; }

void LogMsg(int level, const char* fmt, ...) {
    if (!g_LogCallback) return;

    // Buffer for formatting
    char buffer[2048];

    va_list args;
    va_start(args, fmt);
    vsnprintf(buffer, sizeof(buffer), fmt, args);
    va_end(args);

    g_LogCallback(level, buffer);
}
