#!/bin/sh
# Builds the two voice libraries DissonanceVoip loads, for macOS arm64:
#   out/libAudioPluginDissonance.dylib  WebRTC M59 audio processing + RNNoise 0.1.1 + AudioPluginDissonance.cc
#   out/libopus.dylib                   libopus 877d3d2c (float, MSVC x64 config) + opus_wrappers.c
# Needs only Xcode Command Line Tools, curl and python3. Upstream sources are downloaded at pinned commits,
# sha256-checked and cached in ${LMC_CACHE:-~/Library/Caches/lethal-mac-converter}/src; build intermediates
# go to a temp dir that is deleted on exit. Behaviour: SPEC.md. Parity check: test/ (see test/test.csproj).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=${LMC_CACHE:-$HOME/Library/Caches/lethal-mac-converter}/src
OUT=$HERE/out
B=$(mktemp -d "${TMPDIR:-/tmp}/lmc-voice.XXXXXX"); trap 'rm -rf "$B"' EXIT INT TERM
JOBS=$(sysctl -n hw.ncpu)
export MACOSX_DEPLOYMENT_TARGET=11.0
mkdir -p "$SRC" "$OUT"

# ---- upstream sources ----------------------------------------------------------------------------------
# Each source is a cache dir checked by one sha256 over its sorted file list and contents (gitiles builds its
# archives on the fly, so archive bytes aren't stable; the files are). Cached files are never modified.
treehash() { (cd "$1" && find . -type f ! -name .ok -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | shasum -a 256 | cut -c1-64); }
fetch() {  # fetch NAME SHA256 GETTER  (GETTER fills the empty dir it is given)
  [ -f "$SRC/$1/.ok" ] && return
  echo "fetching $1"
  rm -rf "$SRC/$1.tmp"; mkdir "$SRC/$1.tmp"
  "$3" "$SRC/$1.tmp"
  got=$(treehash "$SRC/$1.tmp")
  [ "$got" = "$2" ] || { echo "$1: content sha256 $got, expected $2" >&2; exit 1; }
  touch "$SRC/$1.tmp/.ok"; rm -rf "$SRC/$1"; mv "$SRC/$1.tmp" "$SRC/$1"
}

