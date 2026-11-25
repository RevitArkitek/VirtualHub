// The model library: one card per exported model, newest first.
//
// Data comes from /api/models, which each plugin's server builds by reading the shared library
// folder. Nothing is stored in this page, so an export made in Revit shows up here on refresh —
// and the folder is the only source of truth, which is why deleting a model folder deletes its card.

const state = { models: [], filter: 'all', query: '', loaded: false };

const els = {
  grid: document.getElementById('grid'),
  empty: document.getElementById('empty'),
  error: document.getElementById('error'),
  errorText: document.getElementById('errorText'),
  nomatch: document.getElementById('nomatch'),
  nomatchText: document.getElementById('nomatchText'),
  nomatchClear: document.getElementById('nomatchClear'),
  sub: document.getElementById('sub'),
  tabs: document.getElementById('tabs'),
  foot: document.getElementById('foot'),
  refresh: document.getElementById('refresh'),
  search: document.getElementById('search'),
  clearSearch: document.getElementById('clearSearch'),
};

const SOURCES = [
  { key: 'all', label: 'All' },
  { key: 'revit', label: 'Revit', pill: 'revit' },
  { key: 'ingetrazo', label: 'IngeTrazo', pill: 'ingetrazo' },
];

function pillFor(source) {
  const s = SOURCES.find((x) => x.key === source);
  return s && s.pill ? s.pill : 'other';
}

function pillText(model) {
  if (model.source === 'revit') return 'Revit';
  if (model.source === 'ingetrazo') return 'IngeTrazo';
  return model.sourceLabel || 'Model';
}

function mb(bytes) {
  if (!bytes) return null;
  return (bytes / 1048576).toFixed(bytes >= 10485760 ? 0 : 1) + ' MB';
}

function tris(n) {
  if (!n) return null;
  if (n >= 1e6) return (n / 1e6).toFixed(n >= 1e7 ? 0 : 1) + 'M tris';
  if (n >= 1e3) return Math.round(n / 1e3) + 'k tris';
  return n + ' tris';
}

