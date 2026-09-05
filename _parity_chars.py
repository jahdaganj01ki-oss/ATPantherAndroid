import re, sys, io, json

def literals(path):
    out = []
    with io.open(path, encoding='utf-8') as f:
        for n, line in enumerate(f, 1):
            for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', line):
                s = m.group(1)
                if any(ord(c) > 127 for c in s):
                    out.append((n, s))
    return out

def cps(s):
    return ' '.join('U+%04X' % ord(c) if ord(c) > 127 else c for c in s)

targets = sys.argv[1:]
for t in targets:
    print('=' * 70)
    print(t)
    for n, s in literals(t):
        print('  %4d  %s' % (n, cps(s)))
