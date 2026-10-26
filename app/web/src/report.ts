// REPORT: blocks card (left), PAGE preview (center, PNG from the engine), block properties (right).
import { bridge } from './bridge';
import { I } from './icons';
import { State } from './types';
import { esc, h } from './util';

export class Report {
  el: HTMLElement; left: HTMLElement; center: HTMLElement; right: HTMLElement; private img: HTMLImageElement; private version = -1; private S: State | null = null;
  constructor() {
    this.el = h(`<div style="display:contents"></div>`);
    this.left = h(`<aside class="card aside"><div class="card-h"><span class="ic">${I.list()}</span><span class="t">REPORT BLOCKS</span><span class="m"></span></div><div class="rep-blocks"><div class="pills"><button class="pill h26" data-a="add:Title">+ TITLE</button><button class="pill h26" data-a="add:Text">+ TEXT</button><button class="pill h26" data-a="add:Chart">+ CHART</button><button class="pill h26" data-a="add:Table">+ TABLE</button></div><div class="list"></div><div class="pills"><button class="pill h26" data-a="png">EXPORT PNG</button><button class="pill h26" data-a="pdf">EXPORT PDF</button></div></div></aside>`);
    this.center = h(`<section class="card" style="flex:1"><div class="card-h"><span class="ic">${I.file()}</span><span class="t">PAGE</span><span class="m">A4 · 794 × 1123</span></div><div class="page-host"><img alt="Report page"></div></section>`);
    this.img = this.center.querySelector('img')!;
    this.right = h(`<aside class="card aside"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">PROPERTIES</span><span class="m">report</span></div><div class="prop-body"></div></aside>`);
    this.el.append(this.left, this.center, this.right);
    this.left.addEventListener('click', e => {
      const t = e.target as HTMLElement; const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
      if (a.startsWith('add:')) bridge.call('report.add', { kind: a.slice(4), chart: this.S?.activeChart ?? 0 });
      else if (a === 'png' || a === 'pdf') bridge.call('exportDialog', { pdf: a === 'pdf' });
      else if (a.startsWith('rm:')) bridge.call('report.remove', { idx: +a.slice(3) });
      else if (a.startsWith('up:')) bridge.call('report.move', { idx: +a.slice(3), dir: -1 });
      else if (a.startsWith('dn:')) bridge.call('report.move', { idx: +a.slice(3), dir: 1 });
    });
    this.left.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.txt != null) bridge.call('report.text', { idx: +t.dataset.txt, text: t.value }); });
    new ResizeObserver(() => this.refreshPage(true)).observe(this.center);
  }
  setState(S: State) {
    this.S = S; const R = S.report;
    this.left.querySelector('.card-h .m')!.textContent = String(R.blocks.length);
    this.left.querySelector('.list')!.innerHTML = R.blocks.map(b => `<div class="rep-row"><span class="k">${esc(b.kind.toUpperCase())}</span>${b.kind === 'Title' || b.kind === 'Text' ? `<input type="text" data-txt="${b.idx}" value="${esc(b.text)}">` : `<span class="n">${esc(b.summary)}</span>`}<button class="xbtn" data-a="up:${b.idx}" title="Move up">${I.chev()}</button><button class="xbtn" data-a="rm:${b.idx}" aria-label="Remove block">${I.close()}</button></div>`).join('') || `<div class="faint11">No blocks — add a title, a chart snapshot or a value table.</div>`;
    this.right.querySelector('.prop-body')!.innerHTML = `<div class="prop-title">Report</div><div class="kv"><span class="k">blocks</span><span class="v">${R.blocks.length}</span></div><div class="kv"><span class="k">charts available</span><span class="v">${R.charts.length}</span></div><div class="kv"><span class="k">page</span><span class="v">A4 · 96 dpi</span></div><div class="hr"></div><div class="lblh">CHARTS</div>${R.charts.map(c => `<div class="kv"><span class="k">${esc(c.name)}</span><span class="v">#${c.id}</span></div>`).join('') || '<div class="faint11">No charts in VIEW.</div>'}`;
    if (R.version !== this.version) { this.version = R.version; this.refreshPage(); }
  }
  private pending = false;
  async refreshPage(onlyIfVisible = false) {
    if (this.pending || !this.S || this.S.mode !== 'REPORT') return;
    const host = this.center.querySelector('.page-host') as HTMLElement; const w = Math.max(200, Math.min(794, Math.floor(host.clientWidth - 34)));
    if (onlyIfVisible && w === (this as any)._w) return; (this as any)._w = w;
    this.pending = true;
    try { const data = await bridge.call<string>('report.page', { width: Math.round(w * (window.devicePixelRatio || 1)) }); if (data) { this.img.src = data; this.img.style.width = w + 'px'; } } finally { this.pending = false; }
  }
}
