# Voice plugin spec (clean-room input)

This spec is the only input for the clean-room implementation of the voice libraries. It describes
interfaces and observable behaviour as facts. It contains no code from any proprietary binary.

Sources of these facts:
- the P/Invoke declarations in the game's `DissonanceVoip.dll`, which is public interface information
- black-box parity testing against the Windows libraries
- version fingerprinting against upstream open-source releases
- **"dirty room" analysis of the Windows `AudioPluginDissonance.dll`, done earlier for interoperability.**
  This covers which WebRTC components it enables and how it configures them, the order of calls, and how
  the RNNoise frame is scaled. Those findings appear below only as plain-language facts about how the
  open-source libraries are configured. No code, pseudo-code or structure from that analysis was passed on.
  The implementer worked only from this document and upstream sources, and never saw the analysis output.

## Target
Two macOS arm64 dynamic libraries that `DissonanceVoip.dll` loads through `DllImport`:
- `libAudioPluginDissonance.dylib`, for `DllImport("AudioPluginDissonance")`
- `libopus.dylib`, for `DllImport("opus")`

Both must be cdecl C exports. .NET marshals `bool` as a 4-byte value, so every bool parameter and bool
return value is a 32-bit int.

**Goal:** bit-identical output to the Windows libraries for the same input. The harness (below) checks this.

## Upstream libraries (open source, fetch pinned at build time)
- **WebRTC audio processing at M59:** `branch-heads/59`, commit `eeab9ccb2417`, BSD.
  - It must be compiled so that the native processing rate can be 48 kHz.
  - Upstream caps the band-split processing rate at 32 kHz when `WEBRTC_ARCH_ARM_FAMILY` is defined.
    That cap is in `FindNativeProcessRateToUse` in `audio_processing_impl.cc`. Windows (x64) does not
    have it, so build that file with the non-ARM branch. Keep NEON elsewhere.
- **RNNoise 0.1.1** (xiph), BSD.
- **libopus**, base commit `877d3d2c` (v1.2.1-30 on upstream master), BSD. Build details are below.
- **sse2neon v1.9.1**, MIT. It is only needed for the Opus x86 kernels.
- Compile all C/C++ with `-ffp-contract=off`. The Windows build never fuses multiply-adds.

## libAudioPluginDissonance

**Handle.** The preprocessor handle that crosses the boundary is a pointer to the WebRTC
`AudioProcessing` object.

### Enums (int32 on the wire)
| Enum | Values |
|---|---|
| NoiseSuppressionLevels | Disabled = -1, Low = 0, Moderate = 1, High = 2, VeryHigh = 3 |
| AecSuppressionLevels | Disabled = -1, Low = 0, Moderate = 1, High = 2 |
| AecmRoutingMode | Disabled = -1, QuietEarpieceOrHeadset = 0, Earpiece = 1, LoudEarpiece = 2, Speakerphone = 3, LoudSpeakerphone = 4 |
| VadSensitivityLevels | Low = 0, Medium = 1, High = 2, VeryHigh = 3 |

- **ProcessorErrors** use WebRTC `AudioProcessing::Error` values: Ok = 0, Unspecified = -1, … , NotEnabled = -12.
- **FilterState:** FilterNotRunning = 0, FilterNoInstance = 1, FilterNoSamplesSubmitted = 2, FilterOk = 3.

The non-negative enum values map one-to-one onto the corresponding WebRTC M59 enums:
- NS `Level`
- AEC `SuppressionLevel`
- AECM `RoutingMode`
- VAD `Likelihood`

### Exports
**`IntPtr Dissonance_CreatePreprocessor(int nsLevel, int aecLevel, bool aecDelayAgnostic, bool aecExtended, bool aecRefined, int aecmRoutingMode, bool aecmComfortNoise)`**

Create an `AudioProcessing` from an empty `webrtc::Config`. Return null if creation fails. Then:
1. Apply an `AudioProcessing::Config` with these components:
   - level controller ON, with its default initial peak level
   - residual echo detector ON
   - high-pass filter ON
   - echo canceller 3 OFF
