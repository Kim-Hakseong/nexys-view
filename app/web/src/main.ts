// App shell: header (brand · mode pills · tools · LED) / main (mode-specific) / footer; state fan-out; keyboard; file drop; test hooks.
import { Analysis } from './analysis';
import { bridge } from './bridge';
import { Help } from './help';
import { I } from './icons';
import { NavMode } from './navmode';
import { Navigator } from './navigator';
import { PlotView, DRAG_CH } from './plot';
import { Properties } from './properties';
import { Report } from './report';
import { State } from './types';
import { esc, fmtInt, h } from './util';
import { ViewArea } from './view';

let S: State | null = null;
const app = document.getElementById('app')!;
const hdr = h(`<header class="hdr"></header>`);
const main = h(`<main class="main"></main>`);
const ftr = h(`<footer class="ftr"><span class="d"></span><span class="txt"></span><span class="r"></span></footer>`);
app.append(hdr, main, ftr);
const help = new Help();
const nav = new Navigator(); const view = new ViewArea(); const props = new Properties(); const navMode = new NavMode(); const analysis = new Analysis(); const report = new Report();
let frameInfo = '';

function renderHeader() {
  if (!S) return;
  const mode = (m: string) => `<a data-mode="${m}" class="${S!.mode === m ? 'on' : ''}">${m}</a>`;
  const tool = (a: string, icon: string, label: string, title: string, disabled = false) => `<button type="button" class="tool" data-a="${a}" aria-label="${label}" title="${title}"${disabled ? ' disabled' : ''}>${icon}<span>${label}</span></button>`;
  const app = (S as any).app ?? { versionText: '', beta: false };
  hdr.innerHTML = `<div class="hdr-brand"><div class="hdr-logo" data-a="about" title="About Nexys VIEW (F1)"><img src="brand/Logo.png" alt="Nexys VIEW"></div><div class="hdr-name"><small>${esc((app.versionText || '').toUpperCase())}${S.developer ? ' · DEVELOPER' : ''}</small></div></div>
    <nav aria-label="Mode" class="hdr-nav"><div class="modes">${mode('NAVIGATOR')}${mode('VIEW')}${mode('ANALYSIS')}${mode('REPORT')}</div></nav>
    <div class="hdr-tools">${tool('open', I.folder(), 'OPEN', 'Open file (Ctrl+O)')}${tool('save', I.save(), 'SAVE', 'Save all channels as TDMS (Ctrl+S)')}<div class="vsep"></div>${tool('undo', I.undo(), 'UNDO', S.undo.can ? `Undo ${S.undo.name} (Ctrl+Z)` : 'Undo (Ctrl+Z)', !S.undo.can)}${tool('redo', I.redo(), 'REDO', 'Redo (Ctrl+Y)', !S.undo.canRedo)}<div class="vsep"></div>${tool('theme', S.theme === 'dark' ? I.sun() : I.theme(), 'THEME', 'Toggle light / dark chrome (Ctrl+T)')}${tool('plot', I.plot(), 'PLOT · ' + S.plot.toUpperCase(), 'Toggle dark / light plot (Ctrl+P)')}<div class="vsep"></div>${tool('dev', I.info(), 'DEV', 'Developer mode (Ctrl+D)')}</div>
    <div class="vsep"></div><button type="button" class="tool icon" data-a="help" aria-label="HELP" title="사용 가이드 · 기능 투어 (F1)">${I.help()}</button>
    <div class="led-pill"><span class="led ${S.status.led}"></span><span class="led-text">${S.status.led.toUpperCase()}</span></div>`;
}
hdr.addEventListener('click', e => {
  const t = e.target as HTMLElement;
  const m = (t.closest('[data-mode]') as HTMLElement | null)?.dataset.mode; if (m) { setMode(m); return; }
  const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
  if (a === 'about') { showAbout(); return; }
  if (a === 'help') { help.show(); return; }
  if (a === 'open') bridge.call('openDialog'); else if (a === 'save') bridge.call('saveDialog'); else if (a === 'undo') bridge.call('undo'); else if (a === 'redo') bridge.call('redo');
  else if (a === 'theme') bridge.call('setTheme', { theme: S?.theme === 'dark' ? 'light' : 'dark' }); else if (a === 'plot') bridge.call('setPlot', { plot: S?.plot === 'dark' ? 'light' : 'dark' }); else if (a === 'dev') bridge.call('setDeveloper', { on: !S?.developer });
});
function setMode(m: string) { bridge.call('setMode', { mode: m }); }

