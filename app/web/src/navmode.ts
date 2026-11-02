// NAVIGATOR mode center: DATA SOURCES (mockup navigator.html) — actions, file cards, 3 summary stats.
import { bridge } from '@core/bridge';
import { I } from '@core/icons';
import { State } from '@core/types';
import { esc, fmtBytes, fmtInt, h } from '@core/util';

export class NavMode {
  el: HTMLElement; private body: HTMLElement; private meta: HTMLElement; private S: State | null = null;
  constructor() {
    this.el = h(`<section class="card" style="flex:1"><div class="card-h"><span class="ic">${I.db()}</span><span class="t">DATA SOURCES</span><span class="m" data-meta></span></div><div class="scroll"><div class="ds-body"></div></div></section>`);
    this.body = this.el.querySelector('.ds-body')!; this.meta = this.el.querySelector('[data-meta]')!;
    this.body.addEventListener('click', e => this.onClick(e));
  }
  setState(S: State) {
    this.S = S;
    const samples = S.files.reduce((a, f) => a + f.samples, 0);
    this.meta.textContent = S.files.length ? `${S.files.length} file${S.files.length !== 1 ? 's' : ''} · ${fmtInt(samples)} samples` : '';
    const nSel = S.selectedFiles.length >= 2 ? S.selectedFiles.length : S.files.length;
    const actions = `<div class="ds-actions"><button class="pill h32 primary" data-a="open">${I.folderW()}OPEN FILE</button><button class="pill h32" data-a="plugin">OPEN WITH DATAPLUGIN</button>${S.files.length >= 2 ? `<button class="pill h32" data-a="compare">COMPARE ${nSel} FILES</button>` : ''}<span class="hint">Drop .tdms / .tdm / .dxd / .mf4 / .csv anywhere</span></div>`;
    const cards = S.files.map(f => `<div class="ds-card${S.selectedFile === f.name || S.selectedFiles.includes(f.name) ? ' sel' : ''}" data-file="${esc(f.name)}"><span class="ic">${I.db(18)}</span><div class="body"><div class="nm"><b>${esc(f.name.replace(/\.[^.]+$/, ''))}</b><span class="chip">${esc(f.format)}</span>${f.warnings ? `<span class="chip" style="color:var(--amber)">${f.warnings} warn</span>` : ''}${f.format === 'CSV' ? `<button class="pill ds-import" data-import="${esc(f.name)}" title="가져오기 설정(구분자·소수점·헤더·시간 열)으로 다시 열기">IMPORT…</button>` : ''}</div><div class="path">${esc(f.dir)}\\…${f.startText ? `  ·  ${esc(f.startText.slice(0, 16))}` : ''}${f.duration != null ? ` · ${f.duration.toFixed(1)} s` : ''}</div></div>
      <div class="nums"><div><div class="k">CHANNELS</div><div class="v">${f.channelCount}</div></div><div><div class="k">SAMPLES</div><div class="v">${fmtInt(f.samples)}</div></div><div><div class="k">SIZE</div><div class="v">${f.sizeBytes ? fmtBytes(f.sizeBytes) : '—'}</div></div><div><div class="k">OPEN</div><div class="v g">${f.openMs.toFixed(0)} ms</div></div></div>
      <button class="xbtn" aria-label="Close file" data-close="${esc(f.name)}">${I.close()}</button></div>`).join('');
    const common = S.common.length, total = S.files[0]?.channelCount ?? 0;
    const starts = S.files.map(f => f.start).filter((s): s is number => s != null);
    const ends = S.files.map(f => (f.start ?? NaN) + (f.duration ?? 0)).filter(x => Number.isFinite(x));
    const span = starts.length >= 2 ? Math.max(...ends) - Math.min(...starts) : (S.files[0]?.duration ?? 0);
    const stats = S.files.length ? `<div class="stat3"><div><div class="k">COMMON CHANNELS</div><div class="v"><b>${common}</b><span>of ${total}</span></div><div class="s">identical names in ${nSel} files</div></div><div><div class="k">TIME SPAN</div><div class="v"><b>${span >= 1000 ? (span / 60).toFixed(1) : span.toFixed(1)}</b><span>${span >= 1000 ? 'min' : 's'}</span></div><div class="s">${starts.length >= 2 ? 'aligned by start time' : 'single file'}</div></div><div><div class="k">CACHE</div><div class="v"><b>${S.cache.ready}</b><span>of ${S.cache.total}</span></div><div class="s">pyramids ready · ${S.cache.mb.toFixed(1)} MB</div></div></div>` : `<div class="faint11">No data sources.</div>`;
    this.body.innerHTML = actions + `<div class="ds-list">${cards}</div>` + stats;
  }
  private onClick(e: MouseEvent) {
    const t = e.target as HTMLElement; const S = this.S!;
    const close = (t.closest('[data-close]') as HTMLElement | null)?.dataset.close; if (close) { bridge.call('closeFile', { name: close }); return; }
    const imp = (t.closest('[data-import]') as HTMLElement | null)?.dataset.import; if (imp) { bridge.call('csv.review', { name: imp }); return; }
    const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a;
    if (a === 'open') { bridge.call('openDialog'); return; }
    if (a === 'plugin') { bridge.call('openPluginDialog'); return; }
    if (a === 'compare') { document.dispatchEvent(new CustomEvent('nx-compare')); return; }
    const card = t.closest('[data-file]') as HTMLElement | null;
    if (card) { const name = card.dataset.file!; if (e.ctrlKey) { const sel = new Set(S.selectedFiles); sel.has(name) ? sel.delete(name) : sel.add(name); bridge.call('selectFiles', { names: Array.from(sel) }); } bridge.call('selectFile', { name }); }
  }
}
