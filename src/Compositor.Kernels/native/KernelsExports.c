// Windows-only additions. The reference C files are unchanged; this file adds what the
// DLL boundary needs.
#include <stdlib.h>

// wand_trace hands back malloc'd buffers. They must be released by the same C runtime that
// allocated them, so the managed side calls this instead of any free() of its own.
void kernels_free(void *pointer) { free(pointer); }
