// 1.0-rc: BUS mode — MIL-STD-1553 / ARINC 429 capture: empty state, filter bar (+presets, text), virtualized sortable list with
// column picker / time display / keyboard, decode panel (ICD apply, hex·bits·EU), trend chart (page-0 chart), session statistics, CSV exports.
import { bridge } from './bridge';
import { I } from './icons';
import { PlotView } from './plot';
import { State } from './types';
import { esc, fmtInt, h } from './util';

const ROW_H = 22, HEAD_H = 26, BLOCK = 200, MAX_SPACER = 6_000_000;
type Row = any;
const hx = (v: number, w = 4) => v.toString(16).toUpperCase().padStart(w, '0');
const COLS_1553: { id: string; label: string; w: string; sort?: string }[] = [
  { id: 'i', label: '#', w: '76px' }, { id: 't', label: 'TIME', w: '110px', sort: 't' }, { id: 'bus', label: 'BUS', w: '36px', sort: 'bus' }, { id: 'rt', label: 'RT', w: '32px', sort: 'rt' }, { id: 'sa', label: 'SA', w: '32px', sort: 'sa' },
  { id: 'tr', label: 'T/R', w: '58px', sort: 'tr' }, { id: 'wc', label: 'WC', w: '32px', sort: 'wc' }, { id: 'cwd1', label: 'CWD1', w: '48px', sort: 'cwd1' }, { id: 'swd1', label: 'SWD1', w: '48px', sort: 'swd1' }, { id: 'err', label: 'ERR', w: '48px', sort: 'err' },
  { id: 'act', label: 'ACT', w: '48px', sort: 'act' }, { id: 'n', label: 'N', w: '28px', sort: 'n' }, { id: 'icd', label: 'ICD', w: '110px' }, { id: 'data', label: 'DATA', w: '1fr' }];
const COLS_429: { id: string; label: string; w: string; sort?: string }[] = [
  { id: 'i', label: '#', w: '76px' }, { id: 't', label: 'TIME', w: '110px', sort: 't' }, { id: 'label', label: 'LABEL', w: '52px', sort: 'label' }, { id: 'sdi', label: 'SDI', w: '36px', sort: 'sdi' }, { id: 'dataHex', label: 'DATA', w: '60px', sort: 'data' },
  { id: 'data', label: 'DEC', w: '70px' }, { id: 'ssmName', label: 'SSM', w: '70px', sort: 'ssm' }, { id: 'parity', label: 'PARITY', w: '56px', sort: 'parity' }, { id: 'word', label: 'WORD', w: '80px', sort: 'word' }, { id: 'act', label: 'ACT', w: '1fr' }];

export class BusMode {
  el: HTMLElement; private S: State | null = null; private M: any = null;
  private list: HTMLElement; private scroll: HTMLElement; private spacer: HTMLElement; private inner: HTMLElement; private head: HTMLElement; private meta: HTMLElement; private filters: HTMLElement;
  private empty: HTMLElement; private dec: HTMLElement; private decBody: HTMLElement; private decMeta: HTMLElement; private plotCard: HTMLElement; private plotHost: HTMLElement; private plotMeta: HTMLElement; private statsCard: HTMLElement; private statsBody: HTMLElement;
  private blocks = new Map<number, Row[]>(); private loading = new Set<number>(); private total = 0; private sel = -1; private selRow = -1; private filterMs = 0; private protocol = '';
  private pv: PlotView | null = null; private chartId = -1; private presetName = ''; private hidden = new Set<string>(); private showStats = false; private lastDecode: any = null;
  filter: any = { rt: -1, sa: -1, tr: -1, bus: -1, errorsOnly: false, text: '', label: -1, sdi: -1, ssm: -1, parityOnly: false };

