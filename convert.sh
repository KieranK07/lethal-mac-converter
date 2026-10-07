#!/bin/sh
# Turn your own Windows copy of Lethal Company into a native Apple Silicon app.
#
#   double-click "Convert Lethal Company.command", or ./convert.sh with no options: asks for your Steam login
#     once and downloads your copy with Valve's SteamCMD
#   ./convert.sh --game "/path/to/Lethal Company"        # use an existing Windows install instead
#   ./convert.sh --steam-user <your Steam login name>
#   options: --out "<path>.app" (default ~/Applications/Lethal Company.app), --no-steam-tile,
#            --force (rebuild even if the app is already up to date)
#
# Updating: run it again after Lethal Company updates. It downloads only what changed, and rebuilds only when
# the game or this converter changed (about 10 min); otherwise it says the app is up to date. Saves and settings
# live outside the app (~/Library/Application Support/com.ZeekerssRBLX.Lethal-Company), so they're kept.
#
# Nothing from the game, Unity or Valve is in this repository. Everything is downloaded from its official
# source or built from source here, on your Mac, into ~/Library/Caches/lethal-mac-converter.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
export LMC_CACHE="${LMC_CACHE:-$HOME/Library/Caches/lethal-mac-converter}"
OUT_APP="$HOME/Applications/Lethal Company.app"
GAME= STEAM_USER= STEAM_TILE=1 FORCE=
while [ $# -gt 0 ]; do
  case $1 in
    --game) GAME=$2; shift 2 ;;
    --steam-user) STEAM_USER=$2; shift 2 ;;
    --out) OUT_APP=$2; shift 2 ;;
    --no-steam-tile) STEAM_TILE=; shift ;;
    --force) FORCE=1; shift ;;
    *) echo "unknown option $1"; exit 2 ;;
  esac
done
say() { printf '\n==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
mkdir -p "$LMC_CACHE"

# --- 0. requirements -----------------------------------------------------------------------------------
[ "$(uname -m)" = arm64 ] || die "this converter builds for Apple Silicon Macs only"
xcode-select -p >/dev/null 2>&1 || die "install Xcode Command Line Tools first: xcode-select --install"
command -v python3 >/dev/null || die "python3 not found (it comes with the Command Line Tools)"
if ! command -v dotnet >/dev/null && [ ! -x "$LMC_CACHE/dotnet/dotnet" ]; then
  say "Installing the .NET SDK (Microsoft's official installer) into the cache"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$LMC_CACHE/dotnet-install.sh"
  sh "$LMC_CACHE/dotnet-install.sh" --channel 10.0 --install-dir "$LMC_CACHE/dotnet" >/dev/null
fi
[ -x "$LMC_CACHE/dotnet/dotnet" ] && export PATH="$LMC_CACHE/dotnet:$PATH"
export DOTNET_ROLL_FORWARD=Major DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# --- 1. Unity's terms ----------------------------------------------------------------------------------
if [ ! -f "$LMC_CACHE/unity-terms-accepted" ]; then
  cat <<'TERMS'

This converter downloads Unity's official macOS player runtime (Unity 2022.3.62f2, the engine version
Lethal Company uses) from Unity's servers. Its use is governed by Unity's terms:
  https://unity.com/legal/terms-of-service   https://unity.com/legal/editor-terms-of-service
TERMS
  printf 'Type "yes" to accept Unity'"'"'s terms and continue: '
  read -r answer
  [ "$answer" = yes ] || die "Unity's terms were not accepted"
  date > "$LMC_CACHE/unity-terms-accepted"
fi

# --- 2. your Windows game files ------------------------------------------------------------------------
if [ -z "$GAME" ] && [ -z "$STEAM_USER" ]; then
  STEAM_USER=$(cat "$LMC_CACHE/steam-user" 2>/dev/null || true)
  if [ -z "$STEAM_USER" ]; then
    printf '\nYour Steam login name (the one you sign in with, not your profile name): '
    read -r STEAM_USER
    [ -n "$STEAM_USER" ] || die "no Steam login given"
  fi
