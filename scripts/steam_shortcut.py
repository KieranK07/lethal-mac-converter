#!/usr/bin/env python3
"""steam_shortcut.py: add (or update) a "non-Steam game" entry for the converted app in Steam's library.

    python3 -I scripts/steam_shortcut.py --app "<path>.app" [--name "Lethal Company"] [--vdf <shortcuts.vdf>] [--check]

Without --vdf it edits every Steam account's userdata/<id>/config/shortcuts.vdf on this Mac. Steam must be
closed: it rewrites the file from memory on exit. The game itself still talks to Steam as app 1966720, so
friends, lobbies and invites work; only the library tile is a shortcut. Launching from the tile is what
gets the Steam overlay: Steam only injects it into games it starts.

The tile gets the real game's artwork (cover, banner, hero, logo, icon) from Steam's own cache of it on this
Mac (appcache/librarycache/1966720), copied into the account's config/grid folder.
--check exits 0 if every account already has the tile pointing at this app, so the caller can skip closing Steam.
"""
import argparse, binascii, glob, os, shutil, struct, subprocess, sys

STEAM = os.path.expanduser('~/Library/Application Support/Steam')
ART = os.path.join(STEAM, 'appcache/librarycache/1966720')

# Binary VDF: 0x00 map start, 0x01 string, 0x02 int32, 0x08 map end; keys and strings NUL-terminated UTF-8.
def read_vdf(b):
    pos = 0
    def cstr():
        nonlocal pos
        end = b.index(b'\0', pos)
        s = b[pos:end].decode('utf-8', 'surrogateescape')
        pos = end + 1
        return s
    def node():
        nonlocal pos
        d = {}
        while True:
            t = b[pos]; pos += 1
            if t == 8:
                return d
            k = cstr()
            if t == 0: d[k] = node()
            elif t == 1: d[k] = cstr()
            elif t == 2: d[k] = struct.unpack_from('<i', b, pos)[0]; pos += 4
            else: sys.exit(f'unsupported VDF value type {t}')
    root = node()
    return root

def write_vdf(d):
    out = bytearray()
    def node(d):
        for k, v in d.items():
            key = k.encode('utf-8', 'surrogateescape') + b'\0'
            if isinstance(v, dict): out.extend(b'\0' + key); node(v); out.append(8)
            elif isinstance(v, int): out.extend(b'\2' + key + struct.pack('<i', v))
            else: out.extend(b'\1' + key + v.encode('utf-8', 'surrogateescape') + b'\0')
    node(d)
    out.append(8)
    return bytes(out)

def shortcut_appid(exe, name):
    # Steam's id for a shortcut: crc32(exe + name) with the top bit set, stored as a signed int32
    v = (binascii.crc32((exe + name).encode('utf-8')) & 0xffffffff) | 0x80000000
    return struct.unpack('<i', struct.pack('<I', v))[0]

def executable(app):
    # Steam starts the bundle's binary itself (not via `open`), so its overlay library gets injected
    return os.path.join(app, 'Contents/MacOS', os.path.basename(app)[:-len('.app')])

def shortcuts_of(vdf_path):
    return read_vdf(open(vdf_path, 'rb').read()).get('shortcuts', {}) if os.path.exists(vdf_path) else {}

def has(vdf_path, app, name):
    exe = f'"{executable(app)}"'
    return any(v.get('AppName') == name and v.get('Exe') == exe for v in shortcuts_of(vdf_path).values())

def copy_art(grid, appid):
    """Steam's custom-artwork names: <id>p (cover), <id> (banner), <id>_hero, <id>_logo; returns the icon path."""
    os.makedirs(grid, exist_ok=True)
    uid = appid & 0xffffffff
    for src, dst in (('library_600x900.jpg', f'{uid}p.jpg'), ('header.jpg', f'{uid}.jpg'), ('logo.png', f'{uid}_logo.png')):
        if os.path.exists(os.path.join(ART, src)):
            shutil.copyfile(os.path.join(ART, src), os.path.join(grid, dst))
    for hero in glob.glob(os.path.join(ART, '*', 'library_hero.jpg'))[:1]:
        shutil.copyfile(hero, os.path.join(grid, f'{uid}_hero.jpg'))
    # the community icon: the one loose 32x32 <hash>.jpg next to header.jpg
    icons = [f for f in glob.glob(os.path.join(ART, '*.jpg')) if os.path.basename(f) not in ('header.jpg', 'library_600x900.jpg')]
    if not icons:
        return ''
    shutil.copyfile(icons[0], os.path.join(grid, f'{uid}_icon.jpg'))
    return os.path.join(grid, f'{uid}_icon.jpg')

