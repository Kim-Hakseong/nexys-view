// 0.10.0: MIL-STD-1553 mode — filter bar, virtualized message list, decode panel, word trend (engine chart on page 0).
import { bridge } from './bridge';
import { I } from './icons';
import { PlotView } from './plot';
import { State } from './types';
import { esc, fmtInt, h } from './util';

const ROW_H = 22, HEAD_H = 26, BLOCK = 200, MAX_SPACER = 6_000_000;
interface Row { i: number; t: number; bus: string; rt: number; sa: number; tr: number; wc: number; cwd1: number; swd1: number; err: number; act: number; n: number; data: string; icd: string | null; }
const hx = (v: number) => v.toString(16).toUpperCase().padStart(4, '0');

export class Mil1553Mode {
  el: HTMLElement; private S: State | null = null;
  private list: HTMLElement; private scroll: HTMLElement; private spacer: HTMLElement; private inner: HTMLElement; private head: HTMLElement; private meta: HTMLElement;
  private dec: HTMLElement; private decBody: HTMLElement; private decMeta: HTMLElement; private plotCard: HTMLElement; private plotHost: HTMLElement; private plotMeta: HTMLElement;
  private blocks = new Map<number, Row[]>(); private loading = new Set<number>(); private total = 0; private sel = -1; private selRow = -1; private filterMs = 0;
  private pv: PlotView | null = null; private chartId = -1; private trendId = ''; private frameTimes: number[] = [];
  filter = { rt: -1, sa: -1, tr: -1, bus: -1, errorsOnly: false };

  constructor() {
    this.el = h(`<div class="center m1553"></div>`);
    this.list = h(`<section class="card" style="flex:1"><div class="card-h"><span class="ic">${I.list()}</span><span class="t">MESSAGES</span><span class="m" data-meta></span></div>
      <div class="m1553-filters"><span class="lbl-in">RT</span><input class="in mono" data-f="rt" value="" placeholder="*" title="RT 0-31 or *"><span class="lbl-in">SA</span><input class="in mono" data-f="sa" value="" placeholder="*" title="SA 0-31 or *">
      <span class="lbl-in">T/R</span><button class="pill h24 on" data-tr="-1">ANY</button><button class="pill h24" data-tr="0">BC→RT</button><button class="pill h24" data-tr="1">RT→BC</button>
      <span class="lbl-in">BUS</span><button class="pill h24 on" data-bus="-1">A+B</button><button class="pill h24" data-bus="0">A</button><button class="pill h24" data-bus="1">B</button>
      <span class="lbl-in">ERRORS</span><button class="pill h24" data-err="1">ONLY</button><span class="sp"></span><span class="lbl-in">GO TO</span><input class="in mono" data-f="goto" placeholder="#row" style="width:80px"></div>
      <div class="tscroll m1553-scroll"><div class="tspacer"><div class="thead"><span>#</span><span>TIME (s)</span><span>BUS</span><span>RT</span><span>SA</span><span>T/R</span><span>WC</span><span>CWD1</span><span>SWD1</span><span>ERR</span><span>ACT</span><span>ICD</span><span>DATA</span></div><div class="tinner"></div></div></div></section>`);
    this.head = this.list.querySelector('.thead')!; this.meta = this.list.querySelector('[data-meta]')!;
    this.scroll = this.list.querySelector('.tscroll')!; this.spacer = this.list.querySelector('.tspacer')!; this.inner = this.list.querySelector('.tinner')!;
    this.scroll.addEventListener('scroll', () => this.paint());
    this.inner.addEventListener('click', e => { const r = (e.target as HTMLElement).closest('[data-i]') as HTMLElement | null; if (r) this.select(+r.dataset.i!, +r.dataset.row!); });
    const filters = this.list.querySelector('.m1553-filters')!;
    filters.addEventListener('click', e => {
      const b = (e.target as HTMLElement).closest('button') as HTMLElement | null; if (!b) return;
      if (b.dataset.tr != null) { this.filter.tr = +b.dataset.tr; filters.querySelectorAll('[data-tr]').forEach(x => x.classList.toggle('on', x === b)); }
      else if (b.dataset.bus != null) { this.filter.bus = +b.dataset.bus; filters.querySelectorAll('[data-bus]').forEach(x => x.classList.toggle('on', x === b)); }
      else if (b.dataset.err != null) { this.filter.errorsOnly = !this.filter.errorsOnly; b.classList.toggle('on', this.filter.errorsOnly); }
      this.applyFilter();
    });
    filters.addEventListener('change', e => {
      const inp = e.target as HTMLInputElement; const f = inp.dataset.f;
      if (f === 'goto') { const n = parseInt(inp.value.replace(/[^0-9]/g, ''), 10); if (n >= 0) this.scrollTo(n); return; }
      if (f === 'rt' || f === 'sa') { const v = inp.value.trim(); this.filter[f] = v === '' || v === '*' ? -1 : Math.max(0, Math.min(31, parseInt(v, 10) || 0)); this.applyFilter(); }
    });
    this.dec = h(`<section class="card m1553-dec"><div class="card-h"><span class="ic">${I.info()}</span><span class="t">MESSAGE</span><span class="m" data-meta></span></div><div class="scroll"><div class="dec-body"><div class="muted11" style="padding:12px">Select a message</div></div></div></section>`);
    this.decBody = this.dec.querySelector('.dec-body')!; this.decMeta = this.dec.querySelector('[data-meta]')!;
    this.decBody.addEventListener('click', e => { const w = (e.target as HTMLElement).closest('[data-ch]') as HTMLElement | null; if (w) this.trend(w.dataset.ch!, w.dataset.label || ''); });
    this.plotCard = h(`<section class="card m1553-plot"><div class="card-h"><span class="ic">${I.lineC()}</span><span class="t">TREND</span><span class="m" data-meta></span></div><div class="m1553-plot-host"><div class="muted11" style="padding:12px">Click a word in the decode panel (or a channel of the file in the navigator) to plot its trend</div></div></section>`);
    this.plotHost = this.plotCard.querySelector('.m1553-plot-host')!; this.plotMeta = this.plotCard.querySelector('[data-meta]')!;
    const bottom = h(`<div class="m1553-bottom"></div>`); bottom.append(this.dec, this.plotCard);
    this.el.append(this.list, bottom);
  }

