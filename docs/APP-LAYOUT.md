# App layout: where every file in the Mac `.app` comes from

Measured 2026-10-07 against a working reference build, `LC Hybrid.app` (296 files; private, not in this repo).
The `scripts/assemble_app.py` stage reproduces it from official sources plus the user's own game.

Hash sources (md5 unless noted):
- **Game:** a Windows Steam install, hashed in place with `Get-FileHash`.
- **LCPort:** an earlier Unity Mac build (private, not in this repo).
- **pkg:** `UnitySetup-Mac-Mono-Support-for-Editor-2022.3.62f2.pkg`, sha256 `ada8c5eb…75e9`, signed "Developer ID Installer: Unity Technologies SF".
- **editor pkg:** `MacEditorInstallerArm64/Unity-2022.3.62f2.pkg` (4.5 GB). Only read with HTTP Range requests, never stored.

## 1. Sources at a glance

| Source | Files | How the converter gets it |
|---|---|---|
| Unity Mac Mono support pkg | player executable, 4 dylibs, `unity default resources`, `MainMenu.nib`, Mono `etc/` (19), Info.plist template, PrivacyInfo, 67 `UnityEngine*.dll` | Download 552 MB, check sha256, stream-extract 96 files (37 MB), delete the pkg |
| Unity Mac editor pkg | 22 Mono class libraries (`unityjit-macos`); the editor's `Resources/unity_builtin_extra` (source of `Hidden/VideoDecodeOSX`) | Stream the Payload, stop right after `MonoBleedingEdge/lib/mono/unityjit-macos/` (~1 min 50 s). The builtin_extra (148 MB, kept) sits at ~4.0 GB of the 4.5 GB Payload, before the class libraries, so the same stream picks it up. Each file is sha256-pinned |
| User's Windows game | all Data files, 77 Managed DLLs | Clone (`cp -c`) |
| Patched data (transplant + shader stage) | 41 serialized files, `Resources/unity_builtin_extra` | `--data` overlay |
| Ours | 5 native plugins, `Facepunch.Steamworks.Win64.dll` (POSIX build) | `--plugins` |
| Generated from templates | Info.plist values, DefaultPreferences.plist, boot.config edit | In the script |

## 2. File mapping: everything outside `Contents/Resources/Data/`

| `.app` path | Source | Proof |
|---|---|---|
| `Contents/MacOS/Lethal Company` | pkg `Variations/macos_arm64_player_nondevelopment_mono/UnityPlayer.app/Contents/MacOS/UnityPlayer`, renamed | Unity's ad-hoc signature removed and re-signed. With `codesign --remove-signature` on pkg, LCPort and hybrid copies, all three are 49,416 bytes and differ only in one byte (`__LINKEDIT` vmsize). Code is identical |
| `Contents/Frameworks/UnityPlayer.dylib` | pkg `…/Frameworks/UnityPlayer.dylib` | LCPort md5 `5c6928e0…` = pkg. Hybrid differs only by its signature (2 load-command bytes after removing it) |
| `Contents/Frameworks/libmonobdwgc-2.0.dylib` | pkg, same folder | `fdfc4788…` = pkg (LCPort); hybrid differs only by signature |
| `Contents/Frameworks/libmono-native.dylib` | pkg | `702f7f9a…` = pkg; hybrid differs only by signature |
| `Contents/Frameworks/libMonoPosixHelper.dylib` | pkg | `c1e9bbdc…` = pkg; hybrid differs only by signature |
| `Contents/Resources/unity default resources` | pkg `…/UnityPlayer.app/Contents/Resources/unity default resources` | `e85804a9…` = pkg = hybrid. The game's Windows one in `Data/Resources/` is 1,564,240 bytes and isn't used |
| `Contents/Resources/MainMenu.nib/{designable,keyedobjects}.nib` | pkg `Source/Player/MacPlayer/MacPlayerEntryPoint/Resources/MainMenu.nib/` | `a097fcc5…`, `23e0a1f0…` = pkg = hybrid |
| `Contents/MonoBleedingEdge/etc/mono/**` (19 files) | pkg `MonoBleedingEdge/etc/` | All 19 equal pkg = hybrid. They also equal the game's Windows `MonoBleedingEdge/etc` |
| `Contents/Info.plist` | **generated**: pkg template `…/MacPlayerEntryPoint/Info.plist` plus values (§4) | Script output is plist-equal to the hybrid's |
| `Contents/Resources/DefaultPreferences.plist` | **generated** (§4) | plist-equal to the hybrid's |
| `Contents/Resources/PrivacyInfo.xcprivacy` | pkg `Tools/XCode/PrivacyInfo.xcprivacy` (238 B) | The hybrid has Unity's 3,364 B merge of package privacy manifests. That's App Store metadata, never read at runtime, so the base file is used |
| `Contents/PlugIns/libsteam_api.dylib`, `libsteam_api_core.dylib` | ours (Steam shim) | built by other stages |
| `Contents/PlugIns/libopus.dylib`, `libAudioPluginDissonance.dylib` | ours (voice) | built by other stages |
| `Contents/PlugIns/discord_game_sdk.dylib` | Discord SDK (fetched at convert time) | other stage |
| `Contents/_CodeSignature/CodeResources` | `codesign --force --deep -s -` | — |

