// NAVIGATOR aside: search (substring or * ? wildcards), TREE/FLAT, DROP ALL, virtualized tree
// (file 30px → group 26px → channel 26px, name hierarchy on '.' and '/'), drag source for channels (Ctrl-click two → XY).
import { bridge } from '@core/bridge';
import { I } from '@core/icons';
import { DRAG_CH, DRAG_GROUP } from '@core/plot';
import { ChannelDto, FileDto, State } from '@core/types';
import { curveColor, esc, fmtInt, h, sparkSvg } from '@core/util';

interface Row { kind: 'file' | 'group' | 'node' | 'ch'; depth: number; h: number; key: string; file: FileDto; name: string; meta: string; ch?: ChannelDto; count?: number; open?: boolean; }

export function matcher(q: string): (s: string) => boolean {
  q = q.trim(); if (!q) return () => true;
  if (!q.includes('*') && !q.includes('?')) { const l = q.toLowerCase(); return s => s.toLowerCase().includes(l); }
  const re = new RegExp('^' + q.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$', 'i');
  return s => re.test(s);
}
export const nameParts = (n: string) => { const p = n.split(/[./\\]/).filter(x => x.length > 0); return p.length ? p : [n]; };

export class Navigator {
  el: HTMLElement; private tree: HTMLElement; private spacer: HTMLElement; private inner: HTMLElement; private input: HTMLInputElement; private btnTree: HTMLElement; private btnFlat: HTMLElement; private btnDrop: HTMLElement; private meta: HTMLElement;
  private S: State | null = null; private rows: Row[] = []; private collapsed = new Set<string>(); private forceHier: boolean | null = null;
  query = ''; matches: ChannelDto[] = []; private picked: string[] = [];
  constructor() {
    this.el = h(`<aside class="card aside nav"></aside>`);
    this.el.innerHTML = `<div class="card-h"><span class="ic">${I.list()}</span><span class="t">NAVIGATOR</span><span class="m" data-meta></span></div>
      <div class="nav-search"><label for="q" style="position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)">Search channels</label>
        <div class="search">${I.search()}<input id="q" type="search" placeholder="Search channels  ·  Sine*" autocomplete="off"></div>
        <div class="nav-btns"><button class="pill on" data-b="tree">TREE</button><button class="pill" data-b="flat">FLAT</button><button class="pill right" data-b="drop">DROP ALL</button></div></div>
      <div class="tree"><div class="spacer" style="position:relative"><div class="inner" style="position:absolute;left:0;right:0;top:0"></div></div></div>`;
    this.tree = this.el.querySelector('.tree')!; this.spacer = this.el.querySelector('.spacer')!; this.inner = this.el.querySelector('.inner')!;
    this.input = this.el.querySelector('input')!; this.meta = this.el.querySelector('[data-meta]')!;
    this.btnTree = this.el.querySelector('[data-b=tree]')!; this.btnFlat = this.el.querySelector('[data-b=flat]')!; this.btnDrop = this.el.querySelector('[data-b=drop]')!;
    this.input.addEventListener('input', () => { this.query = this.input.value; this.rebuild(); });
    this.btnTree.addEventListener('click', () => { this.forceHier = true; this.applyHier(true); });
    this.btnFlat.addEventListener('click', () => { this.forceHier = false; this.applyHier(false); });
    this.btnDrop.addEventListener('click', () => { if (this.matches.length) bridge.call('dropAll', { channels: this.matches.slice(0, 64).map(c => c.id) }); });
    this.tree.addEventListener('scroll', () => this.paint());
    this.inner.addEventListener('click', e => this.onClick(e));
    this.inner.addEventListener('dblclick', e => { const r = this.rowOf(e); if (r?.ch) bridge.call('addCurves', { chart: this.S?.activeChart ?? 0, channels: [r.ch.id], zone: 'plot' }).then(() => bridge.call('setMode', { mode: 'VIEW' })); });
    this.inner.addEventListener('dragstart', e => this.onDragStart(e));
    this.inner.addEventListener('dragend', () => Navigator.endPreview());
    document.addEventListener('dragover', e => Navigator.movePreview(e.clientX, e.clientY));
    document.addEventListener('drop', () => Navigator.endPreview()); document.addEventListener('dragend', () => Navigator.endPreview());
    new ResizeObserver(() => this.paint()).observe(this.tree);
  }
  private applyHier(on: boolean) { if (!this.S) return; for (const f of this.S.files) bridge.call('setHier', { file: f.name, on }); }
  private rowOf(e: Event): Row | undefined { const el = (e.target as HTMLElement).closest('[data-i]') as HTMLElement | null; return el ? this.rows[+el.dataset.i!] : undefined; }

  private sig = ''; private seen = new Set<string>();
  setState(S: State) {
    this.S = S;
    const nf = S.files.length, nc = S.files.reduce((a, f) => a + f.channelCount, 0);
    this.meta.textContent = nf ? `${nf} file${nf !== 1 ? 's' : ''} · ${nc} ch` : '';
    const anyHier = S.files.some(f => f.hier);
    this.btnTree.className = 'pill' + (anyHier ? ' on' : ''); this.btnFlat.className = 'pill' + (!anyHier && nf > 0 ? ' on' : '');
    S.files.forEach((f, i) => { if (!this.seen.has(f.name)) { this.seen.add(f.name); if (i > 0) for (const g of f.groups) this.collapsed.add('f:' + f.name + '/g:' + g.name); } });
    const sig = `${(S as any).filesVersion}|${S.files.map(f => f.name + f.hier).join(',')}|${this.query}`;
    if (sig !== this.sig) { this.sig = sig; this.rebuild(); } else this.paint();
  }

  private rebuild() {
    const S = this.S; this.rows = []; this.matches = [];
    if (!S) return;
    const m = matcher(this.query); const q = this.query.trim().length > 0;
    for (const f of S.files) {
      const fkey = 'f:' + f.name; const fopen = !this.collapsed.has(fkey);
      this.rows.push({ kind: 'file', depth: 0, h: 30, key: fkey, file: f, name: f.name.replace(/\.[^.]+$/, ''), meta: `${f.format} · ${f.channelCount} ch`, open: fopen });
      if (!fopen) continue;
      for (const g of f.groups) {
        const gkey = fkey + '/g:' + g.name; const gopen = q || !this.collapsed.has(gkey);
        const chans = g.channels.filter(c => !q || m(c.name) || m(g.name));
        if (q) this.matches.push(...chans);
        if (q && chans.length === 0) continue;
        this.rows.push({ kind: 'group', depth: 1, h: 26, key: gkey, file: f, name: g.name, meta: `${chans.length} ch`, open: gopen });
        if (!gopen) continue;
        if (f.hier) {
          // name hierarchy: Sys_Man.ACT_Apply → Sys_Man ▸ ACT_Apply
          type Node = { name: string; key: string; children: Map<string, Node>; chans: ChannelDto[] };
          const root: Node = { name: '', key: gkey, children: new Map(), chans: [] };
          for (const c of chans) {
            const parts = nameParts(c.name); let n = root;
            for (let i = 0; i < parts.length - 1; i++) { let ch = n.children.get(parts[i]); if (!ch) { ch = { name: parts[i], key: n.key + '/n:' + parts[i], children: new Map(), chans: [] }; n.children.set(parts[i], ch); } n = ch; }
            n.chans.push(c);
          }
          const count = (n: Node): number => n.chans.length + Array.from(n.children.values()).reduce((a, c) => a + count(c), 0);
          const emit = (n: Node, depth: number) => {
            for (const c of n.chans) this.rows.push({ kind: 'ch', depth, h: 26, key: c.id, file: f, name: n === root ? c.name : nameParts(c.name).slice(-1)[0], meta: this.chMeta(c), ch: c });
            for (const sub of n.children.values()) {
              const open = q || !this.collapsed.has(sub.key);
              this.rows.push({ kind: 'node', depth, h: 26, key: sub.key, file: f, name: sub.name, meta: `${count(sub)} ch`, open });
              if (open) emit(sub, depth + 1);
            }
          };
          emit(root, 2);
        } else for (const c of chans) this.rows.push({ kind: 'ch', depth: 2, h: 26, key: c.id, file: f, name: c.name, meta: this.chMeta(c), ch: c });
      }
    }
    this.btnDrop.textContent = q && this.matches.length ? `DROP ALL (${Math.min(64, this.matches.length)})` : 'DROP ALL';
    this.btnDrop.className = 'pill right' + (q && this.matches.length ? ' primary' : '');
    let top = 0; for (const r of this.rows) { (r as any).top = top; top += r.h + 1; }
    this.spacer.style.height = Math.max(0, top - 1) + 'px';
    this.paint();
  }
  private chMeta(c: ChannelDto) { return (c.unit ? c.unit + ' · ' : '') + fmtInt(c.length); }

  private paint() {
    const S = this.S; if (!S) { this.inner.innerHTML = ''; return; }
    const st = this.tree.scrollTop, vh = this.tree.clientHeight;
    let lo = 0, hi = this.rows.length;
    // binary search first visible
    let a = 0, b = this.rows.length; while (a < b) { const mid = (a + b) >> 1; if ((this.rows[mid] as any).top + this.rows[mid].h < st) a = mid + 1; else b = mid; } lo = a;
    let out = ''; let i = lo;
    for (; i < hi; i++) { const r = this.rows[i]; const top = (r as any).top; if (top > st + vh) break; out += this.rowHtml(r, i, top, S); }
    this.inner.innerHTML = out;
  }

  private rowHtml(r: Row, i: number, top: number, S: State): string {
    const pos = `style="position:absolute;top:${top}px;left:0;right:0"`;
    const chev = `<span class="chev${r.open ? ' open' : ''}">${I.chev()}</span>`;
    if (r.kind === 'file') {
      const sel = S.selectedFiles.includes(r.file.name) || S.selectedFile === r.file.name && S.selectedFiles.length === 0;
      return `<div class="row-file${S.selectedFiles.includes(r.file.name) ? ' sel' : ''}" data-i="${i}" draggable="true" title="끌어서 VIEW에 놓으면 파일 전체가 차트로" ${pos}>${chev}<span class="dbic">${I.db()}</span><span class="name">${esc(r.name)}</span><span class="chip">${esc(r.meta)}</span></div>`;
    }
    if (r.kind === 'group') return `<div class="row-grp" data-i="${i}" draggable="true" title="끌어서 VIEW에 놓으면 그룹 전체가 차트로" ${pos}>${chev}<span class="name">${esc(r.name)}</span><span class="m">${esc(r.meta)}</span></div>`;
    if (r.kind === 'node') return `<div class="row-grp" data-i="${i}" draggable="true" ${pos} style="position:absolute;top:${top}px;left:0;right:0;padding-left:${26 + 16 * (r.depth - 1)}px">${chev}<span class="name">${esc(r.name)}</span><span class="m">${esc(r.meta)}</span></div>`;
    const c = r.ch!; const sel = S.selected === c.id;
    const inChart = S.charts.find(ch => ch.curves.some(cv => cv.channelId === c.id));
    const spark = S.sparks[c.id] && (sel || inChart) ? `<span class="spark">${sparkSvg(S.sparks[c.id], inChart ? curveColor(inChart.curves.find(cv => cv.channelId === c.id)!.color, S.plot) : '#E8601C')}</span>` : '';
    // 0.9.4: the pick mark is a class (a second style attribute used to drop the absolute position → rows piled up at the top of the tree)
    const picked = this.picked.includes(c.id) && this.picked.length === 2 ? ' picked' : '';
    return `<div class="row-ch${sel ? ' sel' : ''}${picked}" data-i="${i}" data-ch="${esc(c.id)}" draggable="true" style="position:absolute;top:${top}px;left:0;right:0;padding-left:${42 + 16 * (r.depth - 2)}px"><span class="ic">${c.isText ? I.text() : c.step ? I.step() : I.line()}</span><span class="name">${esc(r.name)}</span>${spark}<span class="m">${esc(r.meta)}</span></div>`;
  }

  private onClick(e: MouseEvent) {
    const r = this.rowOf(e); if (!r) return;
    if (r.kind === 'ch') {
      const id = r.ch!.id;
      if (e.ctrlKey) { if (!this.picked.includes(id)) this.picked.push(id); if (this.picked.length > 2) this.picked.shift(); } else this.picked = [id];
      bridge.call('select', { channel: id }); this.paint(); return;
    }
    if (r.kind === 'file') {
      if (e.ctrlKey) { const sel = new Set(this.S!.selectedFiles); if (sel.has(r.file.name)) sel.delete(r.file.name); else sel.add(r.file.name); bridge.call('selectFiles', { names: Array.from(sel) }); bridge.call('selectFile', { name: r.file.name }); return; }
      if ((e.target as HTMLElement).closest('.chev')) { this.toggle(r.key); return; }
      bridge.call('selectFile', { name: r.file.name }); bridge.call('selectFiles', { names: [] }); return;
    }
    this.toggle(r.key);
  }
  private toggle(key: string) { if (this.collapsed.has(key)) this.collapsed.delete(key); else this.collapsed.add(key); this.rebuild(); }

  private onDragStart(e: DragEvent) {
    const r = this.rowOf(e); if (!r || !e.dataTransfer) return;
    let chans: ChannelDto[] = [];
    if (r.kind === 'ch') {
      const id = r.ch!.id;
      if (this.picked.length === 2 && this.picked.includes(id)) { chans = this.picked.map(p => this.find(p)!).filter(Boolean); e.dataTransfer.setData('application/x-nexys-xy', '1'); }
      else chans = [r.ch!];
    } else {
      // whole file / group / name node: every channel under it from the file data (collapsed children included), ≤ 256
      chans = this.channelsUnder(r).slice(0, 256);
      e.dataTransfer.setData(DRAG_GROUP, r.kind);
    }
    if (chans.length === 0) { e.preventDefault(); return; }
    this.startPreview(e, chans);
    e.dataTransfer.setData(DRAG_CH, JSON.stringify(chans.map(c => ({ id: c.id, name: c.name, time: c.time }))));
    e.dataTransfer.setData('text/plain', chans.map(c => c.name).join(', '));
    e.dataTransfer.effectAllowed = 'copyMove';
  }
  /** Channels under a file / group / name-node row (search filter applied like the tree). */
  channelsUnder(r: Row): ChannelDto[] {
    const m = matcher(this.query); const q = this.query.trim().length > 0;
    const keep = (c: ChannelDto, g: string) => !c.isText && (!q || m(c.name) || m(g));
    if (r.kind === 'file') return r.file.groups.flatMap(g => g.channels.filter(c => keep(c, g.name)));
    const gname = (r.key.match(/\/g:([^/]*)/) ?? [])[1] ?? ''; const g = r.file.groups.find(x => x.name === gname); if (!g) return [];
    if (r.kind === 'group') return g.channels.filter(c => keep(c, g.name));
    const path = r.key.split('/n:').slice(1);   // node path parts
    return g.channels.filter(c => keep(c, g.name) && nameParts(c.name).length > path.length && path.every((p, i) => nameParts(c.name)[i] === p));
  }
  // ---- drag preview (0.9.4): neutral card that follows the pointer and stays inside the window (the OS ghost is replaced by a blank image)
  static preview: HTMLElement | null = null;
  private static blank: HTMLCanvasElement | null = null;
  private startPreview(e: DragEvent, chans: ChannelDto[]) {
    if (!Navigator.blank) { Navigator.blank = document.createElement('canvas'); Navigator.blank.width = Navigator.blank.height = 1; }
    try { e.dataTransfer!.setDragImage(Navigator.blank, 0, 0); } catch { }
    Navigator.preview?.remove();
    const names = chans.slice(0, 3).map(c => `<span>${esc(c.name)}</span>`).join('') + (chans.length > 3 ? `<span>… +${chans.length - 3}</span>` : '');
    const el = h(`<div class="drag-preview"><b>${chans.length === 1 ? esc(chans[0].name) : `${chans.length} channels`}</b>${chans.length === 1 ? '' : names}</div>`);
    document.body.append(el); Navigator.preview = el;
    Navigator.movePreview(e.clientX, e.clientY);
  }
  static movePreview(x: number, y: number) {
    const el = Navigator.preview; if (!el) return;
    const w = el.offsetWidth, hh = el.offsetHeight;
    el.style.left = Math.max(0, Math.min(window.innerWidth - w, x + 14)) + 'px';
    el.style.top = Math.max(0, Math.min(window.innerHeight - hh, y + 14)) + 'px';
  }
  static endPreview() { Navigator.preview?.remove(); Navigator.preview = null; }
  find(id: string): ChannelDto | undefined { for (const f of this.S?.files ?? []) for (const g of f.groups) for (const c of g.channels) if (c.id === id) return c; return undefined; }
  setPicked(ids: string[]) { this.picked = ids.slice(0, 2); this.paint(); }
  setSearch(q: string) { this.input.value = q; this.query = q; this.rebuild(); }
  rowElement(channelId: string): HTMLElement | null {
    const i = this.rows.findIndex(r => r.ch?.id === channelId); if (i < 0) return null;
    this.tree.scrollTop = Math.max(0, (this.rows[i] as any).top - 60); this.paint();
    return this.inner.querySelector(`[data-ch="${CSS.escape(channelId)}"]`);
  }
}
