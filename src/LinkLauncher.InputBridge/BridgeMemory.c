#include <stddef.h>

/* Compiler-generated copies must not introduce a CRT dependency. Volatile
   accesses keep these minimal routines from being optimized into themselves. */
void * __cdecl memcpy(void *destination, const void *source, size_t count)
{
    volatile unsigned char *target = (volatile unsigned char *)destination;
    const volatile unsigned char *input = (const volatile unsigned char *)source;
    size_t i;
    for (i = 0; i < count; ++i) target[i] = input[i];
    return destination;
}

void * __cdecl memset(void *destination, int value, size_t count)
{
    volatile unsigned char *target = (volatile unsigned char *)destination;
    size_t i;
    for (i = 0; i < count; ++i) target[i] = (unsigned char)value;
    return destination;
}
