// Right aside: PROPERTIES for the selected channel (mockup view.html) or chart / file (same tokens).
// 0.9.3: cursor mode + band start/end inputs, x-axis link, detail chart, y-axis range/log/side, curve style (line/step/points,
// width, color, own axis), exports.
import { bridge } from '@core/bridge';
import { I } from '@core/icons';
import { ChartDto, FileDto, PropsDto, State } from '@core/types';
import { curveColor, esc, fmtInt, fmtRead, fmtSec, fmtSigned, fmtValue, h, rangeBarSvg } from '@core/util';

const kv = (k: string, v: string, sans = false) => v === '' || v == null ? '' : `<div class="kv"><span class="k">${esc(k)}</span><span class="v${sans ? ' sans' : ''}" title="${esc(v)}">${esc(v)}</span></div>`;
const sect = (icon: string, title: string, body: string, meta = '', grow = false) => `<div class="sect${grow ? ' grow' : ''}"><div class="sect-h">${icon}${esc(title)}${meta ? `<span class="m">${esc(meta)}</span>` : ''}</div>${body}</div>`;
const num = (key: string, value: number | string, title = '') => `<span class="in mini"><input type="text" data-in="${key}" value="${esc(typeof value === 'number' ? fmtValue(value, 8) : value)}" title="${esc(title)}" spellcheck="false"></span>`;

