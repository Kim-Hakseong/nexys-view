export const h = (html: string): HTMLElement => { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild as HTMLElement; };
export const esc = (s: any) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
export const fmtInt = (n: number) => Math.round(n).toLocaleString('en-US');
export const fmtCompact = (n: number) => n >= 1e6 ? (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M' : n >= 1e4 ? (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'k' : fmtInt(n);
/** Ticks.FormatValue port: `sig` significant digits, scientific outside 1e-4..1e9. */
export function fmtValue(v: number | null | undefined, sig = 6): string {
  if (v == null || Number.isNaN(v)) return '—';
  const a = Math.abs(v);
  if (a === 0) return '0';
  if (a >= 1e9 || a < 1e-4) return v.toExponential(4).replace(/\.?0+e/, 'e').replace('e+', 'e');
  const dec = Math.max(0, sig - 1 - Math.floor(Math.log10(a)));
  return v.toFixed(Math.min(dec, 9));
}
/** Compact value for readouts: ≤ 2 decimals when |v| ≥ 100, else 4 significant. */
export function fmtRead(v: number | null | undefined): string {
  if (v == null || Number.isNaN(v)) return '—';
  const a = Math.abs(v);
  if (a === 0) return '0.00';
  if (a >= 1e6 || a < 1e-3) return v.toExponential(3);
  if (a >= 100) return v.toFixed(2);
  if (a >= 10) return v.toFixed(2);
  return v.toFixed(a >= 1 ? 3 : 4);
}
export const fmtSigned = (v: number | null | undefined) => v == null || Number.isNaN(v) ? '—' : (v >= 0 ? '+' : '') + fmtRead(v);
export const fmtBytes = (b: number) => b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : (b / 1024).toFixed(0) + ' KB';
export const fmtSec = (s: number) => s >= 3600 ? (s / 3600).toFixed(2) + ' h' : s >= 60 ? (s / 60).toFixed(1) + ' min' : s.toFixed(1) + ' s';
export function fmtRel(seconds: number, dec = 3): string {
  const neg = seconds < 0; seconds = Math.abs(seconds);
  const whole = Math.floor(seconds), frac = seconds - whole;
  const hh = Math.floor(whole / 3600), mm = Math.floor((whole % 3600) / 60), ss = whole % 60;
  const core = (hh > 0 ? String(hh).padStart(2, '0') + ':' : '') + String(mm).padStart(2, '0') + ':' + String(ss).padStart(2, '0') + (dec > 0 ? frac.toFixed(dec).slice(1) : '');
  return (neg ? '-' : '') + core;
}
export const PALETTE_DARK = ['#FFD400', '#00D1FF', '#4CFF4C', '#FF5CE1', '#FF8A00', '#8FA8FF', '#FF5A5A', '#B6FF00'];
export const PALETTE_LIGHT = ['#1F77B4', '#E8601C', '#2CA02C', '#9467BD', '#17BECF', '#D62728', '#8C564B', '#BCBD22'];
export function curveColor(index: number, plot: string): string {
  const pal = plot === 'light' ? PALETTE_LIGHT : PALETTE_DARK;
  const base = pal[index % pal.length];
  const round = Math.floor(index / pal.length);
  if (round === 0) return base;
  // lightness variants from the 9th curve (CurvePalette.Get)
  const r = parseInt(base.slice(1, 3), 16) / 255, g = parseInt(base.slice(3, 5), 16) / 255, b = parseInt(base.slice(5, 7), 16) / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b); let hh = 0, s = 0; const l = (max + min) / 2;
  if (max !== min) { const d = max - min; s = l > 0.5 ? d / (2 - max - min) : d / (max + min); hh = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4; hh /= 6; }
  const nl = (round % 2 === 1 ? Math.min(85, l * 100 + 18) : Math.max(25, l * 100 - 18)) / 100;
  return `hsl(${Math.round(hh * 360)} ${Math.round(s * 100)}% ${Math.round(nl * 100)}%)`;
}
export const debounce = <T extends (...a: any[]) => void>(fn: T, ms: number) => { let t = 0; return (...a: Parameters<T>) => { clearTimeout(t); t = window.setTimeout(() => fn(...a), ms); }; };
/** Dot sparkline (56×14) from ≤14 values: 14 dots, the series in `color`, flat/NaN dots in border color. */
export function sparkSvg(vals: number[] | undefined, color: string, w = 56, hgt = 14): string {
  if (!vals || vals.length === 0) return '';
  const fin = vals.filter(v => Number.isFinite(v)); if (fin.length === 0) return '';
  const lo = Math.min(...fin), hi = Math.max(...fin); const n = vals.length;
  let s = `<svg aria-hidden="true" width="${w}" height="${hgt}" viewBox="0 0 ${w} ${hgt}">`;
  for (let i = 0; i < n; i++) {
    const v = vals[i]; const cx = (w / n) * (i + 0.5);
    if (!Number.isFinite(v)) { s += `<circle cx="${cx.toFixed(1)}" cy="7" r="1.4" fill="#B9B5AC"></circle>`; continue; }
    const cy = hi > lo ? 12 - ((v - lo) / (hi - lo)) * 10 : 7;
    s += `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="1.6" fill="${color}"></circle>`;
  }
  return s + '</svg>';
}
/** Dot range bar (244×16, 42 dots): band dots in --band, current dot accent2 r 3.2. */
export function rangeBarSvg(min: number, max: number, cur: number | null, band: number[] | null, w = 244): string {
  const n = 42, pad = 6, step = (w - 2 * pad) / (n - 1);
  const t = (v: number) => max > min ? Math.max(0, Math.min(1, (v - min) / (max - min))) : 0.5;
  const ci = cur == null || Number.isNaN(cur) ? -1 : Math.round(t(cur) * (n - 1));
  const b0 = band ? Math.round(t(band[0]) * (n - 1)) : -1, b1 = band ? Math.round(t(band[1]) * (n - 1)) : -2;
  let s = `<svg aria-hidden="true" width="${w}" height="16" viewBox="0 0 ${w} 16">`;
  for (let i = 0; i < n; i++) {
    const cx = (pad + i * step).toFixed(1);
    if (i === ci) s += `<circle cx="${cx}" cy="8" r="3.2" fill="#E8601C"></circle>`;
    else s += `<circle cx="${cx}" cy="8" r="1.8" fill="${i >= b0 && i <= b1 ? 'var(--band)' : '#B9B5AC'}"></circle>`;
  }
  return s + '</svg>';
}