  constructor() {
    this.el = h(`<div class="center m1553"></div>`);
    this.empty = h(`<section class="card bus-empty"><div class="bus-empty-in">${I.table(28)}<b>버스 캡처 파일이 없습니다</b><p>MIL-STD-1553 / ARINC 429 캡처(.mon)를 열거나 내장 샘플로 시작하세요. 파일은 창 어디에나 끌어다 놓아도 됩니다.</p><div class="btns"><button class="pill h32 primary" data-a="open">${I.folderW()}OPEN FILE</button><button class="pill h32" data-a="sample1553">OPEN 1553 SAMPLE</button><button class="pill h32" data-a="sample429">OPEN 429 SAMPLE</button></div><div class="hints">20,000 메시지 1553 캡처 · 2,000 워드 429 캡처 (가상 데이터)</div></div></section>`);
    this.empty.addEventListener('click', e => { const a = (e.target as HTMLElement).closest('[data-a]') as HTMLElement | null; if (!a) return; if (a.dataset.a === 'open') bridge.call('openDialog'); else bridge.call('m1553.openSample', { protocol: a.dataset.a === 'sample429' ? 'A429' : '1553' }); });
    this.list = h(`<section class="card" style="flex:1"><div class="card-h"><span class="ic">${I.list()}</span><span class="t">MESSAGES</span><span class="m" data-meta></span></div><div class="m1553-filters"></div>
      <div class="tscroll m1553-scroll" tabindex="0"><div class="tspacer"><div class="thead"></div><div class="tinner"></div></div></div></section>`);
    this.filters = this.list.querySelector('.m1553-filters')!; this.head = this.list.querySelector('.thead')!; this.meta = this.list.querySelector('[data-meta]')!;
    this.scroll = this.list.querySelector('.tscroll')!; this.spacer = this.list.querySelector('.tspacer')!; this.inner = this.list.querySelector('.tinner')!;
    this.scroll.addEventListener('scroll', () => this.paint());
    this.inner.addEventListener('click', e => { const r = (e.target as HTMLElement).closest('[data-i]') as HTMLElement | null; if (r) this.select(+r.dataset.i!, +r.dataset.row!); });
    this.head.addEventListener('click', e => { const c = (e.target as HTMLElement).closest('[data-sort]') as HTMLElement | null; if (!c) return; const col = c.dataset.sort!; const cur = this.M?.sort ?? { col: '', dir: 1 }; const dir = cur.col === col ? (cur.dir > 0 ? -1 : 1) : 1; this.sort(col, dir); });
    this.scroll.addEventListener('keydown', e => this.onKey(e));
    this.filters.addEventListener('click', e => this.onFilterClick(e));
    this.filters.addEventListener('change', e => this.onFilterChange(e));
    this.dec = h(`<section class="card m1553-dec"><div class="card-h"><span class="ic">${I.info()}</span><span class="t">MESSAGE</span><span class="m" data-meta></span><button class="pill h24" data-a="icdApply" title="ICD 적용/해제">ICD</button></div><div class="scroll"><div class="dec-body"><div class="muted11" style="padding:12px">메시지를 선택하세요</div></div></div></section>`);
    this.decBody = this.dec.querySelector('.dec-body')!; this.decMeta = this.dec.querySelector('[data-meta]')!;
    this.dec.addEventListener('click', e => { const t = e.target as HTMLElement; const w = t.closest('[data-ch]') as HTMLElement | null; if (w) { this.trend(w.dataset.ch!, w.dataset.label || ''); return; } if ((t.closest('[data-a]') as HTMLElement | null)?.dataset.a === 'icdApply') bridge.call('icd.apply', { on: !(this.M?.icd?.applied ?? true) }).then(() => { if (this.sel >= 0) this.select(this.sel, this.selRow); }); });
    this.plotCard = h(`<section class="card m1553-plot"><div class="card-h"><span class="ic">${I.lineC()}</span><span class="t">TREND</span><span class="m" data-meta></span></div><div class="m1553-plot-host"><div class="muted11" style="padding:12px">디코드 패널의 워드(▸ trend)를 누르거나 Enter를 누르면 추이가 그려집니다. NAVIGATOR의 채널을 VIEW 차트에 끌어다 놓을 수도 있어요.</div></div></section>`);
    this.plotHost = this.plotCard.querySelector('.m1553-plot-host')!; this.plotMeta = this.plotCard.querySelector('[data-meta]')!;
    this.statsCard = h(`<section class="card m1553-stats"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">SESSION STATISTICS</span><span class="m" data-meta></span></div><div class="scroll"><div class="stats-body"></div></div></section>`);
    this.statsBody = this.statsCard.querySelector('.stats-body')!;
    const bottom = h(`<div class="m1553-bottom"></div>`); bottom.append(this.dec, this.plotCard);
    this.el.append(this.empty, this.list, bottom, this.statsCard);
    try { const hid = localStorage.getItem('nx.bus.hidden'); if (hid) this.hidden = new Set(JSON.parse(hid)); } catch { }
  }