fi
if [ -n "$STEAM_USER" ]; then
  # UNTESTED end to end: needs a real login. SteamCMD for macOS is an Intel binary (Rosetta).
  arch -x86_64 /usr/bin/true 2>/dev/null || die "SteamCMD needs Rosetta: softwareupdate --install-rosetta --agree-to-license"
  if [ ! -x "$LMC_CACHE/steamcmd/steamcmd.sh" ]; then
    say "Downloading Valve's SteamCMD"
    mkdir -p "$LMC_CACHE/steamcmd"
    curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz | tar -xz -C "$LMC_CACHE/steamcmd"
  fi
  GAME="$LMC_CACHE/game"
  say "Downloading your Windows copy (app 1966720) as $STEAM_USER; SteamCMD will ask for your password / Steam Guard"
  "$LMC_CACHE/steamcmd/steamcmd.sh" +@sSteamCmdForcePlatformType windows +force_install_dir "$GAME" \
    +login "$STEAM_USER" +app_update 1966720 validate +quit
  echo "$STEAM_USER" > "$LMC_CACHE/steam-user"
fi
[ -n "$GAME" ] || die "pass --game <Windows install folder> or --steam-user <Steam login>"
[ -d "$GAME/Lethal Company_Data" ] || die "no 'Lethal Company_Data' in $GAME"
UNITY=$(head -c 4096 "$GAME/Lethal Company_Data/globalgamemanagers" | LC_ALL=C grep -a -o -m1 '20[0-9][0-9]\.[0-9]*\.[0-9]*f[0-9]*' || true)
[ "$UNITY" = 2022.3.62f2 ] || die "this game build uses Unity ${UNITY:-?}; the converter supports 2022.3.62f2. Get a newer converter."

# Up to date? The stamp names the game build (Steam build id, or a fingerprint of the files) and this converter.
ACF="$GAME/steamapps/appmanifest_1966720.acf"
if [ -f "$ACF" ]; then GAME_ID="steam-$(sed -n 's/.*"buildid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$ACF" | head -1)"
else GAME_ID="files-$(cd "$GAME" && find . -type f -print0 | sort -z | xargs -0 stat -f '%N %z %m' | shasum | cut -c1-16)"; fi
CONV_ID=$(cd "$HERE" && find convert.sh tools scripts native -type f ! -path '*/out/*' ! -path '*/bin/*' ! -path '*/obj/*' ! -name '*.pyc' -print0 \
  | sort -z | xargs -0 cat | shasum | cut -c1-16)
STAMP="game=$GAME_ID converter=$CONV_ID"
STAMP_FILE="$OUT_APP/Contents/Resources/converter-stamp.txt"
if [ -z "$FORCE" ] && [ "$(cat "$STAMP_FILE" 2>/dev/null)" = "$STAMP" ]; then
  say "$OUT_APP is up to date ($GAME_ID). Nothing to do."
  exit 0
fi

# --- 3. native libraries, built from source ------------------------------------------------------------
PLUGINS="$LMC_CACHE/plugins"; rm -rf "$PLUGINS"; mkdir -p "$PLUGINS"
say "Building the Steam layer";  sh "$HERE/native/steam/build.sh"
say "Building voice chat";       sh "$HERE/native/voice/build.sh"
cp "$HERE/native/steam/out/"* "$HERE/native/voice/out/"* "$PLUGINS/"
say "Fetching Discord's Game SDK 3.2.1 (official download)"
DZ="$LMC_CACHE/discord_game_sdk-3.2.1.zip"
[ -f "$DZ" ] || curl -fsSL https://dl-game-sdk.discordapp.net/3.2.1/discord_game_sdk.zip -o "$DZ"
echo "6757bb4a1f5b42aa7b6707cbf2158420278760ac5d80d40ca708bb01d20ae6b4  $DZ" | shasum -a 256 -c - >/dev/null || die "Discord SDK checksum mismatch"
unzip -o -q -j "$DZ" lib/aarch64/discord_game_sdk.dylib -d "$PLUGINS"

