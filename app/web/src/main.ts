// App shell: header (brand · mode pills · tools · LED) / main (mode-specific) / footer; state fan-out; keyboard; file drop; test hooks.
import { Analysis } from './analysis';
import { bridge, WIN } from './bridge';
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
import { Mil1553Mode } from './mil1553';

let S: State | null = null;
const app = document.getElementById('app')!;
const hdr = h(`<header class="hdr"></header>`);
const main = h(`<main class="main"></main>`);
const ftr = h(`<footer class="ftr"><span class="d"></span><span class="txt"></span><span class="r"></span></footer>`);
app.append(hdr, main, ftr);
const help = new Help();
const nav = new Navigator(); const view = new ViewArea(); const props = new Properties(); const navMode = new NavMode(); const analysis = new Analysis(); const report = new Report(); const m1553 = new Mil1553Mode();
let frameInfo = '';

function renderHeader() {
  if (!S) return;
  const mode = (m: string, icon: string) => `<button type="button" class="tool mode${S!.mode === m ? ' on' : ''}" data-mode="${m}" aria-label="${m}" aria-pressed="${S!.mode === m}" title="${m} (Ctrl+${m === 'NAVIGATOR' ? 1 : m === 'VIEW' ? 2 : m === 'ANALYSIS' ? 3 : 4})">${icon}<span>${m}</span></button>`;
  const tool = (a: string, icon: string, label: string, title: string, disabled = false) => `<button type="button" class="tool" data-a="${a}" aria-label="${label}" title="${title}"${disabled ? ' disabled' : ''}>${icon}<span>${label}</span></button>`;
  const app = (S as any).app ?? { versionText: '', beta: false };
  if (WIN > 0) {
    const n = S.charts.filter(c => (c.windowId || 0) === WIN).length;
    hdr.innerHTML = `<div class="hdr-brand"><div class="hdr-logo" data-a="about" title="About Nexys VIEW (F1)"><img src="brand/Logo.png" alt="Nexys VIEW"></div><div class="hdr-name"><small>${esc((app.versionText || '').toUpperCase())}</small></div></div>
      <div class="hdr-tools"><span class="win-title">WINDOW ${WIN} · ${n} chart${n !== 1 ? 's' : ''}</span><div class="vsep"></div>${tool('returnAll', I.undo(), 'RETURN ALL', '모든 차트를 주 창으로 (창 닫기와 같음)')}<div class="vsep"></div>${tool('theme', S.theme === 'dark' ? I.sun() : I.theme(), 'THEME', 'Light / dark (Ctrl+T)')}${tool('plot', I.plot(), 'PLOT · ' + S.plot.toUpperCase(), 'Plot light / dark (Ctrl+P)')}
      <div class="led-pill"><span class="led ${S.status.led}"></span><span class="led-text">${S.status.led.toUpperCase()}</span></div></div>`;
    return;
  }
  hdr.innerHTML = `<div class="hdr-brand"><div class="hdr-logo" data-a="about" title="About Nexys VIEW (F1)"><img src="brand/Logo.png" alt="Nexys VIEW"></div><div class="hdr-name"><small>${esc((app.versionText || '').toUpperCase())}</small></div></div>
    <div class="hdr-tools"><nav aria-label="Mode" class="tools-modes">${mode('NAVIGATOR', I.list(15))}${mode('VIEW', I.lineC(15))}${S.legacyTabs ? mode('ANALYSIS', I.sliders(15)) + mode('REPORT', I.file(15)) : ''}${S.files.some(f => f.format === 'MIL1553') || S.mode === 'MIL1553' ? mode('MIL1553', I.table(15)) : ''}</nav><div class="vsep"></div>${tool('open', I.folder(), 'OPEN', 'Open file (Ctrl+O)')}${tool('save', I.save(), 'SAVE', 'Save all channels as TDMS (Ctrl+S)')}<div class="vsep"></div>${tool('undo', I.undo(), 'UNDO', S.undo.can ? `Undo ${S.undo.name} (Ctrl+Z)` : 'Undo (Ctrl+Z)', !S.undo.can)}${tool('redo', I.redo(), 'REDO', 'Redo (Ctrl+Y)', !S.undo.canRedo)}<div class="vsep"></div>${tool('theme', S.theme === 'dark' ? I.sun() : I.theme(), 'THEME', 'Toggle light / dark chrome (Ctrl+T)')}${tool('plot', I.plot(), 'PLOT · ' + S.plot.toUpperCase(), 'Toggle dark / light plot (Ctrl+P)')}<div class="vsep"></div></div>
    ${(S as any).features?.icdExport ? `<button type="button" class="tool icon" data-a="icdImport" aria-label="ICD IMPORT" title="ICD 가져오기 (CSV / JSON)">${I.tag()}</button><button type="button" class="tool icon" data-a="icdExport" aria-label="EXPORT PROJECT" title="버스 시뮬레이터 프로젝트 내보내기">${I.link()}</button><div class="vsep"></div>` : ''}<button type="button" class="tool icon" data-a="settings" aria-label="SETTINGS" title="설정">${I.sliders()}</button>
    <div class="vsep"></div><button type="button" class="tool icon" data-a="help" aria-label="HELP" title="사용 가이드 · 기능 투어 (F1)">${I.help()}</button>
    <div class="led-pill"><span class="led ${S.status.led}"></span><span class="led-text">${S.status.led.toUpperCase()}</span></div>`;
}
hdr.addEventListener('click', e => {
  const t = e.target as HTMLElement;
  const m = (t.closest('[data-mode]') as HTMLElement | null)?.dataset.mode; if (m) { setMode(m); return; }
  const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
  if (a === 'about') { showAbout(); return; }
  if (a === 'help') { help.show(); return; }
  if (a === 'settings') { showSettings(); return; }
  if (a === 'icdImport') { bridge.call('icd.importDialog'); return; }
  if (a === 'icdExport') { bridge.call('icd.exportDialog'); return; }
  if (a === 'returnAll') { bridge.call('window.returnAll', { id: WIN }); return; }
  if (a === 'open') bridge.call('openDialog'); else if (a === 'save') bridge.call('saveDialog'); else if (a === 'undo') bridge.call('undo'); else if (a === 'redo') bridge.call('redo');
  else if (a === 'theme') bridge.call('setTheme', { theme: S?.theme === 'dark' ? 'light' : 'dark' }); else if (a === 'plot') bridge.call('setPlot', { plot: S?.plot === 'dark' ? 'light' : 'dark' }); else if (a === 'dev') bridge.call('setDeveloper', { on: !S?.developer });
});
function setMode(m: string) { bridge.call('setMode', { mode: m }); }

