// 1.0-rc: ICD module dialog — IMPORT (file → column mapping → hub), EDIT (messages / words tables with validation), EXPORT (formats, preview).
import { bridge } from './bridge';
import { esc, h } from './util';

const COLS = ['message', 'rt', 'sa', 'tr', 'wc', 'period_ms', 'bus', 'word', 'bit_start', 'bit_len', 'type', 'scale', 'offset', 'unit', 'name'];
const TYPES = ['BNR', 'BCD', 'Discrete', 'SIMSCALAR', 'DECSCALAR', 'IEEE32', 'IEEE64', 'BNR48', 'BNR64', 'MIL1750_32', 'MIL1750_48', 'MIL1750_64', 'CAPS32', 'CAPS48', 'DEC64', 'Custom', 'String'];

export class IcdDialog {
  private dlg: HTMLElement | null = null; private tab = 'edit'; private hub: any = null; private sel = 0; private preview: any = null; private previewPath = ''; private mapping: Record<string, number> = {}; private exportPreview: any = null; private formats = new Set(['project', 'xml', 'device']); private lastExport: any = null;

  async show(tab = 'edit') {
    this.tab = tab; document.querySelector('.icd-bg')?.remove();
    this.dlg = h(`<div class="modal-bg icd-bg"><div class="modal icd" role="dialog" aria-label="ICD"><div class="icd-h"><b>ICD</b><span class="muted11 mono" data-sub></span><div class="icd-tabs"><button class="tab" data-t="import">IMPORT</button><button class="tab" data-t="edit">EDIT</button><button class="tab" data-t="export">EXPORT</button></div><button class="xbtn" data-a="close">✕</button></div><div class="icd-body"></div><div class="icd-f"><span class="muted11">hub schema 1.0 · <span data-path></span></span><span class="sp"></span><button class="pill h24" data-a="new">NEW</button><button class="pill h24" data-a="open">OPEN…</button><button class="pill h24" data-a="save">SAVE…</button><button class="pill h24" data-a="dummy" title="내장 더미 ICD(10 메시지)">DUMMY</button><button class="pill h24 primary" data-a="exportGo">EXPORT…</button></div></div></div>`);
    this.dlg.addEventListener('click', e => this.onClick(e)); this.dlg.addEventListener('change', e => this.onChange(e));
    document.body.append(this.dlg); await this.refresh();
  }
  close() { this.dlg?.remove(); this.dlg = null; }
  private get body() { return this.dlg!.querySelector('.icd-body') as HTMLElement; }

  async refresh() {
    if (!this.dlg) return; this.hub = await bridge.call('icd.hub');
    (this.dlg.querySelector('[data-sub]') as HTMLElement).textContent = this.hub.messages?.length ? `${this.hub.source ?? ''} · ${this.hub.messages.length} messages · ${this.hub.messages.reduce((a: number, m: any) => a + m.words.length, 0)} words` : 'no ICD loaded';
    (this.dlg.querySelector('[data-path]') as HTMLElement).textContent = this.hub.path ?? '';
    this.dlg.querySelectorAll('.icd-tabs .tab').forEach(b => b.classList.toggle('on', (b as HTMLElement).dataset.t === this.tab));
    if (this.tab === 'import') this.renderImport(); else if (this.tab === 'export') await this.renderExport(); else this.renderEdit();
  }

