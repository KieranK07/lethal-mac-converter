#!/bin/sh
# Build the game's Steam layer for Apple Silicon, from source, on the user's Mac:
#   out/Facepunch.Steamworks.Win64.dll  Facepunch.Steamworks 2.3.2 (MIT, the version the game ships), rebuilt
#                                       for the Posix platform under its Windows assembly name
#   out/libsteam_api.dylib              our arm64+x86_64 shim: Steamworks SDK 1.48's flat API (what 2.3.2
#                                       binds to) over a newer universal libsteam_api
#   out/libsteam_api_core.dylib         that newer libsteam_api, unmodified (Valve-signed), loaded via @loader_path
# Ship the two dylibs together in the same Plugins folder.
# Third-party inputs are downloaded into $LMC_CACHE, pinned by commit or sha256; none are in this repo.
# Needs: Xcode command line tools, git, curl, unzip, python3, dotnet SDK (any recent). Rosetta: optional (difftest).
set -eu
export LC_ALL=C
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$HERE/out
CACHE=${LMC_CACHE:-$HOME/Library/Caches/lethal-mac-converter}/steam
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}" DOTNET_ROLL_FORWARD=Major \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

FP_URL=https://github.com/Facepunch/Facepunch.Steamworks
# tag 2.3.2: C# source, Generator/steam_sdk (= SDK 1.48 headers + steam_api.json), Valve's 1.48 macOS lib (x86_64)
FP_TAG=2.3.2 FP_COMMIT=d06054875ae63b1e605929d83dcb7729f8faebc0
V148_SHA=5ad00aa60eb19180a79048314ce6c940bef138b6dccfc685db3d8272d89cbb63
# tag 2.4.1: Valve's newer universal libsteam_api.dylib (same file in 2.4.0-2.5.x)
CORE_COMMIT=b4d3a65f8686b40bb6ea09241f78ac0aa9f23753
CORE_SHA=b2260d2b2ff6ac8d2d10770047967ceb18022fc5c27f94e3246bd7d2a1da82c0
# Roslyn 3.4.0 = the compiler Facepunch's Feb 2020 CI used. With it the unmodified Win64
# config reproduces the shipped DLL's IL exactly; a newer csc only reorders types.
ROSLYN_URL=https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/3.4.0
ROSLYN_SHA=671a4e1bd3c10142513e24447d95866255e4c09ca102b0a85e4c70a1d02b314a

mkdir -p "$CACHE" "$OUT"
W=$(cd "$(mktemp -d)" && pwd -P)  # physical path, so PathMap matches
trap 'rm -rf "$W"' EXIT

sha_ok() { test "$(shasum -a 256 "$1" | cut -d' ' -f1)" = "$2" || { echo "sha256 mismatch: $1" >&2; exit 1; }; }
fetch() {  # url sha256 file: download once into the cache, verify every run
  [ -f "$3" ] || { curl -fsSL "$1" -o "$3.part" && mv "$3.part" "$3"; }
  sha_ok "$3" "$2"
}

FP=$CACHE/Facepunch.Steamworks-$FP_TAG
if [ ! -d "$FP" ]; then
  rm -rf "$FP.part"
  git -c advice.detachedHead=false clone -q --depth 1 --branch "$FP_TAG" --filter=blob:none --sparse "$FP_URL.git" "$FP.part"
  git -C "$FP.part" sparse-checkout set Facepunch.Steamworks Generator/steam_sdk UnityPlugin/redistributable_bin/osx
  mv "$FP.part" "$FP"
fi
test "$(git -C "$FP" rev-parse HEAD)" = "$FP_COMMIT"
git -C "$FP" diff --quiet HEAD  # cache untouched: builds work on copies
SDK=$FP/Generator/steam_sdk
V148=$FP/UnityPlugin/redistributable_bin/osx/libsteam_api.bundle
sha_ok "$V148" "$V148_SHA"
CORE=$CACHE/libsteam_api-$CORE_COMMIT.dylib
fetch "https://raw.githubusercontent.com/Facepunch/Facepunch.Steamworks/$CORE_COMMIT/UnityPlugin/redistributable_bin/osx/libsteam_api.dylib" "$CORE_SHA" "$CORE"
fetch "$ROSLYN_URL" "$ROSLYN_SHA" "$CACHE/microsoft.net.compilers.toolset.3.4.0.nupkg"