2. **AEC.** If `aecLevel == -1`, disable it. Otherwise:
   - set the suppression level, turn drift compensation off and enable it
   - pass extra options `DelayAgnostic(aecDelayAgnostic)`, `ExtendedFilter(aecExtended)` and
     `RefinedAdaptiveFilter(aecRefined)`
3. **AECM.** If `aecmRoutingMode != -1`, set comfort noise to `aecmComfortNoise` and set the routing mode.
   Enable AECM exactly when `aecmRoutingMode != -1`.
4. **NS.** If `nsLevel != -1`, set the level. Enable NS exactly when `nsLevel != -1`.
5. **AGC.** Use mode adaptive-digital and enable it, always.
6. **VAD.** Enable it, always. Then call `Initialize()`.
7. Return the `AudioProcessing*`.

**Other preprocessor exports:**
- **`void Dissonance_DestroyPreprocessor(IntPtr)`:** delete it.
- **`void Dissonance_ConfigureNoiseSuppression(IntPtr, int nsLevel)`:** the same rule as step 4 of create.
- **`void Dissonance_ConfigureVadSensitivity(IntPtr, int level)`:** set the VAD likelihood.
- **`void Dissonance_ConfigureAecSuppression(IntPtr, int aecLevel, int aecmRouting)`:**
  - AEC: if -1, disable. Otherwise set the level and enable. Drift and extra options are left untouched.
  - AECM: if -1, disable. Otherwise set the routing mode and enable. Comfort noise is left untouched.
- **`bool Dissonance_GetVadSpeechState(IntPtr)`:** the VAD's stream-has-voice result.
- **`void Dissonance_SetAgcIsOutputMutedState(IntPtr, bool)`:** WebRTC "output will be muted".
- **`ProcessorErrors Dissonance_PreprocessCaptureFrame(IntPtr, int sampleRate, float[] input, float[] output, int streamDelay)`:**
  1. If AEC or AECM is enabled, set the stream delay to `streamDelay` clamped to `[0, 499]` ms.
  2. Process one mono float frame with the float `ProcessStream` overload:
     - input config: `sampleRate`, 1 channel, no keyboard channel
     - output config: 48000 Hz, 1 channel
  3. Return its error code.

  Dissonance calls this once per 10 ms frame.

**Echo-cancellation filter plumbing** (inert in Lethal Company):
- A process-global "current instance" pointer starts null and is protected by a mutex.
- A global filter state int starts at 0 (FilterNotRunning). Only the Unity mixer effect would change it,
  and Lethal Company's mixers don't contain that effect, so it stays 0.
- **`bool Dissonance_PreprocessorExchangeInstance(IntPtr previous, IntPtr replacement)`:** under the lock,
  if the current instance equals `previous`, set it to `replacement` and return true. Otherwise return false.
- **`int Dissonance_GetFilterState()`:** returns the global, which is 0.
- **`void Dissonance_GetAecMetrics(IntPtr floatBuffer, int length)`:** under the lock:
  1. Zero `length` floats (if `length > 0`).
  2. If there's no current instance, return.
  3. Otherwise write up to 10 floats from the instance's `GetStatistics()`, in this order: delay median,
     delay standard deviation, fraction of poor delays, echo return loss (average, minimum, maximum),
     ERLE (average, minimum, maximum), residual echo likelihood.

**Exports the Windows DLL also has, which Lethal Company never calls.** Provide them so the export list
matches:
- **`Dissonance_EnableAgc(IntPtr, bool)`:** enable or disable AGC. The parity harness uses it.
- **`Dissonance_SetStreamKeyPressed(IntPtr, bool)`:** WebRTC "stream key pressed".
- **`Dissonance_ConfigureAgcTarget(IntPtr, float t)`:** AGC target level dBFS = `int(t*31)`, clamped to `[0, 31]`.
- **`Dissonance_ConfigureAgcCompressionGain(IntPtr, float g)`:** compression gain dB = `int(g*90)`, clamped to `[0, 90]`.

