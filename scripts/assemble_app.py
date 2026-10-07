#!/usr/bin/env python3
"""assemble_app.py: build the native Apple Silicon .app skeleton and its Data folder.

    python3 -I scripts/assemble_app.py --game <Windows game dir> --unity-pkg-cache <dir> \
        --plugins <dir> --data <patched Data dir> --out <path>.app

Sources (see docs/APP-LAYOUT.md for the file-by-file mapping and the evidence):
  * Unity's official MacStandaloneSupport pkg (downloaded into --unity-pkg-cache, sha256-pinned, deleted
    after extraction): the arm64 non-development Mono player, the UnityEngine.* engine modules, Mono etc/,
    the Info.plist template and MainMenu.nib.
  * Unity's official Mac Editor pkg, streamed with HTTP Range requests: only the macOS Mono class
    libraries (MonoBleedingEdge/lib/mono/unityjit-macos) are read; the stream stops right after them.
  * The user's Windows game: every Data file and every other Managed DLL. The HDRP runtime DLL gets one
    5-byte IL patch (DLSS off, exactly what the Mac compile of the same source does).
  * --plugins: our native plugins (*.dylib -> Contents/PlugIns) and our managed overrides (*.dll -> Managed).
  * --data: files that replace the game's (patched serialized files, Resources/unity_builtin_extra).
    Managed/, Plugins/ and the three startup configs in it are ignored; this stage owns them.
Shader translation is a separate stage. stdlib only.
"""
import argparse, hashlib, json, os, plistlib, re, shutil, stat, struct, subprocess, sys
import urllib.request, zlib
import xml.etree.ElementTree as ET

UNITY_VERSION, UNITY_HASH = '2022.3.62f2', '7670c08855a9'
BASE = f'https://download.unity3d.com/download_unity/{UNITY_HASH}'
SUPPORT_PKG = f'UnitySetup-Mac-Mono-Support-for-Editor-{UNITY_VERSION}.pkg'
SUPPORT_URL = f'{BASE}/MacEditorTargetInstaller/{SUPPORT_PKG}'
SUPPORT_SHA256 = 'ada8c5ebaa3431d54a5490df95196d8b439a55d57a703c5aade9d48fd33375e9'
EDITOR_URL = f'{BASE}/MacEditorInstallerArm64/Unity-{UNITY_VERSION}.pkg'

PLAYER = 'Variations/macos_arm64_player_nondevelopment_mono/UnityPlayer.app/Contents/'
ENTRY = 'Source/Player/MacPlayer/MacPlayerEntryPoint/'
SUPPORT_WANT = (PLAYER, 'Variations/mono/Managed/', 'MonoBleedingEdge/etc/', ENTRY + 'Info.plist',
                ENTRY + 'Resources/MainMenu.nib/', 'Tools/XCode/PrivacyInfo.xcprivacy')
