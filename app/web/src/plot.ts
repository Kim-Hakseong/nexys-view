// Chart card: header (grip · title · meta · X pill · time pill · cursor-mode pill · link pill · legend · menu · close), read-out strip
// (cursors A/B or band statistics), canvas plot. The canvas draws frames served by the engine (shared Float32 buffer + JSON header);
// gestures run locally and request new frames (latest-wins), the committed view goes to the engine for undo / zoom history.
// 0.9.3: right-drag zoom box (Shift = x only, Ctrl = y only), band cursor (handles, move, keyboard nudge), overview/detail link,
// x-axis link, log axes, curve width / points, chart menu (detail link, table, exports, PNG).
import { bridge, WIN } from './bridge';
import { I } from './icons';
import { ChartDto, FrameHdr, State } from './types';
import { curveColor, esc, fmtRead, fmtSigned, fmtValue, h } from './util';

export const DRAG_CH = 'application/x-nexys-channels';
export const DRAG_GROUP = 'application/x-nexys-group';
export const DRAG_CHART = 'application/x-nexys-chart';
export const DRAG_CURVE = 'application/x-nexys-curve';

export interface FrameTiming { seq: number; t0: number; t1: number; engineMs: number; }

type BandHit = 'start' | 'end' | 'inside' | null;

export class PlotView {
  el: HTMLElement; private head: HTMLElement; private curTbl: HTMLElement; private box: HTMLElement; private canvas: HTMLCanvasElement; private ctx: CanvasRenderingContext2D;
  private hint: HTMLElement; private dropfx: HTMLElement;
  dto: ChartDto; plot = 'dark';
  private seq = 0; private inFlight = false; private pendingReq: any = null; lastHdr: FrameHdr | null = null; private drawnSeq = -1;
  private sentAt = new Map<number, number>();
  timings: FrameTiming[] = []; frames = 0; lastEngineMs = 0; lastTotalMs = 0; lastStats: FrameHdr['stats'] | null = null; lastPaintEpoch = 0; paintEpochs: number[] = [];
  // local view during gestures
  private xmin = 0; private xmax = 1; private gestureFrom: [number, number] | null = null; private dragging = false;
  private L = 56; private R = 16; private T = 16; private B = 50; private laneN = 0;
  private dpr = 1; private cssW = 0; private cssH = 0;
  private ro: ResizeObserver;
  private area: HTMLElement; private menu: HTMLElement | null = null;
  // band (local copy while dragging; otherwise from the frame header / dto)
  private band: { x0: number; x1: number } | null = null; private bandPending = false; private bandDragging = false; private lastBandPost = 0;
  onActivate: () => void = () => {};
  settleWaiters: (() => void)[] = [];

  constructor(dto: ChartDto, plot: string) {
    this.dto = dto; this.plot = plot; this.xmin = dto.xmin; this.xmax = dto.xmax;
    if (dto.band?.on) this.band = { x0: dto.band.x0, x1: dto.band.x1 };
    this.el = h(`<section class="card chart" data-chart="${dto.id}"></section>`);
    this.head = h(`<div class="chart-h"></div>`);
    this.curTbl = h(`<div class="cur-table" style="display:none"></div>`);
    const wrap = h(`<div class="plot-wrap"></div>`);
    this.box = h(`<div class="plot-box"></div>`);
    this.canvas = document.createElement('canvas');
    this.ctx = this.canvas.getContext('2d', { alpha: false })!;
    this.hint = h(`<div class="hint">Drop a channel here</div>`);
    this.dropfx = h(`<div class="dropfx"><span></span></div>`);
    this.area = h(`<div class="zoombox" style="display:none"></div>`);
    this.box.append(this.canvas, this.hint, this.dropfx, this.area); wrap.append(this.box);
    this.el.append(this.head, this.curTbl, wrap);
    this.ro = new ResizeObserver(() => this.onResize());
    this.ro.observe(this.box);
    this.bindPointer(); this.bindDrop();
    bridge.on('frame', this.onFrame);
    this.renderHead();
  }

  dispose() { this.ro.disconnect(); bridge.off('frame', this.onFrame); this.closeMenu(); }

  update(dto: ChartDto, plot: string) {
    const replot = plot !== this.plot || dto.cursorMode !== this.dto.cursorMode;
    this.dto = dto; this.plot = plot;
    if (!this.dragging) { this.xmin = dto.xmin; this.xmax = dto.xmax; }
    if (!this.bandDragging) this.band = dto.band?.on ? { x0: dto.band.x0, x1: dto.band.x1 } : null;
    this.renderHead();
    if (replot) this.request({ adopt: true }); else if (this.lastHdr) this.draw(this.lastHdr);
  }

  // ------------------------------------------------------------------ header
  private renderHead() {
    const d = this.dto, idx = this.el.dataset.index ?? '1';
    const nLanes = d.laneCount;
    const nCurves = (d as any).curveCount ?? d.curves.filter(c => !c.lane).length;
    const meta = `${nCurves} curve${nCurves !== 1 ? 's' : ''}${nLanes ? ` · ${nLanes} lane${nLanes !== 1 ? 's' : ''}` : ''}`;
    let pills = '';
    if (d.xChannel) {
      pills += `<span class="pill on" data-act="xinfo">${I.clock(12, 'var(--on-accent)')}X · ${esc(d.xChannel.name.toUpperCase())}</span>`;
      const tm = d.xTimeMode === 'absolute' ? 'ABS · HH:mm:ss' : d.xTimeMode === 'relative' ? 'REL · mm:ss' : 'NUMERIC';
      pills += `<button class="pill" data-act="cycleTime" title="Click: cycle relative / absolute / numeric">${tm}</button>`;
    } else if (d.suggestedX) {
      pills += `<button class="pill" data-act="useX" title="Map every curve to ${esc(d.suggestedX.name)} [${esc(d.suggestedX.unit)}]">${I.clock(12, 'currentColor')}USE ${esc(d.suggestedX.name.toUpperCase())} AS X</button>`;
    }
    const band = d.cursorMode === 'band';
    pills += `<button class="pill${band ? ' on' : ''}" data-act="cursorMode" title="${band ? '밴드 커서 · 클릭하면 자유 커서 A/B (Ctrl+B)' : '자유 커서 A/B · 클릭하면 밴드 커서 (Ctrl+B)'}">${band ? 'BAND' : 'A / B'}</button>`;
    if (d.detailOf != null) pills += `<span class="chip" data-act="detailinfo" title="개요 차트의 밴드 구간을 보여주는 상세 차트">linked · ${esc((d as any).detailOfName || 'Chart ' + d.detailOf)}</span>`;
    else if (d.linkKey) pills += `<button class="pill${d.linkX ? ' on' : ''}" data-act="link" title="${d.linkX ? 'X축 링크 켜짐 · 클릭하면 이 차트만 따로 움직임' : 'X축 링크 꺼짐 · 클릭하면 같은 X를 쓰는 차트와 함께 움직임'}">${I.link(11, d.linkX ? 'var(--on-accent)' : 'currentColor')}LINK</button>`;
    const legTitle = d.hasDetail ? 'Click: show in the detail chart · Shift+click: show/hide · drag to another chart' : 'Click: show/hide · drag to another chart';
    const legend = d.curves.filter(c => !c.lane).map(c => `<span class="leg${c.visible ? '' : ' off'}" data-leg="${c.idx}" draggable="true" title="${legTitle}"><span class="swatch" style="background:${curveColor(c.color, this.plot)}"></span>${esc(c.name)}</span>`).join('');
    this.head.innerHTML = `<span class="grip" draggable="true" title="Drag to reorder / swap">${I.grip()}</span><span class="title">${esc(d.title || 'Chart ' + idx)}</span><span class="meta">${meta}</span>${pills}<div class="legend">${legend}</div><button class="xbtn" aria-label="Chart menu" data-act="menu" title="차트 메뉴">${I.more()}</button><button class="xbtn" aria-label="Close chart" data-act="close">${I.close()}</button>`;
    this.hint.style.display = d.curves.length === 0 ? 'flex' : 'none';
    this.renderCursorTable();
  }

