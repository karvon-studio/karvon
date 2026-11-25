// punycode (RFC 3492).
'use strict';
const BASE = 36, TMIN = 1, TMAX = 26, SKEW = 38, DAMP = 700;
const points = s => Array.from(s, c => c.codePointAt(0));
function adapt(delta, n, first) { delta = first ? Math.floor(delta / DAMP) : delta >> 1; delta += Math.floor(delta / n); let k = 0; while (delta > (BASE - TMIN) * TMAX >> 1) { delta = Math.floor(delta / (BASE - TMIN)); k += BASE; } return k + Math.floor((BASE - TMIN + 1) * delta / (delta + SKEW)); }
const digit = d => String.fromCharCode(d < 26 ? d + 97 : d + 22);
function encode(text) {
  const input = points(text), basic = input.filter(c => c < 128);
  let out = basic.map(c => String.fromCharCode(c)).join(''), n = 128, delta = 0, bias = 72, h = basic.length;
  if (h) out += '-';
  while (h < input.length) {
    const m = Math.min(...input.filter(c => c >= n));
    delta += (m - n) * (h + 1); n = m;
    for (const c of input) {
      if (c < n) delta++;
      if (c === n) {
        let q = delta;
        for (let k = BASE; ; k += BASE) { const t = k <= bias ? TMIN : k >= bias + TMAX ? TMAX : k - bias; if (q < t) break; out += digit(t + (q - t) % (BASE - t)); q = Math.floor((q - t) / (BASE - t)); }
        out += digit(q); bias = adapt(delta, h + 1, h === basic.length); delta = 0; h++;
      }
    }
    delta++; n++;
  }
  return out;
}
function decode(text) {
  const at = text.lastIndexOf('-'), out = at > 0 ? points(text.slice(0, at)) : [];
  let n = 128, i = 0, bias = 72;
  for (let p = at > 0 ? at + 1 : 0; p < text.length;) {
    const old = i;
    for (let w = 1, k = BASE; ; k += BASE) {
      const c = text.charCodeAt(p++), d = c >= 48 && c <= 57 ? c - 22 : c >= 65 && c <= 90 ? c - 65 : c >= 97 && c <= 122 ? c - 97 : BASE;
      if (d >= BASE) throw new RangeError('Invalid input');
      i += d * w;
      const t = k <= bias ? TMIN : k >= bias + TMAX ? TMAX : k - bias;
      if (d < t) break;
      w *= BASE - t;
    }
    bias = adapt(i - old, out.length + 1, old === 0); n += Math.floor(i / (out.length + 1)); i %= out.length + 1;
    out.splice(i++, 0, n);
  }
  return String.fromCodePoint(...out);
}
module.exports = { encode, decode, toASCII: s => s.split('.').map(l => /[^\0-\x7f]/.test(l) ? 'xn--' + encode(l.toLowerCase()) : l).join('.'), toUnicode: s => s.split('.').map(l => /^xn--/i.test(l) ? decode(l.slice(4).toLowerCase()) : l).join('.'), ucs2: { decode: points, encode: a => String.fromCodePoint(...a) } };
