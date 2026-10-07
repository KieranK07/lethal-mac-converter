#!/usr/bin/env python3
"""build_inputsystem.py: compile Unity's Input System package for the macOS player, like Unity's own Mac build.

    python3 -I scripts/build_inputsystem.py --game <Windows game dir> --unity-pkg-cache <dir> --out <plugins>/Unity.InputSystem.dll

The game ships the Windows compile of com.unity.inputsystem, which has no `#if UNITY_STANDALONE_OSX` code: no
macOS Xbox HID layouts and no Nimbus+/GameController support, so Mac pads aren't gamepads. This builds the same
package version from Unity's public registry with the compiler options, define symbols and references Unity
2022.3.62f2 uses for a macOS standalone Mono non-development player (copied from Unity's own Bee .rsp for that
build). The result is IL-identical to the Unity-built Mac DLL apart from Unity's editor-only source-path table
(UnitySourceGeneratedAssemblyMonoScriptTypes_v1, made by an editor source generator; the player never reads it).

Downloads (all sha256-pinned, kept under $LMC_CACHE, archives deleted after extraction):
  * com.unity.inputsystem 1.14.0 from packages.unity.com (Unity Companion License)
  * NETStandard.Library.Ref 2.1.0 from nuget.org (MIT): netstandard.dll, byte-identical to Unity's
    Editor/Data/NetStandard/ref/2.1.0/netstandard.dll
  * Microsoft.Net.Compilers.Toolset 4.3.1 from nuget.org (MIT): Roslyn 4.3.1, the compiler Unity 2022.3.62 ships
Engine references are the macOS UnityEngine modules that assemble_app.py extracts from Unity's Mac pkg.
Needs `dotnet` on PATH (any .NET 6+ runtime). stdlib only.
"""
import argparse, hashlib, json, os, re, shutil, subprocess, sys, tarfile, zipfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import assemble_app as A

CACHE = os.environ.get('LMC_CACHE') or os.path.expanduser('~/Library/Caches/lethal-mac-converter')
VERSION = '1.14.0'  # the game's: Unity.InputSystem 1.14.0.0, built from Library\PackageCache\com.unity.inputsystem@1.14.0
PACKAGE = (f'https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-{VERSION}.tgz',
           'e90f0147b1c7545f4b062af806097c78e3e31bdc7fe8e3c4e5fc6bb43f30f8c7')
NETSTD = ('https://api.nuget.org/v3-flatcontainer/netstandard.library.ref/2.1.0/netstandard.library.ref.2.1.0.nupkg',
          '46ea2fcbd10a817685b85af7ce0c397d12944bdc81209e272de1e05efd33c78a')
ROSLYN = ('https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.3.1/'
          'microsoft.net.compilers.toolset.4.3.1.nupkg',
          'ccbb7f75ba7271f5fad020e8b1b4eeffe5c56da5dd3a17797c2943aacf29ef78')
CSC = 'tasks/net6.0/bincore/'
CSC_FILES = {'csc.dll', 'csc.deps.json', 'csc.runtimeconfig.json',
             'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'}
# sha256 of the output. The build is deterministic (same bytes from any cache/game path). Verified 2026-10-07 against
# Unity's own Mac build of this package (docs/APP-LAYOUT.md section 5): identical types, members and IL except
# Unity's editor-only source-path table. It also covers the game's UnityEngine.UI.dll (its MVID lands in the PDB id).
OUT_SHA256 = 'fe17b19243872be0cb72865b493c7f7572d18851349ad8e9ffbf66fa8d158071'

