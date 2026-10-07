/* Non-variadic forwarders for opus_*_ctl: Apple arm64 passes variadic arguments on the stack, so
   P/Invoke can't call the variadic ctls directly. */
#include "opus.h"

#define EXPORT __attribute__((visibility("default")))

EXPORT int dissonance_opus_encoder_ctl_in(OpusEncoder *st, int request, int value) {
  return opus_encoder_ctl(st, request, value);
}
EXPORT int dissonance_opus_encoder_ctl_out(OpusEncoder *st, int request, int *value) {
  return opus_encoder_ctl(st, request, value);
}
EXPORT int dissonance_opus_decoder_ctl_in(OpusDecoder *st, int request, int value) {
  return opus_decoder_ctl(st, request, value);
}
EXPORT int dissonance_opus_decoder_ctl_out(OpusDecoder *st, int request, int *value) {
  return opus_decoder_ctl(st, request, value);
}