function renderMain() {
  if (!S) return;
  const want: HTMLElement[] = WIN > 0 ? [view.el] : S.mode === 'NAVIGATOR' ? [nav.el, navMode.el, props.el] : S.mode === 'ANALYSIS' ? [nav.el, analysis.fns, analysis.center, analysis.params] : S.mode === 'REPORT' ? [report.left, report.center, report.right] : S.mode === 'MIL1553' ? [nav.el, m1553.el] : [nav.el, view.el, props.el];
  want.forEach((el, i) => { if (main.children[i] !== el) main.insertBefore(el, main.children[i] ?? null); });
  while (main.children.length > want.length) main.lastElementChild!.remove();
}
function renderFooter() {
  if (!S) return;
  (ftr.querySelector('.d') as HTMLElement).className = 'd ' + S.status.led;
  ftr.querySelector('.txt')!.textContent = S.status.text || (S.files.length ? `${S.files.length} files open · ${S.files.reduce((a, f) => a + f.channelCount, 0)} channels` : 'Open a file or drop it onto the window');
  ftr.querySelector('.r')!.textContent = (S.developer ? 'DEVELOPER · ' : '') + frameInfo;
}
document.addEventListener('nx-frame', (e: any) => {
  const d = e.detail; if (!d?.stats) return;
  frameInfo = `frame ${d.ms.toFixed(1)} ms · ${fmtInt(d.stats.samples)} samples · pyramid L${d.stats.level}`;
  ftr.querySelector('.r')!.textContent = (S?.developer ? 'DEVELOPER · ' : '') + frameInfo;
});

