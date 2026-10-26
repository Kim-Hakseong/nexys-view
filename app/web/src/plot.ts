// Chart card: header (grip · title · meta · X pill · time pill · legend · close), cursor table, canvas plot.
// The canvas draws frames served by the engine (shared Float32 buffer + JSON header); gestures run locally and
// request new frames (latest-wins), the committed view goes to the engine for undo.
import { bridge } from './bridge';
import { I } from './icons';
import { ChartDto, CurveHdr, FrameHdr, State } from './types';
import { curveColor, esc, fmtRead, fmtSigned, h } from './util';

export const DRAG_CH = 'application/x-nexys-channels';
export const DRAG_CHART = 'application/x-nexys-chart';
export const DRAG_CURVE = 'application/x-nexys-curve';

export interface FrameTiming { seq: number; t0: number; t1: number; engineMs: number; }

export class PlotView {
  el: HTMLElement; private head: HTMLElement; private curTbl: HTMLElement; private box: HTMLElement; private canvas: HTMLCanvasElement; private ctx: CanvasRenderingContext2D;
  private hint: HTMLElement; private dropfx: HTMLElement;
  dto: ChartDto; plot = 'dark';
  private seq = 0; private inFlight = false; private pendingReq: any = null; private lastHdr: FrameHdr | null = null; private drawnSeq = -1;
  private sentAt = new Map<number, number>();
  timings: FrameTiming[] = []; frames = 0; lastEngineMs = 0; lastTotalMs = 0; lastStats: FrameHdr['stats'] | null = null;
  // local view during gestures
  private xmin = 0; private xmax = 1; private gestureFrom: [number, number] | null = null;
  private L = 56; private R = 16; private T = 16; private B = 50; private laneN = 0;
  private dpr = 1; private cssW = 0; private cssH = 0;
  private ro: ResizeObserver;
  private area: HTMLElement; private areaStart: [number, number] | null = null;
  onActivate: () => void = () => {};
  settleWaiters: (() => void)[] = [];

  constructor(dto: ChartDto, plot: string) {
    this.dto = dto; this.plot = plot; this.xmin = dto.xmin; this.xmax = dto.xmax;
    this.el = h(`<section class="card chart" data-chart="${dto.id}"></section>`);
    this.head = h(`<div class="chart-h"></div>`);
    this.curTbl = h(`<div class="cur-table" style="display:none"></div>`);
    const wrap = h(`<div class="plot-wrap"></div>`);
    this.box = h(`<div class="plot-box"></div>`);
    this.canvas = document.createElement('canvas');
    this.ctx = this.canvas.getContext('2d', { alpha: false })!;
    this.hint = h(`<div class="hint">Drop a channel here</div>`);
    this.dropfx = h(`<div class="dropfx"><span></span></div>`);
    this.area = h(`<div style="position:absolute;display:none;border:1px dashed #E8601C;background:rgba(232,96,28,0.08);pointer-events:none"></div>`);
    this.box.append(this.canvas, this.hint, this.dropfx, this.area); wrap.append(this.box);
    this.el.append(this.head, this.curTbl, wrap);
    this.ro = new ResizeObserver(() => this.onResize());
    this.ro.observe(this.box);
    this.bindPointer(); this.bindDrop();
    bridge.on('frame', this.onFrame);
    this.renderHead();
  }

  dispose() { this.ro.disconnect(); bridge.off('frame', this.onFrame); }

  update(dto: ChartDto, plot: string) {
    const replot = plot !== this.plot;
    this.dto = dto; this.plot = plot;
    if (!this.gestureFrom) { this.xmin = dto.xmin; this.xmax = dto.xmax; }
    this.renderHead();
    if (replot) this.request();
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
    const legend = d.curves.filter(c => !c.lane).map(c => `<span class="leg${c.visible ? '' : ' off'}" data-leg="${c.idx}" draggable="true" title="Click: show/hide · drag to another chart"><span class="swatch" style="background:${curveColor(c.color, this.plot)}"></span>${esc(c.name)}</span>`).join('');
    this.head.innerHTML = `<span class="grip" draggable="true" title="Drag to reorder / swap">${I.grip()}</span><span class="title">${esc(d.title || 'Chart ' + idx)}</span><span class="meta">${meta}</span>${pills}<div class="legend">${legend}</div><button class="xbtn" aria-label="Close chart" data-act="close">${I.close()}</button>`;
    this.hint.style.display = d.curves.length === 0 ? 'flex' : 'none';
    this.renderCursorTable();
  }

