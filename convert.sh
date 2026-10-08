#!/bin/sh
# Turn your own Windows copy of Lethal Company into a native Apple Silicon app.
#
#   double-click "LethalMac.command", or ./convert.sh with no options: asks for your Steam login
#     once and downloads your copy with Valve's SteamCMD
#   ./convert.sh --game "/path/to/Lethal Company"        # use an existing Windows install instead
#   ./convert.sh --steam-user <your Steam login name>
#   options: --out "<path>.app" (default ~/Applications/Lethal Company.app), --no-steam-tile,
#            --force (rebuild even if the app is already up to date), --no-update-check
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
GAME= STEAM_USER= STEAM_TILE=1 FORCE= UPDATE_CHECK=1
while [ $# -gt 0 ]; do
  case $1 in
    --game) GAME=$2; shift 2 ;;
    --steam-user) STEAM_USER=$2; shift 2 ;;
    --out) OUT_APP=$2; shift 2 ;;
    --no-steam-tile) STEAM_TILE=; shift ;;
    --force) FORCE=1; shift ;;
    --no-update-check) UPDATE_CHECK=; shift ;;
    *) echo "unknown option $1"; exit 2 ;;
  esac
done
say() { printf '\n==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
mkdir -p "$LMC_CACHE"
need_steamcmd() {  # Valve's SteamCMD for macOS, an Intel binary (Rosetta)
  arch -x86_64 /usr/bin/true 2>/dev/null || die "SteamCMD needs Rosetta: softwareupdate --install-rosetta --agree-to-license"
  if [ ! -x "$LMC_CACHE/steamcmd/steamcmd.sh" ]; then
    say "Downloading Valve's SteamCMD"
    mkdir -p "$LMC_CACHE/steamcmd"
    curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz | tar -xz -C "$LMC_CACHE/steamcmd"
  fi
}
# Hash of the files under these paths (build outputs and tests left out): decides what needs rebuilding.
srchash() {
  (cd "$HERE" && find "$@" -type f ! -path '*/out/*' ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/test/*' \
    ! -name '*.pyc' ! -name '.DS_Store' -print0 | sort -z | xargs -0 shasum) | shasum | cut -c1-16
}
# SteamCMD with its own home folder: sharing the Steam app's, its saved login was wiped whenever Steam started.
steamcmd() {
  mkdir -p "$LMC_CACHE/steamcmd-home"
  HOME="$LMC_CACHE/steamcmd-home" "$LMC_CACHE/steamcmd/steamcmd.sh" "$@"
}
latest_build() {  # the public build id, asked anonymously (no login, so the Steam app isn't signed out)
  steamcmd +login anonymous +app_info_update 1 +app_info_print 1966720 +quit 2>/dev/null |
    awk '/"branches"/ {b=1} b && /"public"/ {p=1} p && /"buildid"/ {gsub(/"/, "", $2); print $2; exit}'
}
# Steam keeps shortcuts.vdf in memory and writes it back on exit, so it must be closed while we add the
# library entry; and SteamCMD signing in as you signs the Steam app out. Close it once; reopen it on exit.
close_steam() {  # $1: why
  pgrep -x steam_osx >/dev/null || return 0
  say "Closing Steam for a moment ($1); it reopens at the end"
  osascript -e 'quit app "Steam"' >/dev/null 2>&1 || true
  for _ in $(seq 60); do pgrep -x steam_osx >/dev/null || break; sleep 1; done
  trap 'open -a Steam' EXIT
}
# Steam's own Lethal Company entry plays this app: Play, playtime, presence, invites and the overlay all
# work as the real game. Needs the SteamCMD install (its appmanifest) to register as installed; a copied
# install (--game without one) gets a separate library entry instead, where the overlay doesn't survive.
add_steam_tile() {
  [ -n "$STEAM_TILE" ] && [ -d "$HOME/Library/Application Support/Steam/userdata" ] || return 0
  S="$HERE/scripts"
  if [ -f "$GAME/steamapps/appmanifest_1966720.acf" ]; then
    python3 -I "$S/steam_appinfo.py" --check && python3 -I "$S/steam_launch.py" --app "$OUT_APP" --game "$GAME" --check \
      && ! python3 -I "$S/steam_shortcut.py" --app "$OUT_APP" --check && return 0
    close_steam "to make Play on Lethal Company in your library start the Mac app"
    python3 -I "$S/steam_appinfo.py" || echo "(could not enable Play for Lethal Company in Steam)"
    python3 -I "$S/steam_launch.py" --app "$OUT_APP" --game "$GAME" || echo "(could not set Lethal Company's launch options)"
    python3 -I "$S/steam_shortcut.py" --app "$OUT_APP" --remove || true  # the old separate entry, if any
  else
    python3 -I "$S/steam_shortcut.py" --app "$OUT_APP" --check && return 0
    close_steam "to add Lethal Company to your library"
    python3 -I "$S/steam_shortcut.py" --app "$OUT_APP" || echo "(could not add the Steam library entry)"
  fi
}
# A LaunchAgent that checks Steam once a day (and at login) and notifies when the game has updated.
install_update_check() {
  [ -n "$UPDATE_CHECK" ] || return 0
  ( need_steamcmd ) || { echo "(skipping the daily update check: no SteamCMD)"; return 0; }
  cp "$HERE/scripts/update_check.sh" "$HERE/scripts/steam_appinfo.py" "$LMC_CACHE/"
  PL="$HOME/Library/LaunchAgents/com.lethal-mac-converter.update-check.plist"
  mkdir -p "$HOME/Library/LaunchAgents"
  python3 -I -c 'import plistlib, sys; plistlib.dump({"Label": "com.lethal-mac-converter.update-check",
    "ProgramArguments": ["/bin/sh", sys.argv[1], sys.argv[2]], "RunAtLoad": True,
    "StartCalendarInterval": {"Hour": 12, "Minute": 0}, "ProcessType": "Background"}, open(sys.argv[3], "wb"))' \
    "$LMC_CACHE/update_check.sh" "$OUT_APP" "$PL"
  launchctl bootout "gui/$(id -u)" "$PL" 2>/dev/null || true
  launchctl bootstrap "gui/$(id -u)" "$PL"
  echo "Daily update check on (turn off: sh \"$LMC_CACHE/update_check.sh\" uninstall)"
}

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
# Which converter built the app: only the files that shape it count, so changes to e.g. the Steam entry or
# the update check never force a rebuild.
CONV_ID=$(srchash tools native scripts/assemble_app.py scripts/build_inputsystem.py scripts/video_shader.py)
STAMP_FILE="$OUT_APP/Contents/Resources/converter-stamp.txt"
up_to_date() {
  say "$OUT_APP is up to date ($1). Nothing to do."
  add_steam_tile
  install_update_check
  exit 0
}
if [ -n "$STEAM_USER" ]; then
  need_steamcmd
  GAME="$LMC_CACHE/game"
  # Already built from the current Steam build? Then no login at all.
  if [ -z "$FORCE" ] && [ -f "$GAME/steamapps/appmanifest_1966720.acf" ]; then
    LATEST=$(latest_build)
    [ -n "$LATEST" ] && [ "$(cat "$STAMP_FILE" 2>/dev/null)" = "game=steam-$LATEST converter=$CONV_ID" ] && up_to_date "steam-$LATEST"
  fi
  # SteamCMD signing in as you replaces the Steam app's session ("Session Replaced"), and the app doesn't
  # reconnect by itself, so games then can't reach Steam.
  close_steam "SteamCMD signs in as you, which would sign the Steam app out"
  say "Getting your Windows copy (app 1966720) as $STEAM_USER; the first time, SteamCMD asks for your password / Steam Guard"
  CHECK=validate  # check every file only on the first download; after that Steam sends just what changed
  [ -f "$GAME/steamapps/appmanifest_1966720.acf" ] && CHECK=
  steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir "$GAME" +login "$STEAM_USER" +app_update 1966720 $CHECK +quit
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
STAMP="game=$GAME_ID converter=$CONV_ID"
[ -z "$FORCE" ] && [ "$(cat "$STAMP_FILE" 2>/dev/null)" = "$STAMP" ] && up_to_date "$GAME_ID"

# --- 3. native libraries, built from source ------------------------------------------------------------
PLUGINS="$LMC_CACHE/plugins"; rm -rf "$PLUGINS"; mkdir -p "$PLUGINS"
for part in steam voice; do  # rebuilt only when their sources changed
  ID=$(srchash "native/$part")
  if [ "$(cat "$HERE/native/$part/out/.built-from" 2>/dev/null)" != "$ID" ]; then
    say "Building the $part library"
    sh "$HERE/native/$part/build.sh"
    echo "$ID" > "$HERE/native/$part/out/.built-from"
  fi
done
cp "$HERE/native/steam/out/"* "$HERE/native/voice/out/"* "$PLUGINS/"
say "Fetching Discord's Game SDK 3.2.1 (official download)"
DZ="$LMC_CACHE/discord_game_sdk-3.2.1.zip"
[ -f "$DZ" ] || curl -fsSL https://dl-game-sdk.discordapp.net/3.2.1/discord_game_sdk.zip -o "$DZ"
echo "6757bb4a1f5b42aa7b6707cbf2158420278760ac5d80d40ca708bb01d20ae6b4  $DZ" | shasum -a 256 -c - >/dev/null || die "Discord SDK checksum mismatch"
unzip -o -q -j "$DZ" lib/aarch64/discord_game_sdk.dylib -d "$PLUGINS"

# --- 4. shaders: translate the game's Direct3D shaders to Metal -----------------------------------------
ID=$(srchash tools/xlat)
if [ "$(cat "$LMC_CACHE/tools/.xlat-from" 2>/dev/null)" != "$ID" ] || [ ! -f "$LMC_CACHE/tools/libxlat.dylib" ]; then
  say "Building the shader translator"
  OUT="$LMC_CACHE/tools" sh "$HERE/tools/xlat/build.sh"
  echo "$ID" > "$LMC_CACHE/tools/.xlat-from"
fi
dotnet build "$HERE/tools/lmc/lmc.csproj" -c Release -v q -nologo -o "$LMC_CACHE/tools" >/dev/null
if [ ! -f "$LMC_CACHE/classdata.tpk" ]; then
  # Unity's class layouts for AssetsTools.NET, from the UABEA v8 release (MIT)
  curl -fsSL https://github.com/nesrak1/UABEA/releases/download/v8/uabea-ubuntu.zip -o "$LMC_CACHE/uabea.zip"
  echo "c542dd3b5b091b34645ee1c7180df7138f9f3fe5253222f151d7185e542402b2  $LMC_CACHE/uabea.zip" | shasum -a 256 -c - >/dev/null || die "UABEA checksum mismatch"
  unzip -o -q -j "$LMC_CACHE/uabea.zip" classdata.tpk -d "$LMC_CACHE" && rm "$LMC_CACHE/uabea.zip"
fi
say "Translating shaders (about 8 min the first time; afterwards only new or changed shaders)"
DATA="$LMC_CACHE/data"; rm -rf "$DATA"; mkdir -p "$DATA"
dotnet "$LMC_CACHE/tools/lmc.dll" metalize "$GAME/Lethal Company_Data" "$DATA" "$LMC_CACHE/metal-cache"
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

# --- 7. Steam library entry and daily update check ---------------------------------------------------------
add_steam_tile
install_update_check
say "Done: $OUT_APP"
