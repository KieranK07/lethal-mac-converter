// libAudioPluginDissonance.dylib: the native side of DissonanceVoip's DllImport("AudioPluginDissonance"),
// written from SPEC.md on top of WebRTC M59 audio processing and RNNoise 0.1.1.
// Every bool crossing the boundary is a 32-bit int (.NET's default bool marshalling).
#include <algorithm>
#include <cstring>
#include <mutex>

#include "rnnoise.h"
#include "webrtc/modules/audio_processing/include/audio_processing.h"

using webrtc::AudioProcessing;

extern "C" const float* lmc_rnnoise_band_gains(const DenoiseState* st);  // rnnoise_gains.c

#define EXPORT extern "C" __attribute__((visibility("default")))

namespace {
std::mutex g_lock;                        // guards g_instance
AudioProcessing* g_instance = nullptr;    // the AEC filter's "current instance"
int g_filter_state = 0;                   // FilterNotRunning; only the (absent) mixer effect would change it

AudioProcessing* Apm(void* h) { return static_cast<AudioProcessing*>(h); }

void SetNs(AudioProcessing* apm, int level) {
  if (level != -1) apm->noise_suppression()->set_level(static_cast<webrtc::NoiseSuppression::Level>(level));
  apm->noise_suppression()->Enable(level != -1);
}
}  // namespace

EXPORT void* Dissonance_CreatePreprocessor(int nsLevel, int aecLevel, int aecDelayAgnostic, int aecExtended,
                                           int aecRefined, int aecmRoutingMode, int aecmComfortNoise) {
  webrtc::Config empty;
  AudioProcessing* apm = AudioProcessing::Create(empty);
  if (!apm) return nullptr;

  AudioProcessing::Config cfg;
  cfg.level_controller.enabled = true;  // default initial_peak_level_dbfs
  cfg.residual_echo_detector.enabled = true;
  cfg.high_pass_filter.enabled = true;
  cfg.echo_canceller3.enabled = false;
  apm->ApplyConfig(cfg);

  webrtc::EchoCancellation* aec = apm->echo_cancellation();
  if (aecLevel == -1) {
    aec->Enable(false);
  } else {
    aec->set_suppression_level(static_cast<webrtc::EchoCancellation::SuppressionLevel>(aecLevel));
    aec->enable_drift_compensation(false);
    aec->Enable(true);
    webrtc::Config extra;
    extra.Set<webrtc::DelayAgnostic>(new webrtc::DelayAgnostic(aecDelayAgnostic != 0));
    extra.Set<webrtc::ExtendedFilter>(new webrtc::ExtendedFilter(aecExtended != 0));
    extra.Set<webrtc::RefinedAdaptiveFilter>(new webrtc::RefinedAdaptiveFilter(aecRefined != 0));
    apm->SetExtraOptions(extra);
  }

  webrtc::EchoControlMobile* aecm = apm->echo_control_mobile();
  if (aecmRoutingMode != -1) {
    aecm->enable_comfort_noise(aecmComfortNoise != 0);
    aecm->set_routing_mode(static_cast<webrtc::EchoControlMobile::RoutingMode>(aecmRoutingMode));
  }
  aecm->Enable(aecmRoutingMode != -1);

  SetNs(apm, nsLevel);

  apm->gain_control()->set_mode(webrtc::GainControl::kAdaptiveDigital);
  apm->gain_control()->Enable(true);

  apm->voice_detection()->Enable(true);
  apm->Initialize();
  return apm;
}

EXPORT void Dissonance_DestroyPreprocessor(void* h) { delete Apm(h); }

EXPORT void Dissonance_ConfigureNoiseSuppression(void* h, int nsLevel) { SetNs(Apm(h), nsLevel); }

EXPORT void Dissonance_ConfigureVadSensitivity(void* h, int level) {
  Apm(h)->voice_detection()->set_likelihood(static_cast<webrtc::VoiceDetection::Likelihood>(level));
}

EXPORT void Dissonance_ConfigureAecSuppression(void* h, int aecLevel, int aecmRouting) {
  webrtc::EchoCancellation* aec = Apm(h)->echo_cancellation();
  if (aecLevel == -1) {
    aec->Enable(false);
  } else {
    aec->set_suppression_level(static_cast<webrtc::EchoCancellation::SuppressionLevel>(aecLevel));
    aec->Enable(true);
  }
  webrtc::EchoControlMobile* aecm = Apm(h)->echo_control_mobile();
  if (aecmRouting == -1) {
    aecm->Enable(false);
  } else {
    aecm->set_routing_mode(static_cast<webrtc::EchoControlMobile::RoutingMode>(aecmRouting));
    aecm->Enable(true);
  }
}