  get scale() { const full = this.total * ROW_H; return full > MAX_SPACER ? MAX_SPACER / full : 1; }
  private rowAt(st: number) { return Math.floor(st / (ROW_H * this.scale)); }

  setState(S: State) {
    const prev = this.S; this.S = S; const m = (S as any).mil1553;
    const total = m?.filtered ?? 0;
    if (!prev || (prev as any).mil1553?.file !== m?.file || total !== this.total) { this.total = total; this.blocks.clear(); this.loading.clear(); this.spacer.style.height = (HEAD_H + Math.min(MAX_SPACER, total * ROW_H)) + 'px'; }
    this.filterMs = m?.filterMs ?? 0;
    this.meta.textContent = m?.file ? `${esc(m.file)} · ${fmtInt(total)} of ${fmtInt(m.count)} records${m.filterMs ? ` · filter ${m.filterMs.toFixed(1)} ms` : ''}` : 'open a .mon capture';
    this.paint();
    // chart on page 0
    const c = S.charts.find(c => c.id === this.chartId);
    if (c) { if (!this.pv) { this.pv = new PlotView(c, S.plot); this.plotHost.innerHTML = ''; this.plotHost.append(this.pv.el); } this.pv.update(c, S.plot); this.pv.request({ adopt: true }); }
  }

  private async applyFilter() {
    const f = this.filter; const t0 = performance.now();
    const n = await bridge.call<number>('m1553.filter', { rt: f.rt, sa: f.sa, tr: f.tr, bus: f.bus, errorsOnly: f.errorsOnly });
    this.filterMs = performance.now() - t0; this.total = n; this.blocks.clear(); this.loading.clear(); this.spacer.style.height = (HEAD_H + Math.min(MAX_SPACER, n * ROW_H)) + 'px'; this.scroll.scrollTop = 0; this.paint();
    return n;
  }

