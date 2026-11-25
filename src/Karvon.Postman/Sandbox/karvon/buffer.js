// Node's Buffer: what scripts use of it.
'use strict';
const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
function utf8(text) {
  const out = [];
  for (const ch of String(text)) {
    let c = ch.codePointAt(0);
    if (c >= 0xd800 && c <= 0xdfff) c = 0xfffd;
    if (c < 0x80) out.push(c);
    else if (c < 0x800) out.push(0xc0 | c >> 6, 0x80 | c & 63);
    else if (c < 0x10000) out.push(0xe0 | c >> 12, 0x80 | c >> 6 & 63, 0x80 | c & 63);
    else out.push(0xf0 | c >> 18, 0x80 | c >> 12 & 63, 0x80 | c >> 6 & 63, 0x80 | c & 63);
  }
  return out;
}
function text(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length;) {
    const b = bytes[i++], n = b < 0x80 ? 0 : b >= 0xf0 && b < 0xf8 ? 3 : b >= 0xe0 ? 2 : b >= 0xc2 ? 1 : -1;
    if (n === 0) { out += String.fromCharCode(b); continue; }
    let c = n < 0 ? -1 : b & (n === 1 ? 31 : n === 2 ? 15 : 7);
    for (let k = 0; c >= 0 && k < n; k++) { if ((bytes[i] & 0xc0) !== 0x80) c = -1; else c = c << 6 | bytes[i++] & 63; }
    out += c < 0 || c > 0x10ffff ? '�' : String.fromCodePoint(c);
  }
  return out;
}
function base64(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const n = bytes[i] << 16 | (bytes[i + 1] || 0) << 8 | (bytes[i + 2] || 0);
    out += ALPHABET[n >> 18] + ALPHABET[n >> 12 & 63] + (i + 1 < bytes.length ? ALPHABET[n >> 6 & 63] : '=') + (i + 2 < bytes.length ? ALPHABET[n & 63] : '=');
  }
  return out;
}
function unbase64(s) {
  const out = [];
  let n = 0, bits = 0;
  for (const ch of String(s).replace(/-/g, '+').replace(/_/g, '/').replace(/[^A-Za-z0-9+/]/g, '')) {
    n = (n << 6 | ALPHABET.indexOf(ch)) & 0xffffff; bits += 6;
    if (bits >= 8) { bits -= 8; out.push(n >> bits & 255); }
  }
  return out;
}
const encoded = (s, e) => {
  switch (String(e || 'utf8').toLowerCase()) {
    case 'hex': return (String(s).match(/../g) || []).map(h => parseInt(h, 16));
    case 'base64': case 'base64url': return unbase64(s);
    case 'latin1': case 'binary': case 'ascii': return Array.from(String(s), c => c.charCodeAt(0) & 255);
    case 'utf16le': case 'ucs2': return Array.from(String(s)).flatMap(c => [c.charCodeAt(0) & 255, c.charCodeAt(0) >> 8]);
    default: return utf8(s);
  }
};
class Buffer extends Uint8Array {
  static from(value, encoding) {
    const bytes = typeof value === 'string' ? encoded(value, encoding) : value instanceof ArrayBuffer ? new Uint8Array(value) : value && value.type === 'Buffer' ? value.data : Array.from(value);
    const b = new Buffer(bytes.length);
    b.set(bytes);
    return b;
  }
  static alloc(n, fill) { const b = new Buffer(n); if (fill !== undefined) b.fill(typeof fill === 'string' ? fill.charCodeAt(0) : fill); return b; }
  static allocUnsafe(n) { return new Buffer(n); }
  static isBuffer(v) { return v instanceof Buffer; }
  static byteLength(s, e) { return typeof s === 'string' ? encoded(s, e).length : s.byteLength; }
  static concat(list) { return Buffer.from([].concat(...list.map(b => Array.from(b)))); }
  toString(encoding, start, end) {
    const bytes = Array.from(this).slice(start, end);
    switch (String(encoding || 'utf8').toLowerCase()) {
      case 'hex': return bytes.map(b => b.toString(16).padStart(2, '0')).join('');
      case 'base64': return base64(bytes);
      case 'base64url': return base64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
      case 'latin1': case 'binary': return bytes.map(b => String.fromCharCode(b)).join('');
      case 'ascii': return bytes.map(b => String.fromCharCode(b & 127)).join('');
      default: return text(bytes);
    }
  }
  equals(other) { return this.length === other.length && this.every((b, i) => b === other[i]); }
  toJSON() { return { type: 'Buffer', data: Array.from(this) }; }
  write(s, offset) { this.set(utf8(s), offset || 0); return utf8(s).length; }
  readUInt8(i) { return this[i || 0]; }
  readUInt16BE(i) { i = i || 0; return this[i] << 8 | this[i + 1]; }
  readUInt32BE(i) { i = i || 0; return (this[i] << 24 | this[i + 1] << 16 | this[i + 2] << 8 | this[i + 3]) >>> 0; }
}
module.exports = { Buffer, utf8, text, base64, unbase64 };
