// Right aside: PROPERTIES for the selected channel (mockup view.html) or chart / file (same tokens).
import { bridge } from './bridge';
import { I } from './icons';
import { ChartDto, FileDto, PropsDto, State } from './types';
import { curveColor, esc, fmtInt, fmtRead, fmtSec, fmtSigned, fmtValue, h, rangeBarSvg } from './util';

const kv = (k: string, v: string, sans = false) => v === '' || v == null ? '' : `<div class="kv"><span class="k">${esc(k)}</span><span class="v${sans ? ' sans' : ''}" title="${esc(v)}">${esc(v)}</span></div>`;
const sect = (icon: string, title: string, body: string, meta = '', grow = false) => `<div class="sect${grow ? ' grow' : ''}"><div class="sect-h">${icon}${esc(title)}${meta ? `<span class="m">${esc(meta)}</span>` : ''}</div>${body}</div>`;

export class Properties {
  el: HTMLElement; private body: HTMLElement; private meta: HTMLElement; private S: State | null = null;
  constructor() {
    this.el = h(`<aside class="card aside props"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">PROPERTIES</span><span class="m" data-meta></span></div><div class="scroll body" style="display:flex;flex-direction:column"></div></aside>`);
    this.body = this.el.querySelector('.body')!; this.meta = this.el.querySelector('[data-meta]')!;
    this.body.addEventListener('click', e => this.onClick(e));
    this.body.addEventListener('change', e => this.onChange(e));
  }
  setState(S: State) {
    this.S = S;
    const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0];
    if (S.mode === 'NAVIGATOR') { this.renderFile(S.fileProps ?? S.files.find(f => f.name === S.selectedFile) ?? S.files[0] ?? null, S); return; }
    if (S.props) { this.renderChannel(S.props, chart, S); return; }
    if (chart) { this.renderChart(chart, S); return; }
    this.meta.textContent = ''; this.body.innerHTML = `<div class="prop-body"><div class="muted11">Select a channel, chart or curve.</div></div>`;
  }

  private renderChannel(p: PropsDto, chart: ChartDto | undefined, S: State) {
    this.meta.textContent = p.name;
    const when = p.valueWhen ? ` <span>· ${esc(p.valueWhen)}</span>` : '';
    const delta = p.valueB != null && p.value != null ? `<span class="delta">Δ ${fmtSigned(p.valueB - p.value)} to B</span>` : '';
    const min = p.min ?? 0, max = p.max ?? 0;
    const big = p.isText ? '' : `<div class="prop-big"><div class="lbl">${esc(p.valueLabel)}${when}</div><div class="val"><span class="num">${p.value == null ? '—' : fmtRead(p.value)}</span><span class="unit">${esc(p.unit)}</span>${delta}</div>
      <div class="bar">${p.ready ? rangeBarSvg(min, max, p.value, p.band) : ''}</div><div class="ends"><span>min ${p.ready ? fmtValue(min, 4) : '…'}</span><span class="c">${p.band ? 'A–B range' : 'range'}</span><span>max ${p.ready ? fmtValue(max, 4) : '…'}</span></div></div>`;
    const data = kv('samples', fmtInt(p.length)) + kv('type', p.dataType || '—') + kv('unit', p.unit || '—') + kv('x channel', p.xChannel ?? '') + kv('rate', p.rate ? fmtValue(p.rate, 4) + ' Hz' : '') + kv('render', p.render) + (p.description ? kv('description', p.description, true) : '');
    const time = (p.wfStart ? kv('wf_start_time', p.wfStart) : '') + (p.absolute != null ? kv('absolute', p.absolute.toExponential(3).replace('e+', 'E') + ' s') : '') + (chart ? kv('display', p.display) : '');
    let curvesBody = ''; let curvesMeta = '';
    if (chart) {
      curvesMeta = chart.title || `Chart ${S.charts.indexOf(chart) + 1}`;
      curvesBody = chart.curves.map(c => `<div class="cv-row${c.channelId === p.id ? ' sel' : ''}" data-cv="${c.idx}"><span class="swatch" style="background:${curveColor(c.color, S.plot)}"></span><span class="n">${esc(c.name)}${c.lane ? ' · lane' : c.step ? ' · step' : ''}</span><span class="u">${esc(c.unit || (c.dataType === 'Boolean' ? 'bool' : ''))}</span></div>`).join('');
    }
    // actions for the curve of this channel in the active chart
    let act = '';
    if (p.curveIdx != null && chart) {
      const c = chart.curves[p.curveIdx];
      act = `<div class="pills" style="margin-top:6px"><button class="pill${c.step ? ' on' : ''}" data-a="step">STEP</button><button class="pill${!c.step ? ' on' : ''}" data-a="line">LINE</button><button class="pill${c.right ? ' on' : ''}" data-a="right">RIGHT AXIS</button>${p.timeCandidate ? `<button class="pill" data-a="setx">SET AS X</button>` : ''}<button class="pill" data-a="rmcurve">REMOVE</button></div>`;
    } else if (chart && !p.isText) act = `<div class="pills" style="margin-top:6px"><button class="pill" data-a="addcurve">ADD TO CHART</button>${p.timeCandidate ? `<button class="pill" data-a="setx">SET AS X</button>` : ''}</div>`;
    const props = p.props.length ? sect(I.tag(), 'PROPERTIES', p.props.map(x => kv(x.k, x.v, !/^-?[\d.eE+-]+$/.test(x.v))).join(''), String(p.props.length)) : '';
    this.body.innerHTML = big + sect(I.tag(), 'DATA', data) + (time ? sect(I.clockM(), 'TIME', time) : '') + (chart ? sect(I.lineM(), 'CURVES', curvesBody + act + this.chartControls(chart), curvesMeta, true) : sect(I.lineM(), 'CURVE', act)) + props;
  }

  private chartControls(c: ChartDto): string {
    const tm = c.xTimeMode;
    const x = c.xChannel ? `<div class="kv" style="margin-top:6px"><span class="k">x axis</span><span class="v">${esc(c.xChannel.name)} [${esc(c.xChannel.unit)}]</span></div><div class="pills"><button class="pill${tm === 'relative' ? ' on' : ''}" data-a="tm:relative">REL</button><button class="pill${tm === 'absolute' ? ' on' : ''}" data-a="tm:absolute">ABS</button><button class="pill${tm === 'none' ? ' on' : ''}" data-a="tm:none">NUMERIC</button><button class="pill" data-a="clearx">CLEAR X</button></div>`
      : c.suggestedX ? `<div class="kv" style="margin-top:6px"><span class="k">suggested x</span><span class="v">${esc(c.suggestedX.name)} [${esc(c.suggestedX.unit)}]</span></div><div class="pills"><button class="pill primary" data-a="usex">USE AS X</button></div>` : '';
    const lanes = `<div class="pills" style="margin-top:6px"><button class="pill${c.lanes ? ' on' : ''}" data-a="lanes">DIGITAL LANES</button><button class="pill${c.grid ? ' on' : ''}" data-a="grid">GRID</button><button class="pill" data-a="zoomfull">ZOOM FULL</button></div>`;
    const align = c.alignMode !== 'none' ? `<div class="kv" style="margin-top:6px"><span class="k">alignment</span><span class="v">${esc(c.alignMode)}${c.alignMode === 'manual' ? ` (${c.alignOffsetMs} ms/file)` : ''}</span></div>` + c.curves.map(cv => kv(cv.file.replace(/\.[^.]+$/, ''), (cv.xOffset >= 0 ? '+' : '') + cv.xOffset.toFixed(3) + ' s')).join('') : '';
    return x + lanes + align;
  }

  private renderChart(c: ChartDto, S: State) {
    this.meta.textContent = c.title || `Chart ${S.charts.indexOf(c) + 1}`;
    const title = `<div class="sect"><div class="field"><label>CHART TITLE</label><div class="in"><input type="text" data-in="title" value="${esc(c.title)}" placeholder="Chart ${S.charts.indexOf(c) + 1}"></div></div>${kv('x range', `${fmtValue(c.xmin, 6)} … ${fmtValue(c.xmax, 6)}`)}${kv('x unit', c.xUnit)}${kv('curves', String(c.curves.length))}</div>`;
    const curves = c.curves.map(cv => `<div class="cv-row" data-cv="${cv.idx}"><span class="swatch" style="background:${curveColor(cv.color, S.plot)}"></span><span class="n">${esc(cv.name)}${cv.lane ? ' · lane' : cv.step ? ' · step' : ''}</span><span class="u">${esc(cv.unit)}</span></div>`).join('');
    this.body.innerHTML = title + sect(I.lineM(), 'CURVES', curves + this.chartControls(c), '', true);
  }

  private renderFile(f: FileDto | null, S: State) {
    this.meta.textContent = 'file';
    if (!f) { this.body.innerHTML = `<div class="prop-body"><div class="muted11">No file open.</div></div>`; return; }
    const rows = kv('format', f.format + (f.format === 'TDMS' ? ' 2.0' : '')) + kv('segments', f.segments ?? '—') + kv('groups', String(f.groups.length)) + kv('channels', String(f.channelCount)) + kv('start', f.start != null ? f.startText.split(' ').slice(-1)[0] : '—') + kv('duration', f.duration != null ? fmtSec(f.duration) : '—') + kv('rate', f.rate != null ? fmtValue(f.rate, 4) + ' Hz' : '—');
    const al = S.align.mode;
    this.body.innerHTML = `<div class="prop-body"><div class="prop-title">${esc(f.name.replace(/\.[^.]+$/, ''))}</div>${rows}<div class="hr"></div><div class="lblh">ALIGNMENT</div><div class="pills"><button class="pill h24${al === 'start' ? ' on' : ''}" data-a="al:start">START TIME</button><button class="pill h24${al === 'zero' ? ' on' : ''}" data-a="al:zero">ZERO</button><button class="pill h24${al === 'manual' ? ' on' : ''}" data-a="al:manual">MANUAL</button></div>${al === 'manual' ? `<div class="field"><label>OFFSET PER FILE</label><div class="in"><input type="text" data-in="offset" value="${S.align.offsetMs}"><span class="u">ms</span></div></div>` : ''}${f.props.length ? `<div class="hr"></div><div class="lblh">FILE PROPERTIES</div>${f.props.map(x => kv(x.k, x.v, true)).join('')}` : ''}</div>`;
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
    const id = chart.id;
    if (a === 'step' || a === 'line') { if (p?.curveIdx != null) bridge.call('setCurve', { chart: id, idx: p.curveIdx, step: a === 'step' }); }
    else if (a === 'right') { if (p?.curveIdx != null) bridge.call('setCurve', { chart: id, idx: p.curveIdx, right: !chart.curves[p.curveIdx].right }); }
    else if (a === 'rmcurve') { if (p?.curveIdx != null) bridge.call('removeCurve', { chart: id, idx: p.curveIdx }); }
    else if (a === 'addcurve') { if (p) bridge.call('addCurves', { chart: id, channels: [p.id] }); }
    else if (a === 'setx') { if (p) bridge.call('setX', { chart: id, channel: p.id }); }
    else if (a === 'usex') { if (chart.suggestedX) bridge.call('setX', { chart: id, channel: chart.suggestedX.id }); }
    else if (a === 'clearx') bridge.call('clearX', { chart: id });
    else if (a.startsWith('tm:')) bridge.call('setChart', { chart: id, timeMode: a.slice(3) });
    else if (a === 'lanes') bridge.call('setChart', { chart: id, lanes: !chart.lanes });
    else if (a === 'grid') bridge.call('setChart', { chart: id, grid: !chart.grid });
    else if (a === 'zoomfull') bridge.call('zoomFull', { chart: id });
  }
  private onChange(e: Event) {
    const S = this.S; if (!S) return;
    const t = e.target as HTMLInputElement; const k = t.dataset.in;
    if (k === 'title') { const chart = S.charts.find(c => c.id === S.activeChart) ?? S.charts[0]; if (chart) bridge.call('setChart', { chart: chart.id, title: t.value }); }
    if (k === 'offset') bridge.call('setAlign', { mode: S.align.mode, offsetMs: parseFloat(t.value) || 0 });
  }
}
