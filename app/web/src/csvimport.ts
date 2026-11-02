// 1.1: CSV import dialog — shown when the sniffer is not confident (ambiguous delimiter / decimal, many non-numeric tokens,
// ragged rows, no numeric column) or the file is big, and from a file card's IMPORT… button. Every control change re-sniffs
// through the host (csv.preview) and refreshes the preview grid; OPEN confirms (csv.open), CANCEL / Esc drops the review.
import { bridge } from '@core/bridge';
import { CsvFormatDto, CsvReviewDto } from '@core/types';
import { esc, fmtBytes, fmtInt, h } from '@core/util';

let shown = 0;            // token of the review on screen (the engine resends the same review with every state push)
let dlg: HTMLElement | null = null;
let pending: Promise<any> | null = null;
/** Resolves when the last preview round-trip is done (tests). */
export const csvIdle = () => pending ?? Promise.resolve();

const ENCODINGS: [string, string][] = [['utf-8', 'UTF-8'], ['cp949', 'CP949 (한국어)'], ['latin1', 'Latin-1'], ['utf-16le', 'UTF-16 LE'], ['utf-16be', 'UTF-16 BE']];
const DELIMS: [string, string][] = [[',', '쉼표  ,'], [';', '세미콜론  ;'], ['\t', '탭'], ['|', '파이프  |'], [' ', '공백']];
const DECIMALS: [string, string][] = [['.', '마침표  .'], [',', '쉼표  ,']];
const MISSING = new Set(['', '-', 'nan', 'n/a', 'na', 'null', 'none', '#n/a', 'inf', '-inf', '+inf', 'infinity', '-infinity', '?']);

export function showCsvImport(r: CsvReviewDto | null | undefined) {
  if (!r) { if (dlg) { dlg.remove(); dlg = null; } shown = 0; return; }
  if (r.token === shown) return;
  dlg?.remove(); shown = r.token;
  dlg = build(r); document.body.append(dlg);
  (dlg.querySelector('[data-k=delimiter]') as HTMLElement | null)?.focus();
}

function isNum(s: string, dec: string): boolean {
  const t = s.trim(); if (MISSING.has(t.toLowerCase())) return true;
  const u = dec === ',' ? (t.includes('.') ? 'x' : t.replace(',', '.')) : t;
  return /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(u);
}