# --- 4. shaders: translate the game's Direct3D shaders to Metal -----------------------------------------
say "Building the shader translator"
OUT="$LMC_CACHE/tools" sh "$HERE/tools/xlat/build.sh"
dotnet build "$HERE/tools/lmc/lmc.csproj" -c Release -v q -nologo -o "$LMC_CACHE/tools" >/dev/null
if [ ! -f "$LMC_CACHE/classdata.tpk" ]; then
  # Unity's class layouts for AssetsTools.NET, from the UABEA v8 release (MIT)
  curl -fsSL https://github.com/nesrak1/UABEA/releases/download/v8/uabea-ubuntu.zip -o "$LMC_CACHE/uabea.zip"
  echo "c542dd3b5b091b34645ee1c7180df7138f9f3fe5253222f151d7185e542402b2  $LMC_CACHE/uabea.zip" | shasum -a 256 -c - >/dev/null || die "UABEA checksum mismatch"
  unzip -o -q -j "$LMC_CACHE/uabea.zip" classdata.tpk -d "$LMC_CACHE" && rm "$LMC_CACHE/uabea.zip"
fi
say "Translating shaders (several minutes)"
DATA="$LMC_CACHE/data"; rm -rf "$DATA"; mkdir -p "$DATA"
dotnet "$LMC_CACHE/tools/lmc.dll" metalize "$GAME/Lethal Company_Data" "$DATA"
say "Adding Unity's macOS video shader (from Unity's Mac editor pkg)"
python3 -I "$HERE/scripts/video_shader.py" --unity-pkg-cache "$LMC_CACHE/unity" --lmc "$LMC_CACHE/tools/lmc.dll" --data "$DATA"

# --- 5. Unity's Input System, compiled for macOS (controllers) ----------------------------------------
# The game's Windows compile lacks the macOS gamepad layouts (Xbox over HID/Bluetooth, Nimbus+). Same package
# version from Unity's registry, built the way Unity builds it for a Mac player; it overrides the game's DLL.
say "Compiling Unity's Input System for macOS"
python3 -I "$HERE/scripts/build_inputsystem.py" --game "$GAME" --unity-pkg-cache "$LMC_CACHE/unity" \
  --out "$PLUGINS/Unity.InputSystem.dll"

# --- 6. assemble and sign the app ----------------------------------------------------------------------
# Built next to the old app and swapped in at the end, so a failed update leaves the old one working.
say "Assembling $OUT_APP"
NEW_APP="${OUT_APP%.app}.updating.app"
python3 -I "$HERE/scripts/assemble_app.py" --game "$GAME" --unity-pkg-cache "$LMC_CACHE/unity" \
  --plugins "$PLUGINS" --data "$DATA" --out "$NEW_APP"
echo "$STAMP" > "$NEW_APP/Contents/Resources/converter-stamp.txt"
codesign --force --deep -s - "$NEW_APP"
rm -rf "$OUT_APP"
mv "$NEW_APP" "$OUT_APP"

# --- 7. a tile in your Steam library (optional) ----------------------------------------------------------
if [ -n "$STEAM_TILE" ] && [ -d "$HOME/Library/Application Support/Steam/userdata" ]; then
  if pgrep -x steam_osx >/dev/null; then
    echo "To add it to your Steam library: quit Steam, then run"
    echo "  python3 -I \"$HERE/scripts/steam_shortcut.py\" --app \"$OUT_APP\""
  else
    python3 -I "$HERE/scripts/steam_shortcut.py" --app "$OUT_APP" || echo "(could not add the Steam library tile)"
  fi
fi
say "Done: $OUT_APP"
