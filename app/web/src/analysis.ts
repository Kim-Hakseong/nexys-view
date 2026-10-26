// ANALYSIS: FUNCTIONS list (240px) · result card (header, preview plot, 4 stats, result channels) · PARAMETERS (300px).
import { bridge } from './bridge';
import { I } from './icons';
import { PlotView } from './plot';
import { AnalysisDto, State } from './types';
import { curveColor, esc, fmtInt, fmtValue, h } from './util';

const DESC: Record<string, string> = { Statistics: 'count · min · max · mean · RMS · std · sum', FFT: 'amplitude spectrum', 'IIR low-pass': 'Biquad · low-pass', 'IIR high-pass': 'Biquad · high-pass', 'FIR low-pass': 'windowed-sinc', Derivative: 'd/dt', Integral: '∫ dt', Resample: 'linear', 'Formula channel': 'calculator syntax' };

export class Analysis {
  el: HTMLElement; fns: HTMLElement; center: HTMLElement; params: HTMLElement; private S: State | null = null; private preview: PlotView | null = null;
  private resCard: HTMLElement; private resHead: HTMLElement; private plotHost: HTMLElement; private statsEl: HTMLElement; private rcCard: HTMLElement;
  constructor() {
    this.el = h(`<div style="display:contents"></div>`);
    this.fns = h(`<section class="card fns" style="width:240px;flex:0 0 240px"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">FUNCTIONS</span><span class="m">9</span></div><div class="fn-list"></div></section>`);
    this.center = h(`<section class="center"></section>`);
    this.resCard = h(`<div class="card" style="flex:0 0 auto"></div>`);
    this.resHead = h(`<div class="res-h"></div>`);
    this.plotHost = h(`<div class="res-plot"></div>`);
    this.resCard.append(this.resHead, this.plotHost);
    this.statsEl = h(`<div class="stat4" style="display:none"></div>`);
    this.rcCard = h(`<section class="card" style="flex:1"><div class="card-h"><span class="ic">${I.list()}</span><span class="t">RESULT CHANNELS</span><span class="m">0</span></div><div class="scroll"><div class="rc-list"></div></div></section>`);
    this.center.append(this.resCard, this.statsEl, this.rcCard);
    this.params = h(`<aside class="card aside"><div class="card-h"><span class="ic">${I.sliders()}</span><span class="t">PARAMETERS</span><span class="m"></span></div><div class="scroll"><div class="par-body"></div></div></aside>`);
    this.el.append(this.fns, this.center, this.params);
    this.fns.addEventListener('click', e => { const r = (e.target as HTMLElement).closest('[data-fn]') as HTMLElement | null; if (r) bridge.call('analysis.function', { name: r.dataset.fn }); });
    this.params.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.p) bridge.call('analysis.param', { key: t.dataset.p, value: t.value }); });
    this.params.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT') { const t = e.target as HTMLInputElement; if (t.dataset.p) bridge.call('analysis.param', { key: t.dataset.p, value: t.value }).then(() => this.run()); } });
    this.params.addEventListener('click', e => {
      const a = ((e.target as HTMLElement).closest('[data-a]') as HTMLElement | null)?.dataset.a;
      if (a === 'run') this.run(); else if (a === 'cancel') bridge.call('analysis.cancel');
      else if (a === 'useSel') { const id = this.S?.selected; if (id) bridge.call('analysis.inputs', { channels: [id] }); }
    });
    this.resHead.addEventListener('click', e => { const a = ((e.target as HTMLElement).closest('[data-a]') as HTMLElement | null)?.dataset.a; if (a === 'show') bridge.call('analysis.show'); });
    this.params.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes('application/x-nexys-channels')) { e.preventDefault(); this.params.classList.add('over'); } });
    this.params.addEventListener('dragleave', () => { this.params.classList.remove('over'); });
    this.params.addEventListener('drop', e => { this.params.classList.remove('over'); const d = e.dataTransfer?.getData('application/x-nexys-channels'); if (!d) return; e.preventDefault(); const chans = JSON.parse(d) as { id: string }[]; bridge.call('analysis.inputs', { channels: chans.map(c => c.id) }); });
  }
  run() { bridge.call('analysis.run'); }

  setState(S: State) {
    this.S = S; const A = S.analysis;
    const short: Record<string, string> = { 'IIR filter': `${A.params['iir_type'] ?? 'Biquad'} ${(A.params['iir_resp'] ?? 'low-pass') === 'high-pass' ? 'HP' : 'LP'} · active`, FFT: `${A.params['fft_window'] ?? 'Hann'} · ${A.params['fft_n'] ?? '4096'}` };
    this.fns.querySelector('.fn-list')!.innerHTML = A.functions.map(f => `<div class="fn-row${f.name === A.function ? ' sel' : ''}" data-fn="${esc(f.name)}"><span class="n">${esc(f.name === 'Formula channel' ? 'Formula' : f.name)}</span><span class="m">${f.name === A.function ? esc(short[f.name] ?? 'active') : f.name === 'FFT' ? esc(short.FFT) : ''}</span></div>`).join('');
    this.fns.querySelector('.card-h .m')!.textContent = String(A.functions.length);
    // result header
    const input = A.inputs[0]?.name ?? '';
    const hasOut = A.results.length > 0;
    this.resHead.innerHTML = `<span class="t">${esc(A.function)}${input ? ' · ' + esc(input.split('/').pop()!) : ''}</span><span class="s">${esc(A.subtitle || DESC[A.function] || '')}${A.lastRunMs ? ` · ${A.lastRunMs.toFixed(0)} ms` : ''}</span><span class="lg"><span class="swatch" style="background:${curveColor(1, S.plot)};opacity:0.5"></span>input <span class="swatch" style="background:${curveColor(0, S.plot)};margin-left:8px"></span>result</span>${A.canShow ? `<button class="pill h26" data-a="show">SHOW IN VIEW</button>` : ''}`;
    // preview plot
    if (S.preview && S.preview.curves.length > 0) {
      if (!this.preview) { this.preview = new PlotView(S.preview, S.plot); this.plotHost.innerHTML = ''; this.plotHost.append(this.preview.el); this.preview.el.classList.remove('card'); this.preview.el.style.border = '0'; (this.preview.el.querySelector('.chart-h') as HTMLElement).style.display = 'none'; (this.preview.el.querySelector('.plot-wrap') as HTMLElement).style.padding = '0'; }
      this.preview.update(S.preview, S.plot);
      this.plotHost.style.display = '';
    } else { if (this.preview) { this.preview.dispose(); this.preview = null; } this.plotHost.innerHTML = `<div class="plot-box"><div class="hint">${A.notes.length ? esc(A.notes[0]) : 'Run a function — the result is previewed here'}</div></div>`; this.plotHost.style.display = ''; }
    // stats
    if (A.stats) {
      const s = A.stats; const u = s.unit || '';
      this.statsEl.style.display = '';
      this.statsEl.innerHTML = [['MIN', s.min], ['MAX', s.max], ['MEAN', s.mean], ['RMS', s.rms]].map(([k, v]) => `<div><div class="k">${k}</div><div class="v"><b>${fmtValue(v as number, Math.abs(v as number) < 1e-4 && v !== 0 ? 3 : 5)}</b><span>${esc(u)}</span></div></div>`).join('');
    } else this.statsEl.style.display = 'none';
    // result channels + notes
    const rc = A.results.map(r => `<div class="rc-row"><span class="swatch" style="background:${curveColor(0, S.plot)}"></span><span class="n">${esc(r.name)} · ${esc(r.meta)}</span><span class="m">${fmtInt(r.length)}${r.unit ? ' · ' + esc(r.unit) : ''}</span></div>`).join('');
    const notes = A.notes.map(n => `<div class="note">${esc(n)}</div>`).join('');
    const extra = A.stats ? `<div class="rc-row"><span class="n">count ${fmtInt(A.stats.count)} · std ${fmtValue(A.stats.std, 7)} · sum ${fmtValue(A.stats.sum, 9)}</span></div>` : '';
    this.rcCard.querySelector('.rc-list')!.innerHTML = rc + extra + notes;
    this.rcCard.querySelector('.card-h .m')!.textContent = String(A.results.length);
    // parameters
    this.params.querySelector('.card-h .m')!.textContent = A.function.split(' ')[0];
    const inputVal = A.inputs.map(i => i.name).join(', ');
    const fsVal = (A as any).fs != null ? fmtValue((A as any).fs, 5) : '';
    const fields = A.schema.map(f => {
      const v = f.key === '_fs' ? fsVal : (A.params[f.key] ?? f.def);
      const ro = f.key === '_fs' || (f.options && f.options.length === 1);
      const inp = f.options && !ro ? `<select data-p="${f.key}">${f.options.map(o => `<option${o === v ? ' selected' : ''}>${esc(o)}</option>`).join('')}</select>` : `<input type="text" ${ro ? 'readonly' : `data-p="${f.key}"`} value="${esc(f.options && ro ? f.options[0] : v)}" aria-label="${f.label}">`;
      return `<div class="field"><label>${f.label}</label><div class="in">${inp}<span class="u">${esc(f.unit)}</span></div></div>`;
    });
    const sel = this.S.selected ? this.S.props?.name : '';
    const grid = fields.length === 1 && A.function === 'Formula channel' ? fields[0] : `<div class="grid2">${fields.join('')}</div>`;
    const inputs = A.inputs.length ? A.inputs : [];
    const first = inputs[0]; const rate = this.S.props && this.S.props.id === first?.id && this.S.props.rate ? this.S.props.rate : null;
    this.params.querySelector('.par-body')!.innerHTML = `<div class="field"><label>INPUT CHANNEL${A.inputs.length > 1 ? `S (${A.inputs.length})` : ''}</label><div class="in"><input type="text" value="${esc(inputVal)}" readonly aria-label="INPUT CHANNEL" placeholder="drop a channel here or select one and press USE"><span class="u">${first ? fmtInt(first.length) : ''}</span></div>${sel && !first ? `<button class="pill" data-a="useSel" style="align-self:flex-start;margin-top:4px">USE ${esc(sel!.toUpperCase())}</button>` : ''}</div>${grid}
      <div class="valid${A.validity.ok ? '' : ' bad'}"><span class="d"></span>${esc(A.validity.text)}</div>
      <button class="pill h36 primary" data-a="run"${A.running ? ' disabled' : ''}>${I.play()}${A.running ? 'RUNNING…' : 'RUN'}</button><button class="pill h30" data-a="cancel" style="justify-content:center">CANCEL</button>`;
  }
}
