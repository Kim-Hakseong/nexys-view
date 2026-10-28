// 1.0-rc: settings dialog — appearance, bus view, modules (feature flags, template folder), profile folders.
import { bridge } from './bridge';
import { State } from './types';
import { esc, h } from './util';

export function showSettings(S: State) {
  if (!S || document.querySelector('.settings')) return;
  const A = S as any; const on = !!A.features?.icdExport; const bt = A.busTime ?? 'rel';
  const pill = (attr: string, label: string, isOn: boolean) => `<button class="pill h24${isOn ? ' on' : ''}" ${attr}>${label}</button>`;
  const dlg = h(`<div class="modal-bg"><div class="modal settings" role="dialog" aria-label="설정"><div class="lblh">SETTINGS</div>
    <div class="sec"><h4>APPEARANCE</h4><div class="row">Chrome theme ${pill('data-theme="light"', 'LIGHT', S.theme === 'light')}${pill('data-theme="dark"', 'DARK', S.theme === 'dark')}<span class="d">Ctrl+T</span></div><div class="row">Plot theme ${pill('data-plot="light"', 'LIGHT', S.plot === 'light')}${pill('data-plot="dark"', 'DARK', S.plot === 'dark')}<span class="d">Ctrl+P</span></div></div>
    <div class="sec"><h4>BUS VIEW</h4><div class="row">Time display ${pill('data-bt="rel"', 'RELATIVE s', bt === 'rel')}${pill('data-bt="abs"', 'ABSOLUTE µs', bt === 'abs')}${pill('data-bt="delta"', 'DELTA', bt === 'delta')}</div><div class="row">Drop rule ${pill('data-drop="unit"', 'BY UNIT', S.dropMode !== 'each')}${pill('data-drop="each"', 'EACH', S.dropMode === 'each')}<span class="d">파일·그룹 드롭 시 차트 수</span></div></div>
    <div class="sec"><h4>MODULES</h4><label class="row"><input type="checkbox" data-f="icdExport"${on ? ' checked' : ''}> ICD export module<span class="d">툴바 ICD / EXPORT 버튼 · features.icdExport</span></label>
      <div class="row">Template project <input class="in mono" data-f="icdTemplateDir" value="${esc(A.mil1553?.templateDir || '')}" placeholder="시뮬레이터 1553 템플릿 프로젝트 폴더" spellcheck="false"><button class="pill h24" data-a="pickTemplate">PICK…</button></div>
      <label class="row"><input type="checkbox" data-f="legacyTabs"${S.legacyTabs ? ' checked' : ''}> ANALYSIS / REPORT tabs<span class="d">legacy</span></label></div>
    <div class="sec"><h4>PROFILE</h4><div class="row">Settings file <span class="mono muted11">%LOCALAPPDATA%\\NexysView\\layout.json</span><button class="pill h24" data-a="openProfile">OPEN FOLDER</button></div><div class="row">Samples <span class="mono muted11">…\\NexysView\\sample\\</span><span class="d">Sample.tdms · Sample1553.mon · Sample429.mon</span></div></div>
    <div class="btns"><button class="pill h30 primary" data-b="ok">CLOSE</button></div></div></div>`);
  dlg.addEventListener('click', e => {
    const t = e.target as HTMLElement; const b = t.closest('button') as HTMLElement | null;
    if ((t.closest('[data-b]') as HTMLElement | null)?.dataset.b === 'ok' || t === dlg) { dlg.remove(); return; }
    if (!b) return; const d = b.dataset;
    if (d.theme) bridge.call('setTheme', { theme: d.theme }); else if (d.plot) bridge.call('setPlot', { plot: d.plot }); else if (d.bt) bridge.call('m1553.busTime', { mode: d.bt }); else if (d.drop) bridge.call('setDropMode', { mode: d.drop });
    else if (d.a === 'pickTemplate') bridge.call('icd.pickTemplate').then(p => { if (p) (dlg.querySelector('[data-f=icdTemplateDir]') as HTMLInputElement).value = String(p); });
    else if (d.a === 'openProfile') bridge.call('openProfileFolder');
    dlg.querySelectorAll('button[data-theme],button[data-plot],button[data-bt],button[data-drop]').forEach(x => { const el = x as HTMLElement; const same = Object.keys(el.dataset).some(k => k in d && el.dataset[k] !== undefined && (k === 'theme' || k === 'plot' || k === 'bt' || k === 'drop') && k in b.dataset); if (same) el.classList.toggle('on', el === b); });
  });
  dlg.addEventListener('change', e => { const t = e.target as HTMLInputElement; if (t.dataset.f === 'icdExport') bridge.call('setFeature', { name: 'icdExport', on: t.checked }); if (t.dataset.f === 'icdTemplateDir') bridge.call('setFeature', { name: 'icdTemplateDir', value: t.value }); if (t.dataset.f === 'legacyTabs') bridge.call('setLegacyTabs', { on: t.checked }); });
  document.body.append(dlg);
}
