// Lets Opus's x86 float kernels (celt/x86/pitch_sse.c, vq_sse2.c) build on arm64. x64 Windows hard-wires
// them (OPUS_X86_PRESUME_SSE/SSE2), and their 4-lane summation order and approximate rcp/rsqrt decide
// Opus's output bits, so the Mac runs the same code. sse2neon maps each SSE op used there to a NEON op with
// identical IEEE results. rcpps/rsqrtps are implementation-defined (Intel and AMD differ), so they come from
// tables measured on the parity PC's Intel CPU (intel_rcp_tables.h).
#pragma once
#define SSE2NEON_PRECISE_MINMAX 1  // maxps semantics, not vmaxq's
#include "sse2neon.h"
#include <string.h>
#include "intel_rcp_tables.h"

static inline uint32_t intel_bits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
static inline float intel_float(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }

// Exact for normal inputs with normal results, the only ones vq_sse2.c feeds them (rcp: sum of |X| in
// (EPSILON, 64) or 1; rsqrt: y + yy >= 1). Zero, denormal, inf and NaN inputs aren't emulated.
static inline float intel_rcp(float x) {
    uint32_t u = intel_bits(x);
    int e = (int)(u >> 23 & 0xff) - 127;
    return intel_float((intel_rcp_base[u >> 12 & 0x7ff] - ((uint32_t)e << 23)) | (u & 0x80000000u));
}
static inline float intel_rsqrt(float x) {  // x = 4^(e>>1) * base, base in [1,2) (e even) or [2,4) (e odd)
    uint32_t u = intel_bits(x);
    int e = (int)(u >> 23 & 0xff) - 127;
    return intel_float(intel_rsqrt_base[e & 1][u >> 13 & 0x3ff] - ((uint32_t)(e >> 1) << 23));
}

static inline __m128 intel_rcp_ps(__m128 a) {
    float v[4];
    _mm_storeu_ps(v, a);
    for (int i = 0; i < 4; i++) v[i] = intel_rcp(v[i]);
    return _mm_loadu_ps(v);
}
static inline __m128 intel_rsqrt_ps(__m128 a) {
    float v[4];
    _mm_storeu_ps(v, a);
    for (int i = 0; i < 4; i++) v[i] = intel_rsqrt(v[i]);
    return _mm_loadu_ps(v);
}
#define _mm_rcp_ps intel_rcp_ps
#define _mm_rsqrt_ps intel_rsqrt_ps