**RNNoise:**
- **`IntPtr Dissonance_CreateRnnoiseState()`:** an RNNoise state with the default model.
- **`void Dissonance_DestroyRnnoiseState(IntPtr)`:** destroy it.
- **`bool Dissonance_RnnoiseProcessFrame(IntPtr, int count, int sampleRate, float[] input, float[] output)`:**
  1. Copy input to output.
  2. If `count != 480` or `sampleRate != 48000`, stop there.
  3. Otherwise scale output by 32768, run RNNoise in place on it, and scale back by 1/32768. Multiply by
     the reciprocal; don't divide.
  4. Return true on both paths. .NET observes true on Windows in the reject case too.
- **`int Dissonance_RnnoiseGetGains(IntPtr, float[] output, int length)`:**
  - Copy `min(length, 22)` floats of the band gains RNNoise computed for the last frame. These are
    RNNoise's internal per-band gain array (`NB_BANDS` = 22).
  - Return the count copied.

## libopus
- Start from upstream libopus at `877d3d2c`, built as the **float** build. Report the same version
  string as Windows: `libopus 1.2.1-32-g5015edae-dirty`.
- Mirror the MSVC x64 configuration (`win32/config.h`):
  - Define `HAVE_LRINTF`, so float→int rounds half-to-even.
  - x64 MSVC presumes SSE/SSE2 and calls the SSE float kernels directly: `xcorr_kernel_sse`,
    `celt_inner_prod_sse`, `dual_inner_prod_sse`, `comb_filter_const_sse` and `op_pvq_search_sse2`.
    Their lane order differs from plain C, so compile those upstream SSE sources on arm64 through sse2neon
    with `SSE2NEON_PRECISE_MINMAX`.
  - SSE4.1 runtime-dispatched kernels are fixed-point SILK and bit-exact with C, so leave them out.
- **`rcpps` and `rsqrtps`** are approximations whose bits are CPU-vendor-specific. The reference Windows
  machine is Intel (i3-13100F).
  - `x86compat/intel_rcp_tables.h` (our own measurements) gives Intel's exact results:
    - `rcp` depends on the top 11 mantissa bits
    - `rsqrt` depends on the top 10 mantissa bits plus exponent parity
    - both rescale exactly with the exponent
  - Emulate them for normal inputs.
- **Four extra non-variadic exports.** Variadic `opus_*_ctl` can't be P/Invoked on Apple arm64 because
  variadic arguments go on the stack, so each of these forwards to the matching ctl and returns its result:
  - `int dissonance_opus_encoder_ctl_in(OpusEncoder*, int request, int value)`
  - `int dissonance_opus_encoder_ctl_out(OpusEncoder*, int request, int* value)`
  - `int dissonance_opus_decoder_ctl_in(OpusDecoder*, int request, int value)`
  - `int dissonance_opus_decoder_ctl_out(OpusDecoder*, int request, int* value)`
- Export the standard libopus API. Windows exports 57 symbols. Dissonance uses:
  - `opus_get_version_string`
  - `opus_encoder_create`, `opus_encoder_destroy`, `opus_encode_float`
  - `opus_decoder_create`, `opus_decoder_destroy`, `opus_decode_float`
  - `opus_pcm_soft_clip`
  - the 4 wrappers above

## How Lethal Company configures it (for the harness)
- 48 kHz mono, 960-sample (20 ms) Opus frames, VOIP application, FEC on, 17 kbps, 10% packet-loss hint
- NS Moderate (1), VAD Medium (1), AEC off (-1), AECM off (-1), RNNoise off

## Verification
The harness is `test/` (ours, written from the P/Invoke declarations only). It runs the same deterministic
signal through the libraries on macOS and on Windows (with the game's own DLLs) and compares the results.
The expected result is bit-identical for:
- WebRTC samples
- VAD
- RNNoise output and gains
- AEC metrics
- Opus packets
- Opus decode, including PLC and FEC

## Found during implementation (black-box)
- **C runtime float maths.** Windows' UCRT returns correctly rounded `expf`/`powf`. Apple's libm is sometimes
  1 ulp off near halfway values, which changed WebRTC noise suppression's startup noise model, so the AGC-off
  path differed in 53 samples. `ucrt_float_math.c` evaluates those float functions in double and rounds
  once, which matches UCRT.
- UCRT's `logf`/`log10f` aren't reproduced this way. They only run on paths Lethal Company doesn't use
  (AEC and metrics).