  // ---------- IMPORT
  private renderImport() {
    const p = this.preview;
    this.body.innerHTML = `<div class="icd-pane" style="flex:0 0 360px"><div class="ph">SOURCE FILE</div><div class="pad"><p class="muted11">CSV · XLSX(첫 시트) · 허브 JSON · 메시지 XML. CSV/XLSX는 워드당 1행이며 열 이름을 자동으로 인식하고, 아래에서 바꿀 수 있습니다.</p><button class="pill h30 primary" data-a="pick">CHOOSE FILE…</button>${this.previewPath ? `<div class="mono muted11" style="margin-top:8px;word-break:break-all">${esc(this.previewPath)}</div>` : ''}</div>
      ${p ? `<div class="ph">COLUMN MAPPING<span class="m">${p.total} rows</span></div><div class="icd-map">${COLS.map(c => `<span class="k${c === 'message' ? ' req' : ''}">${c}</span><select class="sel h24" data-map="${c}"><option value="-1">— (none) —</option>${p.columns.map((h: string, i: number) => `<option value="${i}"${this.mapping[c] === i ? ' selected' : ''}>${esc(h)}</option>`).join('')}</select><span class="mono muted11">${this.mapping[c] != null && this.mapping[c] >= 0 && p.rows[0] ? esc(p.rows[0][this.mapping[c]] ?? '') : ''}</span>`).join('')}</div><div class="pad"><button class="pill h30 primary" data-a="importGo"${this.mapping.message == null || this.mapping.message < 0 ? ' disabled' : ''}>IMPORT ${p.total} ROWS</button></div>` : ''}</div>
      <div class="icd-pane"><div class="ph">PREVIEW<span class="m">first ${p ? Math.min(12, p.rows.length) : 0} rows</span></div><div class="scroll">${p ? `<table class="icd-tbl"><thead><tr>${p.columns.map((c: string) => `<tr>`.length && `<th>${esc(c)}</th>`).join('')}</tr></thead><tbody>${p.rows.map((r: string[]) => `<tr>${p.columns.map((_: string, i: number) => `<td>${esc(r[i] ?? '')}</td>`).join('')}</tr>`).join('')}</tbody></table>` : `<div class="muted11 pad">파일을 고르면 열과 처음 12행이 보입니다.</div>`}</div></div>`;
  }
  async pickFile(path?: string) {
    const p = path ?? await bridge.call<string | null>('icd.pickFile'); if (!p) return;
    if (/\.(json|xml)$/i.test(p)) { await bridge.call('icd.load', { path: p }); this.tab = 'edit'; await this.refresh(); return; }
    this.previewPath = p; this.preview = await bridge.call('icd.preview', { path: p }); this.mapping = { ...this.preview.mapping }; this.tab = 'import'; await this.refresh();
  }
  async importGo() { const r = await bridge.call<any>('icd.importMapped', { path: this.previewPath, mapping: this.mapping }); this.tab = 'edit'; await this.refresh(); return r; }

  // ---------- EDIT
  private renderEdit() {
    const msgs: any[] = this.hub.messages ?? []; if (this.sel >= msgs.length) this.sel = Math.max(0, msgs.length - 1); const m = msgs[this.sel]; const errs: string[] = this.hub.errors ?? [];
    const num = (v: any, f: string, w = 36) => `<input class="cell" type="text" inputmode="numeric" data-f="${f}" value="${esc(v)}" style="width:${w}px">`;
    this.body.innerHTML = `<div class="icd-pane" style="flex:0 0 470px"><div class="ph">MESSAGES<span class="m">${msgs.length}</span><button class="pill h24" data-a="addMessage">+ ADD</button></div><div class="scroll"><table class="icd-tbl edit"><thead><tr><th>NAME</th><th>RT</th><th>SA</th><th>T/R</th><th>WC</th><th>ms</th><th>BUS</th><th></th></tr></thead><tbody>
      ${msgs.map((x, k) => `<tr data-m="${k}" class="${k === this.sel ? 'sel' : ''}"><td><input class="cell" data-f="name" value="${esc(x.name)}" style="width:120px"></td><td>${num(x.rt, 'rt')}</td><td>${num(x.sa, 'sa')}</td><td><select class="cell" data-f="tr"><option value="0"${x.tr === 0 ? ' selected' : ''}>BC→RT</option><option value="1"${x.tr === 1 ? ' selected' : ''}>RT→BC</option></select></td><td>${num(x.wc, 'wc')}</td><td>${num(x.periodMs, 'period_ms', 48)}</td><td><select class="cell" data-f="bus"><option${x.bus === 'A' ? ' selected' : ''}>A</option><option${x.bus === 'B' ? ' selected' : ''}>B</option></select></td><td><button class="xbtn" data-a="deleteMessage" data-m="${k}" title="삭제">✕</button></td></tr>`).join('')}</tbody></table></div>
      <div class="icd-msgs">${errs.length ? errs.slice(0, 8).map(e => `<div class="${e.startsWith('error') ? 'errv' : 'warnv'}">${esc(e)}</div>`).join('') + (errs.length > 8 ? `<div class="muted11">… ${errs.length - 8} more</div>` : '') : '<div class="muted11 okv">valid · no warnings</div>'}</div></div>
      <div class="icd-pane"><div class="ph">WORDS${m ? ` · ${esc(m.name)}` : ''}<span class="m">${m ? m.words.length : 0}</span>${m ? `<button class="pill h24" data-a="addWord">+ ADD WORD</button>` : ''}</div><div class="scroll">${m ? `<table class="icd-tbl edit"><thead><tr><th>NAME</th><th>WORD</th><th>BIT</th><th>LEN</th><th>TYPE</th><th>SCALE</th><th>OFFSET</th><th>UNIT</th><th></th></tr></thead><tbody>
      ${m.words.map((w: any, j: number) => `<tr data-w="${j}"><td><input class="cell" data-f="name" value="${esc(w.name)}" style="width:140px"></td><td>${num(w.index, 'index')}</td><td>${num(w.bitStart, 'bit_start')}</td><td>${num(w.bitLen, 'bit_len')}</td><td><select class="cell" data-f="type">${TYPES.map(t => `<option${w.type === t ? ' selected' : ''}>${t}</option>`).join('')}</select></td><td>${num(w.scale, 'scale', 64)}</td><td>${num(w.offset, 'offset', 64)}</td><td><input class="cell" data-f="unit" value="${esc(w.unit)}" style="width:56px"></td><td><button class="xbtn" data-a="deleteWord" data-w="${j}" title="삭제">✕</button></td></tr>`).join('')}</tbody></table>` : `<div class="muted11 pad">메시지를 추가하거나 ICD를 불러오세요.</div>`}</div></div>`;
  }

