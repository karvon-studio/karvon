// Karvon Studio's Hot Reload for Node.js, loaded before a program Karvon runs (NODE_OPTIONS --require). A saved
// change to a file of the workspace the program has loaded is applied in place, the program going on with its state:
// V8's LiveEdit, through the inspector of the process itself (no port is opened). A change outside the file's
// functions (code that ran when the file loaded) would not run again: Karvon restarts the run for it. What it does is
// told to Karvon as OSC 7700 sequences on stderr, which a terminal does not show; under js-debug (which may read the
// console's messages only), as a console message.
'use strict';
(() => {
  const root = process.env.KARVON_HOT_RELOAD_ROOT;
  const { isMainThread } = require('node:worker_threads');
  if (!root || !isMainThread)
    return;
  const fs = require('node:fs');
  const path = require('node:path');
  const inspector = require('node:inspector');
  const { fileURLToPath } = require('node:url');
  const nodeModule = require('node:module');

  // The console as it is before the program's code runs (which may wrap it).
  const debug = process.env.VSCODE_INSPECTOR_OPTIONS ? console.debug.bind(console) : null;
  const tell = message => {
    const sequence = `\x1b]7700;${JSON.stringify(message)}\x07`;
    try {
      if (debug)
        debug(sequence);
      else
        fs.writeSync(2, sequence + '\n');
    } catch {
      // No stderr to tell it on.
    }
  };

  const session = new inspector.Session();
  try {
    session.connect();
  } catch {
    return;
  }
  const post = (method, params) => new Promise((resolve, reject) =>
    session.post(method, params || {}, (error, result) => error ? reject(error) : resolve(result)));

  /**
   * The files it applies changes to: their script, the source V8 runs, the file's text it came from, and what Node.js
   * put after the text (TypeScript's: its //# sourceURL comment).
   */
  const live = new Map();
  const watched = new Set();
  const pending = new Map();

  /** A file of the workspace's own: not of node_modules, nor this file. */
  const inside = file => {
    const relative = path.relative(root, file);
    return relative && !relative.startsWith('..') && !path.isAbsolute(relative)
      && !relative.split(path.sep).includes('node_modules') && file !== __filename;
  };

  /** TypeScript as Node.js runs it, its types blanked out; undefined when this Node.js cannot. */
  const strip = text => {
    if (typeof nodeModule.stripTypeScriptTypes !== 'function')
      return undefined;
    const warn = process.emitWarning;
    process.emitWarning = () => {};
    try {
      return nodeModule.stripTypeScriptTypes(text);
    } catch {
      return undefined;
    } finally {
      process.emitWarning = warn;
    }
  };

  const sourceOf = (file, text) => /\.[cm]?ts$/.test(file) ? strip(text) : text;

  session.on('Debugger.scriptParsed', ({ params }) => {
    if (!params.url.startsWith('file:'))
      return;
    let file;
    try {
      file = fileURLToPath(params.url);
    } catch {
      return;
    }
    if (!inside(file) || live.has(file))
      return;
    post('Debugger.getScriptSource', { scriptId: params.scriptId }).then(({ scriptSource }) => {
      let text;
      try {
        text = fs.readFileSync(file, 'utf8');
      } catch {
        return;
      }
      // Made by a loader (tsx, a bundler) from the file: not the file's text, which LiveEdit would put in its place.
      const source = sourceOf(file, text);
      if (source === undefined || !scriptSource.startsWith(source))
        return;
      const trailer = scriptSource.slice(source.length);
      if (trailer && !/^\s*\/\/# sourceURL=[^\n]*\s*$/.test(trailer))
        return;
      live.set(file, { scriptId: params.scriptId, source: scriptSource, text, trailer });
      watch(path.dirname(file));
      tell({ live: file });
    }, () => {});
  });

  // A folder's watch, not a file's: an editor may save a file by writing a new one in its place.
  function watch(dir) {
    if (watched.has(dir))
      return;
    watched.add(dir);
    try {
      fs.watch(dir, (event, name) => {
        const file = name && path.join(dir, name.toString());
        if (!file || !live.has(file))
          return;
        clearTimeout(pending.get(file));
        const timer = setTimeout(() => {
          pending.delete(file);
          apply(file).catch(error => tell({ restart: file, reason: String(error && error.message || error) }));
        }, 100);
        timer.unref();
        pending.set(file, timer);
      }).unref();
    } catch {
      // A folder it cannot watch: its files restart the run when saved, as Karvon does for the rest.
    }
  }

  /** Where the new source differs from the old: the old one's range of it. */
  function changed(before, after) {
    const max = Math.min(before.length, after.length);
    let start = 0;
    while (start < max && before[start] === after[start])
      start++;
    let end = 0;
    while (end < max - start && before[before.length - 1 - end] === after[after.length - 1 - end])
      end++;
    return { start, end: before.length - end };
  }

  /** The change is inside one of the script's functions (V8's coverage has their ranges), not in its top level. */
  async function insideFunction(scriptId, length, { start, end }) {
    const { result } = await post('Profiler.getBestEffortCoverage');
    const script = result.find(s => s.scriptId === scriptId);
    return !!script && script.functions.some(f => {
      const range = f.ranges[0];
      const topLevel = range.startOffset === 0 && range.endOffset >= length;
      return !topLevel && range.startOffset < start && end < range.endOffset;
    });
  }

  async function apply(file) {
    const script = live.get(file);
    let text;
    try {
      text = fs.readFileSync(file, 'utf8');
    } catch {
      return;
    }
    if (text === script.text)
      return;
    const started = Date.now();
    const stripped = sourceOf(file, text);
    if (stripped === undefined)
      return tell({ restart: file, reason: 'its types cannot be stripped' });
    const source = stripped + script.trailer;
    if (source === script.source) {
      script.text = text;
      return;
    }
    // Code that does not compile is said, not applied nor restarted for: the fix, once saved, is.
    const trial = await post('Debugger.setScriptSource', { scriptId: script.scriptId, scriptSource: source, dryRun: true });
    if (trial.exceptionDetails || trial.status === 'CompileError') {
      const details = trial.exceptionDetails || {};
      return tell({ error: file, line: (details.lineNumber || 0) + 1, message: details.text || 'it does not compile' });
    }
    if (!await insideFunction(script.scriptId, script.source.length, changed(script.source, source)))
      return tell({ restart: file, reason: 'a change outside its functions' });
    const result = await post('Debugger.setScriptSource', { scriptId: script.scriptId, scriptSource: source });
    if (result.status && result.status !== 'Ok')
      return tell({ restart: file, reason: result.status });
    script.source = source;
    script.text = text;
    tell({ applied: file, ms: Date.now() - started });
  }

  post('Profiler.enable').catch(() => {});
  post('Debugger.enable').catch(() => {});
})();
