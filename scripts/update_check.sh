#!/bin/sh
# Daily check, run by a LaunchAgent that convert.sh installs: has Lethal Company updated on Steam since the
# Mac app was built? If so, show a notification. Asks Steam anonymously (no login) for the public build id.
#
#   sh update_check.sh <path to Lethal Company.app>   # the check
#   sh update_check.sh uninstall                       # remove the daily check
set -u
CACHE="${LMC_CACHE:-$HOME/Library/Caches/lethal-mac-converter}"
LABEL=com.lethal-mac-converter.update-check
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
if [ "${1:-}" = uninstall ]; then
  launchctl bootout "gui/$(id -u)" "$PLIST" 2>/dev/null
  rm -f "$PLIST"
  echo "Daily Lethal Company update check removed."
  exit 0
fi
APP=${1:?usage: update_check.sh <app> | uninstall}

LATEST=$("$CACHE/steamcmd/steamcmd.sh" +login anonymous +app_info_update 1 +app_info_print 1966720 +quit 2>/dev/null |
  awk '/"branches"/ {b=1} b && /"public"/ {p=1} p && /"buildid"/ {gsub(/"/, "", $2); print $2; exit}')
[ -n "$LATEST" ] || exit 0  # offline or Steam down: try again next time

# The app's stamp has the build it was made from when SteamCMD downloaded the game. For a copied install it
# doesn't, so remember the last build seen and tell the user once per new build.
BUILT=$(sed -n 's/^game=steam-\([0-9]*\) .*/\1/p' "$APP/Contents/Resources/converter-stamp.txt" 2>/dev/null)
SEEN=$(cat "$CACHE/seen-buildid" 2>/dev/null)
echo "$LATEST" > "$CACHE/seen-buildid"
if [ -n "$BUILT" ]; then
  [ "$LATEST" = "$BUILT" ] && exit 0
elif [ -z "$SEEN" ] || [ "$LATEST" = "$SEEN" ]; then
  exit 0
fi
osascript -e 'display notification "Double-click Convert Lethal Company.command to update your Mac app (about 10 min)." with title "Lethal Company updated on Steam" sound name "default"'
