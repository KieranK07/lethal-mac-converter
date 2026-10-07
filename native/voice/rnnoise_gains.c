/* Builds RNNoise's denoise.c and exposes DenoiseState.lastg: the per-band (NB_BANDS = 22) gains of the
   last non-silent frame, which DenoiseState keeps private to denoise.c. */
#include "denoise.c"

__attribute__((visibility("hidden"))) const float *lmc_rnnoise_band_gains(const DenoiseState *st) {
  return st->lastg;
}