  // ---------- EXPORT
  private async renderExport() {
    const p = this.exportPreview = await bridge.call<any>('icd.exportPreview'); const M = (window as any).nx?.state?.mil1553 ?? {};
    const chk = (id: string, label: string, note: string, dis = false) => `<label class="opt"><input type="checkbox" data-fmt="${id}"${this.formats.has(id) ? ' checked' : ''}${dis ? ' disabled' : ''}><span><b>${label}</b><span class="muted11">${note}</span></span></label>`;
    const tmplOk = !!p?.templateOk;
    this.body.innerHTML = `<div class="icd-pane" style="flex:0 0 420px"><div class="ph">FORMATS</div><div class="pad">
      ${chk('project', '시뮬레이터 프로젝트 (복합문서)', tmplOk ? `템플릿: ${esc(M.templateDir ?? '')}` : '템플릿 프로젝트 폴더가 없어 생략됩니다 — 설정에서 지정 (PICK…)', false)}
      ${chk('xml', '메시지 XML', 'Definition/LRUs/LRU/SA/Field — 내보낼 때 XSD 검사 + 재판독 비교')}
      ${chk('device', '커스텀 디바이스 XML', '실시간 테스트 시스템 디바이스 정의 — XSD 검사')}
      ${chk('api', '자동화 스크립트', p?.simulatorDetected ? '시뮬레이터 COM 감지됨' : '시뮬레이터 COM 미감지 (BUS_SIM_PROGID) — 골격만', !p?.simulatorDetected)}
      <div class="muted11" style="margin-top:6px">허브 JSON/CSV는 항상 함께 저장됩니다.</div>
      ${!tmplOk ? `<button class="pill h24" data-a="pickTemplate" style="margin-top:8px">PICK TEMPLATE…</button>` : ''}
      ${this.lastExport ? `<div class="ph" style="margin:10px -12px 0">LAST EXPORT</div><div class="mono muted11" style="margin-top:6px">${Object.entries(this.lastExport).map(([k, v]: any) => `${k}: ${v.ok === false ? 'FAIL' : 'ok'}${v.schemaErrors?.length ? ' · schema errors ' + v.schemaErrors.length : ''}${v.roundTrip === false ? ' · round-trip mismatch' : ''}${v.note ? ' · ' + esc(v.note) : ''}`).join('<br>')}</div>` : ''}
      </div></div>
      <div class="icd-pane"><div class="ph">PREVIEW<span class="m">${p ? `${p.messages} messages · ${p.words} fields` : ''}</span></div><div class="scroll pad">${!p || p.ok === false ? `<div class="errv">${(p?.errors ?? ['no ICD']).filter((e: string) => e.startsWith('error')).map(esc).join('<br>') || 'no ICD loaded'}</div>` : ''}${(p?.items ?? []).map((it: any) => `<div class="icd-prev"><b>${esc(it.name)}</b> <span class="mono">${it.cwd}</span> <span class="muted11">RT${it.rt}/SA${it.sa} ${it.dir} WC${it.wc}</span><div class="muted11 mono">${it.fields.map(esc).join(' · ') || '(no fields)'}</div></div>`).join('')}</div></div>`;
  }

