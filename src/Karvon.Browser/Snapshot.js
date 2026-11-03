// Karvon's page side, run in the page by Runtime.evaluate as (this)(op, arg): the page read as a short outline (its
// headings, text and controls, each control with a ref Claude acts on), and a control found by its ref.
(function (op, arg) {
  const interactive = new Set(['link', 'button', 'textbox', 'searchbox', 'checkbox', 'radio', 'combobox', 'listbox', 'option',
    'slider', 'spinbutton', 'switch', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'treeitem']);
  const clean = s => (s || '').replace(/\s+/g, ' ').trim();

  function visible(el) {
    const style = getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0 || style.display === 'contents';
  }

  function role(el) {
    const given = el.getAttribute('role');
    if (given) return given.split(' ')[0];
    const tag = el.tagName.toLowerCase();
    if (tag === 'a') return el.hasAttribute('href') ? 'link' : null;
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'input') {
      const type = (el.getAttribute('type') || 'text').toLowerCase();
      if (type === 'hidden') return null;
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (['submit', 'button', 'reset', 'image'].includes(type)) return 'button';
      if (type === 'range') return 'slider';
      if (type === 'number') return 'spinbutton';
      if (type === 'search') return 'searchbox';
      return 'textbox';
    }
    if (el.isContentEditable && el.parentElement && !el.parentElement.isContentEditable) return 'textbox';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'img') return 'img';
    return { li: 'listitem', ul: 'list', ol: 'list', table: 'table', tr: 'row', th: 'columnheader', td: 'cell', form: 'form',
      dialog: 'dialog', nav: 'navigation', main: 'main', header: 'banner', footer: 'contentinfo', aside: 'complementary' }[tag] || null;
  }

  function name(el) {
    const label = el.getAttribute('aria-label');
    if (label) return clean(label);
    const by = el.getAttribute('aria-labelledby');
    if (by) return clean(by.split(' ').map(id => document.getElementById(id)?.innerText || '').join(' '));
    if (el.labels && el.labels.length) {
      // A label around its control: its own words, not the control's (a list's chosen option).
      const copy = el.labels[0].cloneNode(true);
      copy.querySelectorAll('input, select, textarea, button').forEach(c => c.remove());
      return clean(copy.textContent);
    }
    const tag = el.tagName.toLowerCase();
    if (tag === 'img') return clean(el.getAttribute('alt'));
    if (tag === 'input' && ['submit', 'button', 'reset'].includes(el.type)) return clean(el.value);
    if (['input', 'textarea', 'select'].includes(tag)) return clean(el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('name'));
    return clean(el.innerText || el.textContent || el.getAttribute('title')).slice(0, 120);
  }

  function find(ref) {
    return document.querySelector('[data-karvon-ref="' + ref + '"]');
  }

  function snapshot() {
    document.querySelectorAll('[data-karvon-ref]').forEach(e => e.removeAttribute('data-karvon-ref'));
    const lines = [];
    const refs = {};
    const max = arg && arg.max || 1500;
    let next = 0;
    function walk(node, depth) {
      if (lines.length >= max) return;
      if (node.nodeType === 3) {
        const text = clean(node.textContent);
        if (text) lines.push('  '.repeat(depth) + '- text: ' + JSON.stringify(text.slice(0, 300)));
        return;
      }
      if (node.nodeType !== 1) return;
      const el = node;
      const tag = el.tagName.toLowerCase();
      if (['script', 'style', 'noscript', 'template', 'svg', 'head', 'iframe', 'canvas'].includes(tag)) return;
      if (el.getAttribute('aria-hidden') === 'true' || !visible(el)) return;
      const r = role(el);
      let inner = depth;
      if (r) {
        let line = '  '.repeat(depth) + '- ' + r;
        const leaf = interactive.has(r) || r === 'heading' || r === 'img';
        const n = leaf ? name(el) : '';
        if (n) line += ' ' + JSON.stringify(n.slice(0, 120));
        if (r === 'heading') line += ' [level=' + tag.slice(1) + ']';
        if (interactive.has(r)) {
          const ref = 'e' + (++next);
          el.setAttribute('data-karvon-ref', ref);
          refs[ref] = describe(el);
          line += ' [ref=' + ref + ']';
          if (el.disabled || el.getAttribute('aria-disabled') === 'true') line += ' [disabled]';
          if ((r === 'checkbox' || r === 'radio' || r === 'switch') && (el.checked || el.getAttribute('aria-checked') === 'true')) line += ' [checked]';
          if (el.getAttribute('aria-expanded') === 'true') line += ' [expanded]';
          if (el.getAttribute('aria-selected') === 'true') line += ' [selected]';
          // A password's value never leaves the page.
          if (el.type === 'password') line += ' [password]';
          else if ((r === 'textbox' || r === 'searchbox' || r === 'spinbutton') && el.value) line += ' value=' + JSON.stringify(String(el.value).slice(0, 200));
          if (tag === 'select') line += ' value=' + JSON.stringify(Array.from(el.selectedOptions).map(o => clean(o.text)).join(', '));
          if (r === 'link') {
            const href = el.getAttribute('href');
            if (href && !href.startsWith('javascript:')) line += ' -> ' + href.slice(0, 200);
          }
        }
        lines.push(line);
        inner = depth + 1;
        if (leaf && tag !== 'select') return;
      }
      for (const child of el.childNodes) walk(child, inner);
      if (el.shadowRoot) for (const child of el.shadowRoot.childNodes) walk(child, inner);
    }
    walk(document.body, 0);
    return { title: document.title, url: location.href, lines, refs, truncated: lines.length >= max };
  }

  function describe(el) {
    return { role: role(el), name: name(el).slice(0, 120), tag: el.tagName.toLowerCase(), type: (el.type || '').toLowerCase(),
      password: el.type === 'password', submit: submits(el), form: !!el.form };
  }

  // Whether clicking it sends a form.
  function submits(el) {
    const tag = el.tagName.toLowerCase();
    if (tag === 'input') return ['submit', 'image'].includes((el.type || '').toLowerCase());
    if (tag === 'button') return (el.getAttribute('type') || 'submit').toLowerCase() === 'submit' && !!el.form;
    return false;
  }

  switch (op) {
    case 'snapshot':
      return snapshot();
    case 'point': {
      const el = find(arg);
      if (!el) return { error: 'No element ' + arg + ' on the page now: take a new browser_snapshot.' };
      el.scrollIntoView({ block: 'center', inline: 'center' });
      const r = el.getBoundingClientRect();
      return Object.assign(describe(el), { x: r.left + r.width / 2, y: r.top + r.height / 2 });
    }
    case 'describe': {
      const el = find(arg);
      return el ? describe(el) : { error: 'No element ' + arg + ' on the page now: take a new browser_snapshot.' };
    }
    case 'focus': {
      const el = find(arg.ref);
      if (!el) return { error: 'No element ' + arg.ref + ' on the page now: take a new browser_snapshot.' };
      el.scrollIntoView({ block: 'center' });
      el.focus();
      if (arg.clear) {
        if ('value' in el) {
          el.value = '';
          el.dispatchEvent(new Event('input', { bubbles: true }));
        } else if (el.isContentEditable) {
          document.getSelection().selectAllChildren(el);
        }
      }
      return describe(el);
    }
    case 'select': {
      const el = find(arg.ref);
      if (!el || el.tagName.toLowerCase() !== 'select') return { error: 'No list ' + arg.ref + ' on the page now: take a new browser_snapshot.' };
      const wanted = arg.values.map(v => String(v).toLowerCase());
      const matches = o => wanted.includes(o.value.toLowerCase()) || wanted.includes(clean(o.text).toLowerCase());
      if (el.multiple) {
        for (const option of el.options) option.selected = matches(option);
      } else {
        // One choice: the first that matches (a list of one choice always has one chosen).
        const index = Array.from(el.options).findIndex(matches);
        if (index >= 0) el.selectedIndex = index;
      }
      const chosen = Array.from(el.options).some(matches) ? Array.from(el.selectedOptions).map(o => clean(o.text)) : [];
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return Object.assign(describe(el), { chosen });
    }
    case 'login': {
      // The page's sign-in fields: its first visible password field, and the field for the name before it.
      const fields = Array.from(document.querySelectorAll('input')).filter(i => visible(i) && !i.disabled);
      const password = fields.find(i => i.type === 'password');
      const before = password ? fields.slice(0, fields.indexOf(password)) : fields;
      const user = before.reverse().find(i => ['email', 'text', 'tel'].includes((i.type || 'text').toLowerCase())
        || /user|login|email|name/i.test(i.name + i.id + (i.autocomplete || '')));
      if (password) password.setAttribute('data-karvon-ref', 'karvon-password');
      if (user) user.setAttribute('data-karvon-ref', 'karvon-user');
      return { user: !!user, password: !!password };
    }
    case 'text':
      return (document.body && document.body.innerText || '').includes(arg);
  }
  return null;
})