function applyState(next: State) {
  const prevMode = S?.mode; S = next;
  document.documentElement.dataset.theme = S.theme; document.documentElement.dataset.plot = S.plot;
  renderHeader(); renderMain(); renderFooter();
  if (WIN === 0) help.setState(S);
  nav.setState(S);
  // 0.9.4: this page shows the charts of its window; the main window shows the active page only
  const st = S; const vcharts = st.charts.filter(c => (c.windowId || 0) === WIN && (WIN > 0 || c.pageId === st.activePage));
  const vtables = WIN > 0 ? [] : st.tables.filter(t => t.pageId === st.activePage);
  if (WIN > 0) { view.setState(S, vcharts, vtables); return; }
  if (S.mode === 'VIEW') { view.setState(S, vcharts, vtables); props.setState(S); }
  else if (S.mode === 'NAVIGATOR') { navMode.setState(S); props.setState(S); }
  else if (S.mode === 'MIL1553') m1553.setState(S);
  else if (S.mode === 'ANALYSIS') analysis.setState(S);
  else if (S.mode === 'REPORT') report.setState(S);
  if (prevMode !== S.mode) { if (S.mode === 'VIEW') { view.setState(S, vcharts, vtables); for (const pv of view.plots.values()) pv.request({ adopt: true }); } if (S.mode === 'REPORT') report.refreshPage(); }
}
bridge.on('state', m => { const st = m.state as State; if (!st.files && S) st.files = S.files; else if (!st.files) st.files = []; applyState(st); });
bridge.on('undo', m => { if (!S) return; S.undo = { can: m.can, name: m.name, canRedo: m.canRedo }; renderHeader(); });
bridge.on('status', m => { if (!S) return; S.status = { text: m.text, led: m.led }; renderFooter(); const led = hdr.querySelector('.led'); if (led) { led.className = 'led ' + m.led; (hdr.querySelector('.led-text') as HTMLElement).textContent = m.led.toUpperCase(); } });
bridge.on('invalidate', m => { const ids: number[] = m.charts; const all = ids.includes(-1); for (const [id, pv] of view.plots) if (all || ids.includes(id)) pv.request({ adopt: true }); if (S?.mode === 'ANALYSIS' && (all || ids.includes(0))) (analysis as any).preview?.request(); });
bridge.on('buffer', () => { /* buffers arrive before the first frame; nothing to do */ });

// ---------- Settings (header): feature modules
function showSettings() {
  if (!S || document.querySelector('.settings')) return;
  const on = !!(S as any).features?.icdExport;
  const dlg = h(`<div class="modal-bg"><div class="modal settings" role="dialog"><div class="lblh">SETTINGS</div>
    <label class="opt"><input type="checkbox" data-f="icdExport"${on ? ' checked' : ''}><span><b>ICD export module</b><span class="muted11">features.icdExport — 툴바에 ICD 가져오기 / 버스 시뮬레이터 프로젝트 내보내기 버튼을 켭니다 (기본 꺼짐)</span></span></label>
    <div class="opt" style="flex-direction:column;align-items:stretch;gap:4px"><b>템플릿 프로젝트 폴더</b><span class="muted11">시뮬레이터의 1553 템플릿 프로젝트(.CPJ + 장치 .CDV) 폴더. 비우면 허브 JSON/CSV와 자동화 스크립트만 내보냅니다.</span><input class="in" data-f="icdTemplateDir" value="${esc((S as any).mil1553?.templateDir || '')}" placeholder="C:\\...\\1553 template project" spellcheck="false" style="font-family:var(--mono);font-size:11px;height:24px;padding:0 8px;border:1px solid var(--border-strong);border-radius:4px;background:var(--panel);color:var(--text)"></div>
    <div class="btns"><button class="pill h30 primary" data-b="ok">CLOSE</button></div></div></div>`);
  dlg.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.f === 'icdExport') bridge.call('setFeature', { name: 'icdExport', on: t.checked }); if (t.dataset.f === 'icdTemplateDir') bridge.call('setFeature', { name: 'icdTemplateDir', value: t.value }); });
  dlg.addEventListener('click', e => { const t = e.target as HTMLElement; if ((t.closest('[data-b]') as HTMLElement | null)?.dataset.b === 'ok' || t === dlg) dlg.remove(); });
  document.body.append(dlg);
}

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
  if (!(e.ctrlKey || e.metaKey)) {
    if (e.key === 'F5') e.preventDefault();
    if (inInput || !S || S.mode !== 'VIEW') return;
    const pv = view.plots.get(S.activeChart) ?? view.plots.values().next().value;
    if (!pv) return;
    if (e.key === 'Backspace') { e.preventDefault(); bridge.call('zoomBack', { chart: pv.dto.id }); }
    else if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && pv.dto.cursorMode === 'band' && pv.bandLocal) { e.preventDefault(); pv.nudgeBand((e.key === 'ArrowLeft' ? -1 : 1) * (e.shiftKey ? 10 : 1)); }
    return;
  }
  if (inInput && !['o', 'O', 's', 'S', '1', '2', '3', '4'].includes(e.key)) return;
  const k = e.key.toLowerCase();
  if (k === 'z') { e.preventDefault(); bridge.call('undo'); } else if (k === 'y') { e.preventDefault(); bridge.call('redo'); }
  else if (k === 'o') { e.preventDefault(); bridge.call(e.shiftKey ? 'openPluginDialog' : 'openDialog'); } else if (k === 's') { e.preventDefault(); bridge.call('saveDialog'); }
  else if (k === 't') { e.preventDefault(); bridge.call('setTheme', { theme: S?.theme === 'dark' ? 'light' : 'dark' }); } else if (k === 'p') { e.preventDefault(); bridge.call('setPlot', { plot: S?.plot === 'dark' ? 'light' : 'dark' }); }
  else if (k === 'd' && e.shiftKey) { e.preventDefault(); bridge.call('setDeveloper', { on: !S?.developer }); }
  else if ((e.key === 'PageDown' || e.key === 'PageUp') && WIN === 0 && S) { e.preventDefault(); const st = S; const i = st.pages.findIndex(p => p.id === st.activePage); const j = e.key === 'PageDown' ? i + 1 : i - 1; if (j >= 0 && j < st.pages.length) bridge.call('page.select', { page: st.pages[j].id }); }
  else if (k === '1' && WIN === 0) setMode('NAVIGATOR'); else if (k === '2' && WIN === 0) setMode('VIEW'); else if (k === '3' && S?.legacyTabs) setMode('ANALYSIS'); else if (k === '4' && S?.legacyTabs) setMode('REPORT');
  else if (k === 'b') { e.preventDefault(); const pv = S ? view.plots.get(S.activeChart) ?? view.plots.values().next().value : undefined; if (pv) bridge.call('setCursorMode', { chart: pv.dto.id, mode: pv.dto.cursorMode === 'band' ? 'free' : 'band' }); }
});

