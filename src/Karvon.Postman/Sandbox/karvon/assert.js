// Node's assert.
'use strict';
const util = require('util');
class AssertionError extends Error { constructor(m) { super(m); this.name = 'AssertionError'; } }
const check = (ok, message, text) => { if (!ok) throw message instanceof Error ? message : new AssertionError(message || text()); };
const loose = (a, b) => a == b || (a !== null && b !== null && typeof a === 'object' && typeof b === 'object' && Object.keys(a).length === Object.keys(b).length && Object.keys(a).every(k => loose(a[k], b[k])));
const assert = (v, m) => check(v, m, () => 'The expression is not truthy');
const pair = (test, word) => (a, b, m) => check(test(a, b), m, () => util.inspect(a) + ' ' + word + ' ' + util.inspect(b));
const threw = f => { try { f(); } catch (e) { return { e }; } return null; };
const fits = (e, what) => what === undefined || typeof what === 'string' || (what instanceof RegExp ? what.test(String(e)) : typeof what === 'function' ? e instanceof what || what(e) === true : Object.keys(what).every(k => util.isDeepStrictEqual(e[k], what[k])));
module.exports = Object.assign(assert, {
  AssertionError, ok: assert, fail: m => check(false, m, () => 'Failed'),
  equal: pair((a, b) => a == b, '=='), notEqual: pair((a, b) => a != b, '!='), strictEqual: pair(Object.is, 'is not'), notStrictEqual: pair((a, b) => !Object.is(a, b), 'is'),
  deepEqual: pair(loose, 'deep =='), notDeepEqual: pair((a, b) => !loose(a, b), 'deep !='), deepStrictEqual: pair(util.isDeepStrictEqual, 'is not deeply'), notDeepStrictEqual: pair((a, b) => !util.isDeepStrictEqual(a, b), 'is deeply'),
  match: (s, re, m) => check(re.test(s), m, () => util.inspect(s) + ' does not match ' + re), doesNotMatch: (s, re, m) => check(!re.test(s), m, () => util.inspect(s) + ' matches ' + re),
  throws(f, what, m) { const t = threw(f); check(t && fits(t.e, what), typeof what === 'string' ? what : m, () => t ? 'It threw another: ' + t.e : 'It did not throw'); },
  doesNotThrow(f, m) { const t = threw(f); check(!t, typeof m === 'string' ? m : undefined, () => 'It threw ' + (t && t.e)); },
  ifError(v) { if (v !== null && v !== undefined) throw v; },
});
assert.strict = assert;
