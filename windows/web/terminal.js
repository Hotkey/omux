// One xterm.js instance per omux session, all in this page. The host (C#) owns the
// pseudo consoles; this page renders output, forwards input, and reports terminal events.
'use strict';

const host = window.chrome.webview;
const root = document.getElementById('root');
const sessions = new Map(); // id -> { term, fit, search, el, webgl, status: Map<string, record> }
let current = null;
let appearance = { theme: {}, fontFamily: 'Cascadia Mono, Consolas, monospace', fontSize: 13 };
let shortcuts = []; // [{ name, code, ctrl, shift, alt }]

const post = (message) => host.postMessage(message);

host.addEventListener('message', (event) => {
  const m = event.data;
  switch (m.t) {
    case 'create': create(m.id); break;
    case 'out': sessions.get(m.id)?.term.write(m.d); break;
    case 'show': show(m.id); break;
    case 'destroy': destroy(m.id); break;
    case 'exited': processExited(m.id); break;
    case 'appearance': applyAppearance(m); break;
    case 'shortcuts': shortcuts = m.list; break;
    case 'focus': if (current) sessions.get(current)?.term.focus(); break;
    case 'find': openFind(); break;
  }
});

function create(id) {
  if (sessions.has(id)) return;
  const el = document.createElement('div');
  el.className = 'term';
  root.appendChild(el);

  const term = new Terminal({
    allowProposedApi: true,
    scrollback: 5000,
    fontFamily: appearance.fontFamily,
    fontSize: appearance.fontSize,
    theme: appearance.theme,
    cursorBlink: false,
    windowsPty: { backend: 'conpty' },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  // Ctrl-click: URLs open in the browser.
  term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => {
    if (e.ctrlKey) post({ t: 'openUrl', url: uri });
  }));
  const search = new SearchAddon.SearchAddon();
  term.loadAddon(search);
  search.onDidChangeResults(({ resultIndex, resultCount }) => {
    if (id === current) showCount(resultIndex, resultCount);
  });
  term.open(el);

  const session = { term, fit, search, el, webgl: null, status: new Map() };
  sessions.set(id, session);

  term.onData((d) => post({ t: 'in', id, d }));
  term.onBinary((d) => post({ t: 'in', id, d }));
  term.onResize(({ cols, rows }) => post({ t: 'resize', id, cols, rows }));
  term.onTitleChange((title) => post({ t: 'title', id, title }));
  term.onBell(() => post({ t: 'bell', id }));

  // OSC 7: file://host/C:/path — the shell's working directory.
  term.parser.registerOscHandler(7, (data) => {
    try {
      const url = new URL(data);
      let path = decodeURIComponent(url.pathname);
      if (/^\/[A-Za-z]:/.test(path)) path = path.slice(1);
      post({ t: 'cwd', id, path: path.replace(/\//g, '\\') });
    } catch { /* not a URL */ }
    return true;
  });
  // OSC 9;9: Windows Terminal's working-directory report.
  term.parser.registerOscHandler(9, (data) => {
    if (!data.startsWith('9;')) return false;
    post({ t: 'cwd', id, path: data.slice(2).replace(/^"|"$/g, '') });
    return true;
  });
  // OSC 133;A: a new prompt; working and blocked status records are over.
  term.parser.registerOscHandler(133, (data) => {
    if (data.startsWith('A') && term.buffer.active.type === 'normal') removeActive(id);
    return false;
  });
  // OSC 7501: the Program Status Protocol (idle / working / done / blocked / error).
  term.parser.registerOscHandler(7501, (data) => {
    if (data === '?') {
      post({ t: 'in', id, d: '\x1b]7501;?\x1b\\' });
      return true;
    }
    programStatus(id, data);
    return true;
  });

  // Ctrl-click on a file path previews it in the files panel.
  term.registerLinkProvider({
    provideLinks(y, callback) {
      const line = term.buffer.active.getLine(y - 1);
      if (!line) return callback(undefined);
      const text = line.translateToString(true);
      const links = [];
      const pattern = /(?<![\w])(?:[A-Za-z]:[\\/]|~[\\/])[\w.+@%~\\/-]+/g;
      let match;
      while ((match = pattern.exec(text))) {
        const value = match[0];
        links.push({
          range: { start: { x: match.index + 1, y }, end: { x: match.index + value.length, y } },
          text: value,
          activate: (e, path) => { if (e.ctrlKey) post({ t: 'openPath', id, path }); },
        });
      }
      callback(links);
    },
  });

  term.attachCustomKeyEventHandler((e) => keyHandler(e, term));

  // Right-click: copy the selection, or paste when there is none (Windows Terminal's behaviour).
  el.addEventListener('contextmenu', async (e) => {
    e.preventDefault();
    if (term.hasSelection()) {
      await navigator.clipboard.writeText(term.getSelection());
      term.clearSelection();
    } else {
      const text = await navigator.clipboard.readText();
      if (text) term.paste(text);
    }
  });
}

function keyHandler(e, term) {
  if (e.type !== 'keydown') return true;
  const shortcut = shortcuts.find((s) =>
    s.code === e.code && s.ctrl === e.ctrlKey && s.shift === e.shiftKey && s.alt === e.altKey);
  if (shortcut) {
    e.preventDefault();
    post({ t: 'shortcut', name: shortcut.name });
    return false;
  }
  // Ctrl+C copies when there is a selection, otherwise it is the usual interrupt.
  if (e.ctrlKey && !e.altKey && e.code === 'KeyC' && (e.shiftKey || term.hasSelection())) {
    e.preventDefault();
    if (term.hasSelection()) {
      navigator.clipboard.writeText(term.getSelection());
      term.clearSelection();
    }
    return false;
  }
  // Ctrl+V / Ctrl+Shift+V: let the browser paste; xterm picks it up as a bracketed paste.
  if (e.ctrlKey && !e.altKey && e.code === 'KeyV') return false;
  return true;
}

function show(id) {
  if (current && current !== id) {
    closeFind(false);
    const previous = sessions.get(current);
    if (previous) {
      previous.el.classList.remove('visible');
      disposeWebgl(previous);
    }
  }
  current = id;
  const session = sessions.get(id);
  if (!session) return;
  session.el.classList.add('visible');
  // Only the visible terminal renders with WebGL; browsers cap the number of GL contexts.
  try {
    const webgl = new WebglAddon.WebglAddon();
    webgl.onContextLoss(() => disposeWebgl(session));
    session.term.loadAddon(webgl);
    session.webgl = webgl;
  } catch { session.webgl = null; }
  fitCurrent();
  session.term.focus();
}

function disposeWebgl(session) {
  try { session.webgl?.dispose(); } catch { }
  session.webgl = null;
}

function destroy(id) {
  const session = sessions.get(id);
  if (!session) return;
  disposeWebgl(session);
  session.term.dispose();
  session.el.remove();
  sessions.delete(id);
  if (current === id) current = null;
}

function fitCurrent() {
  const session = current && sessions.get(current);
  if (!session) return;
  try { session.fit.fit(); } catch { }
}

new ResizeObserver(() => fitCurrent()).observe(root);

function applyAppearance(m) {
  appearance = { theme: m.theme, fontFamily: m.fontFamily, fontSize: m.fontSize };
  document.body.style.setProperty('--bg', m.theme.background);
  document.body.style.setProperty('--fg', m.theme.foreground);
  document.body.style.setProperty('--chrome', m.chrome);
  for (const { term } of sessions.values()) {
    term.options.theme = m.theme;
    term.options.fontFamily = m.fontFamily;
    term.options.fontSize = m.fontSize;
  }
  fitCurrent();
}

// MARK: Find (Ctrl+Shift+F)

const find = document.getElementById('find');
const findInput = document.getElementById('findInput');
const findCount = document.getElementById('findCount');
const searchOptions = {
  decorations: {
    matchBackground: '#FFD60A55', matchOverviewRuler: '#FFD60A',
    activeMatchBackground: '#FF9F0A', activeMatchColorOverviewRuler: '#FF9F0A',
  },
};

function openFind() {
  if (!current) return;
  find.classList.add('open');
  findInput.focus();
  findInput.select();
  if (findInput.value) runFind(true);
}

function closeFind(refocus = true) {
  if (!find.classList.contains('open')) return;
  find.classList.remove('open');
  const session = current && sessions.get(current);
  if (!session) return;
  session.search.clearDecorations();
  session.term.clearSelection();
  if (refocus) session.term.focus();
}

function runFind(forward, incremental = false) {
  const session = current && sessions.get(current);
  if (!session) return;
  if (!findInput.value) {
    session.search.clearDecorations();
    showCount(-1, 0);
    return;
  }
  const options = { ...searchOptions, incremental };
  if (forward) session.search.findNext(findInput.value, options);
  else session.search.findPrevious(findInput.value, options);
}

function showCount(index, count) {
  findCount.textContent = !findInput.value ? '' : count === 0 ? 'No results' : index < 0 ? `${count}+` : `${index + 1} of ${count}`;
}

findInput.addEventListener('input', () => runFind(true, true));
findInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); runFind(!e.shiftKey); }
  else if (e.key === 'Escape') { e.preventDefault(); closeFind(); }
});
document.getElementById('findNext').addEventListener('click', () => runFind(true));
document.getElementById('findPrev').addEventListener('click', () => runFind(false));
document.getElementById('findClose').addEventListener('click', () => closeFind());