function renderMain() {
  if (!S) return;
  const want: HTMLElement[] = S.mode === 'NAVIGATOR' ? [nav.el, navMode.el, props.el] : S.mode === 'ANALYSIS' ? [nav.el, analysis.fns, analysis.center, analysis.params] : S.mode === 'REPORT' ? [report.left, report.center, report.right] : [nav.el, view.el, props.el];
  want.forEach((el, i) => { if (main.children[i] !== el) main.insertBefore(el, main.children[i] ?? null); });
  while (main.children.length > want.length) main.lastElementChild!.remove();
}
function renderFooter() {
  if (!S) return;
  (ftr.querySelector('.d') as HTMLElement).className = 'd ' + S.status.led;
  ftr.querySelector('.txt')!.textContent = S.status.text || (S.files.length ? `${S.files.length} files open · ${S.files.reduce((a, f) => a + f.channelCount, 0)} channels` : 'Open a file or drop it onto the window');
  ftr.querySelector('.r')!.textContent = frameInfo;
}
document.addEventListener('nx-frame', (e: any) => {
  const d = e.detail; if (!d?.stats) return;
  frameInfo = `frame ${d.ms.toFixed(1)} ms · ${fmtInt(d.stats.samples)} samples · pyramid L${d.stats.level}`;
  ftr.querySelector('.r')!.textContent = frameInfo;
});

function applyState(next: State) {
  const prevMode = S?.mode; S = next;
  document.documentElement.dataset.theme = S.theme; document.documentElement.dataset.plot = S.plot;
  renderHeader(); renderMain(); renderFooter();
  help.setState(S);
  nav.setState(S);
  if (S.mode === 'VIEW') { view.setState(S); props.setState(S); }
  else if (S.mode === 'NAVIGATOR') { navMode.setState(S); props.setState(S); }
  else if (S.mode === 'ANALYSIS') analysis.setState(S);
  else if (S.mode === 'REPORT') report.setState(S);
  if (prevMode !== S.mode) { if (S.mode === 'VIEW') { view.setState(S); for (const pv of view.plots.values()) pv.request(); } if (S.mode === 'REPORT') report.refreshPage(); }
}
bridge.on('state', m => { const st = m.state as State; if (!st.files && S) st.files = S.files; else if (!st.files) st.files = []; applyState(st); });
bridge.on('undo', m => { if (!S) return; S.undo = { can: m.can, name: m.name, canRedo: m.canRedo }; renderHeader(); });
bridge.on('status', m => { if (!S) return; S.status = { text: m.text, led: m.led }; renderFooter(); const led = hdr.querySelector('.led'); if (led) { led.className = 'led ' + m.led; (hdr.querySelector('.led-text') as HTMLElement).textContent = m.led.toUpperCase(); } });
bridge.on('invalidate', m => { const ids: number[] = m.charts; const all = ids.includes(-1); for (const [id, pv] of view.plots) if (all || ids.includes(id)) pv.request(); if (S?.mode === 'ANALYSIS' && (all || ids.includes(0))) (analysis as any).preview?.request(); });
bridge.on('buffer', () => { /* buffers arrive before the first frame; nothing to do */ });

// ---------- About (F1 / logo): version, beta notice, feedback link
function showAbout() {
  if (!S || document.querySelector('.about')) return;
  const app = (S as any).app ?? {};
  const dlg = h(`<div class="modal-bg"><div class="modal about"><div style="display:flex;align-items:center;gap:10px"><img src="brand/Logo.png" alt="" style="height:18px"><span class="ver">${esc(app.versionText || '')}</span></div>
    <div class="ttl">Nexys VIEW — measurement data viewer</div>
    <p>${app.beta ? '베타 버전입니다. 기능과 화면이 바뀔 수 있고, 문제를 발견하면 아래 피드백으로 알려 주세요.' : ''}</p>
    <p>피드백: <a href="#" data-url="${esc(app.issues || '')}" style="color:var(--accent2);text-decoration:underline">GitHub Issues</a> · 저장소: <a href="#" data-url="${esc(app.repo || '')}" style="color:var(--accent2);text-decoration:underline">${esc(app.repo || '')}</a></p>
    <div class="lic">Fonts: Inter, Instrument Serif, JetBrains Mono, Noto Sans KR (SIL Open Font License 1.1) · Pretendard (OFL) · Icons: Lucide (ISC) · WebView2 Runtime (Microsoft) · SkiaSharp (MIT)</div>
    <div class="btns"><button class="pill h30 primary" data-b="ok">CLOSE</button></div></div></div>`);
  dlg.addEventListener('click', e => {
    const t = e.target as HTMLElement; const u = (t.closest('[data-url]') as HTMLElement | null)?.dataset.url;
    if (u) { e.preventDefault(); bridge.call('openUrl', { url: u }); return; }
    if ((t.closest('[data-b]') as HTMLElement | null)?.dataset.b === 'ok' || t === dlg) dlg.remove();
  });
  document.body.append(dlg);
}
window.addEventListener('keydown', e => { if (e.key === 'F1') { e.preventDefault(); help.show(); } if (e.key === 'Escape') document.querySelector('.modal-bg')?.remove(); });

