// Node's querystring.
'use strict';
const escape = s => encodeURIComponent(s), unescape = s => { try { return decodeURIComponent(s.replace(/\+/g, ' ')); } catch (e) { return s; } };
function parse(text, sep, eq) {
  const out = {};
  for (const part of String(text || '').split(sep || '&').filter(Boolean)) {
    const i = part.indexOf(eq || '='), k = unescape(i < 0 ? part : part.slice(0, i)), v = i < 0 ? '' : unescape(part.slice(i + (eq || '=').length));
    out[k] = k in out ? [].concat(out[k], v) : v;
  }
  return out;
}
const stringify = (o, sep, eq) => Object.keys(o || {}).map(k => [].concat(o[k]).map(v => escape(k) + (eq || '=') + escape(v === undefined || v === null ? '' : String(v))).join(sep || '&')).join(sep || '&');
module.exports = { parse, stringify, escape, unescape, decode: parse, encode: stringify };
