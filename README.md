# lethal-mac-converter

Turns **your own** Steam copy of Lethal Company (Windows) into a native Apple Silicon Mac app. It still plays with
friends on Windows: the converted app talks to Steam as the real game.

This repository contains only our own code. The game, Unity's Mac runtime and every third-party piece are downloaded
on your Mac from their official sources while the converter runs, and the shaders are translated there too.
Nothing from the game is ever shipped. Not affiliated with Zeekerss, Unity, Valve or Discord.

## Use it

1. **Needs:**
   - an Apple Silicon Mac
   - Xcode Command Line Tools (`xcode-select --install`)
   - Rosetta (`softwareupdate --install-rosetta --agree-to-license`), for Valve's SteamCMD
   - Lethal Company on your Steam account
   - about 6 GB of free disk space
2. Double-click **`Convert Lethal Company.command`**. The first time, macOS may refuse to run it. If so, right-click it, choose **Open**, then **Open** again.
3. Answer three prompts:
   - type `yes` to accept Unity's terms
   - your Steam login name
   - SteamCMD's password / Steam Guard prompt
4. Wait. The first run takes about 30–40 min, most of it translating shaders.
5. Play `~/Applications/Lethal Company.app`. If Steam is installed and closed, it also gets a tile in your Steam library.

## Updating

When Lethal Company updates, your Windows friends get it automatically, and you need it too to play with them.
Double-click `Convert Lethal Company.command` again:
- It downloads only what changed. Your Steam login is remembered, and SteamCMD keeps its own session.
- If neither the game nor the converter changed, it says "up to date" and stops (under a minute).
- Otherwise it rebuilds, which takes about 10 min. The new app is built alongside the old one and swapped in at the end, so a failed update leaves your old app working.
- Saves and settings live outside the app, so they're kept.
- The converter also sets up a small daily check (at noon and at login) that asks Steam, without logging in, whether the game has updated. If it has, you get a Mac notification. Turn it off with `sh ~/Library/Caches/lethal-mac-converter/update_check.sh uninstall`, or skip it with `--no-update-check`.
- If an update moves the game to a newer Unity version, the converter stops with a clear message instead of building something broken. Get a newer converter then.

**Already have the Windows files** (e.g. copied from a PC)? `./convert.sh --game "/path/to/Lethal Company"`.

## What it does

| Step | Source |
|---|---|
| Your game's Windows files | Valve's SteamCMD, your login |
| Mac player, engine and Mono libraries | Unity's official 2022.3.62f2 installers (checksummed) |
| Shaders: Direct3D → Metal | translated on your Mac with Unity's open-source [HLSLcc](https://github.com/Unity-Technologies/HLSLcc) |
| Controller support | Unity's Input System 1.14.0 package, compiled for macOS |
| Steam, voice chat | our own native code, built on your Mac |
| Discord | Discord Game SDK 3.2.1 (official download) |

Cache: `~/Library/Caches/lethal-mac-converter` (safe to delete; the next run re-downloads).

Details: [docs/APP-LAYOUT.md](docs/APP-LAYOUT.md) (where every file comes from), [docs/LEGAL-NOTES.md](docs/LEGAL-NOTES.md).