- **Icon:** `PlayerIcon.icns` is generated from the game's own `Lethal Company.exe` icon resource (largest image, 256 px) with `sips` + `iconutil`.

## 3. File mapping: `Contents/Resources/Data/`

| Path | Source | Proof |
|---|---|---|
| 33 `*.resS`, 14 `*.resource`, `app.info`, `StreamingAssets/UnityServicesProjectConfiguration.json`, `UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json` | game, unchanged | 50 files md5-equal to the game |
| `globalgamemanagers`, `globalgamemanagers.assets`, `level0`–`level18`, `resources.assets`, `sharedassets0–18.assets` (41) | **transplant** (`--data`): the game's file with the platform byte 19→2, Metal shader objects, object table | Differ from the game by design; same sizes for the untouched ones |
| `Resources/unity_builtin_extra` | **shader stage** (`--data`): Metal built-ins, plus `Hidden/VideoDecodeOSX` (pathID 16002, `scripts/video_shader.py`) | LCPort `732000` B; game's is the D3D one (620,500 B). The Windows build strips 16002 although its ScriptMapper names it; the Mac player looks it up for video. It is GLSL-only, so for Metal Unity writes it with no programs. Built from the editor pkg's copy; byte-identical to the hybrid's object (sha256 `f96a737d…`) |
| `boot.config` | **generated**: the game's minus one line (§4) | — |
| `ScriptingAssemblies.json`, `RuntimeInitializeOnLoads.json` | the game's, **verbatim** (§4) | — |
| `Managed/*.dll` (167) | see §5 | — |
| ~~`Plugins/x86_64/*`~~, ~~`Resources/unity default resources`~~ | dropped (Windows-only) | — |

## 4. Generated files and how we reproduce them without the Editor

