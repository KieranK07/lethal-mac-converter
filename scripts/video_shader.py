#!/usr/bin/env python3
"""video_shader.py: add Unity's macOS-only built-in shader Hidden/VideoDecodeOSX to the shader stage's output.

    python3 -I scripts/video_shader.py --unity-pkg-cache <dir> --lmc <lmc.dll> --data <metalize output dir>

The game's ScriptMapper names it (builtin_extra pathID 16002) but the Windows build strips it; Unity's Mac build
keeps it. It comes from the Unity Mac editor pkg's unity_builtin_extra (assemble_app.unity_files, sha256-pinned);
`lmc videoosx` turns it into the Metal player object (byte-identical to Unity's, pinned) and inserts it.
"""
import argparse, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import assemble_app as A

ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
ap.add_argument('--unity-pkg-cache', required=True, help='same as assemble_app.py\'s')
ap.add_argument('--lmc', required=True, help='path to lmc.dll')
ap.add_argument('--data', required=True, help='Data dir written by `lmc metalize`')
a = ap.parse_args()
_, editor = A.unity_files(a.unity_pkg_cache)
subprocess.run(['dotnet', a.lmc, 'videoosx', os.path.join(editor, A.EDITOR_EXTRA), a.data], check=True)
