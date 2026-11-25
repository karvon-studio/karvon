// Node's path (POSIX).
'use strict';
function normalize(p) {
  const root = p[0] === '/', out = [];
  for (const part of p.split('/')) if (part === '..') { if (out.length && out[out.length - 1] !== '..') out.pop(); else if (!root) out.push('..'); } else if (part && part !== '.') out.push(part);
  return (root ? '/' : '') + out.join('/') + (p.length > 1 && p.endsWith('/') && out.length ? '/' : '') || (root ? '/' : '.');
}
const basename = (p, ext) => { const b = p.replace(/\/+$/, '').split('/').pop(); return ext && b.endsWith(ext) && b !== ext ? b.slice(0, -ext.length) : b; };
const extname = p => { const b = basename(p), i = b.lastIndexOf('.'); return i > 0 ? b.slice(i) : ''; };
const dirname = p => { const d = p.replace(/\/+$/, '').replace(/\/?[^\/]*$/, ''); return d || (p[0] === '/' ? '/' : '.'); };
const join = (...parts) => normalize(parts.filter(Boolean).join('/') || '.');
const resolve = (...parts) => { let at = ''; for (const p of parts) at = p[0] === '/' ? p : at + '/' + p; return normalize(at[0] === '/' ? at : '/' + at).replace(/(.)\/$/, '$1'); };
function relative(from, to) { const a = resolve(from).split('/').filter(Boolean), b = resolve(to).split('/').filter(Boolean); let i = 0; while (i < a.length && a[i] === b[i]) i++; return [...a.slice(i).map(() => '..'), ...b.slice(i)].join('/'); }
const parse = p => ({ root: p[0] === '/' ? '/' : '', dir: p.includes('/') ? dirname(p) : '', base: basename(p), ext: extname(p), name: basename(p, extname(p)) });
const format = o => (o.dir ? o.dir + (o.dir === o.root ? '' : '/') : o.root || '') + (o.base || (o.name || '') + (o.ext || ''));
module.exports = { sep: '/', delimiter: ':', normalize, basename, extname, dirname, join, resolve, relative, parse, format, isAbsolute: p => p[0] === '/' };
module.exports.posix = module.exports;
