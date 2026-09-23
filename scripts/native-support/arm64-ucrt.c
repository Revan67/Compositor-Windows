#include <stddef.h>

// Minimal process entry and compiler-emitted memory primitives for the ARM64 fallback build.
// The image kernels otherwise use the dynamically linked Windows UCRT for allocation and math.
int __stdcall DllMain(void *module, unsigned long reason, void *reserved) {
    (void)module;
    (void)reason;
    (void)reserved;
    return 1;
}

void *memset(void *destination, int value, size_t count) {
    unsigned char *bytes = (unsigned char *)destination;
    while (count--) *bytes++ = (unsigned char)value;
    return destination;
}

void *memmove(void *destination, const void *source, size_t count) {
    unsigned char *to = (unsigned char *)destination;
    const unsigned char *from = (const unsigned char *)source;
    if (to < from) {
        for (size_t i = 0; i < count; ++i) to[i] = from[i];
    } else if (to > from) {
        while (count--) to[count] = from[count];
    }
    return destination;
}
