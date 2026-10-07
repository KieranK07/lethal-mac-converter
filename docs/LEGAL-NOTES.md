# Legal and licensing notes

Researched 2026-10-07 for a private hobby repo. **Not legal advice.** *(unverified)* = not confirmed from a primary source.

## Bottom line

- **Private, own-copy, nothing redistributed: low practical risk.** Nothing is decrypted or bypassed (DMCA §1201 not triggered); real Steam still checks ownership.
- **Unity is the weakest link.** No Unity term lets a *player* fetch the Mac runtime to run *someone else's* game; the ToS bans "port" and "unbundle". Low exposure while private, high if published.
- **Steam's SSA literally forbids "translate … modify" and "tamper with the execution of" games** unless applicable law allows it. Every BepInEx mod breaks the same clause. Lethal Company has no EULA; Zeekerss tolerates mods.
- **The clean-room voice plugin is the best-supported part in law** (*Sega*, *Connectix*, EU Art. 6, *SAS v WPL*), if the spec stays factual and its provenance note is accurate. It currently omits the decompile.
- **Valve, Discord, HLSLcc, Facepunch, open-source parts: low.** Valve headers on GitHub sit outside the SDK grant but are widely mirrored. Keep the shim a pure forwarder.
- **Before going public:** get Zeekerss's written OK, gate the Unity download behind explicit acceptance, add a non-affiliation disclaimer, never ship outputs (`.app`, translated shaders).

## Components