  private renderCursorTable() {
    const hd = this.lastHdr, d = this.dto;
    if (hd?.band && (d.cursorMode === 'band' || d.band?.on)) {
      const b = hd.band;
      if (!b.readouts) { if (this.curTbl.style.display === 'none' || !this.curTbl.querySelector('.band')) { this.curTbl.innerHTML = `<div class="cur-row band head"><span>BAND  <span class="mono">${esc(b.label0)} → ${esc(b.label1)} · <span style="color:var(--accent2)">${esc(b.lenLabel)}</span></span></span><span class="r">MIN</span><span class="r">MAX</span><span class="r">MEAN</span><span class="r">RMS</span><span class="r">P-P</span><span></span></div>`; this.curTbl.style.display = ''; } else { const hd0 = this.curTbl.querySelector('.head .mono'); if (hd0) hd0.innerHTML = `${esc(b.label0)} → ${esc(b.label1)} · <span style="color:var(--accent2)">${esc(b.lenLabel)}</span>`; } return; }
      const head = `BAND  <span class="mono">${esc(b.label0)} → ${esc(b.label1)} · <span style="color:var(--accent2)">${esc(b.lenLabel)}</span></span>`;
      const v = (x: number | null, approx: boolean) => (approx && x != null ? '≈' : '') + fmtRead(x);
      const rows = b.readouts.map(r => `<div class="cur-row band"><span class="nm"><span class="dot8" style="background:${curveColor(r.color, this.plot)}"></span><span>${esc(r.label)}</span></span><span class="v">${fmtRead(r.min)}</span><span class="v">${fmtRead(r.max)}</span><span class="v">${v(r.mean, r.approx)}</span><span class="v">${v(r.rms, r.approx)}</span><span class="v d">${fmtRead(r.pp)}</span><span class="u">${esc(r.unit)}</span></div>`).join('');
      this.curTbl.innerHTML = `<div class="cur-row band head"><span>${head}</span><span class="r">MIN</span><span class="r">MAX</span><span class="r">MEAN</span><span class="r">RMS</span><span class="r">P-P</span><span></span></div>${rows}`;
      this.curTbl.style.display = ''; return;
    }
    if (!hd || !hd.cursors || (!d.cursorA.on && !d.cursorB.on)) { this.curTbl.style.display = 'none'; return; }
    const c = hd.cursors;
    const head = `CURSOR  <span class="mono">${c.a ? 'A ' + esc(c.a.label) : ''}${c.a && c.b ? ' · ' : ''}${c.b ? 'B ' + esc(c.b.label) : ''}${c.dx != null ? ` · Δx <span style="color:var(--accent2)">${esc(c.dxLabel)}</span>` : ''}</span>`;
    const rows = c.readouts.map(r => `<div class="cur-row"><span class="nm"><span class="dot8" style="background:${curveColor(r.color, this.plot)}"></span><span>${esc(r.label)}</span></span><span class="v">${fmtRead(r.a)}</span><span class="v">${fmtRead(r.b)}</span><span class="v d">${fmtSigned(r.delta)}</span><span class="u">${esc(r.unit)}</span></div>`).join('');
    this.curTbl.innerHTML = `<div class="cur-row head"><span>${head}</span><span class="r">A</span><span class="r">B</span><span class="r">Δ</span><span></span></div>${rows}`;
    this.curTbl.style.display = '';
  }

  // ------------------------------------------------------------------ geometry
  private layoutFor(hd: FrameHdr | null) {
    const axes = hd?.axes ?? []; const nL = Math.max(1, axes.filter(a => !a.right).length), nR = axes.filter(a => a.right).length;
    this.L = 56 + 48 * (nL - 1); this.R = nR > 0 ? 56 + 48 * (nR - 1) : 16; this.T = 16;
    this.laneN = hd ? hd.curves.filter(c => c.mode === 'lane').length : 0;
    this.B = 50 + 20 * this.laneN;
  }
  get plotRect() { return { x: this.L, y: this.T, w: Math.max(1, this.cssW - this.L - this.R), h: Math.max(1, this.cssH - this.T - this.B) }; }
  xToPx(x: number) { const r = this.plotRect; return r.x + (x - this.xmin) / (this.xmax - this.xmin) * r.w; }
  pxToX(px: number) { const r = this.plotRect; return this.xmin + (px - r.x) / r.w * (this.xmax - this.xmin); }
  get xPerPx() { return (this.xmax - this.xmin) / this.plotRect.w; }

  private onResize() {
    const rect = this.box.getBoundingClientRect();
    this.cssW = Math.max(10, Math.round(rect.width)); this.cssH = Math.max(10, Math.round(rect.height));
    this.dpr = window.devicePixelRatio || 1;
    this.canvas.width = Math.round(this.cssW * this.dpr); this.canvas.height = Math.round(this.cssH * this.dpr);
    if (this.lastHdr) this.draw(this.lastHdr);
    this.request({ adopt: true });
  }

