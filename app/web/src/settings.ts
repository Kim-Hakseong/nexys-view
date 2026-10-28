// 1.0-rc: settings dialog — appearance, bus view, modules (feature flags, template folder), profile folders.
import { bridge } from '@core/bridge';
import { State } from '@core/types';
import { h } from '@core/util';

export function showSettings(S: State) {
  if (!S || document.querySelector('.settings')) return;
  const A = S as any; void A;
  const pill = (attr: string, label: string, isOn: boolean) => `<button class="pill h24${isOn ? ' on' : ''}" ${attr}>${label}</button>`;
  const dlg = h(`<div class="modal-bg"><div class="modal settings" role="dialog" aria-label="설정"><div class="lblh">SETTINGS</div>
    <div class="sec"><h4>APPEARANCE</h4><div class="row">Chrome theme ${pill('data-theme="light"', 'LIGHT', S.theme === 'light')}${pill('data-theme="dark"', 'DARK', S.theme === 'dark')}<span class="d">Ctrl+T</span></div><div class="row">Plot theme ${pill('data-plot="light"', 'LIGHT', S.plot === 'light')}${pill('data-plot="dark"', 'DARK', S.plot === 'dark')}<span class="d">Ctrl+P</span></div></div>
    <div class="sec"><h4>VIEW</h4><div class="row">Drop rule ${pill('data-drop="unit"', 'BY UNIT', S.dropMode !== 'each')}${pill('data-drop="each"', 'EACH', S.dropMode === 'each')}<span class="d">파일·그룹 드롭 시 차트 수</span></div></div>
    <div class="sec"><h4>MODULES</h4><label class="row"><input type="checkbox" data-f="legacyTabs"${S.legacyTabs ? ' checked' : ''}> ANALYSIS / REPORT tabs<span class="d">legacy</span></label>
      <div class="row">Bus captures (.mon) <span class="d">NexysBus — ANALYZE · SIMULATE · ICD</span></div></div>
    <div class="sec"><h4>PROFILE</h4><div class="row">Settings file <span class="mono muted11">%LOCALAPPDATA%\\NexysView\\layout.json</span><button class="pill h24" data-a="openProfile">OPEN FOLDER</button></div><div class="row">Samples <span class="mono muted11">…\\NexysView\\sample\\</span><span class="d">Sample.tdms</span></div></div>
    <div class="btns"><button class="pill h30 primary" data-b="ok">CLOSE</button></div></div></div>`);
  dlg.addEventListener('click', e => {
    const t = e.target as HTMLElement; const b = t.closest('button') as HTMLElement | null;
    if ((t.closest('[data-b]') as HTMLElement | null)?.dataset.b === 'ok' || t === dlg) { dlg.remove(); return; }
    if (!b) return; const d = b.dataset;
    if (d.theme) bridge.call('setTheme', { theme: d.theme }); else if (d.plot) bridge.call('setPlot', { plot: d.plot }); else if (d.drop) bridge.call('setDropMode', { mode: d.drop });
    else if (d.a === 'openProfile') bridge.call('openProfileFolder');
    dlg.querySelectorAll('button[data-theme],button[data-plot],button[data-bt],button[data-drop]').forEach(x => { const el = x as HTMLElement; const same = Object.keys(el.dataset).some(k => k in d && el.dataset[k] !== undefined && (k === 'theme' || k === 'plot' || k === 'bt' || k === 'drop') && k in b.dataset); if (same) el.classList.toggle('on', el === b); });
  });
  dlg.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.f === 'legacyTabs') bridge.call('setLegacyTabs', { on: t.checked }); });
  document.body.append(dlg);
}