| Component | Source | License / terms | Risk | Why | Mitigation |
|---|---|---|---|---|---|
| Unity Mac player (`UnityPlayer.dylib`, engine DLLs) | `download.unity3d.com` pkg | Unity ToS §17.2; Software Terms §2 | **Med** (high if public) | Runtime licensed only to developers, inside *their* Projects | Acceptance prompt; prefer the user's own Unity Hub install; ask Zeekerss; never ship the `.app` |
| Translated shaders in game data | Generated locally | SSA §2.G; copyright of Zeekerss, Unity HDRP, asset authors | Low–Med | A persistent adaptation, but private | Never commit or share outputs; keep originals |
| Depot download + data patch | SteamCMD, user's login | SSA §2.G, §4.B "tamper" | Low | Official tool; same act as any mod; no DRM touched | Patch a copy; revert step |
| Voice plugin (clean-room) | Our code; WebRTC M59, RNNoise, Opus (BSD); sse2neon (MIT) | BSD/MIT | Low | Functional facts aren't protected | Fix the spec's provenance; firewall the RE notes |
| Steam shim | Our code; SDK 1.48 headers (Facepunch commit `d060548`); `libsteam_api.dylib` (Facepunch 2.4.1) | Steamworks SDK Access Agreement §1.1, §2.4 | Low–Med | Headers aren't redistributables; Facepunch's MIT can't relicense Valve files; flat layer checked against disassembly (§2.4) | Fetch at build (pinned hash, done); forward only, never talk to Steam directly |
| HLSLcc | GitHub | MIT + bstrlib BSD-3 | Low | Permissive | Keep notices |
| Facepunch.Steamworks 2.3.2; Mono runtime in pkg | GitHub; Unity pkg | MIT ([mono fork](https://github.com/Unity-Technologies/mono) MIT/BSD) | Low | Open source | Keep notices |
| Discord Game SDK 3.2.1 | `dl-game-sdk.discordapp.net` | Discord Developer Terms; no license file in zip | Low | Same release the game ships (DLL byte-identical) | Fetch at build, or no-op stub |
| Paid Asset Store DLLs | User's copy | Asset Store EULA binds Zeekerss, not players | Low | Run unmodified | Never redistribute |
| Name "Lethal Company" | README | Registered TM, Zeekerss Inc. | Low | Nominative use | Disclaimer; no logo or art |

## 1. Steam Subscriber Agreement ([SSA](https://store.steampowered.com/subscriber_agreement/), updated 2026-09-10)

- §2.G: *"Except as otherwise permitted … or under applicable law notwithstanding these restrictions, you may not … translate, reverse engineer, … modify, disassemble, decompile, create derivative works based on"* the Content.
- §4.B: *"You agree that you will not tamper with the execution of Steam or Content and Services unless otherwise authorized by Valve."*
- §2.G(ii) bans *"emulat[ing] or redirect[ing] the communication protocols used by Valve"*. We don't do this: the real Steam is used.
- Read literally, the patch breaches the contract; the "applicable law" carve-out matters in the EU (§7). No bans for private, non-cheating mods found.

## 2. Lethal Company terms

- **No game EULA:** the [store](https://store.steampowered.com/app/1966720/) shows only "Copyright 2025 Zeekerss Inc.", `/eula/1966720_eula_0` errors like a non-existent app, and the install has no license file. The SSA governs.
- Zeekerss, [V80 notes](https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/1828894815563326) (2026-04-05), on mod-loader detection: *"This does not mean that mods are officially supported or recommended, but I understand the value they add."* Tolerance, not a licence.

## 3. Unity

- [ToS](https://unity.com/legal/terms-of-service) §17.2 (updated 2026-06-30) says you may not: *"(a) Reproduce, modify, adapt, translate, port … except as expressly permitted by applicable law"*, *"(h) Unbundle the component parts of any Offering for use separate from each other"*.
- [Software Terms](https://unity.com/legal/terms-of-service/software) §2.1: Editor rights exist *"to develop your Projects"*. §2.2: the Runtime may be distributed *"solely as embedded or incorporated into your Projects"*. Grants are *"primarily for creators creating first-party content"*.
- **Licensee** of a shipped game's runtime is the developer. Players get the *Windows* runtime via Zeekerss; nothing grants them a Mac one.
- The June 2026 "User Modifications" clause (conversion, porting) needs a separate grant only for *non*-supported platforms; macOS arm64 is supported.
- **Runtime fee:** [cancelled](https://unity.com/blog/unity-is-canceling-the-runtime-fee); §2.2 confirms none for Unity 6 or earlier.
- **Precedent:** [unify](https://github.com/0xf4b1/unify) (MIT, since 2023) does the same player swap for Linux with official Unity binaries; [PortMaster](https://portmaster.app/) ports Unity games on a bring-your-own-files basis. No Unity enforcement against either was found *(absence of evidence only)*. Unity did send a trademark takedown to [UnityLauncherPro](https://github.com/unitycoder/UnityLauncherPro/issues/203).
- **Acceptance step:** yes. Show the ToS and Software Terms links and require a typed "I agree" before downloading; the ToS binds anyone *"using the Offerings"*. This is honest and puts the act on the user, but doesn't close the scope gap. Stronger: copy from the user's own Unity Hub install.

## 4. HLSLcc

[license.txt](https://github.com/Unity-Technologies/HLSLcc/blob/master/license.txt) is MIT ("Copyright (c) 2012 James Jones … 2014-2016 Unity Technologies") plus bstrlib under BSD-3. Fetch and build freely; keep notices if binaries are shared.

## 5. Valve redistributables

- [SDK Access Agreement](https://partner.steamgames.com/documentation/sdk_access_agreement/) §1.1: the SDK may be *"locally reproduce[d] … solely to develop the Licensee Software"*. Only `redistributable_bin` may be distributed, *"along with the Licensee Software"*. Acceptance is *"BY DOWNLOADING AND/OR USING THE SDK"*.
- §2.4: *"will not … reverse engineer the functionality of the SDK or develop software to replace the SDK's functionality"*; talk to Steam *"always through the … API provided by the SDK Redistributables"*. The shim re-exposes the 1.48 flat API over a newer Valve-signed core: keep it forwarding-only.
- [Facepunch](https://github.com/Facepunch/Facepunch.Steamworks)'s MIT ("Copyright (c) 2016 Facepunch Studios") covers its own code, not Valve's `UnityPlugin/redistributable_bin` binaries or `Generator/steam_sdk` headers. Public headers are outside Valve's grant, but [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) does the same, with no Valve objection found.
- Fetching at build time for private use: low risk; not vendoring is right. Alternative: the user downloads the SDK from Valve *(unclear whether partner status is required)*.

## 6. Discord Game SDK

The [docs](https://docs.discord.com/developers/developer-tools/game-sdk) mark the SDK *archived* and call the `lib/` files *"the things you want to distribute with your game"*. The [Developer Terms](https://support-dev.discord.com/hc/en-us/articles/8562894815383) license use *"solely as necessary to integrate with, develop, and operate"* apps *(live page blocked with 403; wording from search snippets)*. The zip has no license file. Fetching the Mac build of the release the game already ships: low risk. A no-op stub avoids the question.

## 7. Interoperability law

- **US:** [§1201(f)](https://www.law.cornell.edu/uscode/text/17/1201) only excuses *circumvention*. Nothing is circumvented here, so it's moot.
  - Fair use: [*Sega v. Accolade*](https://en.wikipedia.org/wiki/Sega_v._Accolade): disassembly is fair *"where disassembly is the only way to gain access to the ideas and functional elements … and where there is a legitimate reason"*. [*Sony v. Connectix*](https://en.wikipedia.org/wiki/Sony_Computer_Entertainment,_Inc._v._Connectix_Corp.) extended it to intermediate copying; [*Google v. Oracle*](https://en.wikipedia.org/wiki/Google_LLC_v._Oracle_America,_Inc.) supports re-declaring APIs.
  - Caveats: [*Bowers v. Baystate*](https://en.wikipedia.org/wiki/Bowers_v._Baystate_Technologies) let a contract override that fair use. [bnetd](https://en.wikipedia.org/wiki/Bnetd) lost on the EULA and on §1201. Under [*Vernor*](https://en.wikipedia.org/wiki/Vernor_v._Autodesk,_Inc.), Steam users are likely licensees, so the [§117](https://www.law.cornell.edu/uscode/text/17/117) "essential step" adaptation right probably doesn't apply.
- **EU** ([Dir. 2009/24](https://eur-lex.europa.eu/eli/dir/2009/24/oj)):
  - Art. 5(3) lets a user *"observe, study or test the functioning"* of a program.
  - Art. 6 allows decompilation *"indispensable to obtain the information necessary to achieve the interoperability of an independently created computer program"*.
  - Art. 8 makes contract terms contrary to Art. 6 and 5(3) *"null and void"*. Art. 5(1) (adaptation for use) yields to *"specific contractual provisions"*, which the SSA has.
- **Verdict:** the clean-room plugin fits Art. 6 and *Sega* squarely. Porting the whole game to another OS is less clearly covered: it rests on fair use (private, non-substitutive) and low enforcement, not a statutory exception.

## 8. Clean-room spec

- Facts are unprotected: which WebRTC components are on, parameter values, call order, enum maps. See 17 U.S.C. §102(b), Directive Art. 1(2) (*"ideas and principles … underlie its interfaces, are not protected"*), and [*SAS v WPL*](https://en.wikipedia.org/wiki/SAS_Institute_Inc_v_World_Programming_Ltd).
- Deriving those facts from a decompile is fine under Art. 6. Art. 6(2)(b) allows passing them to the implementer because that is *"necessary for the interoperability"*.
- **Problems:**
  1. `native/voice/SPEC.md` lists its sources as P/Invoke, black-box tests and fingerprinting. If any fact came from the decompile, say so: an inaccurate clean-room record hurts more than the decompile.
  2. Keep pseudocode, Ghidra output and structure mirroring out of the spec.
  3. Publishing the spec widely is greyer under Art. 6(2)(b).

## 9. Trademark

"LETHAL COMPANY" is registered to Zeekerss Inc. (Reg. 2025-04-01, class 9, serials 98433542/98433548, per [Trademarkia](https://www.trademarkia.com/owners/zeekerss-inc); *TSDR not checked*). [Nominative use](https://en.wikipedia.org/wiki/Nominative_use) (*New Kids*) (needed, minimal, no implied endorsement): "lethal-mac-converter" and "converts Lethal Company" pass. Add: *"Unofficial. Not affiliated with or endorsed by Zeekerss Inc., Valve, Unity or Discord. Requires your own copy."* Don't use the logo or art, and don't put "Unity" in the name.

## 10. Asset Store plugins

The [Asset Store EULA](https://unity.com/legal/as-terms) binds the provider and the "END-USER" who bought the asset (Zeekerss), letting him distribute it *"as incorporated and embedded in"* his game. Players aren't parties; their rights come via the SSA. Running the compiled plugins unmodified is ordinary use. Translating the Procedural Lightning shader bytecode is a private adaptation (same issue as §1): low risk, unless translated shaders or the `.app` are shared.