  // ------------------------------------------------------------------ frames
  /** Request a frame. adopt: the engine's x window is authoritative (invalidate / resize / linked move) — no local xmin/xmax is sent. */
  request(opts: { commit?: boolean; adopt?: boolean } = {}) {
    if (this.cssW <= 10) return;
    this.layoutFor(this.lastHdr);
    const r = this.plotRect;
    const req: any = { cmd: 'frame', chart: this.dto.id, w: Math.max(1, Math.round(r.w * this.dpr)), h: r.h, dpr: this.dpr, commit: !!opts.commit };
    if (!opts.adopt) { req.xmin = this.xmin; req.xmax = this.xmax; }
    if (opts.commit && this.gestureFrom) { req.fromX0 = this.gestureFrom[0]; req.fromX1 = this.gestureFrom[1]; }
    if (this.inFlight) { if (this.pendingReq?.commit && !req.commit) { req.commit = true; req.fromX0 = this.pendingReq.fromX0; req.fromX1 = this.pendingReq.fromX1; } this.pendingReq = req; return; }
    this.send(req);
  }
  private send(req: any) { req.seq = ++this.seq; this.inFlight = true; this.sentAt.set(req.seq, performance.now()); bridge.post(req); }

  private onFrame = (m: any) => {
    if (m.chart !== this.dto.id) return;
    this.inFlight = false;
    const t0 = this.sentAt.get(m.seq) ?? performance.now(); this.sentAt.delete(m.seq);
    if (m.gone || m.error) { if (m.error) bridge.log('frame error ' + m.error); }
    else if (m.seq >= this.drawnSeq) {
      this.drawnSeq = m.seq; this.lastHdr = m.header as FrameHdr;
      if (!this.dragging) { this.xmin = this.lastHdr.xmin; this.xmax = this.lastHdr.xmax; }   // the engine's window (linked charts, zoom back, band → detail)
      if (!this.bandDragging) this.band = this.lastHdr.band ? { x0: this.lastHdr.band.x0, x1: this.lastHdr.band.x1 } : null;
      this.draw(this.lastHdr);
      const t1 = performance.now();
      this.frames++; this.lastEngineMs = this.lastHdr.stats.ms; this.lastTotalMs = t1 - t0; this.lastStats = this.lastHdr.stats; this.lastPaintEpoch = performance.timeOrigin + t1; this.paintEpochs.push(this.lastPaintEpoch); if (this.paintEpochs.length > 200) this.paintEpochs.shift();
      this.timings.push({ seq: m.seq, t0, t1, engineMs: this.lastHdr.stats.ms }); if (this.timings.length > 400) this.timings.shift();
      this.renderCursorTable();
      document.dispatchEvent(new CustomEvent('nx-frame', { detail: { chart: this.dto.id, ms: t1 - t0, stats: this.lastHdr.stats } }));
    }
    if (this.pendingReq) { const p = this.pendingReq; this.pendingReq = null; this.send(p); }
    else for (const w of this.settleWaiters.splice(0)) w();
  };
  get busy() { return this.inFlight || !!this.pendingReq; }