def remove(vdf_path, app, name):
    exe = f'"{executable(app)}"'
    shortcuts = shortcuts_of(vdf_path)
    gone = [k for k, v in shortcuts.items() if v.get('AppName') == name and v.get('Exe') == exe]
    if not gone:
        return
    grid = os.path.join(os.path.dirname(vdf_path), 'grid')
    for k in gone:
        uid = shortcuts[k]['appid'] & 0xffffffff
        for f in glob.glob(os.path.join(grid, f'{uid}*')):
            os.remove(f)
    kept = [v for k, v in shortcuts.items() if k not in gone]
    shutil.copy2(vdf_path, vdf_path + '.bak')
    open(vdf_path, 'wb').write(write_vdf({'shortcuts': {str(i): v for i, v in enumerate(kept)}}))
    print(f'{vdf_path}: removed "{name}"')

def add(vdf_path, app, name):
    shortcuts = shortcuts_of(vdf_path)
    exe = f'"{executable(app)}"'
    appid = shortcut_appid(exe, name)
    icon = copy_art(os.path.join(os.path.dirname(vdf_path), 'grid'), appid)
    entry = {'appid': appid, 'AppName': name, 'Exe': exe, 'StartDir': f'"{os.path.dirname(executable(app))}"',
             'icon': icon, 'ShortcutPath': '', 'LaunchOptions': '', 'IsHidden': 0, 'AllowDesktopConfig': 1,
             'AllowOverlay': 1, 'OpenVR': 0, 'Devkit': 0, 'DevkitGameID': '', 'DevkitOverrideAppID': 0,
             'LastPlayTime': 0, 'FlatpakAppID': '', 'tags': {}}
    key = next((k for k, v in shortcuts.items() if v.get('AppName') == name), str(len(shortcuts)))
    shortcuts[key] = {**shortcuts.get(key, {}), **entry}
    if os.path.exists(vdf_path): shutil.copy2(vdf_path, vdf_path + '.bak')
    os.makedirs(os.path.dirname(vdf_path), exist_ok=True)
    open(vdf_path, 'wb').write(write_vdf({'shortcuts': shortcuts}))
    print(f'{vdf_path}: "{name}" -> {app}')

def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--app', required=True)
    ap.add_argument('--name', default='Lethal Company')
    ap.add_argument('--vdf', help='one shortcuts.vdf to edit (default: every Steam account on this Mac)')
    ap.add_argument('--check', action='store_true', help='exit 0 if the tile is already there, 1 if not')
    ap.add_argument('--remove', action='store_true', help='remove the tile (and its artwork)')
    a = ap.parse_args()
    a.app = os.path.abspath(a.app.rstrip('/'))
    # numeric folders only: SteamCMD's anonymous logins leave a userdata/anonymous behind
    vdfs = [a.vdf] if a.vdf else [os.path.join(c, 'shortcuts.vdf') for c in glob.glob(os.path.join(STEAM, 'userdata/*/config'))
                                  if os.path.basename(os.path.dirname(c)).isdigit()]
    if a.check:
        sys.exit(0 if vdfs and all(has(v, a.app, a.name) for v in vdfs) else 1)
    if not a.vdf and subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0:
        sys.exit('Quit Steam first (it overwrites shortcuts.vdf when it exits), then run this again.')
    if not vdfs: sys.exit('No Steam account found on this Mac; log in to Steam once first.')
    for v in vdfs: (remove if a.remove else add)(v, a.app, a.name)

if __name__ == '__main__':
    main()