| File | Reproduced as | Differs from the game's Windows equivalent, and why |
|---|---|---|
| `Info.plist` | pkg template; the script sets 9 keys | No Windows equivalent. Keys are listed below the table |
| `DefaultPreferences.plist` | template: `{"Screenmanager Is Fullscreen mode": "True"}` | Mac-only. On Windows the same default lives in the player. It's the first-run fullscreen default |
| `boot.config` | the game's, minus `xrsdk-pre-init-library=` (empty) | **Only that line.** The Mac player calls `dlopen("")` on the empty value and logs `Plugins: Couldn't open , error: dlopen(, 0x0002) …`; Windows ignores it (its Player.log has no such line). `build-guid` stays the game's (`2ef0cde3…`). Our Unity build had its own (`3815064f…`) and lacked the XR line |
| `ScriptingAssemblies.json` | the game's, verbatim (145 entries) | **None.** Our Unity build listed 143 in a different order, without `UnityEngine.NVIDIAModule.dll` or `Unity.Jobs.dll`. With the game's package DLLs (§5) every listed DLL exists, including `UnityEngine.NVIDIAModule.dll`, which is copied from the game. The script refuses to finish if a listed DLL is missing |
| `RuntimeInitializeOnLoads.json` | the game's, verbatim (40 entries) | **None, and it must be the game's:** five entries name ILPP-generated classes `__JobReflectionRegistrationOutput__<hash>` whose hash is different in every build (Collections, Netcode.Runtime, Networking.Transport, HDRP, Services.QoS). The JSON and the DLLs have to come from the same build |
| `app.info`, the `UnitySubsystems` manifest, the `StreamingAssets` services config | the game's, verbatim | None. Our Unity build's services config lacked `com.unity.services.core.cloud-environment = production` |

Info.plist keys the script sets:
- `CFBundleExecutable` / `CFBundleName` = product from `app.info`.
- `CFBundleIdentifier` = `com.ZeekerssRBLX.Lethal-Company`. It is derived from `app.info`, and it decides the save and prefs folders (`~/Library/Application Support/<id>`).
- `CFBundleShortVersionString` `0.1` (the game's bundleVersion; cosmetic, since `Application.version` reads `globalgamemanagers`) and `CFBundleVersion` `0`.
- `LSApplicationCategoryType` games, `UnityBuildNumber` `7670c08855a9`, and the Unity version string.
- `NSMicrophoneUsageDescription` "Lethal Company uses the microphone for voice chat." macOS denies the microphone without it, and our Unity build failed to post-process without it.

## 5. Managed DLLs (167): ours vs the game's Windows `Managed/`

The method: hashes, then `ilspycmd` C# decompiles of both builds, normalised.
- Normalising removed `UnitySourceGeneratedAssemblyMonoScriptTypes_v1`, which is source-path tables only.
- It also replaced `__JobReflectionRegistrationOutput__<n>` with `…N`.
- Then the two decompiles were diffed.

| Class | Count | DLLs | Converter source |
|---|---|---|---|
| Identical to the game | 15 | Assembly-CSharp(-firstpass), AmazingAssets.TerrainToMesh, ClientNetworkTransform, com.olegknyazev.softmask, DissonanceVoip, DunGen(+2), EasyTextEffects, Facepunch Transport for NGO, Newtonsoft.Json, Unity.Burst.Unsafe, Unity.Collections.LowLevel.ILSupport, Unity.Jobs | game |
| Engine modules from the Unity pkg | 67 | `UnityEngine.dll` + 66 `UnityEngine.*Module.dll` | pkg `Variations/mono/Managed/` (md5-equal to ours) |
| Engine module, Windows-only | 1 | `UnityEngine.NVIDIAModule.dll` | game (needed so the game's HDRP can load; see below) |
| Mono class libraries | 22 | mscorlib, netstandard, System(.*) ×19, Mono.Security | editor pkg `unityjit-macos/` (md5-equal to ours) |
| Package DLL, **IL-identical** (only MVID / PDB id / source-path table / job-hash name differ) | 57 | all `Unity.Services.*`, `Unity.Multiplayer.Tools.*`, `Unity.XR.*`, `Unity.ProBuilder.*`, Burst, Mathematics, Collections, RenderPipelines.Core/Config/ShaderLibrary, VisualEffectGraph, TextMeshPro, Timeline, Animation.Rigging(+DocCodeExamples), AI.Navigation, Profiling.Core, InputSystem.ForUI, Netcode.Runtime, Netcode.Components, Services.QoS, SpatialTracking, XR.LegacyInputHelpers | game |
| Package DLL with a platform `#if` | 4 | see below | game; HDRP gets a 5-byte patch |
| Plugin DLL, ours | 1 | `Facepunch.Steamworks.Win64.dll` | `--plugins` (our POSIX build of MIT Facepunch 2.3.2) |

The four package DLLs with a platform `#if`:
- **`Unity.RenderPipelines.HighDefinition.Runtime`:** `#if ENABLE_NVIDIA` (DLSS). Windows has `UnityEngine.NVIDIA.DebugView`, `DebugDisplaySettings.nvidiaDebugView`, and the real `DLSSPass` that calls `NVUnityPlugin.IsLoaded()`. On Mac, `DLSSPass.SetupFeature` is `return false` and `Create` is `return null`. **Every one of the 786 differing lines is DLSS.**
- **`Unity.InputSystem`:**
  - `UNITY_STANDALONE_OSX` adds `OSXSupport.Initialize()` (Nimbus+ gamepad), `XboxGamepadMacOS`, `XboxOneGampadMacOSWireless` and `XboxGamepadMacOSWireless` (HID layouts).
  - `UNITY_STANDALONE_WIN` adds `XInputControllerWindows` and 4 extra Switch Pro HID matchers.
  - Keyboard and mouse code is identical.
  - **Converter:** rebuilt for macOS from source (§7). Against Unity's Mac build: identical decompile, identical IL, identical type/member/attribute sets. The one missing type is `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` (editor-only source-path table; nothing in the Mac player or engine modules names it). All 264 references from Assembly-CSharp, RenderPipelines.Core, VFX Graph, XR.CoreUtils and XR.OpenXR resolve.
