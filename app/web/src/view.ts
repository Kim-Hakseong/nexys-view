// VIEW center: chart cards (PlotView per chart, keyed by id), value tables, the dashed "new chart" drop zone.
import { bridge } from './bridge';
import { I } from './icons';
import { DRAG_CH, DRAG_CURVE, PlotView } from './plot';
import { State, TableDto } from './types';
import { esc, fmtInt, fmtValue, h } from './util';

export class ViewArea {
  el: HTMLElement; plots = new Map<number, PlotView>(); private zone: HTMLElement; private S: State | null = null; private tables = new Map<number, HTMLElement>();
  constructor() {
    this.el = h(`<div class="center view"></div>`);
    this.zone = h(`<div class="dropzone">${I.lineC()}<span>여기에 채널을 놓으면 새 차트 <span style="color:var(--faint)">·</span> X축 영역에 놓으면 시간 채널로 지정</span></div>`);
    this.zone.addEventListener('dragover', e => { const t = e.dataTransfer?.types ?? []; if (t.includes(DRAG_CH) || t.includes(DRAG_CURVE)) { e.preventDefault(); this.zone.classList.add('over'); } });
    this.zone.addEventListener('dragleave', () => this.zone.classList.remove('over'));
    this.zone.addEventListener('drop', e => {
      this.zone.classList.remove('over'); const dt = e.dataTransfer; if (!dt) return;
      if (dt.types.includes(DRAG_CH)) {
        e.preventDefault(); e.stopPropagation();
        const chans = JSON.parse(dt.getData(DRAG_CH) || '[]') as { id: string }[]; if (!chans.length) return;
        if (e.altKey) { bridge.call('table.add', { channels: chans.map(c => c.id) }); return; }
        const xy = chans.length === 2 && dt.types.includes('application/x-nexys-xy');
        bridge.call('addCurves', { chart: 0, channels: chans.map(c => c.id), zone: xy ? 'xy' : 'plot' });
      } else if (dt.types.includes(DRAG_CURVE)) {
        e.preventDefault(); e.stopPropagation();
        const p = JSON.parse(dt.getData(DRAG_CURVE)); bridge.call('addChart').then(id => bridge.call('moveCurve', { from: p.chart, idx: p.idx, to: id }));
      }
    });
    this.zone.addEventListener('dblclick', () => bridge.call('addChart'));
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
      frag.push(pv.el);
    });
    const tids = new Set(S.tables.map(t => t.id));
    for (const [id, el] of this.tables) if (!tids.has(id)) { el.remove(); this.tables.delete(id); }
    const tels = S.tables.map(t => this.tableEl(t));
    const empty = S.charts.length === 0 && S.tables.length === 0;
    this.zone.classList.toggle('empty', empty);
    this.zone.innerHTML = empty
      ? `${I.lineC(24)}<div style="margin-top:8px">채널을 끌어다 놓으세요</div><div class="hints">더블클릭 · 새 차트      Ctrl+클릭으로 두 채널 선택 후 드롭 · XY 차트      Alt+드롭 · 값 테이블<br>휠 · 줌      드래그 · 이동      Shift+드래그 · 영역 줌      클릭 · 커서 A      Shift+클릭 · 커서 B      더블클릭 · 전체 보기</div>`
      : `${I.lineC()}<span>여기에 채널을 놓으면 새 차트 <span style="color:var(--faint)">·</span> X축 영역에 놓으면 시간 채널로 지정</span>`;
    // keep DOM order = state order
    const want = [...tels, ...frag, this.zone];
    want.forEach((el, i) => { if (this.el.children[i] !== el) this.el.insertBefore(el, this.el.children[i] ?? null); });
    while (this.el.children.length > want.length) this.el.lastElementChild!.remove();
  }

  private tableEl(t: TableDto): HTMLElement {
    let el = this.tables.get(t.id);
    if (!el) {
      el = h(`<section class="card tbl" data-table="${t.id}"><div class="card-h"><span class="ic">${I.table()}</span><span class="t">TABLE</span><span class="m"></span><button class="xbtn" style="margin-left:8px" aria-label="Remove table">${I.close()}</button></div><div class="scroll"><table class="grid"></table></div><div class="tbl-foot"><button class="pill" data-p="prev">PREV</button><button class="pill" data-p="next">NEXT</button><span class="pos"></span></div></section>`);
      el.querySelector('.xbtn')!.addEventListener('click', () => bridge.call('table.remove', { table: t.id }));
      (el as any)._first = 0;
      el.querySelector('[data-p=prev]')!.addEventListener('click', () => { (el as any)._first = Math.max(0, (el as any)._first - 100); this.loadTable(el!, t); });
      el.querySelector('[data-p=next]')!.addEventListener('click', () => { (el as any)._first = Math.min(Math.max(0, t.rows - 100), (el as any)._first + 100); this.loadTable(el!, t); });
      el.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes(DRAG_CH)) { e.preventDefault(); el!.style.borderColor = 'var(--accent2)'; } });
      el.addEventListener('dragleave', () => { el!.style.borderColor = ''; });
      el.addEventListener('drop', e => { el!.style.borderColor = ''; const dt = e.dataTransfer; if (!dt?.types.includes(DRAG_CH)) return; e.preventDefault(); e.stopPropagation(); const chans = JSON.parse(dt.getData(DRAG_CH) || '[]') as { id: string }[]; bridge.call('table.addChannels', { table: t.id, channels: chans.map(c => c.id) }); });
      this.tables.set(t.id, el);
    }
    el.querySelector('.card-h .m')!.textContent = `${t.channels.length} ch · ${fmtInt(t.rows)} rows`;
    this.loadTable(el, t);
    return el;
  }
  private async loadTable(el: HTMLElement, t: TableDto) {
    const first = (el as any)._first as number;
    const r = await bridge.call('table.rows', { table: t.id, first, count: 100 });
    if (!r) return;
    const cols = r.columns as { name: string; unit: string; values: (number | null)[]; x: number[] }[];
    let html = `<thead><tr><th>#</th>${cols.map(c => `<th>${esc(c.name)}${c.unit ? ` [${esc(c.unit)}]` : ''}</th>`).join('')}</tr></thead><tbody>`;
    const n = Math.max(0, ...cols.map(c => c.values.length));
    for (let i = 0; i < n; i++) html += `<tr><td>${fmtInt(r.first + i)}</td>${cols.map(c => `<td>${c.values[i] == null ? '' : fmtValue(c.values[i] as number, 7)}</td>`).join('')}</tr>`;
    el.querySelector('table')!.innerHTML = html + '</tbody>';
    el.querySelector('.pos')!.textContent = `${fmtInt(r.first)} – ${fmtInt(r.first + n)} of ${fmtInt(r.total)}`;
  }
}