// ---------- chart PNG from the properties panel (canvas → host clipboard / file)
document.addEventListener('nx-chart-png', (e: any) => { const pv = view.plots.get(e.detail.chart); if (!pv) return; bridge.call(e.detail.save ? 'png.save' : 'png.copy', { dataUrl: pv.pngDataUrl(), name: (pv.dto.title || 'chart') + '.png' }); });

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
  // ---- 0.9.3 (real DOM events)
  /** Drop a file / group node's channels onto the drop zone (DRAG_GROUP flag → engine drop rule). */
  async dropGroup(chans: { id: string; name: string }[]) { const dt = dragData(chans); dt.setData('application/x-nexys-group', 'group'); const z = view.el.querySelector('.dropzone')!; fire(z, 'dragover', dt); fire(z, 'drop', dt); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  /** Drag a NAVIGATOR row (file/group/node) by its channel id or key: uses the navigator's own dragstart handler. */
  async dragRowToView(sel: string) { const row = nav.el.querySelector(sel) as HTMLElement | null; if (!row) throw new Error('no row ' + sel); const dt = new DataTransfer(); row.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt })); const z = view.el.querySelector('.dropzone')!; fire(z, 'dragover', dt); fire(z, 'drop', dt); await new Promise(r => setTimeout(r, 80)); await settle(); return Array.from(dt.types); },
  async rightZoom(chart: number, fx0: number, fy0: number, fx1: number, fy1: number, mods: any = {}) { const pv = view.plots.get(chart)!; await pv.rightDrag(fx0, fy0, fx1, fy1, mods); await settle(); return pv.view; },
  async leftDrag(chart: number, fx0: number, fx1: number) { const pv = view.plots.get(chart)!; await pv.leftDrag(fx0, fx1); await settle(); return { view: pv.view, band: pv.bandLocal }; },
  /** Band drag inside the band by px steps; returns per-step frame times of `measure` chart (the detail chart) or of the chart itself. */
  async bandDrag(chart: number, px: number, n: number, measure?: number) {
    const pv = view.plots.get(chart)!; const mv = view.plots.get(measure ?? chart)!; const out: number[] = [];
    for (let i = 0; i < n; i++) { const before = mv.frames; pv.nudgeBand(px); await waitFrames(mv, before); out.push(mv.lastTotalMs); }
    await settle(); return out;
  },
  async bandDragLive(chart: number, px: number, n: number, measure?: number) { const pv = view.plots.get(chart)!; const out = await pv.bandDragLive(px, n, view.plots.get(measure ?? chart)!); await settle(); return out; },
  async key(key: string, mods: any = {}) { document.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...mods })); await new Promise(r => setTimeout(r, 60)); await settle(); return true; },
  async tableCopy(table: number, r0: number, c0: number, r1: number, c1: number) { const tv = view.tables.get(table); if (!tv) throw new Error('no table ' + table); tv.select(r0, c0, r1, c1); tv.el.dispatchEvent(new KeyboardEvent('keydown', { key: 'c', ctrlKey: true, bubbles: true, cancelable: true })); await new Promise(r => setTimeout(r, 150)); return await tv.tsv(r0, c0, r1, c1); },
  tableInfo(table: number) { const tv = view.tables.get(table); if (!tv) return null; const el = tv.el; return { cursorRow: tv.dto.cursorRow, rows: tv.dto.rows, scrollTop: (el.querySelector('.tscroll') as HTMLElement).scrollTop, curVisible: !!el.querySelector('tr.cur'), rendered: el.querySelectorAll('tbody tr').length }; },
  async menu(chart: number, item: string) { view.plots.get(chart)!.menuAction(item); await new Promise(r => setTimeout(r, 120)); await settle(); return true; },
  // ---- 0.9.4
  pick(ids: string[]) { nav.setPicked(ids); return true; },
  /** Start a drag on a NAVIGATOR channel row, move the pointer, read the preview card's rect (viewport px). */
  async dragStartRow(channelId: string) { const row = nav.rowElement(channelId); if (!row) throw new Error('row not visible ' + channelId); const r = row.getBoundingClientRect(); const dt = new DataTransfer(); row.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt, clientX: r.left + 20, clientY: r.top + 10 })); (window as any).__dt = dt; await raf(); return this.dragPreview(); },
  async dragMove(x: number, y: number) { document.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: (window as any).__dt, clientX: x, clientY: y })); await raf(); return this.dragPreview(); },
  async dragEnd() { document.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: (window as any).__dt })); await raf(); return !document.querySelector('.drag-preview'); },
  dragPreview() { const el = document.querySelector('.drag-preview') as HTMLElement | null; if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height, right: r.right, bottom: r.bottom, text: el.textContent, vw: window.innerWidth, vh: window.innerHeight }; },
  rowTops(ids: string[]) { return ids.map(id => { const el = nav.el.querySelector(`[data-ch="${CSS.escape(id)}"]`) as HTMLElement | null; return el ? { top: parseFloat(el.style.top), abs: getComputedStyle(el).position === 'absolute' } : null; }); },
  /** Elements whose border / outline / left border uses an accent colour (should be 0 after 0.9.4). */
  accentScan() {
    const css = getComputedStyle(document.documentElement); const accents = ['--accent2', '--accent-line', '--accent'].map(v => css.getPropertyValue(v).trim()).filter(Boolean);
    const toRgb = (c: string) => { const d = document.createElement('div'); d.style.color = c; document.body.append(d); const v = getComputedStyle(d).color; d.remove(); return v; };
    const set = new Set(accents.map(toRgb)); const hits: string[] = [];
    document.querySelectorAll<HTMLElement>('.card, .chart, .chart-h, .tbl, .help-note, .dropfx, .dropzone, .ds-card, .row-ch, .row-file, .ptab, .tool, .pill, .menu, .drag-preview, .tour-card, .tour-spot').forEach(el => {
      const cs = getComputedStyle(el);
      for (const k of ['borderTopColor', 'borderLeftColor', 'borderRightColor', 'borderBottomColor', 'outlineColor'] as const) { const w = k === 'outlineColor' ? cs.outlineWidth : cs[k.replace('Color', 'Width') as 'borderTopWidth']; if (set.has(cs[k]) && parseFloat(w) > 0 && cs.outlineStyle !== 'none') hits.push(el.className + ':' + k); }
    });
    return { accents: Array.from(set), hits };
  },
  pages() { return Array.from(document.querySelectorAll<HTMLElement>('.pages .ptab[data-page]')).map(b => ({ id: +b.dataset.page!, name: (b.querySelector('.nm') as HTMLElement)?.textContent, on: b.classList.contains('on') })); },
  gridCols() { const g = view.el.querySelector('.charts'); return g ? (g.classList.contains('cols2') ? 2 : 1) : 0; },
  async pageTabAction(page: number, act: string) { const b = view.el.querySelector(`.ptab[data-page="${page}"]`) as HTMLElement | null; if (!b) throw new Error('no tab ' + page); if (act === 'click') b.click(); else if (act === 'dblclick') b.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); else if (act === 'remove') (b.querySelector('[data-pact=remove]') as HTMLElement)?.click(); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  async renameInput(name: string) { const inp = view.el.querySelector('.ptab input') as HTMLInputElement | null; if (!inp) throw new Error('no rename input'); inp.value = name; inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  async modalButton(which: string) { const b = document.querySelector(`.modal [data-b="${which}"]`) as HTMLElement | null; if (!b) throw new Error('no modal button ' + which); b.click(); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  /** Pan steps; returns the epoch ms at which each step's frame request was sent (cross-window latency). */
  async panClock(chart: number, n: number, frac = 0.05) { const pv = view.plots.get(chart)!; const out: number[] = []; for (let i = 0; i < n; i++) { const before = pv.frames; out.push(performance.timeOrigin + performance.now()); pv.panBy(frac); await waitFrames(pv, before); await new Promise(r => setTimeout(r, 40)); } return out; },
  frameClock(chart: number) { const pv = view.plots.get(chart); return pv ? { epoch: pv.lastPaintEpoch, frames: pv.frames, xmin: pv.view.xmin, xmax: pv.view.xmax } : null; },
  async gripDragOut(chart: number) { const pv = view.plots.get(chart)!; const grip = pv.el.querySelector('.grip') as HTMLElement; const dt = new DataTransfer(); grip.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: dt })); const ev = new DragEvent('dragend', { bubbles: true, dataTransfer: dt, clientX: -40, clientY: 200, screenX: 100, screenY: 100 }); grip.dispatchEvent(ev); await new Promise(r => setTimeout(r, 300)); await settle(); return true; },
  /** Feature tour at step n / help guide section (captures, gates). */
  async tour(n = 0) { await help.startTour(n); await new Promise(r => setTimeout(r, 250)); await settle(); return !!document.querySelector('.tour-card'); },
  async help(section = 'start') { help.show(section); await new Promise(r => setTimeout(r, 100)); return !!document.querySelector('.help-pane'); },
  async mode(m: string) { setMode(m); await new Promise(r => setTimeout(r, 80)); await settle(); return true; },
  // ---- 0.10.0
  m1553() { return m1553.testApi(); },
  async settings(on: boolean) { showSettings(); const cb = document.querySelector('.settings [data-f=icdExport]') as HTMLInputElement; if (cb.checked !== on) { cb.checked = on; cb.dispatchEvent(new Event('change', { bubbles: true })); } await new Promise(r => setTimeout(r, 120)); await settle(); document.querySelector('.modal-bg')?.remove(); return !!document.querySelector('[data-a=icdImport]') === on; },
  frameStats(chart: number) { const pv = this.plot(chart); return pv ? { frames: pv.frames, lastMs: pv.lastTotalMs, engineMs: pv.lastEngineMs, timings: pv.timings.slice(-60).map(t => t.t1 - t.t0) } : null; },
  async reportPage() { await report.refreshPage(); await new Promise(r => setTimeout(r, 100)); return !!report.el.querySelector('img')?.getAttribute('src'); },
  layout() { const g = (sel: string) => { const el = document.querySelector(sel) as HTMLElement | null; if (!el) return null; const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height), font: cs.fontSize, bg: cs.backgroundColor, color: cs.color }; }; return { header: g('.hdr'), nav: g('.nav'), center: g('.center'), props: g('.props'), footer: g('.ftr'), chartHead: g('.chart-h'), plot: g('.plot-box'), modes: g('.modes'), search: g('.search'), rowFile: g('.row-file'), rowCh: g('.row-ch'), bigNum: g('.prop-big .num'), led: g('.led-pill') }; },
};
async function waitFrames(pv: PlotView, before: number) { for (let i = 0; i < 300; i++) { if (pv.frames > before && !pv.busy) return; await new Promise(r => setTimeout(r, 4)); } }
window.nx = { bridge, test, get state() { return S; }, call: (c: string, a?: any) => bridge.call(c, a) };

// ---------- boot
document.fonts.ready.then(() => bridge.post({ cmd: 'ready' }));
if (!bridge.hosted) bridge.call<State>('state').then(applyState);