  private renderCursorTable() {
    const hd = this.lastHdr, d = this.dto;
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

  private onResize() {
    const rect = this.box.getBoundingClientRect();
    this.cssW = Math.max(10, Math.round(rect.width)); this.cssH = Math.max(10, Math.round(rect.height));
    this.dpr = window.devicePixelRatio || 1;
    this.canvas.width = Math.round(this.cssW * this.dpr); this.canvas.height = Math.round(this.cssH * this.dpr);
    if (this.lastHdr) this.draw(this.lastHdr);
    this.request();
  }

  // ------------------------------------------------------------------ frames
  request(opts: { commit?: boolean } = {}) {
    if (this.cssW <= 10) return;
    this.layoutFor(this.lastHdr);
    const r = this.plotRect;
    const req: any = { cmd: 'frame', chart: this.dto.id, w: Math.max(1, Math.round(r.w * this.dpr)), h: r.h, dpr: this.dpr, xmin: this.xmin, xmax: this.xmax, commit: !!opts.commit };
    if (opts.commit && this.gestureFrom) { req.fromX0 = this.gestureFrom[0]; req.fromX1 = this.gestureFrom[1]; }
    if (this.inFlight) { this.pendingReq = req; return; }
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
      this.draw(this.lastHdr);
      const t1 = performance.now();
      this.frames++; this.lastEngineMs = this.lastHdr.stats.ms; this.lastTotalMs = t1 - t0; this.lastStats = this.lastHdr.stats;
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
    // a frame carries the engine's x window (equals ours unless a gesture moved on); draw in the frame's window
    const fx0 = hd.xmin, fx1 = hd.xmax;
    this.layoutFor(hd);
    const r = this.plotRect; const W = this.cssW, H = this.cssH;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = C.bg; ctx.fillRect(0, 0, W, H);
    const X = (x: number) => r.x + (x - fx0) / (fx1 - fx0) * r.w;
    const axisOf = (id?: string) => hd.axes.find(a => a.id === id) ?? hd.axes[0];
    const Y = (a: { min: number; max: number }, v: number) => r.y + (a.max - v) / (a.max - a.min) * r.h;
    ctx.font = '10px "JetBrains Mono", monospace'; ctx.textBaseline = 'alphabetic';
    // grid
    const xt = hd.xticks.map(t => ({ px: X(t.v), label: t.label })).filter(t => t.px >= r.x - 0.5 && t.px <= r.x + r.w + 0.5);
    const prim = hd.axes[0]; const yt = prim ? prim.ticks.map(t => ({ py: Y(prim, t.v), label: t.label })).filter(t => t.py >= r.y - 0.5 && t.py <= r.y + r.h + 0.5) : [];
    if (this.dto.grid) {
      if (light && xt.length > 1 && yt.length > 1) {
        // dot grid (DESIGN-v3 light plot): dots at every 1/5 of the tick spacing
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
      for (const t of a.ticks) { const y = Y(a, t.v); if (y < r.y - 0.5 || y > r.y + r.h + 0.5) continue; ctx.fillText(t.label, a.right ? ax + 8 : ax - 8, y + 3); }
      if (a.unit) { ctx.font = '10px Inter, sans-serif'; ctx.fillStyle = C.axis; ctx.fillText(a.unit, a.right ? ax + 8 : ax - 8, r.y - 4); }
    }
    // curves (clipped)
    ctx.save(); ctx.beginPath(); ctx.rect(r.x, r.y, r.w, r.h); ctx.clip();
    let first = true;
    for (const c of hd.curves) {
      if (c.mode === 'lane' || !buf) continue;
      const col = curveColor(c.color, this.plot);
      const a = axisOf(c.axis);
      if (c.mode === 'env') {
        // one vertical segment per device column from min to max: translucent band + solid 1px end points (v2 envelope look),
        // so overlapping noisy curves stay distinguishable
        const n = c.n, colW = r.w / n; const lw = 1 / dpr;
        ctx.strokeStyle = col; ctx.lineWidth = lw; ctx.globalAlpha = 0.38; ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const mn = buf[c.off + i]; if (Number.isNaN(mn)) continue; const mx = buf[c.off + n + i];
          const x = r.x + (i + 0.5) * colW; let y0 = Y(a, mx), y1 = Y(a, mn); if (y1 - y0 < lw) { y0 -= lw / 2; y1 += lw / 2; }
          ctx.moveTo(x, y0); ctx.lineTo(x, y1);
        }
        ctx.stroke(); ctx.globalAlpha = 1; ctx.beginPath();
        for (let i = 0; i < n; i++) {
          const mn = buf[c.off + i]; if (Number.isNaN(mn)) continue; const mx = buf[c.off + n + i];
          const x = r.x + (i + 0.5) * colW, y0 = Y(a, mx), y1 = Y(a, mn);
          ctx.moveTo(x, y0 - lw / 2); ctx.lineTo(x, y0 + lw / 2); ctx.moveTo(x, y1 - lw / 2); ctx.lineTo(x, y1 + lw / 2);
        }
        ctx.stroke();
      } else if (c.mode === 'raw') {
        ctx.strokeStyle = col; ctx.lineWidth = first ? 1.4 : 1.2; ctx.lineJoin = 'round'; ctx.beginPath();
        let started = false, py = 0;
        for (let i = 0; i < c.n; i++) {
          const v = buf[c.off + c.n + i]; if (Number.isNaN(v)) { started = false; continue; }
          const x = r.x + buf[c.off + i] * r.w, y = Y(a, v);
          if (!started) { ctx.moveTo(x, y); started = true; } else if (c.step) { ctx.lineTo(x, py); ctx.lineTo(x, y); } else ctx.lineTo(x, y);
          py = y;
        }
        ctx.stroke();
        if (c.n < r.w / 4) { ctx.fillStyle = col; for (let i = 0; i < c.n; i++) { const v = buf[c.off + c.n + i]; if (Number.isNaN(v)) continue; ctx.fillRect(r.x + buf[c.off + i] * r.w - 1.5, Y(a, v) - 1.5, 3, 3); } }
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

  // ------------------------------------------------------------------ gestures
  private bindPointer() {
    const cv = this.canvas;
    let drag: { x0: number; px0: number; vx0: number; vx1: number; moved: boolean; shift: boolean } | null = null;
    cv.addEventListener('pointerdown', e => {
      if (e.button !== 0) return;
      this.onActivate();
      const px = e.offsetX;
      drag = { x0: px, px0: px, vx0: this.xmin, vx1: this.xmax, moved: false, shift: e.shiftKey };
      this.gestureFrom = [this.xmin, this.xmax];
      cv.setPointerCapture(e.pointerId);
    });
    cv.addEventListener('pointermove', e => {
      if (!drag) { const z = this.zoneAt(e.offsetX, e.offsetY); cv.style.cursor = z === 'plot' ? 'crosshair' : 'default'; return; }
      const px = e.offsetX;
      if (Math.abs(px - drag.x0) > 3) drag.moved = true;
      if (!drag.moved) return;
      if (drag.shift) {
        const r = this.plotRect; const a = Math.min(px, drag.x0), b = Math.max(px, drag.x0);
        Object.assign(this.area.style, { display: 'block', left: a + 'px', top: r.y + 'px', width: (b - a) + 'px', height: r.h + 'px' });
        return;
      }
      const r = this.plotRect; const dx = (px - drag.x0) / r.w * (drag.vx1 - drag.vx0);
      this.xmin = drag.vx0 - dx; this.xmax = drag.vx1 - dx; cv.style.cursor = 'grabbing';
      this.request();
    });
    const up = (e: PointerEvent) => {
      if (!drag) return; const d = drag; drag = null; cv.style.cursor = '';
      if (d.shift && d.moved) {
        this.area.style.display = 'none';
        const a = this.pxToX(Math.min(e.offsetX, d.x0)), b = this.pxToX(Math.max(e.offsetX, d.x0)); if (b - a > 0) { this.xmin = a; this.xmax = b; this.request({ commit: true }); }
        this.gestureFrom = null; return;
      }
      this.area.style.display = 'none';
      if (d.moved) { this.request({ commit: true }); this.gestureFrom = null; return; }
      this.gestureFrom = null;
      if (this.dto.curves.length === 0) return;
      const z = this.zoneAt(e.offsetX, e.offsetY); if (z !== 'plot') return;
      bridge.call('setCursor', { chart: this.dto.id, which: e.shiftKey ? 'b' : 'a', x: this.pxToX(e.offsetX) });
    };
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointercancel', () => { drag = null; this.gestureFrom = null; this.area.style.display = 'none'; });
    cv.addEventListener('dblclick', () => { this.onActivate(); bridge.call('zoomFull', { chart: this.dto.id }); });
    cv.addEventListener('contextmenu', e => { e.preventDefault(); bridge.call('clearCursors', { chart: this.dto.id }); });
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

  zoneAt(px: number, py: number): 'plot' | 'left' | 'right' | 'x' {
    const r = this.plotRect;
    if (py > r.y + r.h) return 'x';
    if (px < r.x) return 'left';
    if (px > r.x + r.w) return 'right';
    return 'plot';
  }

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
        const text = z === 'x' ? (chans.length === 1 ? `Use ${chans[0].name} as X axis` : 'Drop one channel to use it as X') : z === 'left' ? `Add to left axis` : z === 'right' ? `Add to right axis` : (chans.length === 2 && types.includes('application/x-nexys-xy') && this.dto.curves.length === 0 ? `XY: ${chans[0].name} → ${chans[1].name}` : (this.dto.curves.length === 0 ? 'New chart: ' : 'Add ') + nm);
        show(z, text);
      } else if (types.includes(DRAG_CURVE)) { e.preventDefault(); dt.dropEffect = 'move'; show('plot', 'Move curve here'); }
    });
    this.box.addEventListener('dragleave', () => { this.dropfx.style.display = 'none'; });
    this.box.addEventListener('drop', e => {
      this.dropfx.style.display = 'none'; const dt = e.dataTransfer; if (!dt) return;
      const types = Array.from(dt.types);
      if (types.includes(DRAG_CH)) {
        e.preventDefault(); e.stopPropagation();
        const chans = names(e); if (chans.length === 0) return;
        const z = this.zoneAt(e.offsetX, e.offsetY);
        const ids = chans.map(c => c.id);
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
      if (leg) { const idx = +leg.dataset.leg!; const c = this.dto.curves[idx]; bridge.call('setCurve', { chart: this.dto.id, idx, visible: !c.visible }); return; }
      const act = (t.closest('[data-act]') as HTMLElement | null)?.dataset.act;
      if (act === 'close') bridge.call('removeChart', { chart: this.dto.id });
      else if (act === 'useX' && this.dto.suggestedX) bridge.call('setX', { chart: this.dto.id, channel: this.dto.suggestedX.id });
      else if (act === 'cycleTime') { const next = this.dto.xTimeMode === 'relative' ? 'absolute' : this.dto.xTimeMode === 'absolute' ? 'none' : 'relative'; bridge.call('setChart', { chart: this.dto.id, timeMode: next }); }
      this.onActivate();
    });
    this.head.addEventListener('dragstart', e => {
      const t = e.target as HTMLElement;
      const leg = t.closest('[data-leg]') as HTMLElement | null;
      if (leg) { e.dataTransfer!.setData(DRAG_CURVE, JSON.stringify({ chart: this.dto.id, idx: +leg.dataset.leg! })); e.dataTransfer!.effectAllowed = 'move'; return; }
      if (t.closest('.grip')) { e.dataTransfer!.setData(DRAG_CHART, String(this.dto.id)); e.dataTransfer!.effectAllowed = 'move'; this.el.classList.add('dragging'); }
    });
    this.head.addEventListener('dragend', () => this.el.classList.remove('dragging'));
    this.el.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes(DRAG_CHART)) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; this.el.style.borderColor = 'var(--accent2)'; } });
    this.el.addEventListener('dragleave', () => { this.el.style.borderColor = ''; });
    this.el.addEventListener('drop', e => {
      this.el.style.borderColor = '';
      const dt = e.dataTransfer; if (!dt || !dt.types.includes(DRAG_CHART)) return;
      e.preventDefault(); const other = +dt.getData(DRAG_CHART); if (other && other !== this.dto.id) bridge.call('swapCharts', { a: other, b: this.dto.id });
    });
    this.el.addEventListener('pointerdown', () => this.onActivate(), { capture: true });
  }
}