function build(r0: CsvReviewDto): HTMLElement {
  let r = r0; let fmt: CsvFormatDto = JSON.parse(JSON.stringify(r0.format));
  let timeTouched = false;   // until the user picks a time column, every re-sniff guesses it again (timeColumn -2)
  const why = r0.format.needsReview && r0.format.reasons.length ? `<div class="csv-why"><b>확인 필요</b><ul>${r0.format.reasons.map(x => `<li>${esc(x)}</li>`).join('')}</ul></div>`
    : r0.big ? `<div class="csv-why"><b>큰 파일</b><ul><li>${fmtBytes(r0.sizeBytes)} · 약 ${fmtInt(r0.estRows)}행 — 파싱 결과를 메모리에 두므로 약 ${fmtInt(r0.estMemoryMb)} MB가 필요합니다. 첫 열기만 느리고, 다음부터는 캐시에서 바로 엽니다.</li></ul></div>`
    : `<div class="csv-why ok"><b>자동 감지</b><ul><li>설정을 확인하거나 바꾼 뒤 열기를 누르세요. 바꾸면 미리보기가 바로 갱신됩니다.</li></ul></div>`;
  const el = h(`<div class="modal-bg csv-bg"><div class="modal csv" role="dialog" aria-modal="true" aria-label="CSV 가져오기">
    <div class="csv-head"><span class="lblh">${r0.reopen ? 'IMPORT SETTINGS' : 'IMPORT CSV'}</span><span class="nm">${esc(r0.name)}</span><span class="m">${fmtBytes(r0.sizeBytes)} · 약 ${fmtInt(r0.estRows)}행${r0.big ? ' · ~' + fmtInt(r0.estMemoryMb) + ' MB RAM' : ''}</span></div>
    ${why}
    <div class="csv-ctl">
      <label>ENCODING<select data-k="encoding">${ENCODINGS.map(([v, l]) => `<option value="${v}">${l}</option>`).join('')}</select></label>
      <label>DELIMITER<select data-k="delimiter">${DELIMS.map(([v, l]) => `<option value="${esc(v)}">${l}</option>`).join('')}</select></label>
      <label>DECIMAL<select data-k="decimal">${DECIMALS.map(([v, l]) => `<option value="${v}">${l}</option>`).join('')}</select></label>
      <label>TIME COLUMN<select data-k="timeColumn"></select></label>
      <label>SKIP LINES<input type="number" min="0" max="1000" data-k="skipLines"></label>
      <label class="ck"><input type="checkbox" data-k="hasNames"> 첫 줄 = 채널 이름</label>
      <label class="ck"><input type="checkbox" data-k="hasUnits"> 다음 줄 = 단위</label>
      <label>COMMENT<input type="text" maxlength="4" data-k="comment" placeholder="#"></label>
    </div>
    <div class="csv-grid"><table><thead></thead><tbody></tbody></table></div>
    <div class="csv-notes muted11"></div>
    <div class="csv-foot">${r0.automation ? '' : `<label class="ck"><input type="checkbox" data-k="remember" checked> 이 파일의 설정 기억 (<span class="mono">${esc(r0.name)}.nvimport.json</span>)</label>`}
      <div class="btns"><button class="pill h30" data-b="cancel">CANCEL</button><button class="pill h30 primary" data-b="ok">${r0.reopen ? 'REOPEN' : 'OPEN'}</button></div></div>
  </div></div>`);
  const q = <T extends HTMLElement>(s: string) => el.querySelector(s) as T;
  const sel = (k: string) => q<HTMLSelectElement>(`[data-k="${k}"]`);
  function fill() {
    sel('encoding').value = fmt.encoding; sel('delimiter').value = fmt.delimiter; sel('decimal').value = fmt.decimal;
    q<HTMLInputElement>('[data-k=skipLines]').value = String(fmt.skipLines); q<HTMLInputElement>('[data-k=hasNames]').checked = fmt.hasNames; q<HTMLInputElement>('[data-k=hasUnits]').checked = fmt.hasUnits;
    q<HTMLInputElement>('[data-k=comment]').value = fmt.comment ?? '';
    const t = sel('timeColumn'); t.innerHTML = `<option value="-1">— (sample index)</option>` + fmt.names.map((n, i) => `<option value="${i}">${esc(n)}${fmt.isText[i] ? ' (text)' : ''}</option>`).join(''); t.value = String(fmt.timeColumn);
  }
  function grid() {
    const f = r.format; const th = f.names.map((n, i) => `<th class="${i === f.timeColumn ? 'time' : ''}${f.isText[i] ? ' txt' : ''}" title="${esc(n)}">${esc(n)}${f.units[i] ? `<span class="u">${esc(f.units[i])}</span>` : ''}</th>`).join('');
    q('thead').innerHTML = `<tr>${th}</tr>`;
    q('tbody').innerHTML = r.preview.map(row => `<tr>${f.names.map((_, i) => { const v = row[i] ?? ''; const num = !f.isText[i] && i !== (f.timeKind === 'datetime' ? f.timeColumn : -1); const bad = num && !isNum(v, f.decimal); return `<td class="${num ? 'num' : ''}${bad ? ' bad' : ''}">${esc(v)}</td>`; }).join('')}</tr>`).join('');
    const notes: string[] = [];
    notes.push(`${f.columns}열 · 숫자 ${f.isText.filter(x => !x).length} · 텍스트 ${f.isText.filter(x => x).length}` + (f.timeColumn >= 0 ? ` · 시간 열 "${f.names[f.timeColumn]}" (${f.timeKind === 'datetime' ? '타임스탬프 → 초' : '초/시간 단위'})` : ' · 시간 열 없음 (샘플 인덱스가 x)'));
    if (f.meta?.length) notes.push(`전문 ${f.meta.length}줄 → 파일 속성`);
    if (f.reasons?.length && r !== r0) notes.push(...f.reasons);
    q('.csv-notes').textContent = notes.join('   ·   ');
  }
  function read() {
    fmt.encoding = sel('encoding').value; fmt.delimiter = sel('delimiter').value; fmt.decimal = sel('decimal').value; fmt.timeColumn = timeTouched ? +sel('timeColumn').value : -2;
    fmt.skipLines = Math.max(0, +q<HTMLInputElement>('[data-k=skipLines]').value || 0); fmt.hasNames = q<HTMLInputElement>('[data-k=hasNames]').checked; fmt.hasUnits = q<HTMLInputElement>('[data-k=hasUnits]').checked;
    fmt.comment = q<HTMLInputElement>('[data-k=comment]').value;
  }
  async function preview() {
    read();
    const p = bridge.call<CsvReviewDto>('csv.preview', { path: r0.path, format: fmt }); pending = p;
    try { const res = await p; if (!res || !dlg || dlg !== el) return; r = res; fmt = JSON.parse(JSON.stringify(res.format)); fill(); grid(); }
    catch (err) { q('.csv-notes').textContent = '미리보기 실패: ' + (err as Error).message; }
    finally { if (pending === p) pending = null; }
  }
  el.addEventListener('change', e => { const k = (e.target as HTMLElement).dataset.k; if (k === 'timeColumn') timeTouched = true; if (k && k !== 'remember') preview(); });
  el.addEventListener('click', e => {
    const b = (e.target as HTMLElement).closest('[data-b]') as HTMLElement | null;
    if (!b && e.target !== el) return;
    if (b?.dataset.b === 'ok') { read(); const remember = (q('[data-k=remember]') as HTMLInputElement | null)?.checked ?? false; bridge.call('csv.open', { path: r0.path, format: fmt, remember }); }
    else bridge.call('csv.cancel');
    el.remove(); dlg = null;
  });
  el.addEventListener('keydown', e => { if (e.key === 'Escape') { e.stopPropagation(); bridge.call('csv.cancel'); el.remove(); dlg = null; } });
  fill(); grid();
  return el;
}

/** Test hook: dialog summary (null when none). */
export function csvDialogInfo() {
  const d = dlg; if (!d) return null;
  return {
    columns: d.querySelectorAll('thead th').length, rows: d.querySelectorAll('tbody tr').length, bad: d.querySelectorAll('td.bad').length,
    reasons: Array.from(d.querySelectorAll('.csv-why li')).map(x => x.textContent ?? ''), title: d.querySelector('.csv-head .lblh')?.textContent ?? '',
    delimiter: (d.querySelector('[data-k=delimiter]') as HTMLSelectElement).value, decimal: (d.querySelector('[data-k=decimal]') as HTMLSelectElement).value, timeColumn: +(d.querySelector('[data-k=timeColumn]') as HTMLSelectElement).value,
    names: Array.from(d.querySelectorAll('thead th')).map(x => x.getAttribute('title') ?? ''), notes: d.querySelector('.csv-notes')?.textContent ?? '', remember: !!d.querySelector('[data-k=remember]'),
  };
}
