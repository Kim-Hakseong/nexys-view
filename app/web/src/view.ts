// VIEW center: chart cards (PlotView per chart, keyed by id), value tables (virtualized, cursor row tracking, rectangular
// selection + Ctrl+C), the dashed "new chart" drop zone with the drop rule (by unit / per channel) and "+ TABLE".
import { bridge } from './bridge';
import { I } from './icons';
import { DRAG_CH, DRAG_CURVE, DRAG_GROUP, PlotView } from './plot';
import { State, TableDto } from './types';
import { esc, fmtInt, fmtValue, h } from './util';

const ROW_H = 22, HEAD_H = 26, MAX_SPACER = 6_000_000;

interface TableBlock { first: number; count: number; columns: { name: string; unit: string; values: (number | null)[]; x: number[] }[]; }

class TableView {
  el: HTMLElement; private scroll: HTMLElement; private spacer: HTMLElement; private inner: HTMLElement; private pos: HTMLElement; private meta: HTMLElement;
  dto: TableDto; private blocks = new Map<number, TableBlock>(); private loading = new Set<number>(); private sel: { r0: number; c0: number; r1: number; c1: number } | null = null; private anchor: [number, number] | null = null;
  private lastCursor: number | null = null; private follow = true;
  constructor(dto: TableDto) {
    this.dto = dto;
    this.el = h(`<section class="card tbl" data-table="${dto.id}" tabindex="0"><div class="card-h"><span class="ic">${I.table()}</span><span class="t">TABLE</span><span class="m"></span><button class="pill h24" data-a="follow" title="커서 A 행을 따라 스크롤">FOLLOW</button><button class="pill h24" data-a="copyall" title="보이는 열 전체를 탭 구분으로 복사 (최대 100,000행)">COPY ALL</button><button class="xbtn" style="margin-left:8px" aria-label="Remove table">${I.close()}</button></div><div class="scroll tscroll"><div class="tspacer"><div class="tinner"></div></div></div><div class="tbl-foot"><span class="pos"></span><span class="hint">셀을 끌어 선택 · Ctrl+C 탭 구분 복사 · 커서 A 행 강조</span></div></section>`);
    this.scroll = this.el.querySelector('.tscroll')!; this.spacer = this.el.querySelector('.tspacer')!; this.inner = this.el.querySelector('.tinner')!; this.pos = this.el.querySelector('.pos')!; this.meta = this.el.querySelector('.card-h .m')!;
    this.el.querySelector('.xbtn')!.addEventListener('click', () => bridge.call('table.remove', { table: this.dto.id }));
    this.el.querySelector('[data-a=follow]')!.addEventListener('click', e => { this.follow = !this.follow; (e.currentTarget as HTMLElement).classList.toggle('on', this.follow); });
    (this.el.querySelector('[data-a=follow]') as HTMLElement).classList.add('on');
    this.el.querySelector('[data-a=copyall]')!.addEventListener('click', () => this.copy(0, 0, Math.min(this.dto.rows, 100000) - 1, this.dto.channels.length + 1));
    this.scroll.addEventListener('scroll', () => this.paint());
    this.inner.addEventListener('pointerdown', e => { const c = this.cellAt(e); if (!c) return; this.anchor = c; this.sel = { r0: c[0], c0: c[1], r1: c[0], c1: c[1] }; this.el.focus(); this.paint(); e.preventDefault(); });
    this.inner.addEventListener('pointermove', e => { if (!this.anchor || !(e.buttons & 1)) return; const c = this.cellAt(e); if (!c) return; this.sel = { r0: Math.min(this.anchor[0], c[0]), c0: Math.min(this.anchor[1], c[1]), r1: Math.max(this.anchor[0], c[0]), c1: Math.max(this.anchor[1], c[1]) }; this.paint(); });
    window.addEventListener('pointerup', () => { this.anchor = null; });
    this.el.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'c' && this.sel) { e.preventDefault(); e.stopPropagation(); this.copy(this.sel.r0, this.sel.c0, this.sel.r1, this.sel.c1); } });
    this.el.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes(DRAG_CH)) { e.preventDefault(); this.el.style.borderColor = 'var(--accent2)'; } });
    this.el.addEventListener('dragleave', () => { this.el.style.borderColor = ''; });
    this.el.addEventListener('drop', e => { this.el.style.borderColor = ''; const dt = e.dataTransfer; if (!dt?.types.includes(DRAG_CH)) return; e.preventDefault(); e.stopPropagation(); const chans = JSON.parse(dt.getData(DRAG_CH) || '[]') as { id: string }[]; bridge.call('table.addChannels', { table: this.dto.id, channels: chans.map(c => c.id) }); });
    new ResizeObserver(() => this.paint()).observe(this.scroll);
  }
  get scale() { const full = this.dto.rows * ROW_H; return full > MAX_SPACER ? MAX_SPACER / full : 1; }
  private rowAt(scrollTop: number) { return Math.floor(scrollTop / (ROW_H * this.scale)); }
  private cellAt(e: PointerEvent): [number, number] | null { const t = (e.target as HTMLElement).closest('td') as HTMLTableCellElement | null; if (!t) return null; const tr = t.parentElement as HTMLTableRowElement; const r = +(tr.dataset.r ?? -1); if (r < 0) return null; return [r, t.cellIndex]; }

  update(dto: TableDto) {
    const changed = dto.rows !== this.dto.rows || dto.channels.length !== this.dto.channels.length || dto.channels.some((c, i) => c.id !== this.dto.channels[i]?.id);
    this.dto = dto;
    if (changed) { this.blocks.clear(); this.loading.clear(); }
    this.meta.textContent = `${dto.channels.length} ch · ${fmtInt(dto.rows)} rows`;
    this.spacer.style.height = (HEAD_H + Math.min(MAX_SPACER, dto.rows * ROW_H)) + 'px';
    if (dto.cursorRow != null && dto.cursorRow !== this.lastCursor && this.follow) {
      this.lastCursor = dto.cursorRow; const row = dto.cursorRow;
      const go = () => { const vis = Math.max(1, Math.floor(this.scroll.clientHeight / ROW_H)); const top = Math.max(0, row - Math.floor(vis / 2)); this.scroll.scrollTop = top * ROW_H * this.scale; this.paint(); };
      if (this.el.isConnected && this.scroll.clientHeight > 0) go(); else requestAnimationFrame(go);
    } else this.lastCursor = dto.cursorRow;
    this.paint();
  }

  private block(first: number): TableBlock | undefined {
    const key = Math.floor(first / 200) * 200; const b = this.blocks.get(key);
    if (b) return b;
    if (!this.loading.has(key)) {
      this.loading.add(key);
      bridge.call('table.rows', { table: this.dto.id, first: key, count: 200 }).then(r => { this.loading.delete(key); if (!r) return; this.blocks.set(key, r); if (this.blocks.size > 40) { const k = this.blocks.keys().next().value; if (k != null) this.blocks.delete(k); } this.paint(); });
    }
    return undefined;
  }
  private value(row: number, col: number): string {
    const b = this.block(row); if (!b) return '…';
    const i = row - b.first; if (i < 0 || i >= b.count) return '';
    if (col === 0) return fmtInt(row);
    const c = b.columns[0]; if (col === 1) return c && i < c.x.length ? fmtValue(c.x[i], 7) : '';
    const cc = b.columns[col - 2]; const v = cc?.values[i]; return v == null ? '' : fmtValue(v as number, 7);
  }
  paint() {
    const st = this.scroll.scrollTop, vh = this.scroll.clientHeight;
    const r0 = Math.max(0, this.rowAt(st) - 2), n = Math.min(this.dto.rows - r0, Math.ceil(vh / ROW_H) + 4);
    const cols = this.dto.channels;
    let html = `<table class="grid"><thead><tr><th>#</th><th>x</th>${cols.map(c => `<th>${esc(c.name)}${c.unit ? ` [${esc(c.unit)}]` : ''}</th>`).join('')}</tr></thead><tbody>`;
    const inSel = (r: number, c: number) => this.sel && r >= this.sel.r0 && r <= this.sel.r1 && c >= this.sel.c0 && c <= this.sel.c1;
    for (let k = 0; k < n; k++) {
      const row = r0 + k; const cur = row === this.dto.cursorRow;
      html += `<tr data-r="${row}" class="${cur ? 'cur' : ''}">`;
      for (let c = 0; c < cols.length + 2; c++) html += `<td class="${inSel(row, c) ? 'sel' : ''}">${this.value(row, c)}</td>`;
      html += '</tr>';
    }
    this.inner.innerHTML = html + '</tbody></table>';
    // position with `top` (not transform) so the sticky header keeps working inside the scroll container
    this.inner.style.top = (this.scale < 1 ? st - (this.rowAt(st) - r0) * ROW_H : r0 * ROW_H) + 'px';
    this.pos.textContent = `${fmtInt(r0)} – ${fmtInt(r0 + n)} of ${fmtInt(this.dto.rows)}` + (this.sel ? ` · selected ${fmtInt(this.sel.r1 - this.sel.r0 + 1)} × ${this.sel.c1 - this.sel.c0 + 1}` : '');
  }
  /** Tab-separated text of a cell rectangle (rows inclusive; columns 0 = #, 1 = x, 2.. = channels), also copied to the clipboard. */
  async copy(r0: number, c0: number, r1: number, c1: number): Promise<string> {
    const text = await this.tsv(r0, c0, r1, c1);
    await bridge.call('clipboardText', { text });
    return text;
  }
  async tsv(r0: number, c0: number, r1: number, c1: number): Promise<string> {
    r1 = Math.min(r1, r0 + 99999, this.dto.rows - 1); const n = r1 - r0 + 1; if (n <= 0) return '';
    const b = await bridge.call('table.rows', { table: this.dto.id, first: r0, count: n }) as TableBlock | null; if (!b) return '';
    const heads = ['#', 'x', ...this.dto.channels.map(c => c.name + (c.unit ? ` [${c.unit}]` : ''))];
    const lines = [heads.slice(c0, c1 + 1).join('\t')];
    for (let i = 0; i < b.count; i++) {
      const cells: string[] = [];
      for (let c = c0; c <= c1; c++) cells.push(c === 0 ? String(r0 + i) : c === 1 ? (b.columns[0] ? String(b.columns[0].x[i]) : '') : (b.columns[c - 2]?.values[i] == null ? '' : String(b.columns[c - 2].values[i])));
      lines.push(cells.join('\t'));
    }
    return lines.join('\n');
  }
  select(r0: number, c0: number, r1: number, c1: number) { this.sel = { r0, c0, r1, c1 }; this.paint(); }
}