# --- libsteam_api.dylib (shim) + libsteam_api_core.dylib
mkdir "$W/inc" && ln -s "$SDK" "$W/inc/steam"  # 1.48's headers include each other as "steam/..."
python3 -I "$HERE/gen.py" "$SDK/steam_api.json" "$W/gen"
cp "$CORE" "$OUT/libsteam_api_core.dylib"
clang++ -std=c++11 -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -Wall -Werror \
  -fvisibility=hidden -fvisibility-inlines-hidden -fno-exceptions -fno-rtti -I"$W/inc" -I"$W/gen" \
  -dynamiclib -install_name @rpath/libsteam_api.dylib \
  "$W/gen/flat148.cpp" "$HERE/manual.cpp" "$OUT/libsteam_api_core.dylib" \
  -Wl,-reexported_symbols_list,"$HERE/reexports.txt" -o "$OUT/libsteam_api.dylib"
# The core's own install name is @loader_path/libsteam_api.dylib (= the shim); point at the renamed file.
install_name_tool -change @loader_path/libsteam_api.dylib @loader_path/libsteam_api_core.dylib "$OUT/libsteam_api.dylib" 2>/dev/null
codesign -f -s - "$OUT/libsteam_api.dylib"

# Every entry point Facepunch 2.3.2 imports (imports.txt, from the DLL's ImplMap) must be
# exported by the shim itself, per arch. Re-exports show up in nm as indirect (I).
for a in arm64 x86_64; do
  missing=$(nm -gU -arch $a "$OUT/libsteam_api.dylib" | awk '{for (i = 1; i <= NF; i++) if ($i ~ /^_/) { print substr($i, 2); break }}' | sort | comm -23 "$HERE/imports.txt" -)
  test -z "$missing" || { echo "$a missing: $missing"; exit 1; }
done
python3 -I "$HERE/check_abi.py" "$V148" "$OUT/libsteam_api.dylib"
if arch -x86_64 /usr/bin/true 2>/dev/null; then
  clang -std=c11 -Wall -Werror -arch x86_64 -o "$W/difftest" "$HERE/difftest.c"
  arch -x86_64 "$W/difftest" "$V148" "$OUT/libsteam_api.dylib"
else
  echo "difftest skipped: needs Rosetta (softwareupdate --install-rosetta)"
fi

# --- Facepunch.Steamworks.Win64.dll
unzip -q "$CACHE/microsoft.net.compilers.toolset.3.4.0.nupkg" 'tasks/netcoreapp2.1/bincore/*' -d "$W/roslyn"
printf '#!/bin/sh\nexec dotnet "%s" "$@"\n' "$W/roslyn/tasks/netcoreapp2.1/bincore/csc.dll" > "$W/roslyn/csc"
chmod +x "$W/roslyn/csc"
# Win64 project keeps the game's assembly name/version/attributes; swap only its platform
# defines for the Posix one (Platform.cs: pack 4, DllImport "libsteam_api").
P=$W/Facepunch.Steamworks
cp -R "$FP/Facepunch.Steamworks" "$P"
sed -i '' 's/PLATFORM_WIN64;PLATFORM_WIN;PLATFORM_64/PLATFORM_POSIX/' "$P/Facepunch.Steamworks.Win64.csproj"
grep -q 'PLATFORM_POSIX</DefineConstants>' "$P/Facepunch.Steamworks.Win64.csproj"
# Debug|net46 = what CI's plain `msbuild` produced and what the game ships.
dotnet build "$P/Facepunch.Steamworks.Win64.csproj" -nologo -v:q -c Debug -f net46 -o "$W/bin" \
  -p:CscToolPath="$W/roslyn" -p:CscToolExe=csc -p:UseSharedCompilation=false \
  -p:IncludeSourceRevisionInInformationalVersion=false -p:GenerateRepositoryUrlAttribute=false -p:EnableSourceLink=false \
  -p:PathMap="$P/=D:\\a\\Facepunch.Steamworks\\Facepunch.Steamworks\\Facepunch.Steamworks\\"  # CI path: same PE layout as shipped DLL
cp "$W/bin/Facepunch.Steamworks.Win64.dll" "$OUT/"

echo "ok: $(wc -l < "$HERE/imports.txt" | tr -d ' ') imports exported for arm64+x86_64"
shasum -a 256 "$OUT"/*