UNITY = '2022.3.62f2'
# Unity 2022.3.62f2, macOS standalone, Mono, non-development player, API level .NET Standard 2.1, new Input System:
# the -define list from Unity's own Library/Bee/artifacts/200b0aP.dag/Unity.InputSystem.rsp for that build, minus
# the asmdef versionDefines (evaluated below). No DEVELOPMENT_BUILD / UNITY_ASSERTIONS / UNITY_EDITOR.
DEFINES = '''
UNITY_2022_3_62 UNITY_2022_3 UNITY_2022 UNITY_5_3_OR_NEWER UNITY_5_4_OR_NEWER UNITY_5_5_OR_NEWER
UNITY_5_6_OR_NEWER UNITY_2017_1_OR_NEWER UNITY_2017_2_OR_NEWER UNITY_2017_3_OR_NEWER UNITY_2017_4_OR_NEWER
UNITY_2018_1_OR_NEWER UNITY_2018_2_OR_NEWER UNITY_2018_3_OR_NEWER UNITY_2018_4_OR_NEWER UNITY_2019_1_OR_NEWER
UNITY_2019_2_OR_NEWER UNITY_2019_3_OR_NEWER UNITY_2019_4_OR_NEWER UNITY_2020_1_OR_NEWER UNITY_2020_2_OR_NEWER
UNITY_2020_3_OR_NEWER UNITY_2021_1_OR_NEWER UNITY_2021_2_OR_NEWER UNITY_2021_3_OR_NEWER UNITY_2022_1_OR_NEWER
UNITY_2022_2_OR_NEWER UNITY_2022_3_OR_NEWER PLATFORM_ARCH_64 UNITY_64 ENABLE_AR ENABLE_AUDIO ENABLE_CACHING
ENABLE_CLOTH ENABLE_MICROPHONE ENABLE_MULTIPLE_DISPLAYS ENABLE_PHYSICS ENABLE_TEXTURE_STREAMING
ENABLE_VIRTUALTEXTURING ENABLE_LZMA ENABLE_UNITYEVENTS ENABLE_VR ENABLE_WEBCAM ENABLE_UNITYWEBREQUEST
ENABLE_WWW ENABLE_CLOUD_SERVICES ENABLE_CLOUD_SERVICES_ADS ENABLE_CLOUD_SERVICES_USE_WEBREQUEST
ENABLE_CLOUD_SERVICES_CRASH_REPORTING ENABLE_CLOUD_SERVICES_NATIVE_CRASH_REPORTING
ENABLE_CLOUD_SERVICES_PURCHASING ENABLE_CLOUD_SERVICES_ANALYTICS ENABLE_CLOUD_SERVICES_BUILD
ENABLE_EDITOR_GAME_SERVICES ENABLE_UNITY_GAME_SERVICES_ANALYTICS_SUPPORT ENABLE_CLOUD_LICENSE
ENABLE_EDITOR_HUB_LICENSE ENABLE_WEBSOCKET_CLIENT ENABLE_GENERATE_NATIVE_PLUGINS_FOR_ASSEMBLIES_API
ENABLE_DIRECTOR_AUDIO ENABLE_DIRECTOR_TEXTURE ENABLE_MANAGED_JOBS ENABLE_MANAGED_TRANSFORM_JOBS
ENABLE_MANAGED_ANIMATION_JOBS ENABLE_MANAGED_AUDIO_JOBS ENABLE_MANAGED_UNITYTLS INCLUDE_DYNAMIC_GI
ENABLE_SCRIPTING_GC_WBARRIERS PLATFORM_SUPPORTS_MONO RENDER_SOFTWARE_CURSOR ENABLE_VIDEO
ENABLE_ACCELERATOR_CLIENT_DEBUGGING ENABLE_NAVIGATION_PACKAGE_DEBUG_VISUALIZATION
ENABLE_NAVIGATION_HEIGHTMESH_RUNTIME_SUPPORT ENABLE_NAVIGATION_UI_REQUIRES_PACKAGE PLATFORM_STANDALONE
TEXTCORE_1_0_OR_NEWER PLATFORM_STANDALONE_OSX UNITY_STANDALONE_OSX UNITY_STANDALONE ENABLE_GAMECENTER
ENABLE_RUNTIME_GI ENABLE_MOVIES ENABLE_NETWORK ENABLE_CRUNCH_TEXTURE_COMPRESSION ENABLE_CLUSTER_SYNC
ENABLE_CLUSTERINPUT ENABLE_SPATIALTRACKING PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP ENABLE_MONO
NET_STANDARD_2_0 NET_STANDARD NET_STANDARD_2_1 NETSTANDARD NETSTANDARD2_1 ENABLE_CUSTOM_RENDER_TEXTURE
ENABLE_DIRECTOR ENABLE_LOCALIZATION ENABLE_SPRITES ENABLE_TERRAIN ENABLE_TILEMAP ENABLE_TIMELINE
ENABLE_INPUT_SYSTEM TEXTCORE_FONT_ENGINE_1_5_OR_NEWER CSHARP_7_OR_LATER CSHARP_7_3_OR_NEWER
'''.split()
# The game's packages that the asmdef's versionDefines name (PORT_LOG "Package versions"). Built-in modules
# (com.unity.modules.*, all 1.0.0) are read from the game's ScriptingAssemblies.json instead.
GAME_PACKAGES = {'com.unity.ugui': '1.0.0'}  # no xr.oculus / xr.googlevr / xr.openvr / xr.windowsmr