  scrollTo(row: number) { const vis = Math.max(1, Math.floor(this.scroll.clientHeight / ROW_H)); const top = Math.max(0, row - Math.floor(vis / 2)); this.scroll.scrollTop = top * ROW_H * this.scale; this.paint(); }

  private block(key: number) {
    if (this.blocks.has(key) || this.loading.has(key)) return;
    this.loading.add(key);
    bridge.call<{ rows: Row[] }>('m1553.rows', { first: key, count: BLOCK }).then(r => { this.loading.delete(key); if (!r) return; this.blocks.set(key, r.rows); if (this.blocks.size > 40) { const k = this.blocks.keys().next().value; if (k != null) this.blocks.delete(k); } this.paint(); });
  }

  paint() {
    const st = this.scroll.scrollTop, vh = this.scroll.clientHeight || 400;
    const r0 = Math.max(0, this.rowAt(st) - 2), n = Math.min(this.total - r0, Math.ceil(vh / ROW_H) + 4);
    if (n <= 0) { this.inner.innerHTML = ''; return; }
    const k0 = Math.floor(r0 / BLOCK) * BLOCK, k1 = Math.floor((r0 + n - 1) / BLOCK) * BLOCK; this.block(k0); if (k1 !== k0) this.block(k1);
    let html = '';
    for (let r = r0; r < r0 + n; r++) {
      const b = this.blocks.get(Math.floor(r / BLOCK) * BLOCK); const row = b ? b[r - Math.floor(r / BLOCK) * BLOCK] : undefined;
      if (!row) { html += `<div class="trow" style="top:${(r - r0) * ROW_H}px"><span class="dim">${fmtInt(r)}</span><span class="dim">…</span></div>`; continue; }
      const err = (row.err & ~1) !== 0;
      html += `<div class="trow${row.i === this.sel ? ' sel' : ''}${err ? ' err' : ''}" data-i="${row.i}" data-row="${r}" style="top:${(r - r0) * ROW_H}px"><span class="dim">${fmtInt(row.i)}</span><span>${row.t.toFixed(6)}</span><span${row.bus === 'B' ? ' class="busb"' : ''}>${row.bus}</span><span>${row.rt}</span><span>${row.sa}</span><span>${row.tr ? 'RT→BC' : 'BC→RT'}</span><span>${row.wc}</span><span>${hx(row.cwd1)}</span><span>${hx(row.swd1)}</span><span${err ? ' class="errv"' : ''}>${hx(row.err)}</span><span>${hx(row.act)}</span><span class="dim">${esc(row.icd ?? '')}</span><span class="dim data">${row.data}</span></div>`;
    }
    this.inner.style.top = (this.scale < 1 ? st - (this.rowAt(st) - r0) * ROW_H : r0 * ROW_H) + 'px';
    this.inner.innerHTML = html;
  }

  async select(i: number, row: number) {
    this.sel = i; this.selRow = row; this.paint();
    const d = await bridge.call<any>('m1553.decode', { i }); if (!d) return;
    this.decMeta.textContent = d.icd ? `ICD: ${d.icd}` : 'no ICD match';
    const kv = (k: string, v: string, cls = '') => `<span class="k">${k}</span><b class="${cls}">${v}</b>`;
    const words = d.words.length ? `<table class="m1553-words"><thead><tr><th>WORD</th><th>NAME</th><th>RAW</th><th>EU</th><th>UNIT</th></tr></thead><tbody>${d.words.map((w: any) => `<tr data-ch="${esc(w.channel)}" data-label="${esc(w.name)} (${esc(w.unit)})" title="Plot trend"><td class="dim">${w.word}<span class="dim"> · ${w.bits}</span></td><td>${esc(w.name)}</td><td class="mono">${w.raw}</td><td class="mono eu">${w.eu == null ? '—' : Number(w.eu).toLocaleString('en-US', { maximumFractionDigits: 6 })}</td><td>${esc(w.unit)}</td></tr>`).join('')}</tbody></table>`
      : `<div class="m1553-raw mono">${(d.raw as string[]).map((x, k) => `<span>${k + 1}:${x}</span>`).join(' ')}</div>`;
    this.decBody.innerHTML = `<div class="kv">${kv('#', fmtInt(d.i))}${kv('TIME', d.t.toFixed(6) + ' s')}${kv('COMMAND', `RT ${d.rt} · SA ${d.sa} · ${d.tr ? 'RT→BC' : 'BC→RT'} · WC ${d.wc} (${d.cwd1}${d.cwd2 !== '0000' ? ' / ' + d.cwd2 : ''})`)}${kv('STATUS', `${d.swd1}${d.swdFlags.length ? ' · ' + d.swdFlags.join(' · ') : ''}`)}${kv('ERROR', `${d.err}${d.errs.length ? ' · ' + d.errs.join(' · ') : ''}`, d.errs.some((x: string) => x !== 'HIT') ? 'errv' : '')}${kv('ACTIVITY', `${d.act} · ${d.acts.join(' ')}`)}${kv('BUS · RESP', `${d.bus} · ${(d.resp1 / 10).toFixed(1)} µs`)}${kv('WORDS', String(d.n))}</div>${words}`;
  }

