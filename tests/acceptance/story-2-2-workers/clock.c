#define _GNU_SOURCE
#include <time.h>
#include <dlfcn.h>
#include <stdlib.h>
/* Acceptance only. DateTime.UtcNow uses CLOCK_REALTIME. Never change monotonic clocks. */
int clock_gettime(clockid_t clock, struct timespec *value) {
    static int (*real_clock)(clockid_t, struct timespec *);
    if (!real_clock) real_clock = dlsym(RTLD_NEXT, "clock_gettime");
    int result = real_clock(clock, value);
    if (!result && clock == CLOCK_REALTIME) {
        const char *offset = getenv("CLOCK_OFFSET_SECONDS");
        if (offset) value->tv_sec += strtol(offset, NULL, 10);
    }
    return result;
}