// The host scans this for file paths mentioned in the session.
window.omuxBufferText = (id) => {
  const session = sessions.get(id);
  if (!session) return '';
  const buffer = session.term.buffer.normal;
  const lines = [];
  for (let i = 0; i < buffer.length; i++) {
    const line = buffer.getLine(i);
    if (!line) continue;
    const text = line.translateToString(true);
    // Join soft-wrapped rows so long paths stay whole.
    if (line.isWrapped && lines.length) lines[lines.length - 1] += text; else lines.push(text);
  }
  return lines.join('\n');
};

// MARK: Program Status Protocol (OSC 7501), the same rules SwiftTerm applies.

function programStatus(id, data) {
  const session = sessions.get(id);
  if (!session || data.length > 4087) return;
  const fields = {};
  for (const pair of data.split(':')) {
    const equal = pair.indexOf('=');
    if (equal < 0) continue;
    const key = pair.slice(0, equal).trim();
    const value = pair.slice(equal + 1).trim();
    if (!/^[a-z]{1,16}$/.test(key) || !/^[A-Za-z0-9_.,+\/=-]*$/.test(value)) continue;
    fields[key] = value;
  }
  const recordId = fields.id ?? '';
  if (fields.state === 'clear') {
    for (const key of [...session.status.keys()]) {
      if (recordId === '' || key === recordId || key.startsWith(recordId + '/')) session.status.delete(key);
    }
    return emitStatus(id);
  }
  if (!['idle', 'working', 'done', 'blocked', 'error'].includes(fields.state)) return;
  const record = {
    state: fields.state,
    kind: fields.state === 'blocked' && ['permission', 'question', 'auth'].includes(fields.kind) ? fields.kind : null,
    message: decodeText(fields.msg),
  };
  // Re-insert so the map's order is oldest update first.
  session.status.delete(recordId);
  session.status.set(recordId, record);
  emitStatus(id);
}

function decodeText(value) {
  if (!value) return null;
  try {
    const bytes = Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    return /[\u0000-\u001f\u007f-\u009f]/.test(text) ? null : text.replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '');
  } catch { return null; }
}

function removeActive(id) {
  const session = sessions.get(id);
  if (!session) return;
  let changed = false;
  for (const [key, record] of session.status) {
    if (record.state === 'working' || record.state === 'blocked') { session.status.delete(key); changed = true; }
  }
  if (changed) emitStatus(id);
}

function processExited(id) { removeActive(id); }

function emitStatus(id) {
  const status = sessions.get(id).status;
  const records = [...status.values()];
  const rootRecord = status.get('') ?? records[records.length - 1];
  if (!rootRecord) return post({ t: 'status', id, state: null, detail: null });
  post({ t: 'status', id, state: rootRecord.state, detail: rootRecord.kind ?? rootRecord.message });
}

post({ t: 'ready' });