def die(msg):
    sys.exit(f'build_inputsystem: {msg}')


def fetch(url, sha, name, keep):
    """Download url once (sha256-checked) into CACHE/name, keeping only the archive members keep() selects."""
    root = os.path.join(CACHE, name)
    if os.path.exists(os.path.join(root, '.complete')):
        return root
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(root)
    arc = root + '.download'
    print(f'downloading {url}')
    subprocess.run(['curl', '-fsSL', '--retry', '3', '-o', arc, url], check=True)
    if A.sha256(arc) != sha:
        os.remove(arc)
        die(f'{url}: sha256 mismatch')
    if url.endswith('.tgz'):
        with tarfile.open(arc) as t:
            members = [(m.name, lambda m=m: t.extractfile(m).read()) for m in t if m.isfile()]
            write_members(root, members, keep)
    else:
        with zipfile.ZipFile(arc) as z:
            write_members(root, [(n, lambda n=n: z.read(n)) for n in z.namelist()], keep)
    os.remove(arc)
    open(os.path.join(root, '.complete'), 'w').close()
    return root


def write_members(root, members, keep):
    for name, read in members:
        if keep(name) and '..' not in name.split('/') and not name.startswith('/'):
            out = os.path.join(root, name)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            open(out, 'wb').write(read())


def vkey(v):
    """Unity version -> comparable tuple: 2022.3.62f2 -> (2022, 3, 62, 2, 2)."""
    # ponytail: a release without suffix sorts below its own betas (6000.1.0 < 6000.1.0b9); no asmdef here hits it
    return tuple(int(x) if x.isdigit() else 'abfp'.index(x) for x in re.findall(r'\d+|[abfp]', v))


def in_range(version, expr):
    """Unity asmdef versionDefines expression: '1.2' (>=), '[a,b)', '(a,b]', '[a]'."""
    v = vkey(version)
    if expr[0] not in '[(':
        return v >= vkey(expr)
    lo, _, hi = expr[1:-1].partition(',')
    if not _:
        return v == vkey(lo)
    return ((not lo or (v >= vkey(lo) if expr[0] == '[' else v > vkey(lo))) and
            (not hi or (v <= vkey(hi) if expr[-1] == ']' else v < vkey(hi))))


