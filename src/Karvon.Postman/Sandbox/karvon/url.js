// Node's url: URL, URLSearchParams, parse, format, resolve.
'use strict';
const qs = require('querystring');
class URLSearchParams {
  constructor(init, changed) {
    this._changed = changed;
    this._list = typeof init === 'string' ? init.replace(/^\?/, '').split('&').filter(Boolean).map(p => { const i = p.indexOf('='); return i < 0 ? [qs.unescape(p), ''] : [qs.unescape(p.slice(0, i)), qs.unescape(p.slice(i + 1))]; })
      : Array.isArray(init) ? init.map(p => [String(p[0]), String(p[1])]) : init && typeof init[Symbol.iterator] === 'function' ? Array.from(init, p => [p[0], p[1]]) : Object.keys(init || {}).map(k => [k, String(init[k])]);
  }
  _did() { if (this._changed) this._changed(this.toString()); }
  get(k) { const p = this._list.find(x => x[0] === k); return p ? p[1] : null; }
  getAll(k) { return this._list.filter(x => x[0] === k).map(x => x[1]); }
  has(k) { return this._list.some(x => x[0] === k); }
  append(k, v) { this._list.push([String(k), String(v)]); this._did(); }
  set(k, v) { const p = this._list.find(x => x[0] === k); if (p) { p[1] = String(v); this._list = this._list.filter(x => x[0] !== k || x === p); } else this._list.push([String(k), String(v)]); this._did(); }
  delete(k) { this._list = this._list.filter(x => x[0] !== k); this._did(); }
  sort() { this._list.sort((a, b) => a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0); this._did(); }
  forEach(f) { this._list.forEach(p => f(p[1], p[0], this)); }
  keys() { return this._list.map(p => p[0])[Symbol.iterator](); }
  values() { return this._list.map(p => p[1])[Symbol.iterator](); }
  entries() { return this._list.map(p => [p[0], p[1]])[Symbol.iterator](); }
  [Symbol.iterator]() { return this.entries(); }
  toString() { return this._list.map(p => qs.escape(p[0]).replace(/%20/g, '+') + '=' + qs.escape(p[1]).replace(/%20/g, '+')).join('&'); }
}
const PORTS = { 'http:': '80', 'https:': '443', 'ws:': '80', 'wss:': '443', 'ftp:': '21' };
const split = text => /^(?:([a-z][a-z0-9+.-]*:))?(?:(\/\/)(?:([^\/?#@]*)@)?(\[[^\]]*\]|[^:\/?#]*)(?::(\d+))?)?([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(String(text).trim()) || [];
const dots = path => { const out = []; for (const part of path.split('/')) if (part === '..') { if (out.length > 1) out.pop(); } else if (part !== '.') out.push(part); if (/\/\.{1,2}$/.test(path)) out.push(''); return out.join('/'); };
class URL {
  constructor(text, base) {
    let m = split(text);
    if (!m[1] && base !== undefined) m = split(resolve(String(base), String(text)));
    if (!m[1] || (m[2] && !m[4])) { const e = new TypeError('Invalid URL'); e.code = 'ERR_INVALID_URL'; e.input = text; throw e; }
    this.protocol = m[1].toLowerCase();
    const auth = (m[3] || '').split(':');
    this.username = auth[0] || ''; this.password = auth.slice(1).join(':');
    this.hostname = (m[4] || '').toLowerCase();
    this.port = m[5] && PORTS[this.protocol] !== m[5] ? m[5] : '';
    this.pathname = m[2] ? dots(m[6] || '/') : m[6] || '';
    this._search = m[7] && m[7].length > 1 ? m[7] : '';
    this.hash = m[8] && m[8].length > 1 ? m[8] : '';
    this.searchParams = new URLSearchParams(this._search, s => { this._search = s ? '?' + s : ''; });
  }
  get search() { return this._search; }
  set search(v) { this._search = v ? (String(v)[0] === '?' ? String(v) : '?' + v) : ''; this.searchParams._list = new URLSearchParams(this._search)._list; }
  get host() { return this.hostname + (this.port ? ':' + this.port : ''); }
  set host(v) { const p = String(v).split(':'); this.hostname = p[0]; this.port = p[1] || ''; }
  get origin() { return this.hostname ? this.protocol + '//' + this.host : 'null'; }
  get href() { return this.protocol + (this.hostname ? '//' + (this.username ? this.username + (this.password ? ':' + this.password : '') + '@' : '') + this.host : '') + this.pathname + this._search + this.hash; }
  toString() { return this.href; }
  toJSON() { return this.href; }
}
function resolve(base, to) {
  const t = split(to), b = split(base);
  if (t[1]) return to;
  const root = (b[1] || '') + (b[2] ? '//' + (b[3] ? b[3] + '@' : '') + (b[4] || '') + (b[5] ? ':' + b[5] : '') : '');
  if (t[2]) return (b[1] || '') + to;
  const rest = (t[7] || '') + (t[8] || '');
  if (!t[6]) return root + (b[6] || '') + (t[7] ? rest : (b[7] || '') + (t[8] || ''));
  return root + dots(t[6][0] === '/' ? t[6] : (b[6] || '/').replace(/[^\/]*$/, '') + t[6]) + rest;
}
function parse(text, query) {
  const m = split(text), host = m[4] ? m[4].toLowerCase() + (m[5] ? ':' + m[5] : '') : null;
  return { protocol: m[1] || null, slashes: m[2] ? true : null, auth: m[3] || null, host, port: m[5] || null, hostname: m[4] ? m[4].toLowerCase() : null, hash: m[8] || null, search: m[7] || null,
    query: query ? qs.parse((m[7] || '').slice(1)) : m[7] ? m[7].slice(1) : null, pathname: m[6] || (host ? '/' : null), path: (m[6] || (host ? '/' : '')) + (m[7] || '') || null, href: String(text) };
}
const format = u => typeof u === 'string' ? u : u instanceof URL ? u.href
  : (u.protocol ? u.protocol.replace(/:?$/, ':') : '') + (u.slashes || u.host || u.hostname ? '//' : '') + (u.auth ? u.auth + '@' : '') + (u.host || (u.hostname || '') + (u.port ? ':' + u.port : '')) + (u.pathname || '')
    + (u.search || (u.query && typeof u.query === 'object' && Object.keys(u.query).length ? '?' + qs.stringify(u.query) : '')) + (u.hash || '');
module.exports = { URL, URLSearchParams, parse, format, resolve };