EXPORT int Dissonance_GetVadSpeechState(void* h) { return Apm(h)->voice_detection()->stream_has_voice() ? 1 : 0; }

EXPORT void Dissonance_SetAgcIsOutputMutedState(void* h, int muted) { Apm(h)->set_output_will_be_muted(muted != 0); }

EXPORT int Dissonance_PreprocessCaptureFrame(void* h, int sampleRate, const float* input, float* output,
                                             int streamDelay) {
  AudioProcessing* apm = Apm(h);
  if (apm->echo_cancellation()->is_enabled() || apm->echo_control_mobile()->is_enabled())
    apm->set_stream_delay_ms(std::min(std::max(streamDelay, 0), 499));
  const float* src[1] = {input};
  float* dst[1] = {output};
  return apm->ProcessStream(src, webrtc::StreamConfig(sampleRate, 1, false), webrtc::StreamConfig(48000, 1, false),
                            dst);
}

EXPORT int Dissonance_PreprocessorExchangeInstance(void* previous, void* replacement) {
  std::lock_guard<std::mutex> lock(g_lock);
  if (g_instance != previous) return 0;
  g_instance = Apm(replacement);
  return 1;
}

EXPORT int Dissonance_GetFilterState() { return g_filter_state; }

EXPORT void Dissonance_GetAecMetrics(float* buffer, int length) {
  std::lock_guard<std::mutex> lock(g_lock);
  if (length > 0) std::memset(buffer, 0, sizeof(float) * length);
  if (!g_instance) return;
  AudioProcessing::AudioProcessingStatistics s = g_instance->GetStatistics();
  const float m[10] = {
      static_cast<float>(s.delay_median),
      static_cast<float>(s.delay_standard_deviation),
      s.fraction_poor_delays,
      s.echo_return_loss.average(),
      s.echo_return_loss.minimum(),
      s.echo_return_loss.maximum(),
      s.echo_return_loss_enhancement.average(),
      s.echo_return_loss_enhancement.minimum(),
      s.echo_return_loss_enhancement.maximum(),
      s.residual_echo_likelihood,
  };
  for (int i = 0; i < length && i < 10; i++) buffer[i] = m[i];
}

// Exported by the Windows DLL but never called by Lethal Company.
EXPORT void Dissonance_EnableAgc(void* h, int enable) { Apm(h)->gain_control()->Enable(enable != 0); }

EXPORT void Dissonance_SetStreamKeyPressed(void* h, int pressed) { Apm(h)->set_stream_key_pressed(pressed != 0); }

EXPORT void Dissonance_ConfigureAgcTarget(void* h, float t) {
  Apm(h)->gain_control()->set_target_level_dbfs(std::min(std::max(static_cast<int>(t * 31), 0), 31));
}

EXPORT void Dissonance_ConfigureAgcCompressionGain(void* h, float g) {
  Apm(h)->gain_control()->set_compression_gain_db(std::min(std::max(static_cast<int>(g * 90), 0), 90));
}

// RNNoise.
EXPORT void* Dissonance_CreateRnnoiseState() { return rnnoise_create(nullptr); }

EXPORT void Dissonance_DestroyRnnoiseState(void* st) { rnnoise_destroy(static_cast<DenoiseState*>(st)); }

EXPORT int Dissonance_RnnoiseProcessFrame(void* st, int count, int sampleRate, const float* input, float* output) {
  if (count > 0) std::memcpy(output, input, sizeof(float) * count);
  if (count != 480 || sampleRate != 48000) return 1;  // Windows reports true here too
  for (int i = 0; i < count; i++) output[i] *= 32768.0f;
  rnnoise_process_frame(static_cast<DenoiseState*>(st), output, output);
  const float scale = 1.0f / 32768.0f;
  for (int i = 0; i < count; i++) output[i] *= scale;
  return 1;
}

EXPORT int Dissonance_RnnoiseGetGains(void* st, float* output, int length) {
  int n = std::min(length, 22);  // NB_BANDS
  if (n <= 0) return 0;
  std::memcpy(output, lmc_rnnoise_band_gains(static_cast<DenoiseState*>(st)), sizeof(float) * n);
  return n;
}
