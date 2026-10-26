// VIEW center: chart cards (PlotView per chart, keyed by id), value tables (virtualized, cursor row tracking, rectangular
// selection + Ctrl+C), the dashed "new chart" drop zone with the drop rule (by unit / per channel) and "+ TABLE".
import { bridge, WIN } from './bridge';
import { I } from './icons';
import { DRAG_CH, DRAG_CURVE, DRAG_GROUP, PlotView } from './plot';
const DRAG_PAGE = 'application/x-nexys-page';
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
    this.el.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes(DRAG_CH)) { e.preventDefault(); this.el.classList.add('over'); } });
    this.el.addEventListener('dragleave', () => { this.el.classList.remove('over'); });
    this.el.addEventListener('drop', e => { this.el.classList.remove('over'); const dt = e.dataTransfer; if (!dt?.types.includes(DRAG_CH)) return; e.preventDefault(); e.stopPropagation(); const chans = JSON.parse(dt.getData(DRAG_CH) || '[]') as { id: string }[]; bridge.call('table.addChannels', { table: this.dto.id, channels: chans.map(c => c.id) }); });
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

  // 0.9.4: pages (main window) and the chart grid
  private pages: HTMLElement = h(`<div class="pages"></div>`);
  private grid: HTMLElement = h(`<div class="charts"></div>`);
  private pagesBound = false;
  private bindPages() {
    this.pagesBound = true;
    this.pages.addEventListener('click', e => {
      const S = this.S; if (!S) return; const t = e.target as HTMLElement;
      const act = (t.closest('[data-pact]') as HTMLElement | null)?.dataset.pact;
      const tab = t.closest('.ptab[data-page]') as HTMLElement | null; const pid = tab ? +tab.dataset.page! : S.activePage;
      if (act === 'remove') { e.stopPropagation(); this.confirmRemove(pid); return; }
      if (act === 'add') { bridge.call('page.add'); return; }
      if (act === 'cols1' || act === 'cols2') { bridge.call('page.columns', { page: S.activePage, n: act === 'cols2' ? 2 : 1 }); return; }
      if (act === 'auto') { bridge.call('page.auto', { on: !S.autoPage.on, n: S.autoPage.n }); return; }
      if (tab && !tab.querySelector('input')) bridge.call('page.select', { page: pid });
    });
    this.pages.addEventListener('dblclick', e => {
      const tab = (e.target as HTMLElement).closest('.ptab[data-page]') as HTMLElement | null; if (!tab || tab.querySelector('input')) return;
      const nm = tab.querySelector('.nm') as HTMLElement; const old = nm.textContent ?? '';
      const inp = h(`<input type="text" value="${esc(old)}" spellcheck="false">`) as HTMLInputElement; nm.replaceWith(inp); inp.focus(); inp.select();
      const done = (commit: boolean) => { const v = inp.value.trim(); if (commit && v && v !== old) bridge.call('page.rename', { page: +tab.dataset.page!, name: v }); else { const back = h(`<span class="nm">${esc(old)}</span>`); inp.replaceWith(back); } };
      inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') { ev.preventDefault(); done(true); } else if (ev.key === 'Escape') done(false); ev.stopPropagation(); });
      inp.addEventListener('blur', () => done(true));
    });
    this.pages.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.pin === 'n' && this.S) { const n = parseInt(t.value, 10); if (n >= 1) bridge.call('page.auto', { on: this.S.autoPage.on, n }); } });
    this.pages.addEventListener('dragstart', e => { const tab = (e.target as HTMLElement).closest('.ptab[data-page]') as HTMLElement | null; if (!tab || !e.dataTransfer) return; e.dataTransfer.setData(DRAG_PAGE, tab.dataset.page!); e.dataTransfer.effectAllowed = 'move'; });
    this.pages.addEventListener('dragover', e => { const tab = (e.target as HTMLElement).closest('.ptab[data-page]') as HTMLElement | null; if (tab && e.dataTransfer?.types.includes(DRAG_PAGE)) { e.preventDefault(); tab.classList.add('dragover'); } });
    this.pages.addEventListener('dragleave', e => { (e.target as HTMLElement).closest?.('.ptab')?.classList.remove('dragover'); });
    this.pages.addEventListener('drop', e => {
      const tab = (e.target as HTMLElement).closest('.ptab[data-page]') as HTMLElement | null; if (!tab || !e.dataTransfer?.types.includes(DRAG_PAGE)) return;
      e.preventDefault(); tab.classList.remove('dragover'); const from = +e.dataTransfer.getData(DRAG_PAGE); const S = this.S; if (!S || from === +tab.dataset.page!) return;
      const index = S.pages.findIndex(p => p.id === +tab.dataset.page!); bridge.call('page.moveTo', { page: from, index });
    });
  }
  private confirmRemove(pid: number) {
    const S = this.S; if (!S) return; const p = S.pages.find(x => x.id === pid); if (!p) return;
    if (p.charts === 0) { bridge.call('page.remove', { page: pid }); return; }
    const other = S.pages.find(x => x.id !== pid)?.name ?? '';
    const dlg = h(`<div class="modal-bg"><div class="modal" role="dialog"><div class="lblh">REMOVE PAGE</div><div class="muted11">${esc(p.name)}: 차트 ${p.charts}개가 ${esc(other)}(으)로 옮겨집니다. 삭제되지 않습니다.</div><div class="btns"><button class="pill h30" data-b="cancel">CANCEL</button><button class="pill h30 primary" data-b="ok">REMOVE PAGE</button></div></div></div>`);
    dlg.addEventListener('click', e => { const b = (e.target as HTMLElement).closest('[data-b]') as HTMLElement | null; if (b?.dataset.b === 'ok') bridge.call('page.remove', { page: pid }); if (b || e.target === dlg) dlg.remove(); });
    document.body.append(dlg);
  }
  private renderPages(S: State) {
    if (!this.pagesBound) this.bindPages();
    const cur = S.pages.find(p => p.id === S.activePage); const cols = cur?.columns ?? 1;
    const tabs = S.pages.map(p => `<button class="ptab${p.id === S.activePage ? ' on' : ''}" data-page="${p.id}" draggable="true" title="클릭: 보기 · 더블클릭: 이름 바꾸기 · 드래그: 순서"><span class="nm">${esc(p.name)}</span><span class="n">${p.charts}</span>${p.id === S.activePage && S.pages.length > 1 ? `<span class="x" data-pact="remove" title="페이지 제거 (차트는 다른 페이지로)">${I.close(10)}</span>` : ''}</button>`).join('');
    this.pages.innerHTML = `<div class="ptabs">${tabs}<button class="ptab add" data-pact="add" title="새 페이지 (Ctrl+PageDown/PageUp: 이동)">+</button></div><span class="sp"></span><span class="lbl-in">GRID</span><button class="pill h24${cols === 1 ? ' on' : ''}" data-pact="cols1">1 COL</button><button class="pill h24${cols === 2 ? ' on' : ''}" data-pact="cols2">2 COL</button><span class="lbl-in">AUTO PAGE</span><button class="pill h24${S.autoPage.on ? ' on' : ''}" data-pact="auto" title="차트가 n개를 넘으면 새 페이지에">${S.autoPage.on ? 'ON' : 'OFF'}</button><span class="in mini" style="width:36px"><input data-pin="n" value="${S.autoPage.n}" title="페이지당 차트 수"></span>`;
    this.grid.classList.toggle('cols2', cols === 2);
  }

  setState(S: State, charts = S.charts, tables = S.tables) {
    this.S = S;
    const ids = new Set(charts.map(c => c.id));
    for (const [id, pv] of this.plots) if (!ids.has(id)) { pv.dispose(); pv.el.remove(); this.plots.delete(id); }
    const frag: HTMLElement[] = [];
    charts.forEach((c, i) => {
      let pv = this.plots.get(c.id);
      if (!pv) { pv = new PlotView(c, S.plot); pv.onActivate = () => { if (this.S?.activeChart !== c.id) bridge.call('setActive', { chart: c.id }); }; this.plots.set(c.id, pv); }
      pv.el.dataset.index = String(S.charts.indexOf(c) + 1);
      pv.update(c, S.plot);
      pv.el.classList.toggle('active', S.activeChart === c.id && charts.length > 1);
      pv.el.classList.toggle('detail', c.detailOf != null);
      frag.push(pv.el); void i;
    });
    const tids = new Set(tables.map(t => t.id));
    for (const [id, tv] of this.tables) if (!tids.has(id)) { tv.el.remove(); this.tables.delete(id); }
    const tels = tables.map(t => { let tv = this.tables.get(t.id); if (!tv) { tv = new TableView(t); this.tables.set(t.id, tv); } tv.update(t); return tv.el; });
    const empty = charts.length === 0 && tables.length === 0;
    this.zone.classList.toggle('empty', empty);
    const rule = `<span class="dz-tools"><span class="dz-l">DROP</span><button class="pill h24${S.dropMode !== 'each' ? ' on' : ''}" data-a="unit" title="파일·그룹 드롭: 단위별 Y축으로 차트 1개 (정수·불리언은 디지털 레인)">BY UNIT</button><button class="pill h24${S.dropMode === 'each' ? ' on' : ''}" data-a="each" title="파일·그룹 드롭: 채널마다 차트">PER CHANNEL</button><button class="pill h24" data-a="table" title="활성 차트의 채널(없으면 선택 채널)로 값 테이블">+ TABLE</button></span>`;
    if (WIN > 0) this.zone.innerHTML = empty ? `${I.lineC(24)}<div style="margin-top:8px">이 창의 차트가 모두 주 창으로 돌아갔습니다</div>` : `${I.lineC()}<span>차트 메뉴(⋯) → 주 창으로 · 창을 닫으면 모든 차트가 주 창으로</span>`;
    else this.zone.innerHTML = empty
      ? `${I.lineC(24)}<div style="margin-top:8px">채널·그룹·파일을 끌어다 놓으세요</div><div class="hints">그룹/파일 드롭 · 바로 차트      더블클릭 · 새 차트      Ctrl+클릭으로 두 채널 선택 후 드롭 · XY 차트      Alt+드롭 · 값 테이블<br>휠 · 줌      드래그 · 이동      오른쪽 드래그 · 영역 확대 (Shift = X만, Ctrl = Y만)      더블클릭 · 전체      Backspace · 줌 되돌리기<br>클릭 · 커서 A      Shift+클릭 · 커서 B      Ctrl+B · 밴드 커서      차트 메뉴 ⋯ · 상세 차트 연결 · 새 창으로 · 내보내기</div>${rule}`
      : `${I.lineC()}<span>여기에 채널·그룹을 놓으면 새 차트 <span style="color:var(--faint)">·</span> X축 영역에 놓으면 시간 채널로 지정</span>${rule}`;
    // chart cards live in the grid (1 / 2 columns); tables above it; pages strip first (main window only)
    frag.forEach((el, i) => { if (this.grid.children[i] !== el) this.grid.insertBefore(el, this.grid.children[i] ?? null); });
    while (this.grid.children.length > frag.length) this.grid.lastElementChild!.remove();
    if (WIN === 0) this.renderPages(S);
    const want = [...(WIN === 0 ? [this.pages] : []), ...tels, this.grid, this.zone];
    want.forEach((el, i) => { if (this.el.children[i] !== el) this.el.insertBefore(el, this.el.children[i] ?? null); });
    while (this.el.children.length > want.length) this.el.lastElementChild!.remove();
  }
}
