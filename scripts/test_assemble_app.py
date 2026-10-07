"""Self-check for the pkg streaming extractor: python3 -I scripts/test_assemble_app.py (macOS, uses pkgbuild)."""
import os, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import assemble_app as A

with tempfile.TemporaryDirectory() as t:
    root = os.path.join(t, 'root')
    for rel, body in {'a/keep.dll': b'K' * 3000, 'a/sub/deep.dll': b'D', 'a/skip.pdb': b'P', 'b/other.dll': b'O'}.items():
        os.makedirs(os.path.dirname(os.path.join(root, rel)), exist_ok=True)
        open(os.path.join(root, rel), 'wb').write(body)
    pkg = os.path.join(t, 'x.pkg')
    subprocess.run(['pkgbuild', '--quiet', '--root', root, '--identifier', 'test.x', '--version', '1', pkg], check=True)
    got = A.extract(pkg, ('a/',), os.path.join(t, 'out'), stop_after='a/')
    assert sorted(got) == ['a/keep.dll', 'a/sub/deep.dll'], got
    assert open(os.path.join(t, 'out/a/keep.dll'), 'rb').read() == b'K' * 3000
print('ok')