  /** Plot a channel (word / EU) on the mode's own engine chart (page 0 → not shown in VIEW). */
  async trend(channelId: string, label: string) {
    if (this.chartId < 0) this.chartId = await bridge.call<number>('m1553.chart');
    const c = this.S?.charts.find(c => c.id === this.chartId);
    if (c) for (const cv of c.curves) await bridge.call('removeCurve', { chart: this.chartId, idx: 0 });
    await bridge.call('addCurves', { chart: this.chartId, channels: [channelId], zone: 'plot' });
    this.trendId = channelId; this.plotMeta.textContent = label;
  }

  /** Self-test hooks (window.nx.test.m1553()). */
  testApi() {
    const self = this;
    return {
      total: () => self.total, selected: () => self.sel, filterMs: () => self.filterMs,
      visible: () => Array.from(self.inner.querySelectorAll<HTMLElement>('.trow[data-i]')).map(r => +r.dataset.i!),
      async scrollTo(row: number) { self.scrollTo(row); await new Promise(r => setTimeout(r, 80)); return self.inner.querySelectorAll('.trow[data-i]').length; },
      /** Scroll n steps of one page each; returns per-step paint ms. */
      /** Scroll n pages; returns [paint ms, scroll-to-next-frame ms] per step. */
      async scrollSteps(n: number) { const out: number[][] = []; for (let k = 0; k < n; k++) { const t0 = performance.now(); self.scroll.scrollTop += self.scroll.clientHeight; self.paint(); const t1 = performance.now(); await new Promise(r => requestAnimationFrame(() => r(null))); out.push([t1 - t0, performance.now() - t0]); } await new Promise(r => setTimeout(r, 120)); return out; },
      async selectRow(row: number) { const el = self.inner.querySelector(`.trow[data-row="${row}"]`) as HTMLElement | null; if (!el) throw new Error('row not visible ' + row); (el as HTMLElement).click(); await new Promise(r => setTimeout(r, 120)); return self.decBody.querySelectorAll('.kv b').length; },
      async filter(f: Partial<typeof self.filter>) { Object.assign(self.filter, f); const t0 = performance.now(); const n = await self.applyFilter(); return { n, ms: performance.now() - t0, engineMs: self.filterMs }; },
      words: () => Array.from(self.decBody.querySelectorAll<HTMLElement>('tr[data-ch]')).map(r => ({ ch: r.dataset.ch, eu: (r.querySelector('.eu') as HTMLElement).textContent })),
      async trendFirst() { const r = self.decBody.querySelector('tr[data-ch]') as HTMLElement | null; if (!r) throw new Error('no ICD word'); r.click(); await new Promise(r => setTimeout(r, 200)); return self.chartId; },
      chart: () => self.chartId, plot: () => self.pv,
      frames: () => self.pv ? { frames: self.pv.frames, lastMs: self.pv.lastTotalMs } : null,
    };
  }
}
