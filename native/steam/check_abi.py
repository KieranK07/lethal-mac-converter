#!/usr/bin/env python3
"""ABI check against Valve's own SDK 1.48 build (x86_64 slice).
Usage: check_abi.py <valve 1.48 libsteam_api> <our libsteam_api.dylib>

For every entry point Facepunch imports that Valve's 1.48 lib implements, the sequence of vtable
slots it calls through and the accessor statics it goes through must be identical in our x86_64
and arm64 slices. Interface version strings we embed must all appear in Valve's 1.48 lib.
"""
import os, re, subprocess, sys

VALVE, OURS = sys.argv[1:3]
ACC = '()::s_CallbackCounterAndContext'
ADAPTED = {'SteamAPI_Init', 'SteamInternal_GameServer_Init'}  # manual.cpp: reimplemented over the newer core


def run(*a):
    return subprocess.run(a, capture_output=True, text=True, check=True).stdout


def fingerprints(lib, arch):
    syms = {}
    for l in run('nm', '-C', '-arch', arch, lib).splitlines():
        p = l.split(None, 2)
        if len(p) == 3 and re.fullmatch(r'[0-9a-f]+', p[0]):
            syms[int(p[0], 16)] = p[2]
    fps, cur, regs, page = {}, None, {}, {}
    for l in run('otool', '-arch', arch, '-tV', lib).splitlines():
        if l.startswith('_') and l.endswith(':'):
            cur = fps.setdefault(l[1:-1], [])
            regs, page = {}, {}
            continue
        p = l.split('\t')
        if cur is None or len(p) < 3:
            continue
        mn, ops = p[1].strip(), p[2]
        if ACC in l:  # x86_64: otool annotates rip-relative refs
            cur.append(('acc', l.split('## ')[1].split(ACC)[0]))
        if arch == 'x86_64':
            m = re.match(r'(0x[0-9a-f]+)?\(%(\w+)\), %(\w+)$', ops)
            if mn == 'movq' and m and m.group(2) != 'rip':
                regs[m.group(3)] = int(m.group(1) or '0', 16)
            elif mn in ('jmpq', 'callq') and ops.startswith('*'):
                m = re.match(r'\*(0x[0-9a-f]+)?\(%(\w+)\)', ops)
                cur.append(('vt', int(m.group(1) or '0', 16) if m else regs.get(ops[2:].split()[0])))
        else:
            m = re.match(r'(x\d+), \[(x\d+)(?:, #(0x[0-9a-f]+|\d+))?\]$', ops)
            if mn == 'ldr' and m:
                regs[m.group(1)] = int(m.group(3) or '0', 0)
            elif mn in ('br', 'blr'):
                cur.append(('vt', regs.get(ops.strip())))
            elif mn == 'adrp':
                page[ops.split(',')[0]] = int(ops.split(';')[1], 16)
            elif mn == 'add':
                m = re.match(r'(x\d+), (x\d+), #(0x[0-9a-f]+)', ops)
                if m and m.group(2) in page:
                    s = syms.get(page[m.group(2)] + int(m.group(3), 16), '')
                    if s.endswith(ACC):
                        cur.append(('acc', s[:-len(ACC)]))
    return fps


def version_strings(lib, arch):
    out = run('otool', '-arch', arch, '-v', '-s', '__TEXT', '__cstring', lib)
    return set(re.findall(r'\b(?:Steam|STEAM)\w*?_?V?\d{3}\b', out))


imports = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'imports.txt')).read().split()
valve = fingerprints(VALVE, 'x86_64')
ours = {a: fingerprints(OURS, a) for a in ('x86_64', 'arm64')}
bad = compared = calls = 0
for name in imports:
    if name not in valve or name not in ours['arm64'] or name in ADAPTED:
        continue  # re-exported core functions have no body here
    compared += 1
    calls += len(valve[name])
    for a in ours:
        if ours[a][name] != valve[name]:
            bad += 1
            print(f'MISMATCH {a} {name}: valve {valve[name]} ours {ours[a][name]}')
extra = version_strings(OURS, 'arm64') - version_strings(VALVE, 'x86_64')
if extra:
    bad += 1
    print('version strings not in Valve 1.48:', sorted(extra))
print(f'abi: {compared} functions / {calls} vtable+accessor refs match Valve 1.48 on x86_64+arm64'
      if not bad else f'abi: {bad} mismatches')
sys.exit(1 if bad else 0)
