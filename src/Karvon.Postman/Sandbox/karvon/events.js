// Node's events.
'use strict';
function EventEmitter() { this._on = {}; }
Object.assign(EventEmitter.prototype, {
  on(name, f) { if (!this._on) this._on = {}; (this._on[name] = this._on[name] || []).push(f); return this; },
  prependListener(name, f) { if (!this._on) this._on = {}; (this._on[name] = this._on[name] || []).unshift(f); return this; },
  once(name, f) { const g = (...a) => { this.off(name, g); f.apply(this, a); }; g.listener = f; return this.on(name, g); },
  off(name, f) { if (this._on && this._on[name]) this._on[name] = this._on[name].filter(x => x !== f && x.listener !== f); return this; },
  removeAllListeners(name) { if (name === undefined) this._on = {}; else if (this._on) delete this._on[name]; return this; },
  emit(name, ...a) { const list = this._on && this._on[name] || []; if (name === 'error' && !list.length) throw a[0] instanceof Error ? a[0] : new Error(String(a[0])); list.slice().forEach(f => f.apply(this, a)); return list.length > 0; },
  listeners(name) { return (this._on && this._on[name] || []).map(f => f.listener || f); },
  listenerCount(name) { return (this._on && this._on[name] || []).length; },
  eventNames() { return Object.keys(this._on || {}).filter(k => this._on[k].length); },
  setMaxListeners() { return this; },
});
EventEmitter.prototype.addListener = EventEmitter.prototype.on;
EventEmitter.prototype.removeListener = EventEmitter.prototype.off;
EventEmitter.EventEmitter = EventEmitter;
module.exports = EventEmitter;