export class Properties {
  el: HTMLElement; private body: HTMLElement; private meta: HTMLElement; private S: State | null = null;
  constructor() {
    this.el = h(`<aside class="card aside props"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">PROPERTIES</span><span class="m" data-meta></span></div><div class="scroll body" style="display:flex;flex-direction:column"></div></aside>`);
    this.body = this.el.querySelector('.body')!; this.meta = this.el.querySelector('[data-meta]')!;
    this.body.addEventListener('click', e => this.onClick(e));
    this.body.addEventListener('change', e => this.onChange(e));
    this.body.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT') { (e.target as HTMLInputElement).blur(); e.stopPropagation(); } });
  }
  setState(S: State) {
    this.S = S;
    const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0];
    if (S.mode === 'NAVIGATOR') { this.renderFile(S.fileProps ?? S.files.find(f => f.name === S.selectedFile) ?? S.files[0] ?? null, S); return; }
    if (document.activeElement && this.body.contains(document.activeElement) && (document.activeElement as HTMLElement).tagName === 'INPUT') return;   // keep the input the user is typing in
    if (S.props) { this.renderChannel(S.props, chart, S); return; }
    if (chart) { this.renderChart(chart, S); return; }
    this.meta.textContent = ''; this.body.innerHTML = `<div class="prop-body"><div class="muted11">Select a channel, chart or curve.</div></div>`;
  }

  private renderChannel(p: PropsDto, chart: ChartDto | undefined, S: State) {
    this.meta.textContent = p.name;
    const when = p.valueWhen ? ` <span>· ${esc(p.valueWhen)}</span>` : '';
    const delta = p.valueB != null && p.value != null ? `<span class="delta">Δ ${fmtSigned(p.valueB - p.value)} to B</span>` : '';
    const min = p.min ?? 0, max = p.max ?? 0;
    const big = p.isText ? '' : `<div class="prop-big"><div class="lbl">${esc(p.valueLabel)}${when}</div><div class="val"><span class="num">${p.value == null ? '—' : p.valueText ? esc(p.valueText) : fmtRead(p.value)}</span><span class="unit">${esc(p.unit)}</span>${delta}</div>
      <div class="bar">${p.ready ? rangeBarSvg(min, max, p.value, p.band) : ''}</div><div class="ends"><span>min ${p.ready ? fmtValue(min, 4) : '…'}</span><span class="c">${p.band ? 'A–B range' : 'range'}</span><span>max ${p.ready ? fmtValue(max, 4) : '…'}</span></div></div>`;
    const data = kv('samples', fmtInt(p.length)) + kv('type', p.dataType || '—') + kv('unit', p.unit || '—') + kv('x channel', p.xChannel ?? '') + kv('rate', p.rate ? fmtValue(p.rate, 4) + ' Hz' : '') + kv('render', p.render) + (p.description ? kv('description', p.description, true) : '');
    const time = (p.wfStart ? kv('wf_start_time', p.wfStart) : '') + (p.absolute != null ? kv('absolute', p.absolute.toExponential(3).replace('e+', 'E') + ' s') : '') + (chart ? kv('display', p.display) : '');
    let curvesBody = ''; let curvesMeta = '';
    if (chart) {
      curvesMeta = chart.title || `Chart ${S.charts.indexOf(chart) + 1}`;
      curvesBody = chart.curves.map(c => `<div class="cv-row${c.channelId === p.id ? ' sel' : ''}" data-cv="${c.idx}"><span class="swatch" style="background:${curveColor(c.color, S.plot)}"></span><span class="n">${esc(c.name)}${c.lane ? ' · lane' : c.step ? ' · step' : c.points ? ' · points' : ''}${c.visible ? '' : ' · hidden'}</span><span class="u">${esc(c.unit || (c.dataType === 'string' ? 'text' : ''))}</span></div>`).join('');
    }
    // actions for the curve of this channel in the active chart
    let act = '';
    if (p.curveIdx != null && chart) {
      const c = chart.curves[p.curveIdx];
      const render = c.points ? 'points' : c.step ? 'step' : 'line';
      act = `<div class="pills" style="margin-top:6px"><button class="pill${render === 'line' ? ' on' : ''}" data-a="line">LINE</button><button class="pill${render === 'step' ? ' on' : ''}" data-a="step">STEP</button><button class="pill${render === 'points' ? ' on' : ''}" data-a="points">POINTS</button>${p.timeCandidate ? `<button class="pill" data-a="setx">SET AS X</button>` : ''}<button class="pill" data-a="rmcurve">REMOVE</button></div>
        <div class="pills" style="margin-top:6px"><span class="lbl-in">WIDTH</span>${[1, 1.5, 2].map(w => `<button class="pill${Math.abs(c.width - w) < 0.01 ? ' on' : ''}" data-a="w:${w}">${w}</button>`).join('')}<span class="lbl-in">AXIS</span><button class="pill${!c.right ? ' on' : ''}" data-a="side:left">L</button><button class="pill${c.right ? ' on' : ''}" data-a="side:right">R</button><button class="pill${(chart.axes.find(a => a.id === c.axisId)?.curves ?? 1) > 1 ? ' on' : ''}" data-a="ax:shared" title="같은 단위의 곡선과 축 공유">SHARED</button><button class="pill${(chart.axes.find(a => a.id === c.axisId)?.curves ?? 1) === 1 && chart.curves.filter(x => x.unit === c.unit).length > 1 ? ' on' : ''}" data-a="ax:own" title="이 곡선만의 축">OWN</button><button class="pill${c.visible ? '' : ' on'}" data-a="hide">${c.visible ? 'HIDE' : 'SHOW'}</button></div>
        <div class="swatches">${Array.from({ length: 8 }, (_, i) => `<button class="sw${c.color % 8 === i ? ' on' : ''}" data-a="color:${i}" style="background:${curveColor(i, S.plot)}" title="색 ${i + 1}"></button>`).join('')}</div>`;
    } else if (chart && !p.isText) act = `<div class="pills" style="margin-top:6px"><button class="pill" data-a="addcurve">ADD TO CHART</button>${p.timeCandidate ? `<button class="pill" data-a="setx">SET AS X</button>` : ''}${chart.hasDetail || chart.detailOf != null ? `<button class="pill" data-a="detailch" title="상세 차트에 이 채널 표시">SHOW IN DETAIL</button>` : ''}</div>`;
    const props = p.props.length ? sect(I.tag(), 'PROPERTIES', p.props.map(x => kv(x.k, x.v, !/^-?[\d.eE+-]+$/.test(x.v))).join(''), String(p.props.length)) : '';
    this.body.innerHTML = big + sect(I.tag(), 'DATA', data) + (time ? sect(I.clockM(), 'TIME', time) : '') + (chart ? sect(I.lineM(), 'CURVES', curvesBody + act + this.chartControls(chart, S), curvesMeta, true) : sect(I.lineM(), 'CURVE', act)) + (chart ? this.axesSection(chart) : '') + props;
  }

  private chartControls(c: ChartDto, S: State): string {
    const tm = c.xTimeMode;
    const x = c.xChannel ? `<div class="kv" style="margin-top:6px"><span class="k">x axis</span><span class="v">${esc(c.xChannel.name)} [${esc(c.xChannel.unit)}]</span></div><div class="pills"><button class="pill${tm === 'relative' ? ' on' : ''}" data-a="tm:relative">REL</button><button class="pill${tm === 'absolute' ? ' on' : ''}" data-a="tm:absolute">ABS</button><button class="pill${tm === 'none' ? ' on' : ''}" data-a="tm:none">NUMERIC</button><button class="pill" data-a="clearx">CLEAR X</button></div>`
      : c.suggestedX ? `<div class="kv" style="margin-top:6px"><span class="k">suggested x</span><span class="v">${esc(c.suggestedX.name)} [${esc(c.suggestedX.unit)}]</span></div><div class="pills"><button class="pill primary" data-a="usex">USE AS X</button></div>` : '';
    const band = c.cursorMode === 'band';
    const cursor = `<div class="kv" style="margin-top:6px"><span class="k">cursor</span><span class="v">${band ? 'band' : 'A / B'}${c.detailOf != null ? ' · detail chart' : c.hasDetail ? ' · has detail' : ''}</span></div>
      <div class="pills"><button class="pill${!band ? ' on' : ''}" data-a="cm:free" title="자유 커서 A/B (클릭 / Shift+클릭)">A / B</button><button class="pill${band ? ' on' : ''}" data-a="cm:band" title="밴드 커서: 구간 통계, 상세 차트 연동 (Ctrl+B)">BAND</button>${c.detailOf == null ? `<button class="pill${c.hasDetail ? ' on' : ''}" data-a="detail" title="아래에 상세 차트를 만들어 밴드 구간을 확대해서 봅니다">${c.hasDetail ? 'DETAIL ✓' : 'LINK DETAIL'}</button>` : ''}${c.linkKey || c.detailOf != null ? `<button class="pill${c.linkX ? ' on' : ''}" data-a="linkx" title="같은 X를 쓰는 차트와 팬·줌 공유">LINK X</button>` : ''}</div>`
      + (band && c.band.on ? `<div class="band-in"><span class="lbl-in">BAND</span>${num('b0', c.band.x0, '시작 (x 단위, Enter)')}<span class="lbl-in">→</span>${num('b1', c.band.x1, '끝 (x 단위, Enter)')}<span class="muted11" style="margin-left:4px">${esc(c.band.label0)} → ${esc(c.band.label1)}</span></div><div class="faint11" style="margin-top:2px">← → 1 px · Shift+← → 10 px · 손잡이 드래그 = 폭 · 안쪽 드래그 = 이동</div>` : '');
    const lanes = `<div class="pills" style="margin-top:6px"><button class="pill${c.lanes ? ' on' : ''}" data-a="lanes">DIGITAL LANES</button><button class="pill${c.grid ? ' on' : ''}" data-a="grid">GRID</button><button class="pill" data-a="zoomfull">ZOOM FULL</button><button class="pill" data-a="zoomback" title="Backspace"${c.zoomHistory ? '' : ' disabled'}>BACK</button></div>`;
    const exp = `<div class="pills" style="margin-top:6px"><span class="lbl-in">EXPORT</span><button class="pill" data-a="csv" title="${c.band.on ? '밴드 구간' : '전체'} → CSV">CSV…</button><button class="pill" data-a="tdms" title="${c.band.on ? '밴드 구간' : '전체'} → TDMS">TDMS…</button><button class="pill" data-a="pngcopy" title="차트 이미지를 클립보드로">PNG COPY</button><button class="pill" data-a="pngsave">PNG…</button><button class="pill" data-a="table" title="이 차트의 채널로 값 테이블">+ TABLE</button></div>`;
    const align = c.alignMode !== 'none' ? `<div class="kv" style="margin-top:6px"><span class="k">alignment</span><span class="v">${esc(c.alignMode)}${c.alignMode === 'manual' ? ` (${c.alignOffsetMs} ms/file)` : ''}</span></div>` + c.curves.map(cv => kv(cv.file.replace(/\.[^.]+$/, ''), (cv.xOffset >= 0 ? '+' : '') + cv.xOffset.toFixed(3) + ' s')).join('') : '';
    void S;
    return x + cursor + lanes + exp + align;
  }

  private axesSection(c: ChartDto): string {
    if (!c.axes.length) return '';
    const rows = c.axes.map(a => `<div class="axis-row" data-axis="${esc(a.id)}"><div class="kv"><span class="k">${esc(a.unit || '(no unit)')} · ${a.curves} curve${a.curves !== 1 ? 's' : ''}</span><span class="v">${a.auto ? 'auto' : 'manual'}${a.log ? ' · log' : ''}</span></div>
      <div class="pills"><button class="pill${a.auto ? ' on' : ''}" data-a="axauto">AUTO</button><button class="pill${!a.auto ? ' on' : ''}" data-a="axmanual">MANUAL</button><button class="pill${a.log ? ' on' : ''}" data-a="axlog" title="로그 축 (0 이하 값은 건너뜀)">LOG</button><button class="pill${!a.right ? ' on' : ''}" data-a="axleft">LEFT</button><button class="pill${a.right ? ' on' : ''}" data-a="axright">RIGHT</button></div>
      ${a.auto ? '' : `<div class="band-in"><span class="lbl-in">MIN</span>${num('axmin', a.min)}<span class="lbl-in">MAX</span>${num('axmax', a.max)}</div>`}</div>`).join('');
    return sect(I.sliders(), 'Y AXES', rows, String(c.axes.length));
  }

  private renderChart(c: ChartDto, S: State) {
    this.meta.textContent = c.title || `Chart ${S.charts.indexOf(c) + 1}`;
    const title = `<div class="sect"><div class="field"><label>CHART TITLE</label><div class="in"><input type="text" data-in="title" value="${esc(c.title)}" placeholder="Chart ${S.charts.indexOf(c) + 1}"></div></div>${kv('x range', `${fmtValue(c.xmin, 6)} … ${fmtValue(c.xmax, 6)}`)}${kv('x unit', c.xUnit)}${kv('curves', String(c.curves.length))}</div>`;
    const curves = c.curves.map(cv => `<div class="cv-row" data-cv="${cv.idx}"><span class="swatch" style="background:${curveColor(cv.color, S.plot)}"></span><span class="n">${esc(cv.name)}${cv.lane ? ' · lane' : cv.step ? ' · step' : cv.points ? ' · points' : ''}</span><span class="u">${esc(cv.unit)}</span></div>`).join('');
    this.body.innerHTML = title + sect(I.lineM(), 'CURVES', curves + this.chartControls(c, S), '', true) + this.axesSection(c);
  }

  private renderFile(f: FileDto | null, S: State) {
    this.meta.textContent = 'file';
    if (!f) { this.body.innerHTML = `<div class="prop-body"><div class="muted11">No file open.</div></div>`; return; }
    const rows = kv('format', f.format + (f.format === 'TDMS' ? ' 2.0' : '')) + kv('segments', f.segments ?? '—') + kv('groups', String(f.groups.length)) + kv('channels', String(f.channelCount)) + kv('start', f.start != null ? f.startText.split(' ').slice(-1)[0] : '—') + kv('duration', f.duration != null ? fmtSec(f.duration) : '—') + kv('rate', f.rate != null ? fmtValue(f.rate, 4) + ' Hz' : '—');
    const al = S.align.mode;
    this.body.innerHTML = `<div class="prop-body"><div class="prop-title">${esc(f.name.replace(/\.[^.]+$/, ''))}</div>${rows}<div class="hr"></div><div class="lblh">ALIGNMENT</div><div class="pills"><button class="pill h24${al === 'start' ? ' on' : ''}" data-a="al:start">START TIME</button><button class="pill h24${al === 'zero' ? ' on' : ''}" data-a="al:zero">ZERO</button><button class="pill h24${al === 'manual' ? ' on' : ''}" data-a="al:manual">MANUAL</button></div>${al === 'manual' ? `<div class="field"><label>OFFSET PER FILE</label><div class="in"><input type="text" data-in="offset" value="${S.align.offsetMs}"><span class="u">ms</span></div></div>` : ''}<div class="hr"></div><div class="lblh">FILE PROPERTIES</div>${f.props.map(x => kv(x.k, x.v, !/^-?[\d.eE+-]+$/.test(x.v))).join('')}</div>`;
  }

  private onClick(e: MouseEvent) {
    const S = this.S; if (!S) return;
    const t = e.target as HTMLElement;
    const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0];
    const cvRow = t.closest('[data-cv]') as HTMLElement | null;
    if (cvRow && chart) { const c = chart.curves[+cvRow.dataset.cv!]; if (c) bridge.call('select', { channel: c.channelId }); return; }
    const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
    const p = S.props;
    if (a.startsWith('al:')) { bridge.call('setAlign', { mode: a.slice(3), offsetMs: S.align.offsetMs }); return; }
    if (!chart) { if (a === 'addcurve' && p) bridge.call('addCurves', { chart: 0, channels: [p.id] }); return; }
    const id = chart.id; const ci = p?.curveIdx;
    const axisId = (t.closest('[data-axis]') as HTMLElement | null)?.dataset.axis;
    if (a === 'line' || a === 'step' || a === 'points') { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, step: a === 'step', points: a === 'points' }); }
    else if (a.startsWith('w:')) { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, width: parseFloat(a.slice(2)) }); }
    else if (a.startsWith('side:')) { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, right: a === 'side:right' }); }
    else if (a.startsWith('ax:')) { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, axis: a.slice(3) }); }
    else if (a.startsWith('color:')) { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, color: +a.slice(6) }); }
    else if (a === 'hide') { if (ci != null) bridge.call('setCurve', { chart: id, idx: ci, visible: !chart.curves[ci].visible }); }
    else if (a === 'rmcurve') { if (ci != null) bridge.call('removeCurve', { chart: id, idx: ci }); }
    else if (a === 'addcurve') { if (p) bridge.call('addCurves', { chart: id, channels: [p.id] }); }
    else if (a === 'detailch') { if (p) bridge.call(chart.detailOf != null ? 'setDetailChannel' : 'linkDetail', { chart: id, channel: p.id }); }
    else if (a === 'setx') { if (p) bridge.call('setX', { chart: id, channel: p.id }); }
    else if (a === 'usex') { if (chart.suggestedX) bridge.call('setX', { chart: id, channel: chart.suggestedX.id }); }
    else if (a === 'clearx') bridge.call('clearX', { chart: id });
    else if (a.startsWith('tm:')) bridge.call('setChart', { chart: id, timeMode: a.slice(3) });
    else if (a.startsWith('cm:')) bridge.call('setCursorMode', { chart: id, mode: a.slice(3) });
    else if (a === 'detail') bridge.call('linkDetail', { chart: id, channel: p?.id });
    else if (a === 'linkx') bridge.call('setLinkX', { chart: id, on: !chart.linkX });
    else if (a === 'lanes') bridge.call('setChart', { chart: id, lanes: !chart.lanes });
    else if (a === 'grid') bridge.call('setChart', { chart: id, grid: !chart.grid });
    else if (a === 'zoomfull') bridge.call('zoomFull', { chart: id });
    else if (a === 'zoomback') bridge.call('zoomBack', { chart: id });
    else if (a === 'table') bridge.call('table.fromChart', { chart: id });
    else if (a === 'csv' || a === 'tdms') { const r = chart.band.on ? { x0: chart.band.x0, x1: chart.band.x1 } : {}; bridge.call('export.range', { kind: a, channels: chart.curves.map(c => c.channelId), chart: id, ...r }); }
    else if (a === 'pngcopy' || a === 'pngsave') document.dispatchEvent(new CustomEvent('nx-chart-png', { detail: { chart: id, save: a === 'pngsave' } }));
    else if (a === 'axauto' && axisId) bridge.call('setAxis', { chart: id, axis: axisId, auto: true });
    else if (a === 'axmanual' && axisId) { const ax = chart.axes.find(x => x.id === axisId); bridge.call('setAxis', { chart: id, axis: axisId, auto: false, min: ax?.min, max: ax?.max }); }
    else if (a === 'axlog' && axisId) { const ax = chart.axes.find(x => x.id === axisId); bridge.call('setAxis', { chart: id, axis: axisId, log: !ax?.log }); }
    else if ((a === 'axleft' || a === 'axright') && axisId) bridge.call('setAxis', { chart: id, axis: axisId, right: a === 'axright' });
  }
  private onChange(e: Event) {
    const S = this.S; if (!S) return;
    const t = e.target as HTMLInputElement; const k = t.dataset.in;
    const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0];
    if (k === 'title') { if (chart) bridge.call('setChart', { chart: chart.id, title: t.value }); }
    else if (k === 'offset') bridge.call('setAlign', { mode: S.align.mode, offsetMs: parseFloat(t.value) || 0 });
    else if ((k === 'b0' || k === 'b1') && chart) { const v = parseFloat(t.value); if (!Number.isFinite(v)) return; const x0 = k === 'b0' ? v : chart.band.x0, x1 = k === 'b1' ? v : chart.band.x1; bridge.call('band', { chart: chart.id, on: true, x0: Math.min(x0, x1), x1: Math.max(x0, x1), commit: true }); }
    else if ((k === 'axmin' || k === 'axmax') && chart) { const axisId = (t.closest('[data-axis]') as HTMLElement | null)?.dataset.axis; const ax = chart.axes.find(x => x.id === axisId); const v = parseFloat(t.value); if (!ax || !Number.isFinite(v)) return; bridge.call('setAxis', { chart: chart.id, axis: ax.id, auto: false, min: k === 'axmin' ? v : ax.min, max: k === 'axmax' ? v : ax.max }); }
  }
}
