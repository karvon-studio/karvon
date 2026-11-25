// Postman's script API (pm.*), over what the host gives: variables, the request, the response, the tests' results.
(function (global, host) {
  'use strict';

  // ---- Assertions (chai's expect, the part Postman's tests use) ----------------------------------------------

  function AssertionError(message) { this.name = 'AssertionError'; this.message = message; }
  AssertionError.prototype = Object.create(Error.prototype);
  AssertionError.prototype.toString = function () { return this.name + ': ' + this.message; };

  const typeOf = v => v === null ? 'null' : Array.isArray(v) ? 'array' : v instanceof RegExp ? 'regexp' : v instanceof Date ? 'date' : v instanceof Error ? 'error' : typeof v;

  function show(v) {
    if (typeof v === 'string') return "'" + v + "'";
    if (typeof v === 'function') return '[Function]';
    if (v === undefined) return 'undefined';
    if (v instanceof RegExp || v instanceof Error) return String(v);
    try { const text = JSON.stringify(v); return text === undefined ? String(v) : text.length > 200 ? text.slice(0, 199) + '…' : text; } catch (e) { return String(v); }
  }

  function eql(a, b) {
    if (Object.is(a, b)) return true;
    if (a instanceof Date && b instanceof Date) return a.getTime() === b.getTime();
    if (a instanceof RegExp && b instanceof RegExp) return String(a) === String(b);
    if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null || Array.isArray(a) !== Array.isArray(b)) return false;
    const ka = Object.keys(a), kb = Object.keys(b);
    return ka.length === kb.length && ka.every(k => Object.prototype.hasOwnProperty.call(b, k) && eql(a[k], b[k]));
  }

  const own = (o, k) => o !== null && o !== undefined && Object.prototype.hasOwnProperty.call(Object(o), k);

  function at(value, path) {
    for (const part of String(path).replace(/\[(\w+)\]/g, '.$1').replace(/^\./, '').split('.')) {
      if (value === null || value === undefined || !(part in Object(value))) return { found: false };
      value = value[part];
    }
    return { found: true, value };
  }

  class Assertion {
    constructor(value, message) { this._v = value; this._m = message; this._not = false; this._deep = false; this._nested = false; this._any = false; }
    _(ok, text, negated) {
      if (this._not ? ok : !ok)
        throw new AssertionError((this._m ? this._m + ': ' : '') + 'expected ' + show(this._v) + ' ' + (this._not ? negated || 'not ' + text : text));
      return this;
    }
    get not() { this._not = !this._not; return this; }
    get deep() { this._deep = true; return this; }
    get nested() { this._nested = true; return this; }
    get any() { this._any = true; return this; }
    get all() { this._any = false; return this; }
    get a() { return this._type(); }
    get an() { return this._type(); }
    _type() {
      const check = type => this._(typeOf(this._v) === String(type).toLowerCase(), 'to be ' + (/^[aeiou]/i.test(type) ? 'an ' : 'a ') + type);
      return this._chain(check);
    }
    // A method that is a part of the chain too (to.be.an('array').that...; to.include.members).
    _chain(method) {
      const self = this;
      return new Proxy(method, { get: (f, name) => name === 'call' || name === 'apply' || name === 'bind' ? f[name].bind(f) : self[name] });
    }
    get include() { return this._include(); }
    get includes() { return this._include(); }
    get contain() { return this._include(); }
    get contains() { return this._include(); }
    _include() {
      this._contains = true;
      return this._chain((what, message) => {
        if (message) this._m = message;
        const v = this._v, same = this._deep ? eql : Object.is;
        const ok = typeof v === 'string' ? v.includes(String(what))
          : Array.isArray(v) ? v.some(x => same(x, what) || x === what)
          : v !== null && typeof v === 'object' && what !== null && typeof what === 'object' ? Object.keys(what).every(k => own(v, k) && (same(v[k], what[k]) || v[k] === what[k]))
          : false;
        return this._(ok, 'to include ' + show(what));
      });
    }
    get length() { return this._length(); }
    get lengthOf() { return this._length(); }
    _length() {
      this._counts = true;
      return this._chain(n => this._(this._size() === n, 'to have a length of ' + n + ' but got ' + this._size(), 'to not have a length of ' + n));
    }
    _size() { const v = this._v; return v === null || v === undefined ? undefined : v.length !== undefined ? v.length : v.size !== undefined ? v.size : Object.keys(v).length; }
    _n() { return this._counts ? this._size() : this._v; }
    equal(x, m) { if (m) this._m = m; return this._deep ? this._(eql(this._v, x), 'to deeply equal ' + show(x)) : this._(this._v === x, 'to equal ' + show(x)); }
    equals(x, m) { return this.equal(x, m); }
    eq(x, m) { return this.equal(x, m); }
    eql(x, m) { if (m) this._m = m; return this._(eql(this._v, x), 'to deeply equal ' + show(x)); }
    eqls(x, m) { return this.eql(x, m); }
    above(n) { return this._(this._n() > n, 'to be above ' + show(n)); }
    gt(n) { return this.above(n); }
    greaterThan(n) { return this.above(n); }
    least(n) { return this._(this._n() >= n, 'to be at least ' + show(n)); }
    gte(n) { return this.least(n); }
    below(n) { return this._(this._n() < n, 'to be below ' + show(n)); }
    lt(n) { return this.below(n); }
    lessThan(n) { return this.below(n); }
    most(n) { return this._(this._n() <= n, 'to be at most ' + show(n)); }
    lte(n) { return this.most(n); }
    within(a, b) { const n = this._n(); return this._(n >= a && n <= b, 'to be within ' + a + '..' + b); }
    closeTo(n, d) { return this._(Math.abs(this._v - n) <= d, 'to be close to ' + n + ' +/- ' + d); }
    get true() { return this._(this._v === true, 'to be true'); }
    get false() { return this._(this._v === false, 'to be false'); }
    get null() { return this._(this._v === null, 'to be null'); }
    get undefined() { return this._(this._v === undefined, 'to be undefined'); }
    get NaN() { return this._(Number.isNaN(this._v), 'to be NaN'); }
    get ok() { return this._(!!this._v, 'to be truthy'); }
    get exist() { return this._(this._v !== null && this._v !== undefined, 'to exist'); }
    get exists() { return this.exist; }
    get empty() {
      const v = this._v;
      return this._(typeof v === 'string' || Array.isArray(v) ? v.length === 0 : v !== null && typeof v === 'object' && Object.keys(v).length === 0, 'to be empty');
    }
    property(name, value) {
      const found = this._nested ? at(this._v, name) : { found: this._v !== null && this._v !== undefined && name in Object(this._v), value: Object(this._v)[name] };
      if (arguments.length < 2) {
        this._(found.found, 'to have property ' + show(name));
        if (!this._not) this._v = found.value;
        return this;
      }
      return this._(found.found && (this._deep ? eql(found.value, value) : found.value === value), 'to have property ' + show(name) + ' of ' + show(value) + (found.found ? ', but got ' + show(found.value) : ''));
    }
    ownProperty(name, value) { return arguments.length < 2 ? this._(own(this._v, name), 'to have own property ' + show(name)) : this.property(name, value); }
    keys(...names) {
      const wanted = names.flatMap(n => Array.isArray(n) ? n : n !== null && typeof n === 'object' ? Object.keys(n) : [n]).map(String);
      const has = Object.keys(this._v === null || this._v === undefined ? {} : this._v);
      const ok = this._any ? wanted.some(k => has.includes(k)) : wanted.every(k => has.includes(k)) && (this._contains || has.length === wanted.length);
      return this._(ok, 'to have key' + (wanted.length === 1 ? ' ' : 's ') + wanted.join(', '));
    }
    key(...names) { return this.keys(...names); }
    members(list) {
      const same = this._deep ? eql : Object.is;
      const ok = Array.isArray(this._v) && list.every(x => this._v.some(y => same(x, y))) && (this._contains || this._v.length === list.length);
      return this._(ok, 'to have the members ' + show(list));
    }
    oneOf(list) { return this._(list.some(x => Object.is(x, this._v)), 'to be one of ' + show(list)); }
    match(re) { return this._(re.test(this._v), 'to match ' + re); }
    matches(re) { return this.match(re); }
    string(s) { return this._(typeof this._v === 'string' && this._v.includes(s), 'to contain ' + show(s)); }
    satisfy(f) { return this._(!!f(this._v), 'to satisfy the function'); }
    satisfies(f) { return this.satisfy(f); }
    instanceof(type) { return this._(this._v instanceof type, 'to be an instance of ' + type.name); }
    instanceOf(type) { return this.instanceof(type); }
    throw(what) {
      let thrown = null;
      try { this._v(); } catch (e) { thrown = e; }
      const text = thrown && String(thrown.message || thrown);
      const ok = thrown !== null && (what === undefined || (typeof what === 'function' ? thrown instanceof what : what instanceof RegExp ? what.test(text) : text.includes(String(what))));
      return this._(ok, 'to throw' + (what === undefined ? '' : ' ' + show(what)));
    }
    throws(what) { return this.throw(what); }
    jsonSchema() { throw new Error("jsonSchema is not available in Karvon's scripts."); }
  }
  for (const word of ['to', 'be', 'been', 'is', 'that', 'which', 'and', 'has', 'have', 'with', 'at', 'of', 'same', 'but', 'does', 'still', 'also'])
    Object.defineProperty(Assertion.prototype, word, { get() { return this; } });

  const expect = (value, message) => new Assertion(value, message);
  expect.fail = message => { throw new AssertionError(message || 'expect.fail()'); };

  // ---- Variables ---------------------------------------------------------------------------------------------

  const scope = name => ({
    get: key => host.get(name, String(key)),
    set: (key, value) => { host.set(name, String(key), value !== null && typeof value === 'object' ? JSON.stringify(value) : value); },
    unset: key => { host.unset(name, String(key)); },
    has: key => host.has(name, String(key)),
    clear: () => { for (const key of Object.keys(JSON.parse(host.all(name)))) host.unset(name, key); },
    toObject: () => JSON.parse(host.all(name)),
    replaceIn: text => typeof text === 'string' ? host.replace(text) : JSON.parse(host.replace(JSON.stringify(text))),
  });

  // ---- The request and the response --------------------------------------------------------------------------

  function headers(list) {
    const find = name => list.find(h => String(h.key).toLowerCase() === String(name).toLowerCase());
    return {
      get: name => { const h = find(name); return h && h.value; },
      one: name => find(name),
      has: (name, value) => { const h = find(name); return !!h && (value === undefined || h.value === value); },
      add: h => { const o = pair(h); list.push(o); },
      upsert: h => { const o = pair(h), old = find(o.key); if (old) old.value = o.value; else list.push(o); },
      remove: name => { for (let i = list.length - 1; i >= 0; i--) if (String(list[i].key).toLowerCase() === String(name).toLowerCase()) list.splice(i, 1); },
      toObject: () => Object.fromEntries(list.map(h => [h.key, h.value])),
      all: () => list.slice(),
      each: f => list.forEach(f),
      map: f => list.map(f),
      filter: f => list.filter(f),
      find: f => list.find(f),
      count: () => list.length,
      toJSON: () => list.slice(),
    };
  }
  const pair = h => { if (typeof h === 'string') { const i = h.indexOf(':'); return { key: h.slice(0, i).trim(), value: h.slice(i + 1).trim() }; } return { key: String(h.key), value: String(h.value) }; };

  function urlOf(text) {
    const match = /^(?:([a-z][\w+.-]*):\/\/)?([^/?#]*)([^?#]*)(?:\?([^#]*))?/i.exec(text) || [];
    const query = (match[4] || '').split('&').filter(Boolean).map(p => { const i = p.indexOf('='); return i < 0 ? { key: p, value: '' } : { key: p.slice(0, i), value: p.slice(i + 1) }; });
    return {
      protocol: match[1], host: (match[2] || '').split('.'), path: (match[3] || '').split('/').filter(Boolean),
      query: { get: k => { const q = query.find(x => x.key === k); return q && q.value; }, has: k => query.some(x => x.key === k), all: () => query, toObject: () => Object.fromEntries(query.map(q => [q.key, q.value])) },
      getHost: () => match[2] || '', getPath: () => match[3] || '/', getQueryString: () => match[4] || '', getRaw: () => text,
      toString: () => text, toJSON: () => text,
    };
  }

  function response(data) {
    const list = data.headers;
    const text = () => data.body;
    let parsed, read = false;
    const json = () => { if (!read) { parsed = JSON.parse(data.body); read = true; } return parsed; };
    const r = {
      code: data.code, status: data.status, responseTime: data.time, responseSize: data.size, headers: headers(list), text, json,
      reason: () => data.status, size: () => ({ body: data.size, header: 0, total: data.size }),
    };
    const fail = (ok, not, what) => { if (not ? ok : !ok) throw new AssertionError('expected response ' + (not ? 'to not ' : 'to ') + what); };
    const isJson = () => { try { json(); return true; } catch (e) { return false; } };
    const build = not => {
      const be = {};
      const flag = (name, test, what) => Object.defineProperty(be, name, { get() { fail(test(), not, what); return true; } });
      const code = (a, b) => () => data.code >= a && data.code <= b;
      flag('ok', code(200, 299), 'have a 2XX status but got ' + data.code);
      flag('success', code(200, 299), 'have a 2XX status but got ' + data.code);
      flag('info', code(100, 199), 'have a 1XX status but got ' + data.code);
      flag('redirection', code(300, 399), 'have a 3XX status but got ' + data.code);
      flag('clientError', code(400, 499), 'have a 4XX status but got ' + data.code);
      flag('serverError', code(500, 599), 'have a 5XX status but got ' + data.code);
      flag('error', code(400, 599), 'have an error status but got ' + data.code);
      for (const [name, n] of [['accepted', 202], ['badRequest', 400], ['unauthorized', 401], ['forbidden', 403], ['notFound', 404], ['rateLimited', 429]])
        flag(name, code(n, n), 'have status ' + n + ' but got ' + data.code);
      flag('json', isJson, 'be JSON');
      flag('withBody', () => data.body.length > 0, 'have a body');
      const have = {
        status: s => fail(typeof s === 'number' ? data.code === s : data.status === s, not, 'have status ' + show(s) + ' but got ' + (typeof s === 'number' ? data.code : show(data.status))),
        header: (k, v) => fail(r.headers.has(k, v), not, 'have header ' + show(k) + (v === undefined ? '' : ' with value ' + show(v) + ' but got ' + show(r.headers.get(k)))),
        body: b => fail(b === undefined ? data.body.length > 0 : b instanceof RegExp ? b.test(data.body) : typeof b === 'string' ? data.body === b : isJson() && eql(json(), b), not, 'have body ' + show(b)),
        jsonBody: (path, value) => {
          if (path === undefined) return fail(isJson(), not, 'have a JSON body');
          if (path !== null && typeof path === 'object') return fail(isJson() && eql(json(), path), not, 'have the JSON body ' + show(path));
          const found = isJson() ? at(json(), path) : { found: false };
          fail(found.found && (value === undefined || eql(found.value, value)), not, 'have ' + show(path) + ' in its JSON body' + (value === undefined ? '' : ' of ' + show(value)));
        },
        responseTime: { below: n => fail(data.time < n, not, 'take less than ' + n + ' ms but took ' + data.time), above: n => fail(data.time > n, not, 'take more than ' + n + ' ms but took ' + data.time) },
      };
      const to = { be, have };
      Object.defineProperty(to, 'not', { get: () => build(!not) });
      return to;
    };
    r.to = build(false);
    return r;
  }

  // ---- pm ----------------------------------------------------------------------------------------------------

  const state = JSON.parse(host.state);
  const pending = [], timers = [];
  const failure = e => e === null || e === undefined ? 'failed' : e.name && e.message !== undefined ? e.name + ': ' + e.message : String(e);

  function test(name, body) {
    const passed = () => host.test(String(name), true, ''), failed = e => host.test(String(name), false, failure(e));
    try {
      if (typeof body !== 'function') { host.skip(String(name)); return pm; }
      if (body.length > 0) {
        pending.push(new Promise(resolve => { body(e => { if (e) failed(e); else passed(); resolve(); }); }));
        return pm;
      }
      const result = body();
      if (result && typeof result.then === 'function') pending.push(result.then(passed, failed)); else passed();
    } catch (e) { failed(e); }
    return pm;
  }
  test.skip = name => { host.skip(String(name)); return pm; };
  test.index = () => host.count();

  function sendRequest(request, callback) {
    let result, error = null;
    const sent = JSON.parse(host.send(JSON.stringify(typeof request === 'string' ? { url: request, method: 'GET' } : request)));
    if (sent.error) error = new Error(sent.error); else result = response(sent);
    if (callback) { callback(error, result); return undefined; }
    return error ? Promise.reject(error) : Promise.resolve(result);
  }

  const requestUrl = { value: urlOf(state.request.url) };
  const request = {
    method: state.request.method, headers: headers(state.request.headers), name: state.name, id: '',
    body: { mode: state.request.mode, raw: state.request.body, toString: () => state.request.body || '' },
    get url() { return requestUrl.value; }, set url(v) { requestUrl.value = urlOf(String(v)); },
    addHeader: h => request.headers.add(h), removeHeader: n => request.headers.remove(n), upsertHeader: h => request.headers.upsert(h),
  };

  const setNext = name => host.next(name === null || name === undefined ? null : String(name));
  const pm = {
    variables: scope('variables'), environment: scope('environment'), collectionVariables: scope('collectionVariables'), globals: scope('globals'), iterationData: scope('iterationData'),
    request, response: state.response ? response(state.response) : undefined,
    info: { eventName: state.event, iteration: state.iteration, iterationCount: state.iterations, requestName: state.name, requestId: '' },
    test, expect, sendRequest,
    execution: { setNextRequest: setNext, skipRequest: () => host.skipRequest(), location: { current: state.name } },
    cookies: { get: () => undefined, has: () => false, toObject: () => ({}) },
    require: name => global.require(name),
  };
  pm.environment.name = state.environment;

  global.pm = pm;
  const log = level => (...items) => host.log(level, items.map(i => typeof i === 'string' ? i : show(i)).join(' '));
  global.console = { log: log('log'), info: log('info'), warn: log('warn'), error: log('error'), debug: log('log'), clear: () => {}, table: log('log') };
  global.setTimeout = (f, ms, ...args) => { timers.push(() => f(...args)); return timers.length; };
  global.setImmediate = f => global.setTimeout(f, 0);
  global.setInterval = () => { throw new Error("setInterval is not available in Karvon's scripts."); };
  global.clearTimeout = id => { if (timers[id - 1]) timers[id - 1] = () => {}; };
  global.btoa = text => host.btoa(String(text));
  global.atob = text => host.atob(String(text));
  global.require = name => {
    if (name === 'chai') return { expect };
    throw new Error("'" + name + "' is not available in Karvon's scripts.");
  };

  // The older API (postman.*, tests[...], responseBody).
  global.tests = {};
  global.postman = {
    setEnvironmentVariable: pm.environment.set, getEnvironmentVariable: pm.environment.get, clearEnvironmentVariable: pm.environment.unset, clearEnvironmentVariables: pm.environment.clear,
    setGlobalVariable: pm.globals.set, getGlobalVariable: pm.globals.get, clearGlobalVariable: pm.globals.unset, clearGlobalVariables: pm.globals.clear,
    setNextRequest: setNext, getResponseHeader: name => pm.response && pm.response.headers.get(name),
  };
  global.environment = pm.environment.toObject();
  global.globals = pm.globals.toObject();
  global.request = { method: state.request.method, url: state.request.url, headers: request.headers.toObject(), data: state.request.body };
  if (state.response) {
    global.responseBody = state.response.body;
    global.responseCode = { code: state.response.code, name: state.response.status, detail: state.response.status };
    global.responseHeaders = pm.response.headers.toObject();
    global.responseTime = state.response.time;
  }

  // After the script: the tests still running, the timers, the old tests[...]; then the request as the script left it.
  global.__finish = async () => {
    for (let i = 0; i < timers.length && i < 1000; i++) timers[i]();
    for (let i = 0; i < pending.length; i++) await pending[i];
    for (const name of Object.keys(global.tests)) host.test(name, !!global.tests[name], global.tests[name] ? '' : 'it is ' + show(global.tests[name]));
    return JSON.stringify({ method: String(request.method), url: requestUrl.value.toString(), headers: request.headers.all(), body: request.body && request.body.raw !== undefined ? request.body.raw : null });
  };
})(globalThis, __host);