WEBRTC=eeab9ccb2417cab18ae1681c6644c25fa4eadcd3  # branch-heads/59
get_webrtc() {  # only the subtrees audio processing needs
  for d in api audio/utility base common_audio modules/audio_coding/codecs/isac modules/audio_processing \
           modules/include system_wrappers; do
    mkdir -p "$1/webrtc/$d"
    curl -fsSL "https://webrtc.googlesource.com/src/+archive/$WEBRTC/webrtc/$d.tar.gz" | tar xzf - -C "$1/webrtc/$d" &
  done
  for f in typedefs.h common_types.h common_types.cc modules/audio_coding/BUILD.gn \
           modules/video_coding/codecs/interface/common_constants.h modules/video_coding/codecs/vp8/include/vp8_globals.h \
           modules/video_coding/codecs/vp9/include/vp9_globals.h modules/video_coding/codecs/h264/include/h264_globals.h; do
    mkdir -p "$1/webrtc/$(dirname "$f")"
    curl -fsSL "https://webrtc.googlesource.com/src/+/$WEBRTC/webrtc/$f?format=TEXT" | base64 --decode > "$1/webrtc/$f" &
  done
  wait  # a failed download shows up as a hash mismatch
}
OPUS=877d3d2cefb868aa0ebc1b000f16824b7bfeedac   # v1.2.1-30
get_opus() {
  curl -fsSL "https://github.com/xiph/opus/archive/$OPUS.tar.gz" |
    tar xzf - -C "$1" --strip-components 1 --include '*/celt/*' --include '*/silk/*' --include '*/src/*' \
      --include '*/include/*' --include '*/*.mk' --include '*/COPYING'
}
RNNOISE=6cbfd53eb348a8d394e0757b4025c6ded34eb2b6  # v0.1.1
get_rnnoise() {
  curl -fsSL "https://github.com/xiph/rnnoise/archive/$RNNOISE.tar.gz" |
    tar xzf - -C "$1" --strip-components 1 --include '*/src/*' --include '*/include/*' --include '*/COPYING'
}
get_sse2neon() { curl -fsSL -o "$1/sse2neon.h" https://raw.githubusercontent.com/DLTcollab/sse2neon/v1.9.1/sse2neon.h; }

fetch webrtc-m59 9523c382a8fae4a95c18fce556a9194066ddb054de4c3b80da08bdc0c2f9a51d get_webrtc
fetch opus 78b55d6c9579661ee940b6743ae919c3a598252a1170347591c285c0f2367643 get_opus
fetch rnnoise-0.1.1 9e454dd0ddc672dca339821b880e8d32289b9488a830494fa60a5191f0041e55 get_rnnoise
fetch sse2neon-1.9.1 608c2ec3c5e7466489bc3b7ab08467185d8bda27b1fcfb311c5b323c26d674a0 get_sse2neon

# ---- parallel compile ----------------------------------------------------------------------------------
n=0
par() { { "$@" || touch "$B/failed"; } & n=$((n + 1)); [ $((n % JOBS)) -ne 0 ] || wait; }
join() { wait; [ ! -e "$B/failed" ] || { echo "compile failed" >&2; exit 1; }; }
# -ffp-contract=off everywhere: MSVC x64 never fuses multiply-adds, clang on arm64 would.
CF="-arch arm64 -O2 -fvisibility=hidden -ffp-contract=off"
# Keep the temp/cache/checkout paths out of __FILE__ strings so the dylibs are reproducible.
CF="$CF -ffile-prefix-map=$B/=build/ -ffile-prefix-map=$SRC/= -ffile-prefix-map=$HERE/="

# ---- WebRTC M59 audio processing (static lib) ----------------------------------------------------------
# Source lists come from M59's own BUILD.gn targets with GN args is_mac, current_cpu=arm64,
# rtc_build_with_neon, !rtc_enable_protobuf, !rtc_enable_intelligibility_enhancer, !rtc_prefer_fixed_point,
# !apm_debug_dump, !rtc_use_openmax_dl.
W=$SRC/webrtc-m59/webrtc
gn() { python3 -I "$HERE/gn_sources.py" "$@" | awk -F'\t' '{print $3}'; }
APM=$(gn $W/modules/audio_processing/BUILD.gn audio_processing audio_processing_neon | grep -vE 'intelligibility|_mips' | sed "s|^|$W/modules/audio_processing/|")
APM_C=$(printf "%s\n" agc/legacy/analog_agc.c agc/legacy/digital_agc.c ns/noise_suppression.c ns/ns_core.c | sed "s|^|$W/modules/audio_processing/|")
CA=$(gn $W/common_audio/BUILD.gn common_audio common_audio_c common_audio_neon common_audio_neon_c common_audio_cc | grep -vE '_sse|_mips|openmax|_arm\.S' | sort -u | sed "s|^|$W/common_audio/|")
BASE=$(printf "%s\n" checks.cc criticalsection.cc event.cc event_tracer.cc logging.cc logging_mac.mm platform_file.cc platform_thread.cc race_checker.cc stringencode.cc stringutils.cc thread_checker_impl.cc timeutils.cc | sed "s|^|$W/base/|")
SW=$(printf "%s\n" aligned_malloc.cc cpu_features.cc metrics_default.cc field_trial_default.cc | sed "s|^|$W/system_wrappers/source/|")
# APM deps: the iSAC C core (its pitch/LPC analysis feeds APM's VAD), audio_frame_operations, webrtc_common.
ISAC=$(gn $W/modules/audio_coding/BUILD.gn isac_c | sed "s|^|$W/modules/audio_coding/|")
MISC="$W/audio/utility/audio_frame_operations.cc $W/common_types.cc"

# Windows parity: on ARM, M59's FindNativeProcessRateToUse caps band-split processing at 32 kHz (48 kHz capture
# loses its top band); x64 processes all three bands at 48 kHz. That #ifdef is the only ARM-specific code in
# APM, so a copy of audio_processing_impl.cc is compiled with the x64 branch. NEON stays on elsewhere.
IMPL=$W/modules/audio_processing/audio_processing_impl.cc
IMPL_X64=$B/audio_processing_impl_x64.cc
sed 's|^#ifdef WEBRTC_ARCH_ARM_FAMILY$|#if 0  // Windows parity: x64 native process rate|' "$IMPL" > "$IMPL_X64"
[ "$(grep -c '^#if 0  // Windows parity' "$IMPL_X64")" = 1 ] || { echo "process-rate patch didn't apply" >&2; exit 1; }

WF="$CF -w -I$SRC/webrtc-m59 -I$W/common_audio/signal_processing/include -I$W/modules/audio_coding/codecs/isac/main/include
    -DWEBRTC_POSIX -DWEBRTC_MAC -DWEBRTC_HAS_NEON -DWEBRTC_APM_DEBUG_DUMP=0 -DWEBRTC_INTELLIGIBILITY_ENHANCER=0 -DWEBRTC_NS_FLOAT -DNDEBUG"
mkdir "$B/apm"
for f in $APM $APM_C $CA $BASE $SW $ISAC $MISC; do
  o=$B/apm/$(echo "${f#$W/}" | tr / _).o
  s=$f; [ "$f" = "$IMPL" ] && s=$IMPL_X64
  case $f in
    *.c)  par clang -std=c11 $WF -c "$s" -o "$o" ;;
    *.cc) par clang++ -std=c++14 $WF -c "$s" -o "$o" ;;
    *.mm) par clang++ -std=c++14 -ObjC++ $WF -c "$s" -o "$o" ;;
  esac
