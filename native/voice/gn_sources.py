"""Print the .c/.cc sources of named targets in an M59 BUILD.gn, tagged with the `if (...)` they sit under.

Usage: gn_sources.py BUILD.gn target [target ...]
Not a GN evaluator: it tracks brace nesting and the condition text so a human (or the build script) can pick.
"""
import re
import sys

path, targets = sys.argv[1], set(sys.argv[2:])
text = open(path).read()
text = re.sub(r"#[^\n]*", "", text)

i, n = 0, len(text)
while i < n:
    m = re.compile(r'(\w+)\("([^"]+)"\)\s*\{').search(text, i)
    if not m:
        break
    kind, name = m.group(1), m.group(2)
    depth, j = 1, m.end()
    conds = []  # stack of (depth_at_open, condition)
    while j < n and depth:
        c = text[j]
        if c == "{":
            pre = text[max(m.end(), j - 200):j]
            cm = re.search(r"(if\s*\((.*?)\)|else)\s*$", pre, re.S)
            depth += 1
            if cm:
                conds.append((depth, " ".join(cm.group(0).split())))
        elif c == "}":
            if conds and conds[-1][0] == depth:
                conds.pop()
            depth -= 1
        elif c == '"' and name in targets:
            k = text.index('"', j + 1)
            s = text[j + 1:k]
            if re.search(r"\.(c|cc)$", s):
                print(f"{name}\t{' & '.join(c for _, c in conds) or '-'}\t{s}")
            j = k
        j += 1
    i = j
