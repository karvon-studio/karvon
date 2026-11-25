// Node's util: what scripts use of it.
'use strict';
function inspect(v, depth) {
  depth = typeof depth === 'number' ? depth : 3;
  if (typeof v === 'string') return "'" + v + "'";
  if (typeof v === 'function') return '[Function' + (v.name ? ': ' + v.name : '') + ']';
  if (v === null || typeof v !== 'object') return String(v);
  if (v instanceof Error || v instanceof RegExp || v instanceof Date) return String(v);
  if (depth < 0) return Array.isArray(v) ? '[Array]' : '[Object]';
  return Array.isArray(v) ? (v.length ? '[ ' + v.map(x => inspect(x, depth - 1)).join(', ') + ' ]' : '[]')
    : Object.keys(v).length ? '{ ' + Object.keys(v).map(k => k + ': ' + inspect(v[k], depth - 1)).join(', ') + ' }' : '{}';
}
function format(first, ...rest) {
  if (typeof first !== 'string') return [first, ...rest].map(x => typeof x === 'string' ? x : inspect(x)).join(' ');
  let i = 0;
  const text = first.replace(/%[sdifjoO%]/g, m => m === '%%' ? '%' : i >= rest.length ? m
    : m === '%s' ? String(rest[i++]) : m === '%d' || m === '%f' ? Number(rest[i++]) : m === '%i' ? parseInt(rest[i++], 10) : m === '%j' ? JSON.stringify(rest[i++]) : inspect(rest[i++]));
  return [text, ...rest.slice(i).map(x => typeof x === 'string' ? x : inspect(x))].join(' ');
}
function same(a, b) {
  if (Object.is(a, b)) return true;
  if (a instanceof Date && b instanceof Date) return a.getTime() === b.getTime();
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object' || Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a), kb = Object.keys(b);
  return ka.length === kb.length && ka.every(k => Object.prototype.hasOwnProperty.call(b, k) && same(a[k], b[k]));
}
module.exports = {
  inspect, format, isDeepStrictEqual: same,
  inherits(ctor, parent) { if (parent) { ctor.super_ = parent; Object.setPrototypeOf(ctor.prototype, parent.prototype); } },
  promisify: f => (...args) => new Promise((resolve, reject) => f(...args, (e, v) => e ? reject(e) : resolve(v))),
  deprecate: f => f, isArray: Array.isArray, isString: v => typeof v === 'string', isObject: v => v !== null && typeof v === 'object', isFunction: v => typeof v === 'function',
  isDate: v => v instanceof Date, isRegExp: v => v instanceof RegExp, isError: v => v instanceof Error,
};