function when(iso) {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return null;
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return Math.floor(s / 60) + ' min ago';
  if (s < 86400) return Math.floor(s / 3600) + ' h ago';
  if (s < 7 * 86400) return Math.floor(s / 86400) + ' d ago';
  return new Date(t).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function card(model) {
  const a = document.createElement('a');
  a.className = 'card';
  a.href = '/viewer.html?model=' + encodeURIComponent(model.slug);

  const head = document.createElement('div');
  head.className = 'head';
  const name = document.createElement('h2');
  name.className = 'name';
  name.textContent = model.name || model.slug;
  const pill = document.createElement('span');
  pill.className = 'pill ' + pillFor(model.source);
  pill.textContent = pillText(model);
  if (model.sourceLabel) pill.title = model.sourceLabel;
  head.append(name, pill);

  const file = document.createElement('p');
  file.className = 'file';
  file.textContent = model.fileName || model.slug;
  if (model.view) {
    const v = document.createElement('span');
    v.className = 'view';
    v.textContent = model.view;
    file.append(v);
  }

  const stats = document.createElement('div');
  stats.className = 'stats';
  const bits = [when(model.exportedUtc), mb(model.glbBytes), tris(model.triangles)]
    .filter(Boolean);
  for (const b of bits) {
    const s = document.createElement('span');
    s.textContent = b;
    stats.append(s);
  }

  const cta = document.createElement('span');
  cta.className = 'cta';
  cta.append(document.createTextNode('Walk the model'));
  const arrow = document.createElement('span');
  arrow.className = 'arrow';
  arrow.textContent = '→';
  cta.append(arrow);

  a.append(head, file, stats, cta);
  return a;
}

function renderTabs() {
  els.tabs.replaceChildren();
  for (const s of SOURCES) {
    const n = s.key === 'all'
      ? state.models.length
      : state.models.filter((m) => m.source === s.key).length;
    if (s.key !== 'all' && n === 0) continue;      // no dead tabs for a source you never used
    const b = document.createElement('button');
    b.type = 'button';
    b.setAttribute('aria-pressed', String(state.filter === s.key));
    b.textContent = s.label;
    const count = document.createElement('span');
    count.className = 'n';
    count.textContent = n;
    b.append(count);
    b.addEventListener('click', () => {
      state.filter = s.key;
      render();
    });
    els.tabs.append(b);
  }
}

// Everything a card says about the model itself, lowercased, so a query like "level 2" matches on
// any two words in any order. The source pill is deliberately left out: the tabs already filter by
// plugin, and matching it here would make "revit" a search term as well as a filter.
function haystack(model) {
  return [
    model.name, model.slug, model.fileName, model.view,
  ].filter(Boolean).join(' ').toLowerCase();
}

function queryTokens() {
  return state.query.trim().toLowerCase().split(/\s+/).filter(Boolean);
}

function setQuery(value) {
  state.query = value;
  if (els.search.value !== value) els.search.value = value;
  els.clearSearch.hidden = value === '';
  renderList();                                   // typing should not rebuild the tabs under the pointer
}

function renderList() {
  const tokens = queryTokens();
  const bySource = state.filter === 'all'
    ? state.models
    : state.models.filter((m) => m.source === state.filter);
  const shown = tokens.length
    ? bySource.filter((m) => {
        const hay = haystack(m);
        return tokens.every((t) => hay.includes(t));
      })
    : bySource;

  els.grid.replaceChildren(...shown.map(card));
  els.empty.hidden = state.models.length > 0 || !state.loaded;
  els.grid.hidden = shown.length === 0;
  els.nomatch.hidden = !(state.loaded && state.models.length > 0 && shown.length === 0);
  if (!els.nomatch.hidden) {
    const q = state.query.trim();
    const scope = state.filter === 'all' ? 'the library' : pillText({ source: state.filter });
    els.nomatchText.textContent = q
      ? 'Nothing in ' + scope + ' matches “' + q + '”. Searches model names, file names and view '
        + 'names.'
      : 'Nothing in ' + scope + ' to show.';
  }

  if (!state.loaded) {
    els.sub.textContent = 'Loading the library…';
  } else if (state.models.length === 0) {
    els.sub.textContent = 'Nothing exported yet.';
  } else if (tokens.length) {
    els.sub.textContent = shown.length + ' of ' + bySource.length + ' '
      + (bySource.length === 1 ? 'model' : 'models') + ' match';
  } else {
    const newest = state.models[0];
    els.sub.textContent = state.models.length === 1
      ? '1 model'
      : state.models.length + ' models';
    if (newest && newest.exportedUtc) {
      els.sub.textContent += ' · most recent ' + when(newest.exportedUtc);
    }
  }

  els.foot.textContent = state.library
    ? 'Library folder: ' + state.library + ' — re-exporting a file replaces its card.'
    : 'Re-exporting a file replaces its card.';
}

function render() {
  renderTabs();
  renderList();
}

async function load() {
  els.refresh.disabled = true;
  try {
    const res = await fetch('/api/models', { cache: 'no-store' });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const data = await res.json();
    state.models = (data.models || []).slice().sort((a, b) =>
      String(b.exportedUtc || '').localeCompare(String(a.exportedUtc || '')));
    state.library = data.library || '';
    state.loaded = true;
    els.error.hidden = true;
  } catch (err) {
    state.loaded = true;
    els.errorText.textContent = 'The server did not return the model list (' + err.message + '). '
      + 'It may be an older build of the plugin’s server, which serves one scene and no library.';
    els.error.hidden = false;
    els.grid.hidden = true;
    els.empty.hidden = true;
    els.sub.textContent = 'Library unavailable';
  } finally {
    els.refresh.disabled = false;
    render();
  }
}

// The page is kept open on the headset while you go back and forth from a model, so re-read the
// library whenever it becomes visible again: that is what makes a fresh export appear without
// anyone thinking about it.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && state.loaded) load();
});
els.refresh.addEventListener('click', load);

els.search.addEventListener('input', () => setQuery(els.search.value));
els.search.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    e.preventDefault();
    setQuery('');
    els.search.blur();
  }
});
els.clearSearch.addEventListener('click', () => {
  setQuery('');
  els.search.focus();
});
els.nomatchClear.addEventListener('click', () => {
  setQuery('');
  els.search.focus();
});

// "/" jumps to the search box, the way it does in a file manager — but not while typing a query
// or with a modifier held, so it never eats a slash you meant to type.
document.addEventListener('keydown', (e) => {
  if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
  const t = e.target;
  if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
  e.preventDefault();
  els.search.focus();
  els.search.select();
});

load();