- **`Unity.Networking.Transport`:** `UnsafeBaselibNetworkArray`. Windows registers one page allocation per packet slot (RIO), Mac one contiguous block. On a Mac the Windows version costs about capacity × 16 KB of memory but works the same way on Baselib's POSIX emulation.
- **`UnityEngine.UI`:** `MultipleDisplayUtilities.RelativeMouseAtScaled` returns `(x, y, displayIndex)` on Windows, `Display.RelativeMouseAt(pos)` elsewhere. The two only differ on multi-monitor setups.

## 6. Replacement tests

**Method:**
- Each variant is an APFS clone of `LC Hybrid.app` with `LSBackgroundOnly` added, run `-batchmode` for 45 s.
- A watchdog kills it if it ever becomes the frontmost app.
- `-batchmode` still loads InitScene, runs every `RuntimeInitializeOnLoad` and builds the HDRP pipeline, which is what failed below.
- The log is diffed against the unmodified app's after stripping timings and pointers.

| Variant | Result (log lines) |
|---|---|
| Baseline (`LC Hybrid.app`) | clean |
| B: all 61 Unity package DLLs → game's; game RIOL/ScriptingAssemblies/boot.config | `TypeLoadException: Invalid type UnityEngine.NVIDIA.DebugView for instance field UnityEngine.Rendering.HighDefinition.DebugDisplaySettings:<nvidiaDebugView>k__BackingField` → `TypeInitializationException: The type initializer for 'UnityEngine.Rendering.HighDefinition.HDRenderPipeline' threw an exception.` Also `Plugins: Couldn't open , error: dlopen(, 0x0002)` from the empty XR boot.config line |
| C: B + the game's `UnityEngine.NVIDIAModule.dll` | `cant resolve internal call to "UnityEngine.NVIDIA.NVUnityPlugin::IsLoaded"` → `MissingMethodException`. The Mac player has no NVIDIA native module |
| D: B but our Mac HDRP; boot.config without the XR line | **identical to baseline** |
| E: B + NVIDIAModule + HDRP patched (`DLSSPass.SetupFeature`: `call IsLoaded` → `ldc.i4.0` + 4×`nop` at file offset `0x7e9b8`; the method then returns false exactly like the Mac compile) | **identical to baseline**. RIOL and ScriptingAssemblies are the game's byte-for-byte |
| F: E + the game's Windows Mono class libraries | `EntryPointNotFoundException: SetThreadErrorMode` (`Interop+Kernel32`, from `System.Console..cctor`), `DllNotFoundException: BCrypt.dll` (`Guid.NewGuid`), `EntryPointNotFoundException: GetTimeZoneInformation`, `Error while loading general save data file!` |
| Script output (`assemble_app.py` with the game's DLLs) | **identical to baseline** |

**GUI test:** played by hand on 2026-10-07 with the converter's output: menus, hosting, the ship, input and
the Steam overlay all work.

## 7. What must stay Mac-specific, and how the converter gets it without shipping it

| Item | Why | Converter source |
|---|---|---|
| Player exe + dylibs, engine modules, Mono `etc`, nib, Info.plist template | platform binaries | Unity support pkg (official download) |
| Mono class libraries | the Windows build P/Invokes `kernel32`/`BCrypt` (test F) | Unity editor pkg, Range-streamed, pinned |
| HDRP | `ENABLE_NVIDIA` (tests B, C) | **5-byte IL patch** of the game's DLL, sha256-checked in and out (`d1985d5a…` → `3b2912d5…`), plus the game's `UnityEngine.NVIDIAModule.dll`. Nothing Unity-compiled is shipped. A game update that changes HDRP stops the script |
| Facepunch.Steamworks | `Pack=8` vs `Pack=4` on 195 structs, `steam_api64` vs `libsteam_api` | our MIT build (ship it) |
| InputSystem | Windows build has no macOS Xbox/Nimbus+ HID layouts, so Xbox pads on Mac aren't recognised as gamepads. Keyboard and mouse are unaffected | **`scripts/build_inputsystem.py`** compiles `com.unity.inputsystem` 1.14.0 from packages.unity.com at convert time (sha256-pinned) with Roslyn 4.3.1 (Unity's compiler, NuGet), Microsoft's netstandard 2.1 ref (byte-identical to Unity's) and the pkg's Mac engine modules, using the 115 defines from Unity's own Mac player `.rsp`. Output is deterministic and pinned (`fe17b192…`); it goes to `--plugins` and overrides the game's DLL |

**Ruled out:**
- The pkg contains no package DLLs: only engine modules, under `Variations/mono/Managed/`.
- Compiling HDRP from the registry tarball would need Unity's ILPP (job reflection registration) and the exact define set.
