#!/usr/bin/env python3
"""compare_apps.py: how a converted app differs from a reference build (e.g. the hand-made hybrid).

    python3 -I scripts/compare_apps.py <A.app> <B.app> [--lmc <path to lmc>]

Lists every file under Contents that differs or exists on one side only. With --lmc, the Unity serialized
files that differ are compared object by object, so shader objects can be told apart from everything else.
"""
import argparse, hashlib, os, subprocess

def files(app):
    root = os.path.join(app, 'Contents')
    out = {}
    for d, _, names in os.walk(root):
        for n in names:
            p = os.path.join(d, n)
            if os.path.islink(p): continue
            h = hashlib.sha256()
            with open(p, 'rb') as f:
                for chunk in iter(lambda: f.read(1 << 20), b''): h.update(chunk)
            out[os.path.relpath(p, root)] = h.hexdigest()
    return out

def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('a'); ap.add_argument('b'); ap.add_argument('--lmc')
    a = ap.parse_args()
    fa, fb = files(a.a), files(a.b)
    same = [p for p in fa if fb.get(p) == fa[p]]
    print(f'{len(same)} identical files')
    for p in sorted(set(fa) | set(fb)):
        if p not in fb: print(f'only A  {p}')
        elif p not in fa: print(f'only B  {p}')
        elif fa[p] != fb[p]: print(f'differ  {p}')
    if a.lmc:
        data = lambda app: os.path.join(app, 'Contents', 'Resources', 'Data')
        subprocess.run([a.lmc, 'objdiff', data(a.a), data(a.b)], check=True)

if __name__ == '__main__':
    main()
