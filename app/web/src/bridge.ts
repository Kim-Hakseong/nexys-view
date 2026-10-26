// WebView2 bridge: JSON commands with replies, shared Float32 frame buffers, event fan-out.
// Outside WebView2 (vite dev server) a tiny mock answers with an empty state so the page renders.
declare global {
  interface Window { chrome?: any; nx: any; }
}

export type Listener = (msg: any) => void;

class Bridge {
  private id = 0;
  private pending = new Map<number, { res: (v: any) => void; rej: (e: any) => void }>();
  private listeners = new Map<string, Listener[]>();
  readonly buffers = new Map<number, Float32Array>();
  readonly hosted: boolean;
  constructor() {
    this.hosted = !!(window.chrome && window.chrome.webview);
    if (this.hosted) {
      window.chrome.webview.addEventListener('message', (e: any) => this.onMessage(e.data));
      window.chrome.webview.addEventListener('sharedbufferreceived', (e: any) => {
        const add = typeof e.additionalData === 'string' ? JSON.parse(e.additionalData) : (e.additionalData ?? {});   // WebView2 hands additionalData as a parsed object
        const buf: ArrayBuffer = e.getBuffer();
        this.buffers.set(add.chart, new Float32Array(buf));
        this.log(`buffer chart ${add.chart} ${buf.byteLength} bytes`);
        this.post({ cmd: 'bufferOk', chart: add.chart });
        this.emit('buffer', add);
      });
    }
  }
  on(ev: string, fn: Listener) { const l = this.listeners.get(ev) || []; l.push(fn); this.listeners.set(ev, l); }
  off(ev: string, fn: Listener) { const l = this.listeners.get(ev) || []; this.listeners.set(ev, l.filter(x => x !== fn)); }
  private emit(ev: string, msg: any) { for (const fn of this.listeners.get(ev) || []) { try { fn(msg); } catch (err) { console.error(err); } } }
  /** Inline frame payload (base64 Float32) when the shared buffer is not available. */
  inline = new Map<number, Float32Array>();
  decode(chart: number, b64: string): Float32Array {
    const bin = atob(b64); const n = bin.length; let u8 = this.inlineBytes; if (!u8 || u8.byteLength < n) { u8 = new Uint8Array(Math.max(n, 1 << 20)); this.inlineBytes = u8; }
    for (let i = 0; i < n; i++) u8[i] = bin.charCodeAt(i);
    const f = new Float32Array(u8.buffer, 0, n >> 2); this.inline.set(chart, f); return f;
  }
  private inlineBytes: Uint8Array | null = null;
  data(chart: number): Float32Array | undefined { return this.buffers.get(chart) ?? this.inline.get(chart); }
  private onMessage(msg: any) {
    if (!msg || typeof msg !== 'object') return;
    if (msg.ev === 'frame' && typeof msg.data === 'string') { this.decode(msg.chart, msg.data); msg.inline = true; }
    if (msg.ev === 'releaseBuffer') { const f = this.buffers.get(msg.chart); if (f) { try { window.chrome.webview.releaseBuffer(f.buffer); } catch { } this.buffers.delete(msg.chart); } this.inline.delete(msg.chart); return; }
    if (msg.ev === 'reply') {
      const p = this.pending.get(msg.id); if (!p) return; this.pending.delete(msg.id);
      if (msg.ok) p.res(msg.result); else p.rej(new Error(msg.result || 'command failed'));
      return;
    }
    this.emit(msg.ev, msg);
  }
  /** Fire-and-forget post (frames). */
  post(msg: any) { if (this.hosted) window.chrome.webview.postMessage(msg); }
  /** Command with reply. */
  call<T = any>(cmd: string, args: Record<string, any> = {}): Promise<T> {
    if (!this.hosted) return Promise.resolve(mock(cmd, args) as T);
    const id = ++this.id;
    return new Promise<T>((res, rej) => { this.pending.set(id, { res, rej }); window.chrome.webview.postMessage({ id, cmd, ...args }); });
  }
  /** Files dropped from the shell: WebView2 hands the host their paths through AdditionalObjects. */
  postFiles(files: FileList | File[]) {
    if (!this.hosted || !window.chrome.webview.postMessageWithAdditionalObjects) return;
    const arr = Array.from(files as any as File[]);
    window.chrome.webview.postMessageWithAdditionalObjects({ cmd: 'files' }, arr);
  }
  log(text: string) { if (this.hosted) this.post({ cmd: 'log', text }); else console.log(text); }
}

function mock(cmd: string, _args: any) {
  if (cmd === 'state') return { theme: 'light', plot: 'dark', mode: 'VIEW', status: { text: 'dev server — no engine', led: 'idle' }, undo: { can: false, canRedo: false }, files: [], charts: [], sparks: {}, tables: [], analysis: { functions: [], inputs: [], notes: [], results: [], params: {}, schema: [], validity: { ok: false, text: '' } }, report: { blocks: [], charts: [] }, cache: { ready: 0, total: 0, mb: 0 }, common: [], selectedFiles: [], align: { mode: 'start', offsetMs: 500 } };
  return null;
}

export const bridge = new Bridge();
/** 0.9.4: 0 = main window, N = detached chart window (index.html?win=N). */
export const WIN = Number(new URLSearchParams(location.search).get('win') || 0);
