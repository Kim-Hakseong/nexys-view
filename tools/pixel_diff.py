"""Structural pixel comparison of app captures vs mockup captures (read-only tool, PIL + numpy).

Usage: python -X utf8 -I tools/pixel_diff.py <app.png> <mockup.png> <out_prefix> [--name NAME]
Writes <out_prefix>_diff.png (|app − mockup| amplified, side by side) and prints a markdown table of per-region metrics:
  region           : named rectangle (header, left aside, center, right aside, footer, header pills, …) in the 1440×900 viewport
  mean |Δ|         : mean absolute channel difference (0–255)
  changed px %     : share of pixels with |Δ| > 24 on any channel
  layout profile   : correlation of the row-wise and column-wise edge profiles (panel borders, row heights, column edges) — layout; immune to data pixels and text anti-aliasing
The data region of the plot (curves) is expected to differ; the chrome regions should agree (gate: layout profile correlation ≥ 0.75).
"""
import sys, json
from PIL import Image, ImageFilter
import numpy as np

REGIONS = {
    'header 52px': (0, 0, 1440, 52),
    'left aside (300)': (16, 64, 316, 864),
    'center': (328, 64, 1112, 864),
    'right aside (300)': (1124, 64, 1424, 864),
    'footer 24px': (0, 876, 1440, 900),
    'mode pills': (520, 8, 920, 44),
    'tool buttons': (900, 8, 1300, 44),
    'nav header + search': (16, 64, 316, 176),
    'nav tree rows': (16, 176, 316, 864),
    'center card header': (328, 64, 1112, 104),
}

def load(p, size=(1440, 900)):
    im = Image.open(p).convert('RGB')
    if im.size != size:
        im = im.crop((0, 0, min(im.size[0], size[0]), min(im.size[1], size[1])))
        bg = Image.new('RGB', size, (236, 234, 229)); bg.paste(im, (0, 0)); im = bg
    return im

def edges(im):
    g = im.convert('L').filter(ImageFilter.FIND_EDGES)
    a = np.asarray(g, dtype=np.float32)
    return a > 40

def corr(u, v):
    u = u - u.mean(); v = v - v.mean()
    n = float(np.sqrt((u * u).sum() * (v * v).sum()))
    return float((u * v).sum() / n) if n > 0 else 1.0

def metrics(a, b, box):
    """mean |Δ|, changed-pixel share, and layout agreement = mean of the row/column edge-profile correlations
    (panel borders, row boundaries and column edges produce the same peaks; text anti-aliasing and data pixels do not move them)."""
    x0, y0, x1, y1 = box
    A = np.asarray(a.crop(box), dtype=np.int16); B = np.asarray(b.crop(box), dtype=np.int16)
    d = np.abs(A - B)
    mean = float(d.mean())
    changed = float((d.max(axis=2) > 24).mean() * 100)
    ea = edges(a.crop(box)).astype(np.float32); eb = edges(b.crop(box)).astype(np.float32)
    rows = corr(ea.sum(axis=1), eb.sum(axis=1)); cols = corr(ea.sum(axis=0), eb.sum(axis=0))
    return mean, changed, (rows + cols) / 2

BORDER = np.array([218, 215, 208], dtype=np.int16)   # #DAD7D0 panel borders / separators (light theme)

def separators(im):
    """y of horizontal separator lines (≥ 200 px long run of border color) and x of vertical panel edges (≥ 300 px tall)."""
    A = np.asarray(im, dtype=np.int16)
    isb = (np.abs(A - BORDER).max(axis=2) <= 8)
    ys = [y for y in range(A.shape[0]) if longest_run(isb[y, :]) >= 200]
    xs = [x for x in range(A.shape[1]) if longest_run(isb[:, x]) >= 300]
    return merge(ys), merge(xs)

def longest_run(v):
    best = cur = 0
    for b in v:
        cur = cur + 1 if b else 0
        if cur > best: best = cur
    return best

def merge(vals, gap=2):
    out = []
    for v in vals:
        if out and v - out[-1][-1] <= gap: out[-1].append(v)
        else: out.append([v])
    return [int(round(sum(g) / len(g))) for g in out]

def compare_lines(a, b, tol=2):
    onlyA = [v for v in a if not any(abs(v - u) <= tol for u in b)]
    onlyB = [v for v in b if not any(abs(v - u) <= tol for u in a)]
    return onlyA, onlyB

