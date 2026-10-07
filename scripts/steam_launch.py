#!/usr/bin/env python3
"""steam_launch.py: make Play on the real Lethal Company entry in Steam (app 1966720) start the Mac app.

    python3 -I scripts/steam_launch.py --app "<path>.app" --game "<SteamCMD game dir>" [--remove] [--check]

1. The SteamCMD copy is registered as installed in Steam's default library: a symlink at
   steamapps/common/Lethal Company and its appmanifest_1966720.acf. Steam's launch entry for the game has no
   OS restriction, so Steam offers Play.
2. The game's launch options become `"<app binary>" %command%`, so Steam runs the Mac app (the .exe path
   it appends is ignored by Unity).

Steam then tracks the game as Lethal Company from the start, so the overlay (Shift+Tab), presence and invites
all work; with a separate library shortcut the overlay died when the game connected as app 1966720.
Steam must be closed: it rewrites these files from memory on exit. Every edited file is backed up as .bak.
"""
import argparse, glob, os, re, shutil, subprocess, sys

APPID = '1966720'
STEAM = os.path.expanduser('~/Library/Application Support/Steam')


# Text VDF (localconfig.vdf): "key" "value" pairs and "key" { ... } blocks. Parsed into lists of
# (key, value-or-list) to keep order, and written back with Steam's own tab layout.
def parse(text):
    tokens = re.findall(r'"((?:[^"\\]|\\.)*)"|([{}])', text)
    pos = 0
    def block():
        nonlocal pos
        out = []
        while pos < len(tokens):
            s, brace = tokens[pos]
            if brace == '}':
                pos += 1
                return out
            pos += 1
            nxt_s, nxt_brace = tokens[pos]
            if nxt_brace == '{':
                pos += 1
                out.append((s, block()))
            else:
                pos += 1
                out.append((s, nxt_s))
        return out
    return block()


def dump(items, depth=0):
    out = []
    tabs = '\t' * depth
    for k, v in items:
        if isinstance(v, list):
            out.append(f'{tabs}"{k}"\n{tabs}{{\n{dump(v, depth + 1)}{tabs}}}\n')
        else:
            out.append(f'{tabs}"{k}"\t\t"{v}"\n')
    return ''.join(out)


def child(items, key, create=True):
    for k, v in items:
        if k.lower() == key.lower() and isinstance(v, list):
            return v
    if not create:
        return None
    new = []
    items.append((key, new))
    return new


def set_value(items, key, value):
    for i, (k, v) in enumerate(items):
        if k == key:
            items[i] = (k, value)
            return
    items.append((key, value))


def launch_options(app):
    exe = os.path.join(app, 'Contents/MacOS', os.path.basename(app.rstrip('/'))[:-len('.app')])
    return '\\"' + exe + '\\" %command%'  # VDF-escaped quotes around the path


def apps_block(tree):
    return child(child(child(child(child(tree, 'UserLocalConfigStore'), 'Software'), 'Valve'), 'Steam'), 'apps')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--app', required=True)
    ap.add_argument('--game', required=True, help='SteamCMD install (has steamapps/appmanifest_1966720.acf)')
    ap.add_argument('--remove', action='store_true', help='undo: unregister and clear the launch options')
    ap.add_argument('--check', action='store_true', help='exit 0 if already set up')
    a = ap.parse_args()
    a.app = os.path.abspath(a.app.rstrip('/'))
    link = os.path.join(STEAM, 'steamapps/common/Lethal Company')
    manifest = os.path.join(STEAM, f'steamapps/appmanifest_{APPID}.acf')
    configs = [c for c in glob.glob(os.path.join(STEAM, 'userdata/*/config/localconfig.vdf'))
               if os.path.basename(os.path.dirname(os.path.dirname(c))).isdigit()]
    want = launch_options(a.app)

    def configured(path):
        tree = parse(open(path, encoding='utf-8').read())
        apps = apps_block(tree)
        app = child(apps, APPID, create=False)
        return app is not None and dict((k, v) for k, v in app if not isinstance(v, list)).get('LaunchOptions') == want

    if a.check:
        ok = os.path.islink(link) and os.path.exists(manifest) and configs and all(configured(c) for c in configs)
        sys.exit(0 if ok else 1)
    if subprocess.run(['pgrep', '-x', 'steam_osx'], capture_output=True).returncode == 0:
        sys.exit('Quit Steam first (it rewrites its config files when it exits).')

    if a.remove:
        if os.path.islink(link):
            os.remove(link)
        if os.path.exists(manifest):
            os.remove(manifest)
    else:
        if os.path.lexists(link) and not os.path.islink(link):
            sys.exit(f'{link} exists and is not our link; not touching it')
        if os.path.islink(link):
            os.remove(link)
        os.symlink(os.path.abspath(a.game), link)
        src = open(os.path.join(a.game, f'steamapps/appmanifest_{APPID}.acf'), encoding='utf-8').read()
        open(manifest, 'w', encoding='utf-8').write(src.replace('"FullValidateAfterNextUpdate"\t\t"1"',
                                                               '"FullValidateAfterNextUpdate"\t\t"0"'))
    for path in configs:
        text = open(path, encoding='utf-8').read()
        tree = parse(text)
        if dump(tree) != text:
            sys.exit(f'{path}: unexpected format; not editing it')
        app = child(apps_block(tree), APPID)
        set_value(app, 'LaunchOptions', '' if a.remove else want)
        shutil.copy2(path, path + '.bak')
        open(path, 'w', encoding='utf-8').write(dump(tree))
        print(f'{path}: launch options {"cleared" if a.remove else "set"}')
    print('removed' if a.remove else f'Lethal Company in Steam now plays {a.app}')


if __name__ == '__main__':
    main()