done

# ---- RNNoise + plugin ----------------------------------------------------------------------------------
RN=$SRC/rnnoise-0.1.1
mkdir "$B/plugin"
for f in $RN/src/rnn.c $RN/src/rnn_data.c $RN/src/rnn_reader.c $RN/src/celt_lpc.c $RN/src/kiss_fft.c $RN/src/pitch.c \
         "$HERE/rnnoise_gains.c"; do  # rnnoise_gains.c compiles denoise.c
  par clang $CF -w -I$RN/include -I$RN/src -c "$f" -o "$B/plugin/$(basename "$f" .c).o"
done
par clang $CF -fno-builtin -c "$HERE/ucrt_float_math.c" -o "$B/plugin/ucrt_float_math.o"  # see file
par clang++ -std=c++14 $CF -Wall -DWEBRTC_POSIX -DWEBRTC_MAC -DNDEBUG -I$RN/include -I$SRC/webrtc-m59 \
  -c "$HERE/AudioPluginDissonance.cc" -o "$B/plugin/AudioPluginDissonance.o"

# ---- Opus ----------------------------------------------------------------------------------------------
# Mirrors the MSVC x64 build (win32/config.h): float build; HAVE_LRINTF so float2int rounds half-to-even like
# cvtss2si. x64 presumes SSE/SSE2, which hard-wires Opus's x86 float kernels (pitch_sse.c, vq_sse2.c); their
# lane order changes output bits, so they are built here through x86compat/ (sse2neon + the parity PC's Intel
# rcpps/rsqrtps tables). The SSE4.1 RTCD kernels are fixed-point SILK, bit-exact with C, so they're left out.
O=$SRC/opus
mk() { sed -n "/^$2 *=/,/^[[:space:]]*$/p" "$O/$1" | tr ' \\' '\n\n' | grep -E '\.c$'; }
OPUS_SRC="$(mk celt_sources.mk CELT_SOURCES) $(mk silk_sources.mk SILK_SOURCES) $(mk silk_sources.mk SILK_SOURCES_FLOAT) $(mk opus_sources.mk OPUS_SOURCES) $(mk opus_sources.mk OPUS_SOURCES_FLOAT) celt/x86/pitch_sse.c celt/x86/vq_sse2.c"
OF="$CF -w -DOPUS_BUILD -DUSE_ALLOCA -DHAVE_LRINTF -DHAVE_LRINT -DPACKAGE_VERSION=\"1.2.1-32-g5015edae-dirty\"
    -DOPUS_X86_MAY_HAVE_SSE -DOPUS_X86_MAY_HAVE_SSE2 -DOPUS_X86_PRESUME_SSE -DOPUS_X86_PRESUME_SSE2
    -I$O/include -I$O/celt -I$O/silk -I$O/silk/float -I$O/src -I$HERE/x86compat -I$SRC/sse2neon-1.9.1"
mkdir "$B/opus"
for f in $OPUS_SRC; do par clang $OF -c "$O/$f" -o "$B/opus/$(echo "$f" | tr / _ | sed 's/\.c$/.o/')"; done
par clang $CF -I$O/include -c "$HERE/opus_wrappers.c" -o "$B/opus/opus_wrappers.o"
join

# ---- link ----------------------------------------------------------------------------------------------
ar rcs "$B/libwebrtc_apm.a" "$B"/apm/*.o
clang++ -arch arm64 -dynamiclib -o "$OUT/libAudioPluginDissonance.dylib" "$B"/plugin/*.o "$B/libwebrtc_apm.a" \
  -framework CoreFoundation -framework CoreServices -framework Foundation \
  -install_name @rpath/libAudioPluginDissonance.dylib -Wl,-dead_strip
clang -arch arm64 -dynamiclib -o "$OUT/libopus.dylib" "$B"/opus/*.o -install_name @rpath/libopus.dylib -Wl,-dead_strip
codesign -f -s - "$OUT/libAudioPluginDissonance.dylib" "$OUT/libopus.dylib" 2>/dev/null

# Export lists must match the Windows DLLs: 19 Dissonance_* functions, 57 opus symbols (53 libopus + 4 wrappers).
d=$(nm -gU "$OUT/libAudioPluginDissonance.dylib" | grep -c ' T _Dissonance_')
o=$(nm -gU "$OUT/libopus.dylib" | grep -c ' T _')
[ "$d" = 19 ] && [ "$o" = 57 ] || { echo "unexpected exports: $d Dissonance, $o opus" >&2; exit 1; }
echo "built $OUT/libAudioPluginDissonance.dylib ($d exports) and $OUT/libopus.dylib ($o exports)"