def probe_compare(app_json, mock_json, name):
    """DOM structure comparison from probe.js output: horizontal rules (y, side, tag), bordered boxes, font histogram."""
    A = json.load(open(app_json, encoding='utf-8')); B = json.load(open(mock_json, encoding='utf-8'))
    def lines(d): return sorted(set((h[0], h[1]) for h in d['hlines']))
    la, lb = lines(A), lines(B)
    onlyA = [l for l in la if not any(abs(l[0] - m[0]) <= 1 and l[1] == m[1] for m in lb)]
    onlyB = [l for l in lb if not any(abs(l[0] - m[0]) <= 1 and l[1] == m[1] for m in la)]
    tagA = {(h[0], h[1]): h[2] for h in A['hlines']}; tagB = {(h[0], h[1]): h[2] for h in B['hlines']}
    print(f'### {name} — DOM structure (probe.js)')
    print(f'- horizontal rules: app {len(la)} / mockup {len(lb)} · matched ±1 px: {len(la) - len(onlyA)}')
    if onlyA: print('- only in app: ' + ', '.join(f'y={y} {s} <{tagA[(y, s)]}>' for y, s in onlyA))
    if onlyB: print('- only in mockup: ' + ', '.join(f'y={y} {s} <{tagB[(y, s)]}>' for y, s in onlyB))
    # boxes: compare y/height only — the mockup probe runs in a headless --dump-dom viewport that is 16 px narrower (scrollbar), so x/width shift
    ba = [tuple(b[1:4:2]) for b in A['boxes']]; bb = [tuple(b[1:4:2]) for b in B['boxes']]
    def near(x, L): return any(all(abs(x[i] - y[i]) <= 1 for i in range(2)) for y in L)
    oba = [b for b in ba if not near(b, bb)]; obb = [b for b in bb if not near(b, ba)]
    print(f'- bordered boxes (y,h): app {len(ba)} / mockup {len(bb)} · only in app: {oba or "none"} · only in mockup: {obb or "none"}')
    fa, fb = A['fonts'], B['fonts']
    def sizes(f): return sorted(set(k.split('/')[0] for k in f), key=lambda s: float(s.replace('px', '')))
    print(f'- font sizes app: {sizes(fa)} · mockup: {sizes(fb)} · only app: {sorted(set(sizes(fa)) - set(sizes(fb)))} · only mockup: {sorted(set(sizes(fb)) - set(sizes(fa)))}')
    fam = lambda f: sorted(set(k.split('/')[2] for k in f))
    print(f'- font families app: {fam(fa)} · mockup: {fam(fb)}')
    print(f'- structure differences: {len(onlyA) + len(onlyB) + len(oba) + len(obb)} (rules + boxes)')
    print()

def main():
    if '--probe' in sys.argv:
        i = sys.argv.index('--probe'); probe_compare(sys.argv[i + 1], sys.argv[i + 2], sys.argv[sys.argv.index('--name') + 1] if '--name' in sys.argv else 'probe'); return
    args = [x for x in sys.argv[1:] if not x.startswith('--')]
    name = sys.argv[sys.argv.index('--name') + 1] if '--name' in sys.argv else args[0]
    app, mock, out = args[0], args[1], args[2]
    a = load(app); b = load(mock)
    rows = []
    for k, box in REGIONS.items():
        m, c, j = metrics(a, b, box)
        rows.append((k, m, c, j))
    # side-by-side + amplified diff image
    d = np.abs(np.asarray(a, dtype=np.int16) - np.asarray(b, dtype=np.int16)).max(axis=2)
    dimg = Image.fromarray(np.clip(d * 3, 0, 255).astype(np.uint8)).convert('RGB')
    sheet = Image.new('RGB', (1440 * 3 + 32, 900), (255, 255, 255))
    sheet.paste(a, (0, 0)); sheet.paste(b, (1456, 0)); sheet.paste(dimg, (2912, 0))
    sheet.save(out + '_diff.png')
    print(f'### {name}')
    print('| region | mean abs diff | changed px % | layout profile corr | verdict |')
    print('|---|---|---|---|---|')
    for k, m, c, j in rows:
        verdict = 'data region' if k == 'center' else ('OK' if j >= 0.75 else 'DIFF')
        print(f'| {k} | {m:.1f} | {c:.1f} | {j:.2f} | {verdict} |')
    ya, xa = separators(a); yb, xb = separators(b)
    oyA, oyB = compare_lines(ya, yb); oxA, oxB = compare_lines(xa, xb)
    print()
    print(f'Separator lines (border #DAD7D0 runs): horizontal app {len(ya)} / mockup {len(yb)} — only in app: {oyA or "none"}, only in mockup: {oyB or "none"}')
    print(f'Panel column edges: app {xa} / mockup {xb} — only in app: {oxA or "none"}, only in mockup: {oxB or "none"}')
    print(f'Structure differences (lines ±2 px): {len(oyA) + len(oyB) + len(oxA) + len(oxB)}')
    json.dump({'regions': {k: {'mean': m, 'changed_pct': c, 'layout_corr': j} for k, m, c, j in rows}, 'h_app': ya, 'h_mock': yb, 'v_app': xa, 'v_mock': xb, 'only_app_h': oyA, 'only_mock_h': oyB, 'only_app_v': oxA, 'only_mock_v': oxB}, open(out + '_diff.json', 'w'), indent=1)

if __name__ == '__main__':
    main()