// ---------- keyboard
document.addEventListener('keydown', e => {
  const inInput = (e.target as HTMLElement).tagName === 'INPUT' || (e.target as HTMLElement).tagName === 'TEXTAREA' || (e.target as HTMLElement).tagName === 'SELECT';
  if (!(e.ctrlKey || e.metaKey)) { if (e.key === 'F5') e.preventDefault(); return; }
  if (inInput && !['o', 'O', 's', 'S', '1', '2', '3', '4'].includes(e.key)) return;
  const k = e.key.toLowerCase();
  if (k === 'z') { e.preventDefault(); bridge.call('undo'); } else if (k === 'y') { e.preventDefault(); bridge.call('redo'); }
  else if (k === 'o') { e.preventDefault(); bridge.call(e.shiftKey ? 'openPluginDialog' : 'openDialog'); } else if (k === 's') { e.preventDefault(); bridge.call('saveDialog'); }
  else if (k === 't') { e.preventDefault(); bridge.call('setTheme', { theme: S?.theme === 'dark' ? 'light' : 'dark' }); } else if (k === 'p') { e.preventDefault(); bridge.call('setPlot', { plot: S?.plot === 'dark' ? 'light' : 'dark' }); }
  else if (k === 'd') { e.preventDefault(); bridge.call('setDeveloper', { on: !S?.developer }); }
  else if (k === '1') setMode('NAVIGATOR'); else if (k === '2') setMode('VIEW'); else if (k === '3') setMode('ANALYSIS'); else if (k === '4') setMode('REPORT');
});

// ---------- files dropped from the shell
document.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes('Files')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
document.addEventListener('drop', e => { if (e.dataTransfer?.types.includes('Files')) { e.preventDefault(); bridge.postFiles(e.dataTransfer.files); } else if (!e.defaultPrevented) e.preventDefault(); });

// ---------- compare dialog (NAVIGATOR mode COMPARE n FILES / navigator Ctrl-select)
document.addEventListener('nx-compare', () => {
  if (!S) return;
  const names = S.selectedFiles.length >= 2 ? S.selectedFiles : S.files.map(f => f.name);
  const common = S.common;
  const timeNames = new Set(S.files.flatMap(f => f.groups.flatMap(g => g.channels.filter(c => c.time).map(c => c.name))));
  const dlg = h(`<div class="modal-bg"><div class="modal"><div class="lblh">COMMON CHANNELS OF ${names.length} FILES</div><div class="muted11">${esc(names.join('  ·  '))}</div><div class="list">${common.map((n, i) => `<label class="ck"><input type="checkbox" value="${esc(n)}"${!timeNames.has(n) && i < 8 && common.filter((x, j) => j < i && !timeNames.has(x)).length < 4 ? ' checked' : ''}> ${esc(n)}</label>`).join('') || '<div class="faint11">No channel name is present in every file.</div>'}</div>
    <div class="lblh">ALIGNMENT</div><div class="pills"><button class="pill h24${S.align.mode === 'start' ? ' on' : ''}" data-m="start">START TIME</button><button class="pill h24${S.align.mode === 'zero' ? ' on' : ''}" data-m="zero">ZERO</button><button class="pill h24${S.align.mode === 'manual' ? ' on' : ''}" data-m="manual">MANUAL</button><div class="in" style="display:flex;align-items:center;height:24px;border:1px solid var(--border-strong);border-radius:6px;padding:0 8px;gap:4px"><input type="text" value="${S.align.offsetMs}" style="width:56px;border:0;background:transparent;font-family:var(--mono);font-size:11px;color:var(--text);outline:none"><span class="faint11">ms</span></div></div>
    <div class="btns"><button class="pill h30" data-b="cancel">CANCEL</button><button class="pill h30 primary" data-b="ok">COMPARE</button></div></div></div>`);
  let mode = S.align.mode;
  dlg.addEventListener('click', e => {
    const t = e.target as HTMLElement; const m = (t.closest('[data-m]') as HTMLElement | null)?.dataset.m;
    if (m) { mode = m; dlg.querySelectorAll('[data-m]').forEach(b => b.classList.toggle('on', (b as HTMLElement).dataset.m === m)); return; }
    const b = (t.closest('[data-b]') as HTMLElement | null)?.dataset.b;
    if (b === 'cancel' || t === dlg) dlg.remove();
    if (b === 'ok') {
      const chans = Array.from(dlg.querySelectorAll('input[type=checkbox]:checked')).map(x => (x as HTMLInputElement).value);
      const off = parseFloat((dlg.querySelector('input[type=text]') as HTMLInputElement).value) || 0;
      dlg.remove(); if (chans.length) bridge.call('compare', { files: names, channels: chans, mode, offsetMs: off });
    }
  });
  document.body.append(dlg);
});

