// Structural probe shared by the app page and the mockup copies (work/webview/mockup/*.html): horizontal rules (1px top/bottom
// borders ≥150px wide), bordered boxes ≥150px tall, and the font histogram (size/weight/family of text leaves).
window.nxProbe = function () {
  const out = { fonts: {}, hlines: [], boxes: [], elems: [] };
  for (const el of document.querySelectorAll('body *')) {
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden') continue;
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    if (el.childElementCount === 0 && (el.textContent || '').trim() && el.tagName !== 'SCRIPT' && el.tagName !== 'STYLE') {
      const k = cs.fontSize + '/' + cs.fontWeight + '/' + cs.fontFamily.split(',')[0].replace(/"/g, '').trim();
      out.fonts[k] = (out.fonts[k] || 0) + 1;
    }
    const bt = parseFloat(cs.borderTopWidth), bb = parseFloat(cs.borderBottomWidth), bl = parseFloat(cs.borderLeftWidth), br = parseFloat(cs.borderRightWidth);
    if (bt >= 1 && cs.borderTopStyle !== 'none' && r.width >= 150) out.hlines.push([Math.round(r.top), 't', el.tagName.toLowerCase(), Math.round(r.width)]);
    if (bb >= 1 && cs.borderBottomStyle !== 'none' && r.width >= 150) out.hlines.push([Math.round(r.bottom - 1), 'b', el.tagName.toLowerCase(), Math.round(r.width)]);
    if (r.width >= 20) out.elems.push([Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height), el.tagName.toLowerCase(), (el.className && typeof el.className === 'string' ? el.className.split(' ')[0] : ''), (el.childElementCount === 0 ? (el.textContent || '').trim().slice(0, 18) : '')]);
    if (r.height >= 150 && r.width >= 150 && (bl >= 1 || br >= 1)) out.boxes.push([Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height), el.tagName.toLowerCase()]);
  }
  out.hlines.sort((a, b) => a[0] - b[0]);
  out.boxes.sort((a, b) => a[1] - b[1] || a[0] - b[0]);
  return out;
};
