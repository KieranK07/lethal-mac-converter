/* Float libm functions as Windows' UCRT rounds them, for the WebRTC code linked into the plugin.
   Apple's expf/powf/sinf/cosf/atan2f are faithfully rounded (<= 1 ulp off); UCRT's are, in black-box tests
   on the parity PC, almost always the correctly rounded result, which evaluating in double and rounding once
   reproduces. Without this, WebRTC NS's startup noise model (expf/powf) differs from Windows by an ulp in some
   frames: invisible behind AGC's int16 stage in Lethal Company's config, visible with AGC off.
   Measured mismatches vs UCRT over 200k inputs each (Apple float -> this file): expf 640 -> 8, powf 945 -> 100,
   sinf 2102 -> 144, cosf 1393 -> 145, atan2f 23022 -> 9. logf/log10f gain nothing this way, so stay Apple's.
   Hidden symbols: they bind only the plugin's own calls. Build with -fno-builtin so these stay double calls. */
#include <math.h>

#define LOCAL __attribute__((visibility("hidden")))

LOCAL float expf(float x) { return (float)exp(x); }
LOCAL float powf(float x, float y) { return (float)pow(x, y); }
LOCAL float sinf(float x) { return (float)sin(x); }
LOCAL float cosf(float x) { return (float)cos(x); }
LOCAL float atan2f(float y, float x) { return (float)atan2(y, x); }