export class ViewArea {
  el: HTMLElement; plots = new Map<number, PlotView>(); tables = new Map<number, TableView>(); private zone: HTMLElement; private S: State | null = null;
  constructor() {
    this.el = h(`<div class="center view"></div>`);
    this.zone = h(`<div class="dropzone"></div>`);
    this.zone.addEventListener('dragover', e => { const t = e.dataTransfer?.types ?? []; if (t.includes(DRAG_CH) || t.includes(DRAG_CURVE)) { e.preventDefault(); this.zone.classList.add('over'); } });
    this.zone.addEventListener('dragleave', () => this.zone.classList.remove('over'));
    this.zone.addEventListener('drop', e => {
      this.zone.classList.remove('over'); const dt = e.dataTransfer; if (!dt) return;
      if (dt.types.includes(DRAG_CH)) {
        e.preventDefault(); e.stopPropagation();
        const chans = JSON.parse(dt.getData(DRAG_CH) || '[]') as { id: string }[]; if (!chans.length) return;
        if (e.altKey) { bridge.call('table.add', { channels: chans.map(c => c.id) }); return; }
        if (dt.types.includes(DRAG_GROUP)) { bridge.call('dropGroup', { channels: chans.map(c => c.id) }); return; }   // file / group / node → chart(s) by the drop rule
        const xy = chans.length === 2 && dt.types.includes('application/x-nexys-xy');
        bridge.call('addCurves', { chart: 0, channels: chans.map(c => c.id), zone: xy ? 'xy' : 'plot' });
      } else if (dt.types.includes(DRAG_CURVE)) {
        e.preventDefault(); e.stopPropagation();
        const p = JSON.parse(dt.getData(DRAG_CURVE)); bridge.call('addChart').then(id => bridge.call('moveCurve', { from: p.chart, idx: p.idx, to: id }));
      }
    });
    this.zone.addEventListener('dblclick', e => { if (!(e.target as HTMLElement).closest('button')) bridge.call('addChart'); });
    this.zone.addEventListener('click', e => {
      const a = (e.target as HTMLElement).closest('[data-a]') as HTMLElement | null; if (!a) return;
      const act = a.dataset.a;
      if (act === 'unit' || act === 'each') bridge.call('setDropMode', { mode: act });
      else if (act === 'table') { const S = this.S; if (!S) return; const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0]; if (chart && chart.curves.length) bridge.call('table.fromChart', { chart: chart.id }); else if (S.selected) bridge.call('table.add', { channels: [S.selected] }); }
    });
  }

  setState(S: State) {
    this.S = S;
    const ids = new Set(S.charts.map(c => c.id));
    for (const [id, pv] of this.plots) if (!ids.has(id)) { pv.dispose(); pv.el.remove(); this.plots.delete(id); }
    const frag: HTMLElement[] = [];
    S.charts.forEach((c, i) => {
      let pv = this.plots.get(c.id);
      if (!pv) { pv = new PlotView(c, S.plot); pv.onActivate = () => { if (this.S?.activeChart !== c.id) bridge.call('setActive', { chart: c.id }); }; this.plots.set(c.id, pv); }
      pv.el.dataset.index = String(i + 1);
      pv.update(c, S.plot);
      pv.el.classList.toggle('active', S.activeChart === c.id && S.charts.length > 1);
      pv.el.classList.toggle('detail', c.detailOf != null);
      frag.push(pv.el);
    });
    const tids = new Set(S.tables.map(t => t.id));
    for (const [id, tv] of this.tables) if (!tids.has(id)) { tv.el.remove(); this.tables.delete(id); }
    const tels = S.tables.map(t => { let tv = this.tables.get(t.id); if (!tv) { tv = new TableView(t); this.tables.set(t.id, tv); } tv.update(t); return tv.el; });
    const empty = S.charts.length === 0 && S.tables.length === 0;
    this.zone.classList.toggle('empty', empty);
    const rule = `<span class="dz-tools"><span class="dz-l">DROP</span><button class="pill h24${S.dropMode !== 'each' ? ' on' : ''}" data-a="unit" title="파일·그룹 드롭: 단위별 Y축으로 차트 1개 (정수·불리언은 디지털 레인)">BY UNIT</button><button class="pill h24${S.dropMode === 'each' ? ' on' : ''}" data-a="each" title="파일·그룹 드롭: 채널마다 차트">PER CHANNEL</button><button class="pill h24" data-a="table" title="활성 차트의 채널(없으면 선택 채널)로 값 테이블">+ TABLE</button></span>`;
    this.zone.innerHTML = empty
      ? `${I.lineC(24)}<div style="margin-top:8px">채널·그룹·파일을 끌어다 놓으세요</div><div class="hints">그룹/파일 드롭 · 바로 차트      더블클릭 · 새 차트      Ctrl+클릭으로 두 채널 선택 후 드롭 · XY 차트      Alt+드롭 · 값 테이블<br>휠 · 줌      드래그 · 이동      오른쪽 드래그 · 영역 확대 (Shift = X만, Ctrl = Y만)      더블클릭 · 전체      Backspace · 줌 되돌리기<br>클릭 · 커서 A      Shift+클릭 · 커서 B      Ctrl+B · 밴드 커서      차트 메뉴 ⋯ · 상세 차트 연결 · 내보내기</div>${rule}`
      : `${I.lineC()}<span>여기에 채널·그룹을 놓으면 새 차트 <span style="color:var(--faint)">·</span> X축 영역에 놓으면 시간 채널로 지정</span>${rule}`;
    const want = [...tels, ...frag, this.zone];
    want.forEach((el, i) => { if (this.el.children[i] !== el) this.el.insertBefore(el, this.el.children[i] ?? null); });
    while (this.el.children.length > want.length) this.el.lastElementChild!.remove();
  }
}
