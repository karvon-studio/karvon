// Node's string_decoder.
'use strict';
const { Buffer } = require('buffer');
class StringDecoder {
  constructor(encoding) { this.encoding = encoding || 'utf8'; this._rest = []; }
  write(b) {
    const all = this._rest.concat(Array.from(b));
    let keep = 0;
    if (/^utf-?8$/i.test(this.encoding))
      for (let i = 1; i <= Math.min(3, all.length); i++) { const c = all[all.length - i]; if ((c & 0xc0) === 0xc0) { if (i < (c >= 0xf0 ? 4 : c >= 0xe0 ? 3 : 2)) keep = i; break; } if (c < 0x80) break; }
    this._rest = keep ? all.slice(-keep) : [];
    return Buffer.from(keep ? all.slice(0, -keep) : all).toString(this.encoding);
  }
  end(b) { const s = (b ? this.write(b) : '') + (this._rest.length ? Buffer.from(this._rest).toString(this.encoding) : ''); this._rest = []; return s; }
}
module.exports = { StringDecoder };