BCL_DIR = 'Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/unityjit-macos/'
# sha256 of the macOS class libraries the game uses (unityjit-macos, Unity 2022.3.62f2 Mac editor).
# A game DLL that has a unityjit-macos namesake but no pin here stops the run: add it after checking.
BCL_SHA256 = {
    'Mono.Security.dll': 'a05b68e3399a4b2ea50a39bed06d631aaa78955849d945543e116bef3acf1e17',
    'System.ComponentModel.Composition.dll': '4a8e05c0602887330990e9a9cb315dfa1b49998d8946d968467e08cd565d0e66',
    'System.Configuration.dll': '56ec4d2ebefc39c92bd0d5fe68c2dfabd4ed5bb4a45ad3c1b3a9aa1a7a000a2d',
    'System.Core.dll': '31e5eb035690b166417192549f3cc0ff45ff0b26575245abd087550027ab3441',
    'System.Data.DataSetExtensions.dll': '25b61d0f08fa7a51380011e79fa44355ebb97296f6790cd17a039f0f5a1fa33a',
    'System.Data.dll': '0acb98c8b20f740006f55c5d4fe2bc9bb6c84260335133798afb9b8029a2bdf8',
    'System.Drawing.dll': 'cfd4252f311328abeec683b5d71c70121a0f43a8d94a4335788b2a3aa6811b05',
    'System.EnterpriseServices.dll': '6e15509cf5113a16090b873b14bd0cba5dca577c2f9504d423dc959e6f8d3e36',
    'System.IO.Compression.FileSystem.dll': '4c3e4ce8428bdbc1df117cc88270bdaa083b553e6863283c652eacbbc52e150a',
    'System.IO.Compression.dll': '1eb65b10a75a3c12606fa179c66ef341e52e6d3ab20d2fb363c2b1fd0379d4c0',
    'System.Net.Http.dll': '9e461ed7eb9f03ff6517ab8302736e73edbc6699d20fa5b20c1a83b574fe2345',
    'System.Numerics.dll': '5761b7dd8b06ec31aab444d1bf5209847619d3d5506cea371babc9af42497e28',
    'System.Runtime.Serialization.dll': '273206c5561cedca498bb04e4fb7191b23ca5013e2bc65cb22388c29ff428fc4',
    'System.Runtime.dll': 'fbc8607d8c4312460f5234c1950a74d098ff8cab5abcc1a093e55b2bbed75d30',
    'System.Security.dll': '80255cc78e33057eaedc23bfb47b0371e2160b3b4309baaa778ea781fab0b99a',
    'System.ServiceModel.Internals.dll': '0096615acbe1c10593ad88ad99fb5d210e6d3d0c54e65d64846eed62e2600cf0',
    'System.Transactions.dll': '3e50c0d0edc514f1d5582e52d2483cd38b979d130fa58bfb5dd810542a53ce65',
    'System.Xml.Linq.dll': '0faa3d926329f5bfd8162567ea2e73b1f7f6c639574bbd89bf6ebbbfa5d183f3',
    'System.Xml.dll': 'a6769c77db5d7e716b10915feb1232105860842873dcfe4e66d03e4aa5cbd9fa',
    'System.dll': '18b4f0a210fec058cf345ef176ebcacc7325ad122d7321a874ba8d4f09adf637',
    'mscorlib.dll': '9434e43dd58f9e3aba5b76c94f5da7fcec20c1bce2186c830039aa77c439b47a',
    'netstandard.dll': 'bf0b7eac9010b75413e4ec1a07a3453bc6a68eeb8e916fbed7c1f2777841f7c7',
}

# DLSSPass.SetupFeature(): `call NVUnityPlugin::IsLoaded()` -> `ldc.i4.0; nop x4`, so it returns false like the
# Mac compile (no ENABLE_NVIDIA). Needs the game's UnityEngine.NVIDIAModule.dll beside it for type loading.
HDRP_DLL = 'Unity.RenderPipelines.HighDefinition.Runtime.dll'
HDRP_SHA256_IN = 'd1985d5a5b73272d0873d85b4a85fe0d76ea1b9c9c4c527bc89f88f4c19f2ae2'
HDRP_OFFSET, HDRP_OLD, HDRP_NEW = 0x7e9b8, '287800000a2d02162a', '1600000000'
HDRP_SHA256_OUT = '3b2912d59c623f0759eeb2ffd48ff04c3ddac4c8f54d7e95a73beeedc42516fb'

OWNED = {'boot.config', 'ScriptingAssemblies.json', 'RuntimeInitializeOnLoads.json'}
MIC_TEXT = 'Lethal Company uses the microphone for voice chat.'


def die(msg):
    sys.exit(f'assemble_app: {msg}')


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def chunks(src, start, length):
    """Bytes [start, start+length) of a local file or an https URL (Range request), 1 MB at a time."""
    if src.startswith('https://'):
        req = urllib.request.Request(src, headers={'Range': f'bytes={start}-{start + length - 1}'})
        f = urllib.request.urlopen(req, timeout=60)
        if f.status != 206:
            die(f'{src}: server ignored the Range request (HTTP {f.status})')
    else:
        f = open(src, 'rb')
        f.seek(start)
    with f:
        left = length
        while left:
            b = f.read(min(1 << 20, left))
            if not b:
                die(f'{src}: truncated')
            left -= len(b)
            yield b


