#!/usr/bin/env python3
"""steam_shortcut.py: add (or update) a "non-Steam game" entry for the converted app in Steam's library.

    python3 -I scripts/steam_shortcut.py --app "<path>.app" [--name "Lethal Company"] [--vdf <shortcuts.vdf>]

Without --vdf it edits every Steam account's userdata/<id>/config/shortcuts.vdf on this Mac. Steam must be
closed: it rewrites the file from memory on exit. The game itself still talks to Steam as app 1966720, so
friends, lobbies and invites work; only the library tile is a shortcut.
"""
import argparse, binascii, glob, os, shutil, struct, subprocess, sys

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

def add(vdf_path, app, name):
    shortcuts = read_vdf(open(vdf_path, 'rb').read()).get('shortcuts', {}) if os.path.exists(vdf_path) else {}
    exe = f'"{app}"'
    entry = {'appid': shortcut_appid(exe, name), 'AppName': name, 'Exe': exe, 'StartDir': f'"{os.path.dirname(app)}"',
             'icon': '', 'ShortcutPath': '', 'LaunchOptions': '', 'IsHidden': 0, 'AllowDesktopConfig': 1,
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
    a = ap.parse_args()
    if a.vdf:
        add(a.vdf, a.app, a.name); return
    if subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0:
        sys.exit('Quit Steam first (it overwrites shortcuts.vdf when it exits), then run this again.')
    users = glob.glob(os.path.expanduser('~/Library/Application Support/Steam/userdata/*/config'))
    if not users: sys.exit('No Steam account found on this Mac; log in to Steam once first.')
    for cfg in users: add(os.path.join(cfg, 'shortcuts.vdf'), a.app, a.name)

if __name__ == '__main__':
    main()
