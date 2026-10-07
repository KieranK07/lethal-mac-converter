#!/usr/bin/env python3
"""steam_appinfo.py: let the Mac Steam client offer Play on Lethal Company (app 1966720).

    python3 -I scripts/steam_appinfo.py [--check] [--undo]

Steam decides Play vs "Stream" / "not available on macOS" from the app's `common/oslist` in its local
app-info cache (appcache/appinfo.vdf). This adds `macos` to that one value for this one app; nothing else in
the cache and no other game is touched. The game's launch entry has no OS restriction, and steam_launch.py
points its launch options at the Mac app.

Steam rewrites the cache whenever the app's store data changes (a new change number); the converter then
re-applies this. Steam must be closed. A backup is kept as appinfo.vdf.lmc-bak.

Format (version 29, 0x07564429): header magic, universe, int64 offset of the key-name table. Then apps:
appid, size, info state, last updated, PICS token (u64), sha1 of the text form, change number, sha1 of the
binary form, binary KeyValues whose keys are indices into the name table. The binary checksum is recomputed
(and, as a self-test, first reproduced for the untouched entry). The text checksum is of Valve's original
server text, which the cache doesn't keep; it stays as it was, so Steam still sees the entry as current.
"""
import argparse, hashlib, os, shutil, struct, subprocess, sys

APPID = 1966720
PATH = os.path.expanduser('~/Library/Application Support/Steam/appcache/appinfo.vdf')
HEADER = struct.Struct('<4IQ20sI20s')  # appid, size, state, last_update, token, text_sha1, change, binary_sha1


def die(msg):
    sys.exit(f'steam_appinfo: {msg}')


def read_names(b):
    off = struct.unpack_from('<q', b, 8)[0]
    count = struct.unpack_from('<I', b, off)[0]
    names, pos = [], off + 4
    for _ in range(count):
        end = b.index(b'\0', pos)
        names.append(b[pos:end].decode('utf-8', 'surrogateescape'))
        pos = end + 1
    return names, off


def parse_kv(b, pos, names):
    """Binary KeyValues -> [(type, key, value)] in file order; returns (items, end)."""
    items = []
    while True:
        t = b[pos]; pos += 1
        if t == 8:
            return items, pos
        key = names[struct.unpack_from('<I', b, pos)[0]]; pos += 4
        if t == 0:
            value, pos = parse_kv(b, pos, names)
        elif t == 1:
            end = b.index(b'\0', pos); value = b[pos:end]; pos = end + 1
        elif t == 2:
            value = struct.unpack_from('<i', b, pos)[0]; pos += 4
        elif t == 7:
            value = struct.unpack_from('<Q', b, pos)[0]; pos += 8
        else:
            die(f'unsupported value type {t}')
        items.append((t, key, value))


def encode_kv(items, names):
    out = bytearray()
    for t, key, value in items:
        out += bytes([t]) + struct.pack('<I', names.index(key))
        if t == 0: out += encode_kv(value, names)
        elif t == 1: out += value + b'\0'
        elif t == 2: out += struct.pack('<i', value)
        elif t == 7: out += struct.pack('<Q', value)
    out.append(8)
    return bytes(out)


def find(items, *path):
    for t, key, value in items:
        if key == path[0]:
            return (items, (t, key, value)) if len(path) == 1 else find(value, *path[1:])
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--check', action='store_true', help='exit 0 if Lethal Company already lists macos')
    ap.add_argument('--undo', action='store_true', help='restore the backup made before the first edit')
    a = ap.parse_args()
    if a.undo:
        if subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0: die('quit Steam first')
        shutil.copy2(PATH + '.lmc-bak', PATH); print('restored'); return

    b = bytearray(open(PATH, 'rb').read())
    if struct.unpack_from('<I', b, 0)[0] != 0x07564429:
        die('unknown appinfo.vdf version (expected 29); Steam changed its format')
    names, names_off = read_names(b)
    pos = 16
    while True:
        appid, size = struct.unpack_from('<II', b, pos)
        if appid in (0, APPID): break
        pos += 8 + size
    if appid != APPID:
        die('Lethal Company is not in Steam\'s app cache yet; open its store or library page once, then retry')
    head = list(HEADER.unpack_from(b, pos))
    kv_start = pos + HEADER.size
    items, kv_end = parse_kv(b, kv_start, names)
    if kv_end != pos + 8 + size:
        die('entry length mismatch')
    blob = bytes(b[kv_start:kv_end])
    # self-test: our encoding and the binary checksum must reproduce the untouched entry
    if encode_kv(items, names) != blob or hashlib.sha1(blob).digest() != head[7]:
        die('could not reproduce the entry or its checksum; not editing')

    hit = find(items, 'appinfo', 'common', 'oslist')
    if not hit:
        die('no common/oslist for Lethal Company')
    parent, (t, key, value) = hit
    oses = value.decode().split(',')
    if a.check:
        sys.exit(0 if 'macos' in oses else 1)
    if 'macos' in oses:
        print('Lethal Company already offers Play on macOS'); return
    if subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0:
        die('quit Steam first (it rewrites the cache when it exits)')
    parent[parent.index((t, key, value))] = (t, key, ','.join(oses + ['macos']).encode())

    new_blob = encode_kv(items, names)
    head[1] = HEADER.size - 8 + len(new_blob)
    head[7] = hashlib.sha1(new_blob).digest()
    b[pos:kv_end] = HEADER.pack(*head) + new_blob
    struct.pack_into('<q', b, 8, names_off + len(new_blob) - len(blob))  # the name table moved
    if not os.path.exists(PATH + '.lmc-bak'):
        shutil.copy2(PATH, PATH + '.lmc-bak')
    tmp = PATH + '.lmc-tmp'
    open(tmp, 'wb').write(b)
    os.replace(tmp, PATH)
    print('Lethal Company now offers Play on macOS')


if __name__ == '__main__':
    main()