  // ------------------------------------------------------------------ drawing (mockup view.html plot svg)
  private draw(hd: FrameHdr) {
    const buf = bridge.data(this.dto.id); const ctx = this.ctx; const dpr = this.dpr;
    if (!buf && !(this as any)._warned) { (this as any)._warned = true; bridge.log(`chart ${this.dto.id}: no frame data`); }
    const css = getComputedStyle(document.documentElement);
    const C = { bg: css.getPropertyValue('--plot-bg').trim(), grid: css.getPropertyValue('--plot-grid').trim(), axis: css.getPropertyValue('--plot-axis').trim(), cursor: css.getPropertyValue('--plot-cursor').trim(), laneAlt: css.getPropertyValue('--plot-lane-alt').trim() };
    const light = this.plot === 'light';
    // a frame carries the engine's x window; during a gesture draw in the local window (the frame is a step behind)
    const fx0 = this.dragging ? this.xmin : hd.xmin, fx1 = this.dragging ? this.xmax : hd.xmax;
    this.layoutFor(hd);
    const r = this.plotRect; const W = this.cssW, H = this.cssH;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = C.bg; ctx.fillRect(0, 0, W, H);
    const X = (x: number) => r.x + (x - fx0) / (fx1 - fx0) * r.w;
    const hx0 = hd.xmin, hx1 = hd.xmax;   // the frame's own window (normalized raw x, env columns)
    const XF = (x: number) => r.x + (x - hx0) / (hx1 - hx0) * r.w;   // frame x → px (equals X when not dragging)
    const colX = (i: number, n: number) => XF(hx0 + (i + 0.5) / n * (hx1 - hx0));
    const axisOf = (id?: string) => hd.axes.find(a => a.id === id) ?? hd.axes[0];
    const Y = (a: { min: number; max: number; log?: boolean }, v: number) => a.log ? (v > 0 ? r.y + (Math.log10(a.max) - Math.log10(v)) / (Math.log10(a.max) - Math.log10(a.min)) * r.h : NaN) : r.y + (a.max - v) / (a.max - a.min) * r.h;
    ctx.font = '10px "JetBrains Mono", monospace'; ctx.textBaseline = 'alphabetic';
    // grid
    const xt = hd.xticks.map(t => ({ px: X(t.v), label: t.label })).filter(t => t.px >= r.x - 0.5 && t.px <= r.x + r.w + 0.5);
    const prim = hd.axes[0]; const yt = prim ? prim.ticks.map(t => ({ py: Y(prim, t.v), label: t.label })).filter(t => t.py >= r.y - 0.5 && t.py <= r.y + r.h + 0.5) : [];
    if (this.dto.grid) {
      if (light && xt.length > 1 && yt.length > 1 && !prim.log) {
        const dx = (xt[1].px - xt[0].px) / 5, dy = Math.abs(yt[1].py - yt[0].py) / 5;
        ctx.fillStyle = C.grid;
        for (let x = xt[0].px - 5 * dx; x <= r.x + r.w; x += dx) { if (x < r.x) continue; for (let y = yt[0].py - 5 * dy; y <= r.y + r.h; y += dy) { if (y < r.y) continue; ctx.fillRect(Math.round(x) - 0.5, Math.round(y) - 0.5, 1.2, 1.2); } }
      } else {
        ctx.strokeStyle = C.grid; ctx.lineWidth = 1; ctx.beginPath();
        for (const t of xt) { const x = Math.round(t.px) + 0.5; ctx.moveTo(x, r.y); ctx.lineTo(x, r.y + r.h); }
        for (const t of yt) { const y = Math.round(t.py) + 0.5; ctx.moveTo(r.x, y); ctx.lineTo(r.x + r.w, y); }
        ctx.stroke();
      }
    }
    // x tick labels (+16) and unit (+30)
    ctx.fillStyle = C.axis; ctx.textAlign = 'center';
    for (const t of xt) ctx.fillText(t.label, t.px, r.y + r.h + 16);
    ctx.font = '10px Inter, sans-serif'; ctx.textAlign = 'right';
    if (hd.xunit) ctx.fillText(this.dto.xChannel ? `${this.dto.xChannel.name}  ·  ${hd.xTimeMode === 'relative' ? 'mm:ss.fff' : hd.xTimeMode === 'absolute' ? hd.xunit : hd.xunit}` : hd.xunit, r.x + r.w, r.y + r.h + 30);
    // y axes
    let li = 0, ri = 0;
    for (const a of hd.axes) {
      const col = a.color != null ? curveColor(a.color, this.plot) : C.axis;
      const ax = a.right ? r.x + r.w + ri++ * 48 : r.x - li++ * 48;
      ctx.font = '10px "JetBrains Mono", monospace'; ctx.fillStyle = col; ctx.textAlign = a.right ? 'left' : 'right';
      for (const t of a.ticks) { const y = Y(a, t.v); if (!(y >= r.y - 0.5 && y <= r.y + r.h + 0.5)) continue; ctx.fillText(t.label, a.right ? ax + 8 : ax - 8, y + 3); }
      const unit = (a.unit || '') + (a.log ? ' log' : '');
      if (unit) { ctx.font = '10px Inter, sans-serif'; ctx.fillStyle = C.axis; ctx.fillText(unit, a.right ? ax + 8 : ax - 8, r.y - 4); }
    }
    // curves (clipped)
    ctx.save(); ctx.beginPath(); ctx.rect(r.x, r.y, r.w, r.h); ctx.clip();
    let first = true;
    for (const c of hd.curves) {
      if (c.mode === 'lane' || !buf) continue;
      const col = curveColor(c.color, this.plot);
      const a = axisOf(c.axis); const lw = c.width ?? 1;
      if (c.mode === 'env') {
        const n = c.n; const hair = 1 / dpr;
        ctx.strokeStyle = col; ctx.lineWidth = hair * lw; ctx.globalAlpha = 0.38; ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const mn = buf[c.off + i]; if (Number.isNaN(mn)) continue; const mx = buf[c.off + n + i];
          const x = colX(i, n); let y0 = Y(a, mx), y1 = Y(a, mn); if (Number.isNaN(y0) || Number.isNaN(y1)) continue; if (y1 - y0 < hair) { y0 -= hair / 2; y1 += hair / 2; }
          ctx.moveTo(x, y0); ctx.lineTo(x, y1);
        }
        ctx.stroke(); ctx.globalAlpha = 1; ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const mn = buf[c.off + i]; if (Number.isNaN(mn)) continue; const mx = buf[c.off + n + i];
          const x = colX(i, n), y0 = Y(a, mx), y1 = Y(a, mn); if (Number.isNaN(y0) || Number.isNaN(y1)) continue;
          ctx.moveTo(x, y0 - hair / 2); ctx.lineTo(x, y0 + hair / 2); ctx.moveTo(x, y1 - hair / 2); ctx.lineTo(x, y1 + hair / 2);
        }
        ctx.stroke();
      } else if (c.mode === 'raw') {
        const px = (i: number) => r.x + buf[c.off + i] * r.w + (XF(hx0) - X(hx0)) * 0;   // normalized to the frame window
        const PX = (i: number) => XF(hx0 + buf[c.off + i] * (hx1 - hx0));
        if (!c.points) {
          ctx.strokeStyle = col; ctx.lineWidth = (first ? 1.4 : 1.2) * lw; ctx.lineJoin = 'round'; ctx.beginPath();
          let started = false, py = 0;
          for (let i = 0; i < c.n; i++) {
            const v = buf[c.off + c.n + i]; if (Number.isNaN(v)) { started = false; continue; }
            const x = PX(i), y = Y(a, v); if (Number.isNaN(y)) { started = false; continue; }
            if (!started) { ctx.moveTo(x, y); started = true; } else if (c.step) { ctx.lineTo(x, py); ctx.lineTo(x, y); } else ctx.lineTo(x, y);
            py = y;
          }
          ctx.stroke();
        }
        if (c.points || c.n < r.w / 4) { const s = c.points ? 1.5 + lw : 1.5; ctx.fillStyle = col; for (let i = 0; i < c.n; i++) { const v = buf[c.off + c.n + i]; if (Number.isNaN(v)) continue; const y = Y(a, v); if (Number.isNaN(y)) continue; ctx.fillRect(PX(i) - s, y - s, 2 * s, 2 * s); } }
        void px;
      } else if (c.mode === 'xy') {
        ctx.fillStyle = col;
        for (let i = 0; i < c.n; i++) { const x = X(buf[c.off + i]), y = Y(a, buf[c.off + c.n + i]); if (Number.isNaN(x) || Number.isNaN(y)) continue; ctx.fillRect(x - 1, y - 1, 2, 2); }
      }
      first = false;
    }
    ctx.restore();
    // lanes (18px rows from plot bottom + 40, alternating bg, label left, 0/1 path, fill where high)
    let lane = 0;
    for (const c of hd.curves) {
      if (c.mode !== 'lane' || !buf) continue;
      const y = r.y + r.h + 40 + lane * 20; const col = curveColor(c.color, this.plot);
      ctx.fillStyle = lane % 2 === 0 ? C.laneAlt : C.bg; ctx.fillRect(r.x, y, r.w, 18);
      ctx.font = '10px Inter, sans-serif'; ctx.fillStyle = C.axis; ctx.textAlign = 'right'; ctx.fillText(c.label.split('/').pop() ?? c.label, r.x - 8, y + 13);
      const n = c.n, colW = r.w / n; const hi = y + 3, lo = y + 15;
      ctx.strokeStyle = col; ctx.lineWidth = 1.6; ctx.beginPath(); let started = false, prev = -1;
      ctx.fillStyle = col; ctx.globalAlpha = 0.14; let run = -1;
      for (let i = 0; i <= n; i++) {
        const v = i < n ? buf[c.off + i] : NaN; const on = v === 1;
        if (on && run < 0) run = i; if (!on && run >= 0) { ctx.fillRect(r.x + run * colW, y + 3, (i - run) * colW, 12); run = -1; }
      }
      ctx.globalAlpha = 1;
      for (let i = 0; i < n; i++) {
        const v = buf[c.off + i]; if (Number.isNaN(v)) { started = false; continue; }
        const x = r.x + i * colW, yy = v === 1 ? hi : lo;
        if (!started) { ctx.moveTo(x, yy); started = true; } else { if (v !== prev) ctx.lineTo(x, prev === 1 ? hi : lo); ctx.lineTo(x, yy); }
        prev = v;
      }
      if (started) ctx.lineTo(r.x + r.w, prev === 1 ? hi : lo);
      ctx.stroke();
      lane++;
    }
    // band reference of a linked overview (faint)
    if (hd.bandRef && !this.band) {
      ctx.save(); ctx.strokeStyle = '#E8601C'; ctx.globalAlpha = 0.45; ctx.lineWidth = 1; ctx.setLineDash([2, 4]); ctx.beginPath();
      for (const x of [X(hd.bandRef.x0), X(hd.bandRef.x1)]) if (x >= r.x && x <= r.x + r.w) { ctx.moveTo(Math.round(x) + 0.5, r.y); ctx.lineTo(Math.round(x) + 0.5, r.y + r.h); }
      ctx.stroke(); ctx.restore();
    }
    // band cursor: translucent region, edge lines, grip handles
    if (this.band) {
      const x0 = Math.max(r.x, X(this.band.x0)), x1 = Math.min(r.x + r.w, X(this.band.x1));
      if (x1 > x0) { ctx.fillStyle = '#E8601C'; ctx.globalAlpha = light ? 0.10 : 0.14; ctx.fillRect(x0, r.y, x1 - x0, r.h); ctx.globalAlpha = 1; }
      for (const x of [X(this.band.x0), X(this.band.x1)]) {
        if (x < r.x - 1 || x > r.x + r.w + 1) continue;
        ctx.strokeStyle = '#E8601C'; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(Math.round(x) + 0.5, r.y); ctx.lineTo(Math.round(x) + 0.5, r.y + r.h); ctx.stroke();
        const hy = r.y + r.h / 2; ctx.fillStyle = '#E8601C'; ctx.beginPath(); ctx.roundRect(x - 4, hy - 14, 8, 28, 3); ctx.fill();
        ctx.fillStyle = '#fff'; for (let k = -1; k <= 1; k++) ctx.fillRect(x - 0.5, hy + k * 5 - 0.5, 1.5, 1.5);
      }
    }
    // cursors: dashed white line + orange pill badge at the top
    const cur = hd.cursors;
    const badge = (x: number, letter: string) => {
      if (x < r.x || x > r.x + r.w) return;
      ctx.save(); ctx.strokeStyle = C.cursor; ctx.globalAlpha = 0.9; ctx.lineWidth = 1; ctx.setLineDash([3, 3]); ctx.beginPath(); ctx.moveTo(Math.round(x) + 0.5, r.y); ctx.lineTo(Math.round(x) + 0.5, r.y + r.h); ctx.stroke(); ctx.restore();
      ctx.fillStyle = '#E8601C'; ctx.beginPath(); ctx.roundRect(x - 9, r.y - 2, 18, 14, 7); ctx.fill();
      ctx.fillStyle = '#fff'; ctx.font = '600 9px Inter, sans-serif'; ctx.textAlign = 'center'; ctx.fillText(letter, x, r.y + 8);
    };
    if (cur?.a) badge(X(cur.a.x), 'A');
    if (cur?.b) badge(X(cur.b.x), 'B');
  }

  // ------------------------------------------------------------------ band helpers
  private bandHit(px: number): BandHit {
    if (!this.band || this.dto.cursorMode !== 'band') return null;
    const a = this.xToPx(this.band.x0), b = this.xToPx(this.band.x1);
    if (Math.abs(px - a) <= 6) return 'start'; if (Math.abs(px - b) <= 6) return 'end';
    return px > a && px < b ? 'inside' : null;
  }
  private postBand(commit: boolean) {
    if (!this.band) return;
    const msg = { cmd: 'band', chart: this.dto.id, on: true, x0: Math.min(this.band.x0, this.band.x1), x1: Math.max(this.band.x0, this.band.x1), commit };
    if (commit) { this.bandPending = false; bridge.call('band', msg); return; }
    const now = performance.now();
    if (now - this.lastBandPost >= 2) { this.lastBandPost = now; bridge.post(msg); return; }
    if (this.bandPending) return; this.bandPending = true;
    setTimeout(() => { this.bandPending = false; if (!this.band) return; this.lastBandPost = performance.now(); bridge.post({ ...msg, x0: Math.min(this.band.x0, this.band.x1), x1: Math.max(this.band.x0, this.band.x1) }); }, 2);
  }
  /** Keyboard: move the band by px (Shift = 10 px); from the properties panel: exact x values. */
  nudgeBand(px: number) { if (!this.band) return; const dx = px * this.xPerPx; this.band = { x0: this.band.x0 + dx, x1: this.band.x1 + dx }; if (this.lastHdr) this.draw(this.lastHdr); this.postBand(true); }
  setBand(x0: number, x1: number, commit = true) { this.band = { x0, x1 }; if (this.lastHdr) this.draw(this.lastHdr); this.postBand(commit); }

  // ------------------------------------------------------------------ gestures
  private bindPointer() {
    const cv = this.canvas;
    type Drag = { btn: number; x0: number; y0: number; vx0: number; vx1: number; moved: boolean; shift: boolean; ctrl: boolean; band: BandHit; b0: number; b1: number };
    let drag: Drag | null = null;
    cv.addEventListener('pointerdown', e => {
      if (e.button !== 0 && e.button !== 2) return;
      this.onActivate(); this.closeMenu();
      const px = e.offsetX, py = e.offsetY;
      drag = { btn: e.button, x0: px, y0: py, vx0: this.xmin, vx1: this.xmax, moved: false, shift: e.shiftKey, ctrl: e.ctrlKey, band: e.button === 0 ? this.bandHit(px) : null, b0: this.band?.x0 ?? 0, b1: this.band?.x1 ?? 0 };
      if (e.button === 0 && !drag.band) { this.gestureFrom = [this.xmin, this.xmax]; }
      cv.setPointerCapture(e.pointerId);
    });
    cv.addEventListener('pointermove', e => {
      if (!drag) { const z = this.zoneAt(e.offsetX, e.offsetY); const bh = this.bandHit(e.offsetX); cv.style.cursor = bh === 'inside' ? 'grab' : bh ? 'ew-resize' : z === 'plot' ? 'crosshair' : 'default'; return; }
      const px = e.offsetX, py = e.offsetY;
      if (Math.abs(px - drag.x0) > 3 || Math.abs(py - drag.y0) > 3) drag.moved = true;
      if (!drag.moved) return;
      const r = this.plotRect;
      if (drag.btn === 2 || (drag.btn === 0 && drag.shift && !drag.band)) {
        // zoom box preview: right drag = both axes, Shift = x only, Ctrl = y only (Shift+left = x only, legacy)
        const xOnly = drag.btn === 0 || drag.shift, yOnly = drag.btn === 2 && drag.ctrl && !drag.shift;
        const a = Math.min(px, drag.x0), b = Math.max(px, drag.x0), c = Math.min(py, drag.y0), d = Math.max(py, drag.y0);
        Object.assign(this.area.style, { display: 'block', left: (yOnly ? r.x : a) + 'px', top: (xOnly ? r.y : c) + 'px', width: (yOnly ? r.w : b - a) + 'px', height: (xOnly ? r.h : d - c) + 'px' });
        return;
      }
      if (drag.band) {
        const dx = (px - drag.x0) * ((drag.vx1 - drag.vx0) / r.w);
        this.bandDragging = true;
        if (drag.band === 'inside') this.band = { x0: drag.b0 + dx, x1: drag.b1 + dx };
        else if (drag.band === 'start') this.band = { x0: Math.min(drag.b0 + dx, drag.b1 - this.xPerPx), x1: drag.b1 };
        else this.band = { x0: drag.b0, x1: Math.max(drag.b1 + dx, drag.b0 + this.xPerPx) };
        cv.style.cursor = drag.band === 'inside' ? 'grabbing' : 'ew-resize';
        if (this.lastHdr) this.draw(this.lastHdr);
        this.postBand(false);
        return;
      }
      const dx = (px - drag.x0) / r.w * (drag.vx1 - drag.vx0);
      this.dragging = true;
      this.xmin = drag.vx0 - dx; this.xmax = drag.vx1 - dx; cv.style.cursor = 'grabbing';
      this.request();
    });
    const up = (e: PointerEvent) => {
      if (!drag) return; const d = drag; drag = null; cv.style.cursor = '';
      this.area.style.display = 'none';
      const r = this.plotRect;
      if (d.btn === 2) {
        if (!d.moved) { bridge.call('clearCursors', { chart: this.dto.id }); this.gestureFrom = null; return; }
        const xOnly = d.shift, yOnly = d.ctrl && !d.shift;
        const a = this.pxToX(Math.min(e.offsetX, d.x0)), b = this.pxToX(Math.max(e.offsetX, d.x0));
        const fy0 = (Math.min(e.offsetY, d.y0) - r.y) / r.h, fy1 = (Math.max(e.offsetY, d.y0) - r.y) / r.h;
        const args: any = { chart: this.dto.id };
        if (!yOnly && b - a > 0) { args.x0 = a; args.x1 = b; }
        if (!xOnly && fy1 - fy0 > 0.005) { args.fy0 = Math.max(0, fy0); args.fy1 = Math.min(1, fy1); }
        if (args.x0 != null || args.fy0 != null) bridge.call('zoomBox', args);
        this.gestureFrom = null; return;
      }
      if (d.band) { this.bandDragging = false; if (d.moved) this.postBand(true); this.gestureFrom = null; return; }
      if (d.shift && d.moved) {
        const a = this.pxToX(Math.min(e.offsetX, d.x0)), b = this.pxToX(Math.max(e.offsetX, d.x0)); if (b - a > 0) { this.xmin = a; this.xmax = b; this.request({ commit: true }); }
        this.gestureFrom = null; return;
      }
      if (d.moved) { this.dragging = false; this.request({ commit: true }); this.gestureFrom = null; return; }
      this.gestureFrom = null;
      if (this.dto.curves.length === 0) return;
      const z = this.zoneAt(e.offsetX, e.offsetY); if (z !== 'plot') return;
      if (this.dto.cursorMode === 'band') return;
      bridge.call('setCursor', { chart: this.dto.id, which: e.shiftKey ? 'b' : 'a', x: this.pxToX(e.offsetX) });
    };
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointercancel', () => { drag = null; this.dragging = false; this.bandDragging = false; this.gestureFrom = null; this.area.style.display = 'none'; });
    cv.addEventListener('dblclick', () => { this.onActivate(); bridge.call('zoomFull', { chart: this.dto.id }); });
    cv.addEventListener('contextmenu', e => { e.preventDefault(); });
    cv.addEventListener('wheel', e => {
      e.preventDefault(); if (this.dto.curves.length === 0) return;
      this.onActivate();
      const f = e.ctrlKey ? (e.deltaY > 0 ? 2 : 0.5) : (e.deltaY > 0 ? 1.25 : 1 / 1.25);
      this.zoomAt(this.pxToX(e.offsetX), f);
    }, { passive: false });
  }
  /** Zoom the local window around x by factor and request a committed frame (undoable). */
  zoomAt(xc: number, f: number) {
    const w = (this.xmax - this.xmin) * f, t = (xc - this.xmin) / (this.xmax - this.xmin);
    this.gestureFrom = [this.xmin, this.xmax];
    this.xmin = xc - w * t; this.xmax = xc + w * (1 - t);
    this.request({ commit: true }); this.gestureFrom = null;
  }
  panBy(frac: number) { const w = this.xmax - this.xmin; this.gestureFrom = [this.xmin, this.xmax]; this.xmin += w * frac; this.xmax += w * frac; this.request({ commit: true }); this.gestureFrom = null; }
  setViewLocal(x0: number, x1: number, commit = false) { this.gestureFrom = [this.xmin, this.xmax]; this.xmin = x0; this.xmax = x1; this.request({ commit }); this.gestureFrom = null; }
  get view() { return { xmin: this.xmin, xmax: this.xmax }; }

  zoneAt(px: number, py: number): 'plot' | 'left' | 'right' | 'x' {
    const r = this.plotRect;
    if (py > r.y + r.h) return 'x';
    if (px < r.x) return 'left';
    if (px > r.x + r.w) return 'right';
    return 'plot';
  }

  // ------------------------------------------------------------------ chart menu (0.9.3)
  private closeMenu() { this.menu?.remove(); this.menu = null; }
  private openMenu(anchor: HTMLElement) {
    this.closeMenu();
    const d = this.dto; const band = this.band;
    const items: { a: string; t: string; on?: boolean; dis?: boolean }[] = [
      { a: 'detail', t: d.detailOf != null ? '이 차트는 상세 차트입니다' : d.hasDetail ? '상세 차트: 선택 채널로 바꾸기' : '상세 차트 연결', dis: d.detailOf != null || d.curves.length === 0 },
      { a: 'cursorMode', t: d.cursorMode === 'band' ? '자유 커서 A/B로' : '밴드 커서로', on: d.cursorMode === 'band' },
      { a: 'link', t: 'X축 링크', on: d.linkX, dis: !d.linkKey && d.detailOf == null },
      { a: 'table', t: '+ TABLE (이 차트의 채널)', dis: d.curves.length === 0 },
      { a: 'zoomfull', t: '전체 보기 (더블클릭)' }, { a: 'zoomback', t: '줌 되돌리기 (Backspace)', dis: d.zoomHistory === 0 },
      { a: 'csv', t: band ? '밴드 구간 → CSV…' : '전체 → CSV…', dis: d.curves.length === 0 }, { a: 'tdms', t: band ? '밴드 구간 → TDMS…' : '전체 → TDMS…', dis: d.curves.length === 0 },
      { a: 'pngcopy', t: '차트 PNG 복사' }, { a: 'pngsave', t: '차트 PNG 저장…' },
      WIN === 0 ? { a: 'detach', t: '새 창으로 (그립을 창 밖으로 끌어도 됨)' } : { a: 'return', t: '주 창으로' },
    ];
    this.menu = h(`<div class="menu" role="menu">${items.map(i => `<button role="menuitem" data-m="${i.a}"${i.dis ? ' disabled' : ''}>${i.on ? I.check() : '<span class="sp"></span>'}${esc(i.t)}</button>`).join('')}</div>`);
    const r = anchor.getBoundingClientRect(); const hr = this.el.getBoundingClientRect();
    Object.assign(this.menu.style, { top: (r.bottom - hr.top + 4) + 'px', right: (hr.right - r.right) + 'px' });
    this.menu.addEventListener('click', e => { const a = (e.target as HTMLElement).closest('[data-m]') as HTMLElement | null; if (!a || (a as HTMLButtonElement).disabled) return; this.closeMenu(); this.menuAction(a.dataset.m!); });
    this.el.append(this.menu);
    setTimeout(() => document.addEventListener('pointerdown', ev => { if (!this.menu?.contains(ev.target as Node)) this.closeMenu(); }, { once: true, capture: true }), 0);
  }
  menuAction(a: string) {
    const d = this.dto; const id = d.id; const chans = d.curves.map(c => c.channelId);
    const range = this.band ? { x0: Math.min(this.band.x0, this.band.x1), x1: Math.max(this.band.x0, this.band.x1) } : {};
    switch (a) {
      case 'detail': bridge.call('linkDetail', { chart: id }); break;
      case 'cursorMode': bridge.call('setCursorMode', { chart: id, mode: d.cursorMode === 'band' ? 'free' : 'band' }); break;
      case 'link': bridge.call('setLinkX', { chart: id, on: !d.linkX }); break;
      case 'table': bridge.call('table.fromChart', { chart: id }); break;
      case 'zoomfull': bridge.call('zoomFull', { chart: id }); break;
      case 'zoomback': bridge.call('zoomBack', { chart: id }); break;
      case 'csv': bridge.call('export.range', { kind: 'csv', channels: chans, chart: id, ...range }); break;
      case 'tdms': bridge.call('export.range', { kind: 'tdms', channels: chans, chart: id, ...range }); break;
      case 'pngcopy': bridge.call('png.copy', { dataUrl: this.pngDataUrl() }); break;
      case 'pngsave': bridge.call('png.save', { dataUrl: this.pngDataUrl(), name: (d.title || 'chart') + '.png' }); break;
      case 'detach': bridge.call('detachChart', { chart: id }); break;
      case 'return': bridge.call('returnChart', { chart: id }); break;
    }
  }
  pngDataUrl() { return this.canvas.toDataURL('image/png'); }

  // ------------------------------------------------------------------ drag & drop
  private bindDrop() {
    const show = (zone: string, text: string) => {
      const r = this.plotRect; const s = this.dropfx.style; s.display = 'block';
      if (zone === 'left') Object.assign(s, { left: '2px', top: r.y + 'px', width: (r.x - 4) + 'px', height: r.h + 'px' });
      else if (zone === 'right') Object.assign(s, { left: (r.x + r.w + 2) + 'px', top: r.y + 'px', width: (this.cssW - r.x - r.w - 4) + 'px', height: r.h + 'px' });
      else if (zone === 'x') Object.assign(s, { left: r.x + 'px', top: (r.y + r.h + 2) + 'px', width: r.w + 'px', height: (this.cssH - r.y - r.h - 4) + 'px' });
      else Object.assign(s, { left: (r.x + 2) + 'px', top: (r.y + 2) + 'px', width: (r.w - 4) + 'px', height: (r.h - 4) + 'px' });
      (this.dropfx.firstElementChild as HTMLElement).textContent = text;
    };
    const names = (e: DragEvent) => { try { const d = JSON.parse(e.dataTransfer?.getData(DRAG_CH) || '[]'); return d as { id: string; name: string; time?: boolean }[]; } catch { return []; } };
    this.box.addEventListener('dragover', e => {
      const dt = e.dataTransfer; if (!dt) return;
      const types = Array.from(dt.types);
      if (types.includes(DRAG_CH)) {
        e.preventDefault(); dt.dropEffect = 'copy';
        const z = this.zoneAt(e.offsetX, e.offsetY);
        const chans = names(e);
        const nm = chans.map(c => c.name).join(', ');
        const text = this.dto.detailOf != null ? (chans.length === 1 ? `Show ${chans[0].name} in this detail chart` : 'Drop one channel to show it here')
          : z === 'x' ? (chans.length === 1 ? `Use ${chans[0].name} as X axis` : 'Drop one channel to use it as X') : z === 'left' ? `Add to left axis` : z === 'right' ? `Add to right axis` : (chans.length === 2 && types.includes('application/x-nexys-xy') && this.dto.curves.length === 0 ? `XY: ${chans[0].name} → ${chans[1].name}` : chans.length > 1 ? `Add ${chans.length} curves` : `Add ${nm}`);
        show(this.dto.detailOf != null ? 'plot' : z, text);
      } else if (types.includes(DRAG_CURVE)) { e.preventDefault(); dt.dropEffect = 'move'; show('plot', 'Move curve here'); }
    });
    this.box.addEventListener('dragleave', () => { this.dropfx.style.display = 'none'; });
    this.box.addEventListener('drop', e => {
      this.dropfx.style.display = 'none'; const dt = e.dataTransfer; if (!dt) return;
      const types = Array.from(dt.types);
      if (types.includes(DRAG_CH)) {
        e.preventDefault(); e.stopPropagation();
        const chans = names(e); if (chans.length === 0) return;
        const ids = chans.map(c => c.id);
        if (this.dto.detailOf != null) { bridge.call('setDetailChannel', { chart: this.dto.id, channel: ids[0] }); this.onActivate(); return; }
        const z = this.zoneAt(e.offsetX, e.offsetY);
        if (z === 'x') { if (ids.length === 1) bridge.call('setX', { chart: this.dto.id, channel: ids[0] }); return; }
        const xy = chans.length === 2 && types.includes('application/x-nexys-xy') && this.dto.curves.length === 0;
        bridge.call('addCurves', { chart: this.dto.id, channels: ids, zone: xy ? 'xy' : z });
        this.onActivate();
      } else if (types.includes(DRAG_CURVE)) {
        e.preventDefault(); e.stopPropagation();
        try { const p = JSON.parse(dt.getData(DRAG_CURVE)); if (p.chart !== this.dto.id) bridge.call('moveCurve', { from: p.chart, idx: p.idx, to: this.dto.id }); } catch { }
      }
    });
    // legend / header actions
    this.head.addEventListener('click', e => {
      const t = e.target as HTMLElement;
      const leg = t.closest('[data-leg]') as HTMLElement | null;
      if (leg) {
        const idx = +leg.dataset.leg!; const c = this.dto.curves[idx];
        if (this.dto.hasDetail && !e.shiftKey) { bridge.call('select', { channel: c.channelId }); bridge.call('linkDetail', { chart: this.dto.id, channel: c.channelId }); }
        else bridge.call('setCurve', { chart: this.dto.id, idx, visible: !c.visible });
        return;
      }
      const act = (t.closest('[data-act]') as HTMLElement | null)?.dataset.act;
      if (act === 'close') bridge.call('removeChart', { chart: this.dto.id });
      else if (act === 'useX' && this.dto.suggestedX) bridge.call('setX', { chart: this.dto.id, channel: this.dto.suggestedX.id });
      else if (act === 'cycleTime') { const next = this.dto.xTimeMode === 'relative' ? 'absolute' : this.dto.xTimeMode === 'absolute' ? 'none' : 'relative'; bridge.call('setChart', { chart: this.dto.id, timeMode: next }); }
      else if (act === 'cursorMode') bridge.call('setCursorMode', { chart: this.dto.id, mode: this.dto.cursorMode === 'band' ? 'free' : 'band' });
      else if (act === 'link') bridge.call('setLinkX', { chart: this.dto.id, on: !this.dto.linkX });
      else if (act === 'menu') { this.openMenu(t.closest('[data-act]') as HTMLElement); return; }
      this.onActivate();
    });
    this.head.addEventListener('dragstart', e => {
      const t = e.target as HTMLElement;
      const leg = t.closest('[data-leg]') as HTMLElement | null;
      if (leg) { e.dataTransfer!.setData(DRAG_CURVE, JSON.stringify({ chart: this.dto.id, idx: +leg.dataset.leg! })); e.dataTransfer!.effectAllowed = 'move'; return; }
      if (t.closest('.grip')) { e.dataTransfer!.setData(DRAG_CHART, String(this.dto.id)); e.dataTransfer!.effectAllowed = 'move'; this.el.classList.add('dragging'); }
    });
    this.head.addEventListener('dragend', e => {
      this.el.classList.remove('dragging');
      // grip dropped outside this window (no target took it) → open a chart window at the pointer's screen position
      if (WIN === 0 && (e.target as HTMLElement).closest('.grip') && e.dataTransfer?.dropEffect === 'none' && (e.clientX < 0 || e.clientY < 0 || e.clientX > window.innerWidth || e.clientY > window.innerHeight))
        bridge.call('detachChart', { chart: this.dto.id, x: e.screenX, y: e.screenY });
    });
    this.el.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes(DRAG_CHART)) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; this.el.classList.add('over'); } });
    this.el.addEventListener('dragleave', () => { this.el.classList.remove('over'); });
    this.el.addEventListener('drop', e => {
      this.el.classList.remove('over');
      const dt = e.dataTransfer; if (!dt || !dt.types.includes(DRAG_CHART)) return;
      e.preventDefault(); const other = +dt.getData(DRAG_CHART); if (other && other !== this.dto.id) bridge.call('swapCharts', { a: other, b: this.dto.id });
    });
    this.el.addEventListener('pointerdown', () => this.onActivate(), { capture: true });
  }

  // ------------------------------------------------------------------ test helpers (real DOM events)
  /** Right-button drag over the plot: fractions of the plot rect; mods: shift = x only, ctrl = y only. */
  async rightDrag(fx0: number, fy0: number, fx1: number, fy1: number, mods: { shift?: boolean; ctrl?: boolean } = {}) {
    const cv = this.canvas; const rr = cv.getBoundingClientRect(); const r = this.plotRect;
    const o = { bubbles: true, pointerId: 2, pointerType: 'mouse', isPrimary: true, button: 2, buttons: 2, shiftKey: !!mods.shift, ctrlKey: !!mods.ctrl } as any;
    const P = (fx: number, fy: number) => ({ clientX: rr.left + r.x + r.w * fx, clientY: rr.top + r.y + r.h * fy });
    cv.dispatchEvent(new PointerEvent('pointerdown', { ...o, ...P(fx0, fy0) }));
    for (let i = 1; i <= 4; i++) cv.dispatchEvent(new PointerEvent('pointermove', { ...o, ...P(fx0 + (fx1 - fx0) * i / 4, fy0 + (fy1 - fy0) * i / 4) }));
    cv.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0, ...P(fx1, fy1) }));
    await new Promise(res => setTimeout(res, 60));
  }
  /** Left-button drag starting at a plot fraction (band handles / inside / pan). */
  async leftDrag(fx0: number, fx1: number, steps = 4) {
    const cv = this.canvas; const rr = cv.getBoundingClientRect(); const r = this.plotRect;
    const o = { bubbles: true, pointerId: 1, pointerType: 'mouse', isPrimary: true, button: 0, buttons: 1 } as any; const y = rr.top + r.y + r.h / 2;
    cv.dispatchEvent(new PointerEvent('pointerdown', { ...o, clientX: rr.left + r.x + r.w * fx0, clientY: y }));
    for (let i = 1; i <= steps; i++) { cv.dispatchEvent(new PointerEvent('pointermove', { ...o, clientX: rr.left + r.x + r.w * (fx0 + (fx1 - fx0) * i / steps), clientY: y })); await new Promise(res => setTimeout(res, 8)); }
    cv.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0, clientX: rr.left + r.x + r.w * fx1, clientY: y }));
    await new Promise(res => setTimeout(res, 60));
  }
  get bandLocal() { return this.band; }
  /** Live band drag (pointer inside the band), one move per step; returns per-step frame times of the measured chart. */
  async bandDragLive(pxPerStep: number, n: number, measure: PlotView) {
    if (!this.band) return [];
    const cv = this.canvas; const rr = cv.getBoundingClientRect(); const r = this.plotRect;
    const o = { bubbles: true, pointerId: 1, pointerType: 'mouse', isPrimary: true, button: 0, buttons: 1 } as any; const y = rr.top + r.y + r.h / 2;
    let x = rr.left + this.xToPx((this.band.x0 + this.band.x1) / 2); const out: number[] = [];
    cv.dispatchEvent(new PointerEvent('pointerdown', { ...o, clientX: x, clientY: y }));
    x += 4; cv.dispatchEvent(new PointerEvent('pointermove', { ...o, clientX: x, clientY: y }));   // past the 3 px move threshold
    for (let i = 0; i < n; i++) {
      const before = measure.frames; x += pxPerStep; const t0 = performance.now();
      cv.dispatchEvent(new PointerEvent('pointermove', { ...o, clientX: x, clientY: y }));
      for (let k = 0; k < 300; k++) { if (measure.frames > before && !measure.busy) break; await new Promise(res => setTimeout(res, 2)); }
      out.push(performance.now() - t0);
    }
    cv.dispatchEvent(new PointerEvent('pointerup', { ...o, buttons: 0, clientX: x, clientY: y }));
    await new Promise(res => setTimeout(res, 60));
    return out;
  }
}
