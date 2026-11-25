// csv-parse/lib/sync: a text of rows.
'use strict';
module.exports = function parse(input, options) {
  const o = options || {}, delimiter = o.delimiter || ',', quote = o.quote === undefined ? '"' : o.quote, escape = o.escape || '"', text = String(input).replace(/^﻿/, '');
  let rows = [], row = [], field = '', quoted = false, was = false;
  const trim = s => o.trim || (o.ltrim && o.rtrim) ? s.trim() : o.ltrim ? s.replace(/^\s+/, '') : o.rtrim ? s.replace(/\s+$/, '') : s;
  const push = () => { row.push(was ? field : trim(field)); field = ''; was = false; };
  const end = () => { push(); if (!(row.length === 1 && row[0] === '' && (o.skip_empty_lines || !rows.length)) && !(o.skip_lines_with_empty_values && row.every(f => f.trim() === ''))) rows.push(row); row = []; };
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) { if (c === escape && text[i + 1] === quote) { field += quote; i++; } else if (c === quote) quoted = false; else field += c; }
    else if (quote && c === quote && field.trim() === '') { quoted = true; was = true; field = ''; }
    else if (o.comment && text.startsWith(o.comment, i) && field === '' && !row.length) { while (i + 1 < text.length && text[i + 1] !== '\n') i++; i++; }
    else if (text.startsWith(delimiter, i)) { push(); i += delimiter.length - 1; }
    else if (c === '\n' || c === '\r') { if (c === '\r' && text[i + 1] === '\n') i++; end(); }
    else if (!(was && /\s/.test(c))) field += c;
  }
  if (quoted) throw new Error('Quoted field not terminated');
  if (field !== '' || row.length || was) end();
  const cast = v => o.cast === true || o.auto_parse ? (v !== '' && !isNaN(v) ? Number(v) : v) : typeof o.cast === 'function' ? o.cast(v) : v;
  let columns = o.columns;
  if (columns === true) columns = rows.shift() || [];
  else if (typeof columns === 'function') columns = columns(rows.shift() || []);
  if (!o.relax_column_count && !o.relax) { const n = columns ? columns.length : rows.length ? rows[0].length : 0; rows.forEach((r, i) => { if (r.length !== n) throw new Error('Number of columns is inconsistent on line ' + (i + (o.columns ? 2 : 1))); }); }
  rows = rows.slice((o.from || 1) - 1, o.to);
  return columns ? rows.map(r => Object.fromEntries(columns.map((k, i) => [typeof k === 'object' && k ? k.name : k, i]).filter(([k]) => k !== false && k !== undefined && k !== null).map(([k, i]) => [k, r[i] === undefined ? undefined : cast(r[i])]))) : rows.map(r => r.map(cast));
};