  // ---------- filters
  private cols() { return (this.protocol === 'A429' ? COLS_429 : COLS_1553).filter(c => !this.hidden.has(c.id)); }
  private renderFilters() {
    const f = this.filter; const M = this.M ?? {}; const presets: any[] = M.presets ?? []; const bt = (this.S as any)?.busTime ?? 'rel';
    const pill = (a: string, label: string, on: boolean, extra = '') => `<button class="pill h24${on ? ' on' : ''}" ${a}${extra}>${label}</button>`;
    const common = `<span class="sp"></span><span class="lbl-in">TIME</span>${pill('data-time="rel"', 'REL', bt === 'rel')}${pill('data-time="abs"', 'ABS µs', bt === 'abs')}${pill('data-time="delta"', 'Δ', bt === 'delta')}<button class="pill h24" data-a="columns" title="열 선택">COLUMNS</button><button class="pill h24" data-a="csvRows" title="현재 목록(필터·정렬 적용) CSV">CSV</button>${this.protocol === '1553' ? `<button class="pill h24" data-a="csvDecoded" title="ICD 디코드 결과 CSV">CSV · EU</button>` : ''}${pill('data-a="stats"', 'STATS', this.showStats)}`;
    if (this.protocol === 'A429') {
      this.filters.innerHTML = `<span class="lbl-in">LABEL</span><input class="in mono" data-f="label" value="${f.label >= 0 ? f.label.toString(8).padStart(3, '0') : ''}" placeholder="octal" title="label (octal) or *"><span class="lbl-in">SDI</span>${[-1, 0, 1, 2, 3].map(v => pill(`data-sdi="${v}"`, v < 0 ? 'ANY' : String(v), f.sdi === v)).join('')}<span class="lbl-in">SSM</span>${[-1, 0, 1, 2, 3].map(v => pill(`data-ssm="${v}"`, v < 0 ? 'ANY' : String(v), f.ssm === v)).join('')}${pill('data-a="parity"', 'PARITY ERRORS', f.parityOnly)}${common}`;
      return;
    }
    this.filters.innerHTML = `<span class="lbl-in">RT</span><input class="in mono" data-f="rt" value="${f.rt >= 0 ? f.rt : ''}" placeholder="*" title="RT 0-31 or *"><span class="lbl-in">SA</span><input class="in mono" data-f="sa" value="${f.sa >= 0 ? f.sa : ''}" placeholder="*" title="SA 0-31 or *">
      <span class="lbl-in">T/R</span>${pill('data-tr="-1"', 'ANY', f.tr === -1)}${pill('data-tr="0"', 'BC→RT', f.tr === 0)}${pill('data-tr="1"', 'RT→BC', f.tr === 1)}<span class="lbl-in">BUS</span>${pill('data-bus="-1"', 'A+B', f.bus === -1)}${pill('data-bus="0"', 'A', f.bus === 0)}${pill('data-bus="1"', 'B', f.bus === 1)}${pill('data-a="err"', 'ERRORS ONLY', f.errorsOnly)}
      <span class="lbl-in">TEXT</span><input class="in mono" data-f="text" style="width:130px" value="${esc(f.text)}" placeholder="hex / name / RT3" title="데이터 hex·ICD 이름·RT/SA 텍스트 검색">
      <span class="lbl-in">PRESET</span><select class="sel h24" data-f="preset"><option value="">—</option>${presets.map(p => `<option value="${esc(p.name)}"${p.name === this.presetName ? ' selected' : ''}>${esc(p.name)}</option>`).join('')}</select><button class="pill h24" data-a="presetSave" title="현재 필터 조합 저장">SAVE</button><button class="pill h24" data-a="presetDel" title="선택 프리셋 삭제">DEL</button>${common}`;
  }
  private onFilterClick(e: Event) {
    const b = (e.target as HTMLElement).closest('button') as HTMLElement | null; if (!b) return; const f = this.filter; const d = b.dataset;
    if (d.tr != null) f.tr = +d.tr; else if (d.bus != null) f.bus = +d.bus; else if (d.sdi != null) f.sdi = +d.sdi; else if (d.ssm != null) f.ssm = +d.ssm;
    else if (d.a === 'err') f.errorsOnly = !f.errorsOnly; else if (d.a === 'parity') f.parityOnly = !f.parityOnly;
    else if (d.time) { bridge.call('m1553.busTime', { mode: d.time }); return; }
    else if (d.a === 'columns') { this.columnPicker(); return; }
    else if (d.a === 'csvRows') { bridge.call('m1553.exportCsvDialog', { decoded: false, first: 0, last: this.total - 1 }); return; }
    else if (d.a === 'csvDecoded') { bridge.call('m1553.exportCsvDialog', { decoded: true, first: 0, last: this.total - 1 }); return; }
    else if (d.a === 'stats') { this.showStats = !this.showStats; this.statsCard.classList.toggle('on', this.showStats); if (this.showStats) this.loadStats(); this.renderFilters(); return; }
    else if (d.a === 'presetSave') { const name = prompt('프리셋 이름', `RT${f.rt >= 0 ? f.rt : '*'} SA${f.sa >= 0 ? f.sa : '*'}${f.errorsOnly ? ' err' : ''}`); if (name) { this.presetName = name; bridge.call('m1553.preset.save', { name, rt: f.rt, sa: f.sa, tr: f.tr, bus: f.bus, errorsOnly: f.errorsOnly, text: f.text }); } return; }
    else if (d.a === 'presetDel') { const sel = this.filters.querySelector('[data-f=preset]') as HTMLSelectElement; if (sel.value) { this.presetName = ''; bridge.call('m1553.preset.delete', { name: sel.value }); } return; }
    else return;
    this.applyFilter();
  }
  private onFilterChange(e: Event) {
    const inp = e.target as HTMLInputElement; const f = this.filter; const k = inp.dataset.f;
    if (k === 'rt' || k === 'sa') { const v = inp.value.trim(); f[k] = v === '' || v === '*' ? -1 : Math.max(0, Math.min(31, parseInt(v, 10) || 0)); }
    else if (k === 'label') { const v = inp.value.trim(); f.label = v === '' || v === '*' ? -1 : parseInt(v, 8); }
    else if (k === 'text') f.text = inp.value;
    else if (k === 'preset') { const p = (this.M?.presets ?? []).find((x: any) => x.name === inp.value); if (p) { f.rt = p.rt; f.sa = p.sa; f.tr = p.tr; f.bus = p.bus; f.errorsOnly = p.errorsOnly; f.text = p.text; this.presetName = p.name; this.renderFilters(); } else { this.presetName = ''; return; } }
    else return;
    this.applyFilter();
  }
  private columnPicker() {
    document.querySelector('.col-pick')?.remove();
    const all = this.protocol === 'A429' ? COLS_429 : COLS_1553;
    const dlg = h(`<div class="modal-bg"><div class="modal col-pick"><div class="lblh">COLUMNS</div><div class="col-grid">${all.map(c => `<label class="opt"><input type="checkbox" data-c="${c.id}"${this.hidden.has(c.id) ? '' : ' checked'}${c.id === 'i' ? ' disabled' : ''}><span>${c.label}</span></label>`).join('')}</div><div class="btns"><button class="pill h30 primary" data-b="ok">CLOSE</button></div></div></div>`);
    dlg.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.checked) this.hidden.delete(t.dataset.c!); else this.hidden.add(t.dataset.c!); try { localStorage.setItem('nx.bus.hidden', JSON.stringify([...this.hidden])); } catch { } this.renderHead(); this.paint(); });
    dlg.addEventListener('click', e => { const t = e.target as HTMLElement; if ((t.closest('[data-b]') as HTMLElement | null)?.dataset.b === 'ok' || t === dlg) dlg.remove(); });
    document.body.append(dlg);
  }

  // ---------- state
  get scale() { const full = this.total * ROW_H; return full > MAX_SPACER ? MAX_SPACER / full : 1; }
  private rowAt(st: number) { return Math.floor(st / (ROW_H * this.scale)); }
  setState(S: State) {
    const prev = this.M; this.S = S; const m = (S as any).mil1553; this.M = m;
    const has = !!m?.file; this.empty.style.display = has ? 'none' : ''; this.list.style.display = has ? '' : 'none'; (this.el.querySelector('.m1553-bottom') as HTMLElement).style.display = has ? '' : 'none'; this.statsCard.style.display = has && this.showStats ? '' : 'none';
    if (!has) return;
    const total = m.filtered ?? 0; const proto = m.protocol || '1553';
    if (!prev || prev.file !== m.file || proto !== this.protocol) { this.protocol = proto; this.hidden = new Set(JSON.parse((() => { try { return localStorage.getItem('nx.bus.hidden.' + proto) ?? '[]'; } catch { return '[]'; } })())); this.sel = -1; this.selRow = -1; this.lastDecode = null; this.decBody.innerHTML = `<div class="muted11" style="padding:12px">메시지를 선택하세요</div>`; this.renderHead(); if (this.showStats) this.loadStats(); }
    if (!prev || prev.file !== m.file || total !== this.total || JSON.stringify(prev.sort) !== JSON.stringify(m.sort)) { this.total = total; this.blocks.clear(); this.loading.clear(); this.spacer.style.height = (HEAD_H + Math.min(MAX_SPACER, total * ROW_H)) + 'px'; if (prev && JSON.stringify(prev.sort) !== JSON.stringify(m.sort)) this.renderHead(); }
    if (!prev || JSON.stringify(prev.presets) !== JSON.stringify(m.presets) || prev.file !== m.file) this.renderFilters();
    if ((this as any)._bt !== (S as any).busTime) { (this as any)._bt = (S as any).busTime; this.renderFilters(); this.renderHead(); this.blocks.clear(); }
    this.filterMs = m.filterMs ?? 0;
    this.meta.textContent = `${m.file} · ${proto} · ${fmtInt(total)} of ${fmtInt(m.count)}${m.filterMs ? ` · filter ${m.filterMs.toFixed(1)} ms` : ''}${m.sort?.col ? ` · sort ${m.sort.col}${m.sort.dir < 0 ? ' ↓' : ' ↑'}` : ''}`;
    this.dec.querySelector('[data-a=icdApply]')!.classList.toggle('on', !!m.icd?.applied); (this.dec.querySelector('[data-a=icdApply]') as HTMLElement).style.display = m.icd && proto === '1553' ? '' : 'none';
    this.paint();
    const c = S.charts.find(c => c.id === this.chartId);
    if (c) { if (!this.pv) { this.pv = new PlotView(c, S.plot); this.plotHost.innerHTML = ''; this.plotHost.append(this.pv.el); } this.pv.update(c, S.plot); this.pv.request({ adopt: true }); }
  }
  private renderHead() {
    const sort = this.M?.sort ?? { col: '', dir: 1 }; const bt = (this.S as any)?.busTime ?? 'rel';
    const cols = this.cols(); const tpl = cols.map(c => c.w).join(' ');
    this.head.style.gridTemplateColumns = tpl; (this.inner as any)._tpl = tpl;
    this.head.innerHTML = cols.map(c => `<span${c.sort ? ` data-sort="${c.sort}" class="sortable${sort.col === c.sort ? ' on' : ''}"` : ''}>${c.id === 't' ? (bt === 'abs' ? 'TIME (µs)' : bt === 'delta' ? 'Δt (ms)' : 'TIME (s)') : c.label}${sort.col === c.sort && c.sort ? (sort.dir < 0 ? ' ↓' : ' ↑') : ''}</span>`).join('');
  }
  private async applyFilter() {
    const f = this.filter; const t0 = performance.now();
    const n = await bridge.call<number>('m1553.filter', { ...f });
    this.filterMs = performance.now() - t0; this.total = n; this.blocks.clear(); this.loading.clear(); this.spacer.style.height = (HEAD_H + Math.min(MAX_SPACER, n * ROW_H)) + 'px'; this.scroll.scrollTop = 0; this.paint();
    return n;
  }
  private async sort(col: string, dir: number) { await bridge.call('m1553.sort', { col, dir }); this.blocks.clear(); this.scroll.scrollTop = 0; }
  scrollTo(row: number) { const vis = Math.max(1, Math.floor(this.scroll.clientHeight / ROW_H)); const top = Math.max(0, row - Math.floor(vis / 2)); this.scroll.scrollTop = top * ROW_H * this.scale; this.paint(); }
  private block(key: number) {
    if (this.blocks.has(key) || this.loading.has(key)) return;
    this.loading.add(key);
    bridge.call<{ rows: Row[] }>('m1553.rows', { first: key, count: BLOCK }).then(r => { this.loading.delete(key); if (!r) return; this.blocks.set(key, r.rows); if (this.blocks.size > 40) { const k = this.blocks.keys().next().value; if (k != null) this.blocks.delete(k); } this.paint(); });
  }
  private cell(c: string, row: Row): string {
    const bt = (this.S as any)?.busTime ?? 'rel';
    switch (c) {
      case 'i': return `<span class="dim">${fmtInt(row.i)}</span>`;
      case 't': return `<span>${bt === 'abs' ? fmtInt(row.tus) : bt === 'delta' ? (row.dt * 1000).toFixed(3) : row.t.toFixed(6)}</span>`;
      case 'bus': return `<span${row.bus === 'B' ? ' class="busb"' : ''}>${row.bus}</span>`;
      case 'tr': return `<span>${row.tr ? 'RT→BC' : 'BC→RT'}</span>`;
      case 'cwd1': case 'swd1': case 'act': return `<span>${hx(row[c])}</span>`;
      case 'err': return `<span${row.e ? ' class="errv"' : ''}>${hx(row.err)}</span>`;
      case 'icd': return `<span class="dim">${esc(row.icd ?? '')}</span>`;
      case 'data': return `<span class="dim data">${this.protocol === 'A429' ? row.data : row.data}</span>`;
      case 'parity': return `<span${row.e ? ' class="errv"' : ''}>${row.parity}</span>`;
      default: return `<span>${esc(row[c] ?? '')}</span>`;
    }
  }
  paint() {
    const st = this.scroll.scrollTop, vh = this.scroll.clientHeight || 400;
    const r0 = Math.max(0, this.rowAt(st) - 2), n = Math.min(this.total - r0, Math.ceil(vh / ROW_H) + 4);
    if (n <= 0) { this.inner.innerHTML = ''; return; }
    const k0 = Math.floor(r0 / BLOCK) * BLOCK, k1 = Math.floor((r0 + n - 1) / BLOCK) * BLOCK; this.block(k0); if (k1 !== k0) this.block(k1);
    const cols = this.cols(); const tpl = cols.map(c => c.w).join(' '); let html = '';
    for (let r = r0; r < r0 + n; r++) {
      const b = this.blocks.get(Math.floor(r / BLOCK) * BLOCK); const row = b ? b[r - Math.floor(r / BLOCK) * BLOCK] : undefined;
      if (!row) { html += `<div class="trow" style="top:${(r - r0) * ROW_H}px;grid-template-columns:${tpl}"><span class="dim">${fmtInt(r)}</span><span class="dim">…</span></div>`; continue; }
      html += `<div class="trow${row.i === this.sel ? ' sel' : ''}${row.e ? ' err' : ''}" data-i="${row.i}" data-row="${r}" style="top:${(r - r0) * ROW_H}px;grid-template-columns:${tpl}">${cols.map(c => this.cell(c.id, row)).join('')}</div>`;
    }
    this.inner.style.top = (this.scale < 1 ? st - (this.rowAt(st) - r0) * ROW_H : r0 * ROW_H) + 'px';
    this.inner.innerHTML = html;
  }
  private onKey(e: KeyboardEvent) {
    if (!this.total) return; const vis = Math.max(1, Math.floor(this.scroll.clientHeight / ROW_H));
    let row = this.selRow; const go = (r: number) => { r = Math.max(0, Math.min(this.total - 1, r)); const el = this.inner.querySelector(`.trow[data-row="${r}"]`) as HTMLElement | null; if (el) this.select(+el.dataset.i!, r); else { this.scrollTo(r); setTimeout(() => { const el2 = this.inner.querySelector(`.trow[data-row="${r}"]`) as HTMLElement | null; if (el2) this.select(+el2.dataset.i!, r); }, 120); } };
    if (e.key === 'ArrowDown') { e.preventDefault(); go(row + 1); } else if (e.key === 'ArrowUp') { e.preventDefault(); go(row - 1); }
    else if (e.key === 'PageDown') { e.preventDefault(); go(row + vis); } else if (e.key === 'PageUp') { e.preventDefault(); go(row - vis); }
    else if (e.key === 'Home') { e.preventDefault(); go(0); } else if (e.key === 'End') { e.preventDefault(); go(this.total - 1); }
    else if (e.key === 'Enter') { e.preventDefault(); const w = this.decBody.querySelector('tr[data-ch]') as HTMLElement | null; if (w) this.trend(w.dataset.ch!, w.dataset.label || ''); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'c') { e.preventDefault(); const el = this.inner.querySelector('.trow.sel') as HTMLElement | null; if (el) bridge.call('clipboardText', { text: Array.from(el.children).map(c => c.textContent).join('\t') }); }
  }
  async select(i: number, row: number) {
    this.sel = i; this.selRow = row; this.paint();
    const d = await bridge.call<any>('m1553.decode', { i }); if (!d) return; this.lastDecode = d;
    const kv = (k: string, v: string, cls = '') => `<span class="k">${k}</span><b class="${cls}">${v}</b>`;
    if (d.protocol === 'A429') {
      this.decMeta.textContent = `label ${d.label} (octal)`;
      this.decBody.innerHTML = `<div class="kv">${kv('#', fmtInt(d.i))}${kv('TIME', `${d.t.toFixed(6)} s · ${fmtInt(d.tUs)} µs`)}${kv('WORD', `${d.word} · ${d.bin}`)}${kv('LABEL', `${d.label} (octal) · ${d.labelDec}`)}${kv('SDI', String(d.sdi))}${kv('DATA', `${d.dataHex} · ${d.data} · BNR ${d.bnr} · bits ${d.dataBin}`)}${kv('SSM', `${d.ssm} · ${d.ssmName}`)}${kv('PARITY', d.parityOk ? 'ok (odd)' : 'ERROR', d.parityOk ? '' : 'errv')}${kv('ACTIVITY', `${d.act} · gap ${d.decgap}`)}</div>`;
      return;
    }
    this.decMeta.textContent = d.icd ? `ICD: ${d.icd}` : (this.M?.icd ? (this.M.icd.applied ? 'no ICD match' : 'ICD off') : 'no ICD');
    const words = d.words.length ? `<table class="m1553-words"><thead><tr><th>WORD</th><th>NAME</th><th>HEX</th><th>BITS</th><th>EU</th><th></th></tr></thead><tbody>${d.words.map((w: any) => `<tr data-ch="${esc(w.channel)}" data-label="${esc(w.name)} (${esc(w.unit)})" title="추이로 보기"><td class="dim">${w.word}<span class="dim"> · ${w.bits}</span></td><td>${esc(w.name)}</td><td class="mono">${w.raw}</td><td class="mono dim">${w.bin}</td><td class="mono eu">${w.eu == null ? '—' : Number(w.eu).toLocaleString('en-US', { maximumFractionDigits: 6 })} <span class="dim">${esc(w.unit)}</span></td><td class="dim">▸ trend</td></tr>`).join('')}</tbody></table>` : '';
    const raw = `<table class="m1553-words raw"><thead><tr><th>W</th><th>HEX</th><th>BITS</th><th>DEC</th></tr></thead><tbody>${d.raw.map((x: any) => `<tr><td class="dim">${x.k}</td><td class="mono">${x.hex}</td><td class="mono dim">${x.bin}</td><td class="mono">${x.dec}</td></tr>`).join('')}</tbody></table>`;
    this.decBody.innerHTML = `<div class="kv">${kv('#', fmtInt(d.i))}${kv('TIME', `${d.t.toFixed(6)} s · ${fmtInt(d.tUs)} µs`)}${kv('COMMAND', `RT ${d.rt} · SA ${d.sa} · ${d.tr ? 'RT→BC' : 'BC→RT'} · WC ${d.wc}${d.modeCode ? ' · mode code' : ''} (${d.cwd1}${d.cwd2 !== '0000' ? ' / ' + d.cwd2 : ''})`)}${kv('STATUS', `${d.swd1}${d.swdFlags.length ? ' · ' + d.swdFlags.join(' · ') : ''}`)}${kv('ERROR', `${d.err}${d.errs.length ? ' · ' + d.errs.join(' · ') : ''}`, d.errs.some((x: string) => x !== 'HIT') ? 'errv' : '')}${kv('ACTIVITY', `${d.act} · ${d.acts.join(' ')}`)}${kv('BUS · RESP', `${d.bus} · ${(d.resp1 / 10).toFixed(1)} µs`)}</div>${words}<div class="lblh" style="padding:6px 12px 0">RAW · ${d.n} words</div>${raw}`;
  }
  async trend(channelId: string, label: string) {
    if (this.chartId < 0) this.chartId = await bridge.call<number>('m1553.chart');
    const c = this.S?.charts.find(c => c.id === this.chartId);
    if (c) for (const _ of c.curves) await bridge.call('removeCurve', { chart: this.chartId, idx: 0 });
    await bridge.call('addCurves', { chart: this.chartId, channels: [channelId], zone: 'plot' });
    this.plotMeta.textContent = label;
  }
  private async loadStats() {
    const s = await bridge.call<any>('m1553.stats'); if (!s) return;
    const stat = (k: string, v: string) => `<div><div class="k">${k}</div><div class="v">${v}</div></div>`;
    if (s.protocol === 'A429') {
      const max = Math.max(1, ...s.perLabel.map((x: any) => x.count));
      this.statsBody.innerHTML = `<div class="stat4">${stat('WORDS', fmtInt(s.count))}${stat('RATE', `${Math.round(s.rateHz)} /s`)}${stat('PARITY ERR', fmtInt(s.parityErrors))}${stat('LOAD', `${s.busLoadPct.toFixed(1)} %`)}</div>${s.perLabel.slice(0, 16).map((x: any) => `<div class="bar"><span>${x.label}</span><i style="width:${(100 * x.count / max).toFixed(1)}%"></i><span>${fmtInt(x.count)}</span></div>`).join('')}`;
      this.statsCard.querySelector('[data-meta]')!.textContent = `${fmtInt(s.count)} · ${s.durationS.toFixed(1)} s`; return;
    }
    const max = Math.max(1, ...s.perRt.map((x: any) => x.count));
    this.statsBody.innerHTML = `<div class="stat4">${stat('MESSAGES', fmtInt(s.count))}${stat('RATE', `${Math.round(s.rateHz)} /s`)}${stat('ERRORS', fmtInt(s.errors))}${stat('BUS LOAD', `${s.busLoadPct.toFixed(1)} %`)}</div><div class="stat4">${stat('BUS A / B', `${fmtInt(s.busA)} / ${fmtInt(s.busB)}`)}${stat('MODE CODES', fmtInt(s.modeCodes))}${stat('DURATION', `${s.durationS.toFixed(2)} s`)}${stat('RTs', String(s.perRt.length))}</div>
      ${s.perRt.map((x: any) => `<div class="bar"><span>RT ${x.rt}</span><i style="width:${(100 * x.count / max).toFixed(1)}%"></i><span>${fmtInt(x.count)}${x.errors ? ` <em class="errv">·${x.errors}</em>` : ''}</span></div>`).join('')}
      <div class="lblh" style="padding:8px 12px 2px">TOP MESSAGES</div>${s.topMessages.map((m: any) => `<div class="bar top"><span>${m.rt}/${m.sa}</span><span class="dim">${m.tr ? 'RT→BC' : 'BC→RT'} ${esc(m.icd ?? '')}</span><span>${fmtInt(m.count)}</span></div>`).join('')}`;
    this.statsCard.querySelector('[data-meta]')!.textContent = `${fmtInt(s.count)} · ${s.durationS.toFixed(1)} s`;
  }

  /** Self-test hooks (window.nx.test.m1553()). */
  testApi() {
    const self = this;
    return {
      total: () => self.total, selected: () => self.sel, filterMs: () => self.filterMs, protocol: () => self.protocol, empty: () => self.empty.style.display !== 'none',
      visible: () => Array.from(self.inner.querySelectorAll<HTMLElement>('.trow[data-i]')).map(r => +r.dataset.i!),
      columns: () => Array.from(self.head.children).map(c => c.textContent),
      async scrollTo(row: number) { self.scrollTo(row); await new Promise(r => setTimeout(r, 80)); return self.inner.querySelectorAll('.trow[data-i]').length; },
      async scrollSteps(n: number) { const out: number[][] = []; for (let k = 0; k < n; k++) { const t0 = performance.now(); self.scroll.scrollTop += self.scroll.clientHeight; self.paint(); const t1 = performance.now(); await new Promise(r => requestAnimationFrame(() => r(null))); out.push([t1 - t0, performance.now() - t0]); } await new Promise(r => setTimeout(r, 120)); return out; },
      async selectRow(row: number) { const el = self.inner.querySelector(`.trow[data-row="${row}"]`) as HTMLElement | null; if (!el) throw new Error('row not visible ' + row); el.click(); await new Promise(r => setTimeout(r, 120)); return self.decBody.querySelectorAll('.kv b').length; },
      async filter(f: any) { Object.assign(self.filter, f); self.renderFilters(); const t0 = performance.now(); const n = await self.applyFilter(); return { n, ms: performance.now() - t0, engineMs: self.filterMs }; },
      async sort(col: string, dir: number) { await self.sort(col, dir); await new Promise(r => setTimeout(r, 150)); self.paint(); return self.inner.querySelectorAll('.trow[data-i]').length; },
      async key(key: string, mods: any = {}) { self.scroll.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...mods })); await new Promise(r => setTimeout(r, 200)); return self.selRow; },
      async clickFilter(sel: string) { const b = self.filters.querySelector(sel) as HTMLElement | null; if (!b) throw new Error('no ' + sel); b.click(); await new Promise(r => setTimeout(r, 150)); return true; },
      async setInput(f: string, v: string) { const inp = self.filters.querySelector(`[data-f=${f}]`) as HTMLInputElement; inp.value = v; inp.dispatchEvent(new Event('change', { bubbles: true })); await new Promise(r => setTimeout(r, 200)); return self.total; },
      words: () => Array.from(self.decBody.querySelectorAll<HTMLElement>('tr[data-ch]')).map(r => ({ ch: r.dataset.ch, eu: (r.querySelector('.eu') as HTMLElement).textContent })),
      decode: () => self.lastDecode,
      async trendFirst() { const r = self.decBody.querySelector('tr[data-ch]') as HTMLElement | null; if (!r) throw new Error('no ICD word'); r.click(); await new Promise(r => setTimeout(r, 200)); return self.chartId; },
      async stats() { self.showStats = true; self.statsCard.classList.add('on'); self.statsCard.style.display = ''; await self.loadStats(); return self.statsBody.querySelectorAll('.bar').length; },
      async hideColumn(id: string) { self.hidden.add(id); self.renderHead(); self.paint(); await new Promise(r => setTimeout(r, 50)); return self.head.children.length; },
      chart: () => self.chartId, frames: () => self.pv ? { frames: self.pv.frames, lastMs: self.pv.lastTotalMs } : null,
    };
  }
}