  private async onClick(e: Event) {
    const t = e.target as HTMLElement; const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; const tab = (t.closest('[data-t]') as HTMLElement | null)?.dataset.t;
    if (t === this.dlg || a === 'close') { this.close(); return; }
    if (tab) { this.tab = tab; await this.refresh(); return; }
    const row = t.closest('tr[data-m]') as HTMLElement | null; if (row && !a && this.tab === 'edit') { const k = +row.dataset.m!; if (k !== this.sel) { this.sel = k; this.renderEdit(); } }
    if (!a) return;
    if (a === 'pick') await this.pickFile(); else if (a === 'importGo') await this.importGo();
    else if (a === 'addMessage') { await bridge.call('icd.edit', { op: 'addMessage' }); this.hub = await bridge.call('icd.hub'); this.sel = this.hub.messages.length - 1; this.renderEdit(); }
    else if (a === 'deleteMessage') { await bridge.call('icd.edit', { op: 'deleteMessage', m: +(t.closest('[data-m]') as HTMLElement).dataset.m! }); await this.refresh(); }
    else if (a === 'addWord') { await bridge.call('icd.edit', { op: 'addWord', m: this.sel }); await this.refresh(); }
    else if (a === 'deleteWord') { await bridge.call('icd.edit', { op: 'deleteWord', m: this.sel, w: +(t.closest('[data-w]') as HTMLElement).dataset.w! }); await this.refresh(); }
    else if (a === 'new') { await bridge.call('icd.new'); this.sel = 0; this.tab = 'edit'; await this.refresh(); }
    else if (a === 'dummy') { await bridge.call('icd.dummy'); this.sel = 0; this.tab = 'edit'; await this.refresh(); }
    else if (a === 'open') await this.pickFile();
    else if (a === 'save') { await bridge.call('icd.saveDialog'); await this.refresh(); }
    else if (a === 'pickTemplate') { await bridge.call('icd.pickTemplate'); await this.refresh(); }
    else if (a === 'exportGo') { if (this.tab !== 'export') { this.tab = 'export'; await this.refresh(); return; } this.lastExport = await bridge.call('icd.exportDialog', { formats: [...this.formats] }); await this.refresh(); }
  }
  private async onChange(e: Event) {
    const t = e.target as HTMLInputElement; const d = t.dataset;
    if (d.map) { this.mapping[d.map] = +t.value; this.renderImport(); return; }
    if (d.fmt) { if (t.checked) this.formats.add(d.fmt); else this.formats.delete(d.fmt); return; }
    if (!d.f) return;
    const wrow = t.closest('tr[data-w]') as HTMLElement | null, mrow = t.closest('tr[data-m]') as HTMLElement | null;
    const numeric = ['rt', 'sa', 'wc', 'index', 'bit_start', 'bit_len', 'tr'].includes(d.f); const dbl = ['period_ms', 'scale', 'offset'].includes(d.f);
    const value = numeric ? parseInt(t.value, 10) || 0 : dbl ? parseFloat(t.value) || 0 : t.value;
    if (wrow) await bridge.call('icd.edit', { op: 'setWord', m: this.sel, w: +wrow.dataset.w!, field: d.f, value });
    else if (mrow) { this.sel = +mrow.dataset.m!; await bridge.call('icd.edit', { op: 'setMessage', m: this.sel, field: d.f, value }); }
    this.hub = await bridge.call('icd.hub'); const focus = document.activeElement; this.renderEdit(); void focus;
  }

  /** Self-test hooks */
  testApi() {
    const self = this;
    return {
      open: (tab: string) => self.show(tab), close: () => self.close(), isOpen: () => !!self.dlg, tab: () => self.tab,
      pick: (path: string) => self.pickFile(path), mapping: () => ({ ...self.mapping }), setMap: (col: string, idx: number) => { self.mapping[col] = idx; self.renderImport(); }, importGo: () => self.importGo(),
      hub: () => self.hub, errors: () => self.hub?.errors ?? [], messages: () => self.hub?.messages?.length ?? 0,
      async edit(args: any) { await bridge.call('icd.edit', args); await self.refresh(); return self.hub; },
      async setCell(sel: string, value: string) { const inp = self.body.querySelector(sel) as HTMLInputElement; if (!inp) throw new Error('no cell ' + sel); inp.value = value; inp.dispatchEvent(new Event('change', { bubbles: true })); await new Promise(r => setTimeout(r, 150)); return self.hub; },
      preview: () => self.exportPreview, formats: () => [...self.formats], setFormats: (f: string[]) => { self.formats = new Set(f); },
    };
  }
}