// ---------- test hooks for --selftest / --stress (real DOM events)
function dragData(chans: { id: string; name: string }[], xy = false) { const dt = new DataTransfer(); dt.setData(DRAG_CH, JSON.stringify(chans)); if (xy) dt.setData('application/x-nexys-xy', '1'); dt.setData('text/plain', chans.map(c => c.name).join(',')); return dt; }
function fire(el: Element, type: string, dt: DataTransfer, x?: number, y?: number) {
  const r = el.getBoundingClientRect(); const cx = x == null ? r.left + r.width / 2 : r.left + x, cy = y == null ? r.top + r.height / 2 : r.top + y;
  el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt, clientX: cx, clientY: cy }));
}
const raf = () => new Promise<void>(r => requestAnimationFrame(() => r()));
async function settle(ms = 30) {
  for (let i = 0; i < 200; i++) { await raf(); if (Array.from(view.plots.values()).every(p => !p.busy) && !((analysis as any).preview?.busy)) break; await new Promise(r => setTimeout(r, 10)); }
  await new Promise(r => setTimeout(r, ms)); await raf();
}
const test = {
  async run(tid: number, fn: () => Promise<any>) { let result: any; try { result = await fn(); } catch (e: any) { result = { error: String(e?.message ?? e) }; bridge.log('test error ' + (e?.stack ?? e)); } bridge.post({ cmd: 'test', tid, result }); },
  settle, state: () => S,
  plot: (chart: number): PlotView | undefined => chart === 0 ? (analysis as any).preview : view.plots.get(chart),
  canvasOf(chart: number) { return this.plot(chart)?.el.querySelector('canvas') as HTMLCanvasElement | undefined; },
  /** Drop channels: target 'new' (drop zone), or a chart id with zone plot|left|right|x; returns after the state settled. */
  async drop(chans: { id: string; name: string }[], target: { chart?: number; zone?: string; xy?: boolean; preview?: boolean } = {}) {
    const dt = dragData(chans, !!target.xy);
    if (target.chart == null) { const z = view.el.querySelector('.dropzone')!; fire(z, 'dragover', dt); if (target.preview) { await raf(); return true; } fire(z, 'drop', dt); }
    else {
      const pv = view.plots.get(target.chart); if (!pv) throw new Error('no chart ' + target.chart);
      const box = pv.el.querySelector('.plot-box')!; const r = (pv as any).plotRect; const bw = box.clientWidth, bh = box.clientHeight;
      const zone = target.zone ?? 'plot';
      const x = zone === 'left' ? r.x / 2 : zone === 'right' ? r.x + r.w + (bw - r.x - r.w) / 2 : r.x + r.w / 2;
      const y = zone === 'x' ? r.y + r.h + (bh - r.y - r.h) / 2 : r.y + r.h / 2;
      fire(box, 'dragover', dt, x, y); if (target.preview) { await raf(); return (pv as any).dropfx.firstElementChild.textContent; } fire(box, 'drop', dt, x, y); fire(box, 'dragleave', dt, x, y);
    }
    await new Promise(r => setTimeout(r, 50)); await settle(); return true;
  },
  /** Wheel zoom steps on a chart canvas; returns per-frame ms (request → painted). */
  async wheel(chart: number, n: number, deltaY = -120, atFrac = 0.5) {
    const pv = view.plots.get(chart)!; const cv = this.canvasOf(chart)!; const out: number[] = [];
    for (let i = 0; i < n; i++) {
      const r = (pv as any).plotRect; const before = pv.frames;
      cv.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY, clientX: cv.getBoundingClientRect().left + r.x + r.w * atFrac, clientY: cv.getBoundingClientRect().top + r.y + r.h / 2 }));
      await waitFrames(pv, before); out.push(pv.lastTotalMs);
    }
    return out;
  },
  async pan(chart: number, n: number, frac = 0.05) { const pv = view.plots.get(chart)!; const out: number[] = []; for (let i = 0; i < n; i++) { const before = pv.frames; pv.panBy(frac); await waitFrames(pv, before); out.push(pv.lastTotalMs); } return out; },
  async zoomSteps(chart: number, n: number, factor = 1 / 1.15) { const pv = view.plots.get(chart)!; const out: number[] = []; for (let i = 0; i < n; i++) { const before = pv.frames; const mid = ((pv as any).xmin + (pv as any).xmax) / 2; pv.zoomAt(mid, factor); await waitFrames(pv, before); out.push(pv.lastTotalMs); } return out; },
  async dragPan(chart: number, px: number) {
    const cv = this.canvasOf(chart)!; const r = cv.getBoundingClientRect(); const pv = view.plots.get(chart)!; const before = pv.frames;
    const o = { bubbles: true, pointerId: 1, pointerType: 'mouse', isPrimary: true, button: 0, buttons: 1 } as any;
    cv.dispatchEvent(new PointerEvent('pointerdown', { ...o, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 }));
    for (let i = 1; i <= 5; i++) cv.dispatchEvent(new PointerEvent('pointermove', { ...o, clientX: r.left + r.width / 2 + px * i / 5, clientY: r.top + r.height / 2 }));
    cv.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0, clientX: r.left + r.width / 2 + px, clientY: r.top + r.height / 2 }));
    await waitFrames(pv, before); await settle(); return pv.lastTotalMs;
  },
  async click(chart: number, frac: number, shift = false) {
    const cv = this.canvasOf(chart)!; const r = cv.getBoundingClientRect(); const pv = view.plots.get(chart)!; const pr = (pv as any).plotRect;
    const o = { bubbles: true, pointerId: 1, pointerType: 'mouse', isPrimary: true, button: 0, shiftKey: shift, clientX: r.left + pr.x + pr.w * frac, clientY: r.top + pr.y + pr.h / 2 } as any;
    cv.dispatchEvent(new PointerEvent('pointerdown', { ...o, buttons: 1 })); cv.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0 }));
    await new Promise(r => setTimeout(r, 60)); await settle(); return true;
  },
  async clickSel(sel: string) { const el = document.querySelector(sel) as HTMLElement | null; if (!el) throw new Error('no element ' + sel); el.click(); await new Promise(r => setTimeout(r, 60)); await settle(); return true; },
  async search(q: string) { nav.setSearch(q); await new Promise(r => setTimeout(r, 30)); return nav.matches.map(c => c.id); },
  async selectChannel(id: string) { const row = nav.rowElement(id); if (!row) throw new Error('row not visible ' + id); row.click(); await new Promise(r => setTimeout(r, 60)); await settle(); return true; },
  /** Feature tour at step n / help guide section (captures, gates). */
  async tour(n = 0) { await help.startTour(n); await new Promise(r => setTimeout(r, 250)); await settle(); return !!document.querySelector('.tour-card'); },
  async help(section = 'start') { help.show(section); await new Promise(r => setTimeout(r, 100)); return !!document.querySelector('.help-pane'); },
  async mode(m: string) { setMode(m); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  frameStats(chart: number) { const pv = this.plot(chart); return pv ? { frames: pv.frames, lastMs: pv.lastTotalMs, engineMs: pv.lastEngineMs, timings: pv.timings.slice(-60).map(t => t.t1 - t.t0) } : null; },
  async reportPage() { await report.refreshPage(); await new Promise(r => setTimeout(r, 100)); return !!report.el.querySelector('img')?.getAttribute('src'); },
  layout() { const g = (sel: string) => { const el = document.querySelector(sel) as HTMLElement | null; if (!el) return null; const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height), font: cs.fontSize, bg: cs.backgroundColor, color: cs.color }; }; return { header: g('.hdr'), nav: g('.nav'), center: g('.center'), props: g('.props'), footer: g('.ftr'), chartHead: g('.chart-h'), plot: g('.plot-box'), modes: g('.modes'), search: g('.search'), rowFile: g('.row-file'), rowCh: g('.row-ch'), bigNum: g('.prop-big .num'), led: g('.led-pill') }; },
};
async function waitFrames(pv: PlotView, before: number) { for (let i = 0; i < 300; i++) { if (pv.frames > before && !pv.busy) return; await new Promise(r => setTimeout(r, 4)); } }
window.nx = { bridge, test, get state() { return S; }, call: (c: string, a?: any) => bridge.call(c, a) };

// ---------- boot
document.fonts.ready.then(() => bridge.post({ cmd: 'ready' }));
if (!bridge.hosted) bridge.call<State>('state').then(applyState);