def payload_range(src):
    """(offset, length) of the Payload member of a flat .pkg (xar archive)."""
    magic, hsize, _, toc_len, _, _ = struct.unpack('>4sHHQQI', b''.join(chunks(src, 0, 28)))
    if magic != b'xar!':
        die(f'{src}: not a .pkg')
    toc = ET.fromstring(zlib.decompress(b''.join(chunks(src, hsize, toc_len))))
    for f in toc.iter('file'):
        if f.findtext('name') == 'Payload':
            d = f.find('data')
            return hsize + toc_len + int(d.findtext('offset')), int(d.findtext('length'))
    die(f'{src}: no Payload')


class Inflate:
    """Readable view of a gzip stream fed by an iterator of compressed chunks."""
    def __init__(self, it):
        self.it, self.z, self.buf = it, zlib.decompressobj(31), bytearray()

    def read(self, n):
        while len(self.buf) < n:
            c = next(self.it, None)
            if c is None:
                die('pkg payload ended early')
            self.buf += self.z.decompress(c)
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out


def extract(src, prefixes, dest, stop_after=None):
    """Stream src's Payload (gzip'd cpio odc) and write the regular files under any of `prefixes` to
    dest/<pkg path>. With stop_after, stop at the first entry outside that directory once inside it.
    Returns the list of extracted pkg paths."""
    s, got, inside = Inflate(chunks(src, *payload_range(src))), [], False
    while True:
        h = s.read(76)
        if h[:6] != b'070707':
            die(f'{src}: Payload is not a cpio odc archive')
        mode, namesize, size = int(h[18:24], 8), int(h[59:65], 8), int(h[65:76], 8)
        name = s.read(namesize)[:-1].decode()
        if name == 'TRAILER!!!':
            break
        path = name[2:] if name.startswith('./') else name
        if stop_after:
            if path.startswith(stop_after):
                inside = True
            elif inside:
                break
        if (stat.S_ISREG(mode) and path.startswith(prefixes) and not path.endswith('.pdb')
                and '..' not in path.split('/')):
            out = os.path.join(dest, path)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, 'wb') as f:
                left = size
                while left:
                    b = s.read(min(1 << 20, left))
                    f.write(b)
                    left -= len(b)
            os.chmod(out, 0o755 if mode & 0o111 else 0o644)
            got.append(path)
        else:
            left = size
            while left:
                left -= len(s.read(min(1 << 20, left)))
    return got


def unity_files(cache):
    """Extract (once) what we need from Unity's official pkgs into cache/unity-<hash>/."""
    root = os.path.join(cache, f'unity-{UNITY_HASH}')
    support, editor = os.path.join(root, 'support'), os.path.join(root, 'editor')
    if not os.path.exists(os.path.join(support, '.complete')):
        pkg = os.path.join(cache, SUPPORT_PKG)
        if not os.path.exists(pkg):
            print(f'downloading {SUPPORT_URL} (552 MB)')
            subprocess.run(['curl', '-fL', '--retry', '3', '-o', pkg + '.part', SUPPORT_URL], check=True)
            os.replace(pkg + '.part', pkg)
        if sha256(pkg) != SUPPORT_SHA256:
            os.remove(pkg)
            die(f'{SUPPORT_PKG}: sha256 mismatch, deleted; run again')
        shutil.rmtree(support, ignore_errors=True)
        n = len(extract(pkg, SUPPORT_WANT, support))
        open(os.path.join(support, '.complete'), 'w').close()
        os.remove(pkg)  # the Mac disk is tight: keep only the ~37 MB we use
        print(f'extracted {n} files from {SUPPORT_PKG} (pkg deleted)')
    if not os.path.exists(os.path.join(editor, '.complete')):
        print('streaming the macOS Mono class libraries out of the Unity Mac editor pkg (stops after ~0.8 GB)')
        shutil.rmtree(editor, ignore_errors=True)
        n = len(extract(EDITOR_URL, (BCL_DIR,), editor, stop_after=BCL_DIR))
        if not n:
            die('unityjit-macos not found in the editor pkg')
        open(os.path.join(editor, '.complete'), 'w').close()
        print(f'extracted {n} class-library files')
    return support, editor