def version_defines(asmdef, versions):
    return sorted({d['define'] for d in asmdef['versionDefines']
                   if d['name'] in versions and in_range(versions[d['name']], d['expression'])})


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains <Name>_Data)')
    ap.add_argument('--unity-pkg-cache', required=True, help='same as assemble_app.py\'s')
    ap.add_argument('--out', required=True, help='output Unity.InputSystem.dll')
    a = ap.parse_args()
    datas = [d for d in os.listdir(a.game) if d.endswith('_Data')]
    if len(datas) != 1:
        die(f'--game: expected one *_Data folder in {a.game}')
    gdata = os.path.join(a.game, datas[0])
    game_dll = os.path.join(gdata, 'Managed', 'Unity.InputSystem.dll')
    if f'com.unity.inputsystem@{VERSION}\\'.encode() not in open(game_dll, 'rb').read():
        die(f'the game\'s Unity.InputSystem.dll is not com.unity.inputsystem {VERSION} (game update?); update the pins')

    pkg = os.path.join(fetch(*PACKAGE, f'inputsystem-{VERSION}', lambda n: n == 'package/LICENSE.md' or (
        n.startswith('package/InputSystem/') and n.endswith(('.cs', '.asmdef')))), 'package')
    netstd = os.path.join(fetch(*NETSTD, 'netstandard-2.1.0', lambda n: n == 'ref/netstandard2.1/netstandard.dll'),
                          'ref/netstandard2.1/netstandard.dll')
    csc = os.path.join(fetch(*ROSLYN, 'roslyn-4.3.1', lambda n: n[len(CSC):] in CSC_FILES and n.startswith(CSC)),
                       CSC, 'csc.dll')
    support, _ = A.unity_files(a.unity_pkg_cache)
    engine = os.path.join(support, 'Variations/mono/Managed')

    # Sources: Unity's asmdef rule, every .cs under the asmdef's folder except folders with their own asmdef
    # (Plugins/InputForUI is Unity.InputSystem.ForUI). Editor/ stays in: it's `#if UNITY_EDITOR` code, as in Unity.
    # Order = Unity's (path, ignoring case); it decides the type order in the DLL.
    top = os.path.join(pkg, 'InputSystem')
    srcs = []
    for dp, dn, fn in os.walk(top):
        if dp != top and any(f.endswith('.asmdef') for f in fn):
            dn[:] = []
            continue
        srcs += [os.path.join(dp, f) for f in fn if f.endswith('.cs')]
    srcs.sort(key=str.upper)

    asmdef = json.load(open(os.path.join(top, 'Unity.InputSystem.asmdef')))
    names = json.load(open(os.path.join(gdata, 'ScriptingAssemblies.json')))['names']
    versions = dict(GAME_PACKAGES, Unity=UNITY)
    versions.update({'com.unity.modules.' + n[12:-10].lower(): '1.0.0' for n in names
                     if n.startswith('UnityEngine.') and n.endswith('Module.dll')})
    defines = DEFINES + version_defines(asmdef, versions)

    build = os.path.join(CACHE, f'inputsystem-{VERSION}', 'build')
    shutil.rmtree(build, ignore_errors=True)
    os.makedirs(build)
    out = os.path.join(build, 'Unity.InputSystem.dll')
    refs = [netstd, os.path.join(gdata, 'Managed', 'UnityEngine.UI.dll')] + sorted(
        os.path.join(engine, f) for f in os.listdir(engine) if f.endswith('.dll'))
    rsp = ['-target:library', f'-out:{out}', '-noconfig', '-nostdlib+', '-langversion:9.0', '-unsafe+',
           '-deterministic', '-optimize+', '-debug:portable', '-nologo', '-RuntimeMetadataVersion:v4.0.30319',
           '-nowarn:0169,0649,0282,1701,1702', '-warn:0', '-utf8output', '-preferreduilang:en-US',
           f'-pathmap:{pkg}=Library/PackageCache/com.unity.inputsystem@{VERSION},{build}=Library/Bee/artifacts']
    rsp += [f'-define:{d}' for d in defines] + [f'-r:{r}' for r in refs] + srcs
    open(os.path.join(build, 'csc.rsp'), 'w').write('\n'.join(f'"{x}"' for x in rsp) + '\n')
    env = dict(os.environ, DOTNET_ROLL_FORWARD='Major', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    print(f'compiling Unity.InputSystem {VERSION} for macOS ({len(srcs)} files)')
    r = subprocess.run(['dotnet', 'exec', csc, f'@{os.path.join(build, "csc.rsp")}'], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if r.returncode:
        die('compile failed:\n' + r.stdout[-4000:])
    got = A.sha256(out)
    if got != OUT_SHA256:
        die(f'built DLL sha256 {got} is not the verified {OUT_SHA256}')
    A.clone(out, a.out)
    shutil.rmtree(build)
    print(f'built {a.out}')


if __name__ == '__main__':
    main()
