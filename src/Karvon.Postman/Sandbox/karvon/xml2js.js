// xml2js: a text of XML as an object (its parser; not its builder).
'use strict';
const defaults = { explicitArray: true, explicitRoot: true, mergeAttrs: false, ignoreAttrs: false, trim: false, normalize: false, normalizeTags: false, attrkey: '$', charkey: '_', explicitCharkey: false, emptyTag: '', preserveChildrenOrder: false };
const ENTITIES = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
const plain = s => s.replace(/&(#x?[0-9a-f]+|\w+);/gi, (m, e) => e[0] === '#' ? String.fromCodePoint(/^#x/i.test(e) ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10)) : e in ENTITIES ? ENTITIES[e] : m);
const through = (list, v, name) => (list || []).reduce((x, f) => f(x, name), v);
function parse(input, options) {
  const o = Object.assign({}, defaults, options), text = String(input).replace(/^﻿/, '').trim();
  if (!text) return null;
  const token = /<!--[\s\S]*?-->|<!\[CDATA\[([\s\S]*?)\]\]>|<\?[\s\S]*?\?>|<!DOCTYPE(?:[^>\[]|\[[\s\S]*?\])*>|<\/\s*([^\s>]+)\s*>|<([^\s\/>!?]+)((?:\s+[^\s=\/>]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>|([^<]+)|(<)/g;
  const stack = [];
  let root, m, done = false;
  const name = n => through(o.tagNameProcessors, o.normalizeTags ? n.toLowerCase() : n);
  function close() {
    const node = stack.pop();
    let chars = node.chars;
    if (o.trim) chars = chars.trim();
    if (o.normalize) chars = chars.replace(/\s{2,}/g, ' ').trim();
    const blank = /^\s*$/.test(chars) && !node.cdata;
    chars = through(o.valueProcessors, chars, node.name);
    let value = node.value;
    const keys = Object.keys(value);
    if (!blank || o.explicitCharkey && !keys.some(k => k !== o.attrkey)) { if (keys.length || o.explicitCharkey) value[o.charkey] = chars; else value = chars; }
    else if (!keys.length) value = typeof o.emptyTag === 'function' ? o.emptyTag() : o.emptyTag;
    const parent = stack[stack.length - 1];
    if (!parent) { root = o.explicitRoot ? { [node.name]: value } : value; done = true; return; }
    if (!(node.name in parent.value)) parent.value[node.name] = o.explicitArray ? [value] : value;
    else parent.value[node.name] = [].concat(Array.isArray(parent.value[node.name]) ? parent.value[node.name] : [parent.value[node.name]], [value]);
  }
  while ((m = token.exec(text))) {
    if (m[7]) throw new Error('Unencoded < (at ' + m.index + ')');
    if (m[1] !== undefined) { if (stack.length) { stack[stack.length - 1].chars += m[1]; stack[stack.length - 1].cdata = true; } }
    else if (m[2]) { const top = stack[stack.length - 1]; if (!top || top.raw !== m[2]) throw new Error('Unexpected close tag: ' + m[2]); close(); }
    else if (m[3]) {
      if (done) throw new Error('Text data outside of root node.');
      const value = {};
      if (!o.ignoreAttrs && m[4]) for (const a of m[4].matchAll(/([^\s=]+)\s*=\s*("[^"]*"|'[^']*')/g)) {
        const key = through(o.attrNameProcessors, a[1]), v = through(o.attrValueProcessors, plain(a[2].slice(1, -1)), a[1]);
        if (o.mergeAttrs) value[key] = o.explicitArray ? [v] : v; else (value[o.attrkey] = value[o.attrkey] || {})[key] = v;
      }
      stack.push({ name: name(m[3]), raw: m[3], value, chars: '' });
      if (m[5]) close();
    }
    else if (m[6] && stack.length) stack[stack.length - 1].chars += plain(m[6]);
    else if (m[6] && m[6].trim()) throw new Error('Non-whitespace before first tag.');
  }
  if (stack.length) throw new Error('Unclosed root tag');
  return root === undefined ? null : root;
}
class Parser {
  constructor(options) { this.options = options; }
  parseString(text, callback) { let result, error = null; try { result = parse(text, this.options); } catch (e) { error = e; } if (callback) callback(error, result); else if (error) throw error; return result; }
  parseStringPromise(text) { return new Promise((resolve, reject) => this.parseString(text, (e, v) => e ? reject(e) : resolve(v))); }
}
const lowerFirst = s => s[0].toLowerCase() + s.slice(1);
module.exports = {
  Parser, defaults: { '0.2': defaults },
  parseString: (text, a, b) => new Parser(typeof a === 'function' ? {} : a).parseString(text, typeof a === 'function' ? a : b),
  parseStringPromise: (text, options) => new Parser(options).parseStringPromise(text),
  processors: { normalize: s => s.toLowerCase(), firstCharLowerCase: lowerFirst, stripPrefix: s => s.replace(/^(?!xmlns)[^:]*:/, ''), parseNumbers: s => s !== '' && !isNaN(s) ? Number(s) : s, parseBooleans: s => /^true$/i.test(s) ? true : /^false$/i.test(s) ? false : s },
};
