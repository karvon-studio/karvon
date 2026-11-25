// uuid: v4 (and v1's and v7's form), validate, NIL.
'use strict';
const v4 = () => crypto.randomUUID();
const stamped = version => () => {
  const b = crypto.getRandomValues(new Uint8Array(16)), now = Date.now();
  if (version === 7) for (let i = 0; i < 6; i++) b[i] = Math.floor(now / Math.pow(256, 5 - i)) % 256;
  else {
    const t = (now + 12219292800000) * 10000, low = t % 4294967296, high = Math.floor(t / 4294967296);
    for (let i = 0; i < 4; i++) b[i] = Math.floor(low / Math.pow(256, 3 - i)) % 256;
    b[4] = Math.floor(high / 256) % 256; b[5] = high % 256; b[6] = Math.floor(high / 16777216) % 16; b[7] = Math.floor(high / 65536) % 256;
  }
  b[6] = (b[6] & 15) | (version << 4); b[8] = (b[8] & 63) | 128;
  const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
  return [h.slice(0, 8), h.slice(8, 12), h.slice(12, 16), h.slice(16, 20), h.slice(20)].join('-');
};
module.exports = v4;
Object.assign(module.exports, { v4, v1: stamped(1), v7: stamped(7), NIL: '00000000-0000-0000-0000-000000000000', validate: s => /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(s), version: s => parseInt(s[14], 16) });