def clone(src, dst):
    """APFS clone (cp -c falls back to a normal copy across volumes)."""
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if os.path.lexists(dst):
        shutil.rmtree(dst) if os.path.isdir(dst) and not os.path.islink(dst) else os.remove(dst)
    subprocess.run(['cp', '-c', '-R', src, dst], check=True)


def patch_hdrp(path):
    if sha256(path) != HDRP_SHA256_IN:
        die(f'{HDRP_DLL}: not the HDRP build this patch was made for (game update?); the Mac pack needs updating')
    b = bytearray(open(path, 'rb').read())
    if b[HDRP_OFFSET:HDRP_OFFSET + len(HDRP_OLD) // 2] != bytes.fromhex(HDRP_OLD):
        die(f'{HDRP_DLL}: unexpected bytes at the patch site')
    b[HDRP_OFFSET:HDRP_OFFSET + len(HDRP_NEW) // 2] = bytes.fromhex(HDRP_NEW)
    if hashlib.sha256(b).hexdigest() != HDRP_SHA256_OUT:
        die(f'{HDRP_DLL}: patched hash mismatch')
    open(path, 'wb').write(b)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--game', required=True, help='Windows game dir (contains <Name>_Data)')
    ap.add_argument('--unity-pkg-cache', required=True)
    ap.add_argument('--plugins', required=True, help='*.dylib -> Contents/PlugIns, *.dll -> Data/Managed')
    ap.add_argument('--data', required=True, help='patched Data dir; its files replace the game\'s')
    ap.add_argument('--out', required=True, help='output .app')
    a = ap.parse_args()
    if not a.out.endswith('.app'):
        die('--out must end in .app')
    datas = [d for d in os.listdir(a.game) if d.endswith('_Data') and os.path.isdir(os.path.join(a.game, d))]
    if len(datas) != 1:
        die(f'--game: expected one *_Data folder in {a.game}')
    gdata = os.path.join(a.game, datas[0])
    company, product = open(os.path.join(gdata, 'app.info')).read().splitlines()[:2]
    os.makedirs(a.unity_pkg_cache, exist_ok=True)
    support, editor = unity_files(a.unity_pkg_cache)
    player = os.path.join(support, PLAYER)

    if os.path.exists(a.out):
        shutil.rmtree(a.out)
    C = os.path.join(a.out, 'Contents')
    R, D = os.path.join(C, 'Resources'), os.path.join(C, 'Resources', 'Data')
    M = os.path.join(D, 'Managed')

    # Unity player runtime (official pkg)
    clone(os.path.join(player, 'MacOS/UnityPlayer'), os.path.join(C, 'MacOS', product))
    clone(os.path.join(player, 'Frameworks'), os.path.join(C, 'Frameworks'))
    clone(os.path.join(player, 'Resources/unity default resources'), os.path.join(R, 'unity default resources'))
    clone(os.path.join(support, ENTRY, 'Resources/MainMenu.nib'), os.path.join(R, 'MainMenu.nib'))
    clone(os.path.join(support, 'Tools/XCode/PrivacyInfo.xcprivacy'), os.path.join(R, 'PrivacyInfo.xcprivacy'))
    clone(os.path.join(support, 'MonoBleedingEdge/etc'), os.path.join(C, 'MonoBleedingEdge/etc'))

    # Info.plist: Unity's template with the values Unity's build writes for this game
    info = plistlib.load(open(os.path.join(support, ENTRY, 'Info.plist'), 'rb'))
    info.update({
        'CFBundleExecutable': product,
        'CFBundleGetInfoString': f'Unity Player version {UNITY_VERSION} ({UNITY_HASH}). '
                                 '(c) 2005-2025 Unity Technologies. All rights reserved.',
        'CFBundleIdentifier': re.sub(r'[^A-Za-z0-9.]', '-', f'com.{company}.{product}'),  # = save/prefs folder
        'CFBundleName': product,
        'CFBundleShortVersionString': '0.1',  # the game's bundleVersion; cosmetic (Application.version reads Data)
        'CFBundleVersion': '0',
        'LSApplicationCategoryType': 'public.app-category.games',
        'UnityBuildNumber': UNITY_HASH,
        'NSMicrophoneUsageDescription': MIC_TEXT,  # macOS refuses microphone access without it (voice chat)
    })
    plistlib.dump(info, open(os.path.join(C, 'Info.plist'), 'wb'))
    plistlib.dump({'Screenmanager Is Fullscreen mode': 'True'}, open(os.path.join(R, 'DefaultPreferences.plist'), 'wb'))

    # Data: the game's, minus Windows-only parts, plus the patched files
    clone(gdata, D)
    shutil.rmtree(os.path.join(D, 'Plugins'), ignore_errors=True)
    if os.path.exists(os.path.join(D, 'Resources/unity default resources')):
        os.remove(os.path.join(D, 'Resources/unity default resources'))  # Mac copy lives in Contents/Resources
    for dp, dn, fn in os.walk(a.data):
        rel = os.path.relpath(dp, a.data)
        if rel.split(os.sep)[0] in ('Managed', 'Plugins'):
            dn[:] = []
            continue
        for f in fn:
            if rel == '.' and f in OWNED:
                continue
            clone(os.path.join(dp, f), os.path.normpath(os.path.join(D, rel, f)))

    # Managed: the game's DLLs, with Unity's macOS engine modules and class libraries, our overrides, HDRP patch
    mods = os.path.join(support, 'Variations/mono/Managed')
    for f in os.listdir(mods):
        clone(os.path.join(mods, f), os.path.join(M, f))
    bcl = os.path.join(editor, BCL_DIR)
    for f in sorted(os.listdir(M)):
        src = next((p for p in (os.path.join(bcl, f), os.path.join(bcl, 'Facades', f)) if os.path.exists(p)), None)
        if src:
            if BCL_SHA256.get(f) != sha256(src):
                die(f'{src}: unpinned or changed class library')
            clone(src, os.path.join(M, f))
    for f in os.listdir(a.plugins):
        src = os.path.join(a.plugins, f)
        clone(src, os.path.join(M if f.endswith('.dll') else os.path.join(C, 'PlugIns'), f))
    patch_hdrp(os.path.join(M, HDRP_DLL))

    # Startup configs: the game's. Drop the empty XR pre-init entry; the Mac player tries dlopen("") on it.
    bc = os.path.join(D, 'boot.config')
    lines = [l for l in open(bc).read().splitlines() if l != 'xrsdk-pre-init-library=']
    open(bc, 'w').write('\n'.join(lines) + '\n')
    names = json.load(open(os.path.join(D, 'ScriptingAssemblies.json')))['names']
    riol = {e['assemblyName'] + '.dll' for e in json.load(open(os.path.join(D, 'RuntimeInitializeOnLoads.json')))['root']}
    missing = sorted(n for n in set(names) | riol if not os.path.exists(os.path.join(M, n)))
    if missing:
        die(f'assemblies listed by the game but missing: {missing}')

    for p in [os.path.join(C, 'MacOS', product)] + [os.path.join(dp, f) for dp, _, fn in os.walk(C)
                                                    for f in fn if f.endswith('.dylib')]:
        os.chmod(p, 0o755)
    subprocess.run(['codesign', '--force', '--deep', '-s', '-', a.out], check=True)
    print(f'built {a.out}')


if __name__ == '__main__':
    main()
