// ProcessTracker dashboard: read-only, no framework. All API text goes in through textContent, never innerHTML.
(function () {
  'use strict';

  var POLL_MS = 2000;
  var STALE_S = 6;
  var GB = 1024 * 1024 * 1024;
  var MB = 1024 * 1024;
  var KEY = 'processtracker.dashboard.v1';

  // ---- persisted UI state (every storage access is guarded) ----
  var ui = { tab: 'sessions', expanded: {}, collapsed: {}, sort: { key: 'priv', dir: -1 }, filter: '' };
  function loadUi() {
    try {
      var raw = window.localStorage.getItem(KEY);
      if (raw) {
        var o = JSON.parse(raw);
        if (o && typeof o === 'object') {
          if (typeof o.tab === 'string') ui.tab = o.tab;
          if (o.expanded && typeof o.expanded === 'object') ui.expanded = o.expanded;
          if (o.collapsed && typeof o.collapsed === 'object') ui.collapsed = o.collapsed;
          if (o.sort && typeof o.sort.key === 'string' && (o.sort.dir === 1 || o.sort.dir === -1)) ui.sort = o.sort;
          if (typeof o.filter === 'string') ui.filter = o.filter;
        }
      }
    } catch (e) { /* storage unavailable or corrupt: keep defaults */ }
  }
  function saveUi() {
    try { window.localStorage.setItem(KEY, JSON.stringify(ui)); } catch (e) { /* ignore */ }
  }

  // ---- helpers ----
  var $ = function (id) { return document.getElementById(id); };
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = String(text);
    return e;
  }
  function gb(bytes) { return (bytes / GB).toFixed(2); }
  function cores(c) { return c.toFixed(2); }
  // Byte deltas cover one collector tick; the service reports its length.
  function mbps(bytes) {
    var sec = (snap && snap.sampleIntervalSeconds > 0) ? snap.sampleIntervalSeconds : 2;
    var v = bytes / MB / sec;
    return v >= 0.05 ? v.toFixed(1) : '0';
  }
  function idleText(sec) {
    if (sec == null || !isFinite(sec)) return '-';
    if (sec < 60) return Math.max(0, Math.floor(sec)) + 's';
    var m = Math.floor(sec / 60);
    if (m < 60) return m + 'm';
    var h = Math.floor(m / 60);
    if (h < 48) return h + 'h';
    return Math.floor(h / 24) + 'd';
  }
  function parseMs(s) { if (!s) return null; var t = Date.parse(s); return isNaN(t) ? null : t; }

  var snap = null;        // last good overview
  var snapAt = 0;         // local time it arrived
  var snapExpand = '';    // the expand list that snapshot was requested with
  var fetchFailed = false;

  // Seconds idle relative to the snapshot's own time, so the display does not drift between polls.
  function idleOf(lastIso, nowMs) {
    var t = parseMs(lastIso);
    return t == null ? null : Math.max(0, (nowMs - t) / 1000);
  }

  // ---- view models ----
  function procNode(p, nowMs) {
    return {
      kind: 'proc', id: p.key, name: p.imageName, pid: p.pid,
      cpu: p.cpuCores, priv: p.privateBytes, ws: p.workingSet,
      read: p.readBytesDelta, write: p.writeBytesDelta,
      idle: idleOf(p.effectiveLastActiveUtc, nowMs),
      state: p.state, alertable: p.alertable, procs: 1, children: []
    };
  }
  // The service sends process rows flat (parentKey); a row whose parent is not among the same session's rows is a root.
  function buildForest(rows, nowMs) {
    var nodes = {};
    rows.forEach(function (p) { nodes[p.key] = procNode(p, nowMs); });
    var roots = [];
    rows.forEach(function (p) {
      var parent = p.parentKey != null ? nodes[p.parentKey] : null;
      if (parent && parent !== nodes[p.key]) parent.children.push(nodes[p.key]); else roots.push(nodes[p.key]);
    });
    return roots;
  }
  function sessionNode(o, rowsByUnit, nowMs) {
    var s = o.session;
    var kids = buildForest(rowsByUnit[s.unitId] || [], nowMs);
    var idle = o.lastActiveUtc ? idleOf(o.lastActiveUtc, nowMs) : idleOf(s.lastTranscriptWriteUtc, nowMs);
    return {
      kind: 'session', id: s.unitId, name: s.title || s.unitId, pid: null, unit: s.unitId,
      cpu: s.cpuCores, priv: s.privateBytes, ws: null, read: null, write: null,
      idle: idle, state: s.isLive ? 'live' : 'ended', live: s.isLive, procs: s.processCount, children: kids
    };
  }

  // ---- sorting / filtering ----
  function val(n, key) {
    switch (key) {
      case 'name': return (n.name || '').toLowerCase();
      case 'state': return n.state;
      case 'cpu': return n.cpu;
      case 'priv': return n.priv;
      case 'ws': return n.ws == null ? -1 : n.ws;
      case 'read': return n.read == null ? -1 : n.read;
      case 'write': return n.write == null ? -1 : n.write;
      case 'idle': return n.idle == null ? -1 : n.idle;
      case 'procs': return n.procs;
      default: return 0;
    }
  }
  function sortSiblings(list) {
    var k = ui.sort.key, d = ui.sort.dir;
    return list.slice().sort(function (a, b) {
      var x = val(a, k), y = val(b, k);
      if (x < y) return -d;
      if (x > y) return d;
      return a.pid - b.pid || 0;
    });
  }
  function matches(n, q) {
    if (!q) return true;
    if ((n.name || '').toLowerCase().indexOf(q) >= 0) return true;
    return n.pid != null && String(n.pid).indexOf(q) >= 0;
  }
  // A node is shown when it, a descendant, or (for sessions) the session itself matches.
  function pruneTree(n, q, parentHit) {
    var hit = parentHit || matches(n, q);
    var kids = [];
    n.children.forEach(function (c) {
      var r = pruneTree(c, q, hit);
      if (r) kids.push(r);
    });
    if (!hit && kids.length === 0) return null;
    var copy = Object.assign({}, n);
    copy.children = kids;
    return copy;
  }

  // ---- header ----
  function setKpi(id, text, cls) {
    var b = $(id).querySelector('b');
    b.textContent = text;
    b.className = cls || '';
  }
  function renderHeader() {
    var load = snap && snap.load;
    if (load) {
      setKpi('k-pressure', load.pressure, 'p-' + load.pressure);
      setKpi('k-cpu', load.cpuBusyPercent.toFixed(0) + '%');
      setKpi('k-cores', load.freeCores.toFixed(1));
      setKpi('k-mem', load.availableGb.toFixed(1) + ' GB');
      setKpi('k-commit', load.commitHeadroomGb.toFixed(0) + ' GB');
      setKpi('k-par', load.suggestedParallelism);
    }
    if (snap) setKpi('k-time', new Date(snapAt).toLocaleTimeString());
    var age = snap ? (Date.now() - parseMs(snap.timestampUtc)) / 1000 : Infinity;
    var stale = fetchFailed || !snap || age > STALE_S;
    var s = $('stale');
    s.hidden = !stale;
    s.textContent = fetchFailed ? 'stale: service unreachable' : (snap ? 'stale: snapshot ' + Math.round(age) + ' s old' : 'waiting for data');
  }

  // ---- sessions tab ----
  var COLS = [
    ['name', 'Process', false], ['state', 'State', false], ['cpu', 'CPU cores', true], ['priv', 'Private GB', true],
    ['ws', 'WS GB', true], ['read', 'Read MB/s', true], ['write', 'Write MB/s', true], ['idle', 'Idle', true], ['procs', 'Procs', true]
  ];
  function header() {
    var h = el('div', 'hdr');
    COLS.forEach(function (c) {
      var s = el('span', (c[2] ? 'num ' : '') + (ui.sort.key === c[0] ? 'sorted' + (ui.sort.dir === 1 ? ' asc' : '') : ''), c[1]);
      s.addEventListener('click', function () {
        if (ui.sort.key === c[0]) ui.sort.dir = -ui.sort.dir;
        else ui.sort = { key: c[0], dir: c[0] === 'name' || c[0] === 'state' ? 1 : -1 };
        saveUi();
        renderSessions();
      });
      h.appendChild(s);
    });
    return h;
  }
  function isOpen(n, q) {
    if (q) return true; // while filtering, show the matches
    if (n.kind === 'session') return ui.expanded[n.id] === true;
    return ui.collapsed[n.id] !== true;
  }
  function addRow(parent, n, depth, q) {
    var row = el('div', 'row ' + n.kind);
    var name = el('div', 'name');
    name.style.setProperty('--depth', String(depth));
    // A collapsed session's rows are not fetched, so it is expandable by its process count, not by loaded rows.
    var hasKids = n.kind === 'session' ? n.procs > 0 : n.children.length > 0;
    var tw = el('button', 'tw' + (hasKids ? '' : ' leaf'), isOpen(n, q) ? '▼' : '▶');
    tw.type = 'button';
    tw.setAttribute('aria-label', 'expand or collapse');
    if (hasKids) {
      tw.addEventListener('click', function () {
        if (n.kind === 'session') ui.expanded[n.id] = !(ui.expanded[n.id] === true);
        else ui.collapsed[n.id] = !(ui.collapsed[n.id] === true);
        saveUi();
        renderSessions();
        if (n.kind === 'session') refreshSoon(); // fetch (or drop) this session's rows now
      });
    }
    name.appendChild(tw);
    name.appendChild(el('span', 't', n.name));
    if (n.pid != null) name.appendChild(el('span', 'pid', n.pid));
    if (n.alertable) { var a = el('span', 'alert', '!'); a.title = 'alertable'; name.appendChild(a); }
    name.title = n.kind === 'session' ? n.unit : n.name + ' ' + n.pid;
    row.appendChild(name);
    row.appendChild(el('span', n.kind === 'session' ? (n.live ? 'live' : 'ended') : 's-' + n.state, n.state));
    var live = n.kind !== 'session' || n.live;
    row.appendChild(el('span', 'num', live ? cores(n.cpu) : '—'));
    row.appendChild(el('span', 'num', live ? gb(n.priv) : '—'));
    row.appendChild(el('span', 'num', n.ws == null ? '' : gb(n.ws)));
    row.appendChild(el('span', 'num', n.read == null ? '' : mbps(n.read)));
    row.appendChild(el('span', 'num', n.write == null ? '' : mbps(n.write)));
    row.appendChild(el('span', 'num', idleText(n.idle)));
    row.appendChild(el('span', 'num', live ? n.procs : '0'));
    parent.appendChild(row);
    if (hasKids && isOpen(n, q)) {
      sortSiblings(n.children).forEach(function (c) { addRow(parent, c, depth + 1, q); });
    }
  }
  // Forget expand/collapse state of sessions and processes that are no longer in the snapshot.
  // Rows of collapsed sessions are not loaded, so process ids cannot be checked against the snapshot: sessions are
  // pruned exactly, collapsed processes are capped (oldest entries go first).
  var MAX_COLLAPSED = 500;
  function pruneState(sessions) {
    var ids = {};
    sessions.forEach(function (n) { ids[n.id] = true; });
    var changed = false;
    Object.keys(ui.expanded).forEach(function (id) { if (!ids[id]) { delete ui.expanded[id]; changed = true; } });
    var keys = Object.keys(ui.collapsed);
    if (keys.length > MAX_COLLAPSED) {
      keys.slice(0, keys.length - MAX_COLLAPSED).forEach(function (id) { delete ui.collapsed[id]; });
      changed = true;
    }
    if (changed) saveUi();
  }
  function renderSessions() {
    var pane = $('tab-sessions');
    pane.textContent = '';
    if (!snap) { pane.appendChild(el('div', 'empty', 'Loading…')); return; }
    var nowMs = parseMs(snap.timestampUtc) || Date.now();
    var q = ui.filter.trim().toLowerCase();
    var rowsByUnit = {};
    snap.processes.forEach(function (p) { (rowsByUnit[p.unitId] = rowsByUnit[p.unitId] || []).push(p); });
    var sessions = snap.sessions.map(function (o) { return sessionNode(o, rowsByUnit, nowMs); });
    pruneState(sessions);
    if (q) sessions = sessions.map(function (s) { return pruneTree(s, q, false); }).filter(Boolean);
    // live first, then by memory (the column sort applies within each session's tree)
    sessions.sort(function (a, b) { return (b.live ? 1 : 0) - (a.live ? 1 : 0) || b.priv - a.priv; });
    var wrap = el('div', 'scroll');
    var grid = el('div', 'grid');
    grid.appendChild(header());
    if (sessions.length === 0) grid.appendChild(el('div', 'empty', q ? 'No match.' : 'No sessions.'));
    sessions.forEach(function (s) { addRow(grid, s, 0, q); });
    wrap.appendChild(grid);
    pane.appendChild(wrap);
  }

  // ---- orphans tab ----
  var copied = { pid: null, ok: false }; // survives re-renders for the 1.5 s the button shows its result
  function paintCopy(btn, pid) {
    var on = copied.pid === pid;
    btn.textContent = on ? (copied.ok ? 'copied' : 'copy failed') : 'copy kill command';
    btn.className = 'copy' + (on && copied.ok ? ' done' : '');
  }
  function copyText(pid, btn) {
    var text = 'proctrack kill ' + pid + ' --tree';
    function done(ok) {
      copied = { pid: pid, ok: ok };
      paintCopy(btn, pid);
      setTimeout(function () {
        if (copied.pid === pid) { copied = { pid: null, ok: false }; }
        if (btn.isConnected) paintCopy(btn, pid);
      }, 1500);
    }
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { done(true); }, function () { done(false); });
        return;
      }
    } catch (e) { /* fall through */ }
    done(false);
  }
  function renderOrphans() {
    var pane = $('tab-orphans');
    pane.textContent = '';
    var q = ui.filter.trim().toLowerCase();
    var list = snap ? snap.orphans : [];
    // already alertable first then by memory from the service; re-assert it so the page does not depend on it
    list = list.slice().sort(function (a, b) {
      return (b.alertable ? 1 : 0) - (a.alertable ? 1 : 0) || b.privateBytes - a.privateBytes || a.pid - b.pid;
    });
    if (q) list = list.filter(function (p) {
      return p.imageName.toLowerCase().indexOf(q) >= 0 || String(p.pid).indexOf(q) >= 0
        || (p.unitId || '').toLowerCase().indexOf(q) >= 0;
    });
    $('orphan-count').textContent = snap ? '(' + snap.orphans.length + ')' : '';
    if (!snap) { pane.appendChild(el('div', 'empty', 'Loading…')); return; }
    if (list.length === 0) { pane.appendChild(el('div', 'empty', q ? 'No match.' : 'No orphans.')); return; }
    var titles = {};
    snap.sessions.forEach(function (o) { titles[o.session.unitId] = o.session.title || o.session.unitId; });
    var nowMs = parseMs(snap.timestampUtc) || Date.now();
    var wrap = el('div', 'scroll');
    var table = el('table');
    var thead = el('thead');
    var hr = el('tr');
    [['Process', ''], ['PID', 'num'], ['Session', ''], ['Private GB', 'num'], ['Idle', 'num'], ['', '']].forEach(function (c) {
      hr.appendChild(el('th', c[1], c[0]));
    });
    thead.appendChild(hr);
    table.appendChild(thead);
    var tb = el('tbody');
    list.forEach(function (p) {
      var tr = el('tr');
      tr.appendChild(el('td', p.alertable ? 'alert' : 's-Orphan', p.imageName + (p.alertable ? '  (alertable)' : '')));
      tr.appendChild(el('td', 'num mono', p.pid));
      var u = p.unitId;
      tr.appendChild(el('td', '', u ? (titles[u] || u) : '—'));
      tr.appendChild(el('td', 'num', gb(p.privateBytes)));
      tr.appendChild(el('td', 'num', idleText(idleOf(p.effectiveLastActiveUtc, nowMs))));
      var td = el('td');
      var btn = el('button', 'copy', 'copy kill command');
      btn.type = 'button';
      paintCopy(btn, p.pid);
      btn.addEventListener('click', function () { copyText(p.pid, btn); });
      td.appendChild(btn);
      tr.appendChild(td);
      tb.appendChild(tr);
    });
    table.appendChild(tb);
    wrap.appendChild(table);
    pane.appendChild(wrap);
  }

  // ---- load tab ----
  function card(label, value, cls) {
    var c = el('div', 'card');
    c.appendChild(el('div', 'lbl', label));
    c.appendChild(el('div', 'val ' + (cls || ''), value));
    return c;
  }
  function renderLoad() {
    var pane = $('tab-load');
    pane.textContent = '';
    if (!snap) { pane.appendChild(el('div', 'empty', 'Loading…')); return; }
    var l = snap.load;
    var cards = el('div', 'cards');
    cards.appendChild(card('Pressure', l.pressure, 'p-' + l.pressure));
    cards.appendChild(card('CPU busy', l.cpuBusyPercent.toFixed(1) + '%'));
    cards.appendChild(card('Free cores', l.freeCores.toFixed(1)));
    cards.appendChild(card('Available memory', l.availableGb.toFixed(1) + ' GB'));
    cards.appendChild(card('Commit headroom', l.commitHeadroomGb.toFixed(1) + ' GB'));
    cards.appendChild(card('Suggested parallelism', l.suggestedParallelism));
    pane.appendChild(cards);
    pane.appendChild(el('h2', '', 'Top sessions'));
    if (!l.topSessions.length) { pane.appendChild(el('div', 'empty', 'No sessions.')); return; }
    var wrap = el('div', 'scroll');
    var table = el('table');
    var hr = el('tr');
    [['Session', ''], ['CPU cores', 'num'], ['Private GB', 'num'], ['Procs', 'num']].forEach(function (c) { hr.appendChild(el('th', c[1], c[0])); });
    var thead = el('thead'); thead.appendChild(hr); table.appendChild(thead);
    var tb = el('tbody');
    l.topSessions.forEach(function (s) {
      var tr = el('tr');
      var name = el('td', '', s.title || s.unitId);
      if (s.cwd) name.title = s.cwd;
      tr.appendChild(name);
      tr.appendChild(el('td', 'num', cores(s.cpuCores)));
      tr.appendChild(el('td', 'num', s.privateGb.toFixed(2)));
      tr.appendChild(el('td', 'num', s.processCount));
      tb.appendChild(tr);
    });
    table.appendChild(tb);
    wrap.appendChild(table);
    pane.appendChild(wrap);
  }

  // ---- tabs ----
  var TABS = ['sessions', 'orphans', 'load'];
  function render() {
    renderHeader();
    renderSessions();
    renderOrphans();
    renderLoad();
  }
  function showTab(name) {
    if (TABS.indexOf(name) < 0) name = 'sessions';
    ui.tab = name;
    TABS.forEach(function (t) { $('tab-' + t).hidden = t !== name; });
    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-tab') === name);
    });
    saveUi();
  }

  // ---- polling: one request at a time, paused while hidden ----
  var inFlight = false;
  var timer = null;
  function schedule(ms) {
    if (timer) clearTimeout(timer);
    timer = setTimeout(poll, ms);
  }
  var MAX_EXPAND = 100; // keeps the request line well under Kestrel's 8 KB limit
  var pendingRefresh = false;
  // The sessions whose process rows the page needs: the expanded ones, or every session with processes while a
  // filter is active (the filter searches process names and PIDs, which collapsed sessions do not load).
  function wantedExpand() {
    var ids;
    if (ui.filter.trim() && snap) {
      ids = snap.sessions.filter(function (o) { return o.session.processCount > 0; })
        .sort(function (a, b) { return b.session.privateBytes - a.session.privateBytes; })
        .map(function (o) { return o.session.unitId; });
    } else {
      ids = Object.keys(ui.expanded).filter(function (id) { return ui.expanded[id] === true; });
    }
    return ids.slice(0, MAX_EXPAND).sort();
  }
  // Fetch now (after an expand/collapse or filter change) instead of waiting for the next 2 s poll.
  function refreshSoon() {
    if (inFlight) pendingRefresh = true; else poll();
  }
  function poll() {
    if (timer) { clearTimeout(timer); timer = null; } // never two timer chains
    if (document.visibilityState === 'hidden') return; // resumed by visibilitychange
    if (inFlight) { schedule(POLL_MS); return; }
    inFlight = true;
    var expand = wantedExpand().join(',');
    var url = '/api/overview' + (expand ? '?expand=' + encodeURIComponent(expand) : '');
    var ctl = typeof AbortController === 'function' ? new AbortController() : null;
    var abort = ctl ? setTimeout(function () { ctl.abort(); }, 8000) : null;
    fetch(url, { cache: 'no-store', credentials: 'same-origin', signal: ctl ? ctl.signal : undefined })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (o) {
        var same = snap && snap.timestampUtc === o.timestampUtc && snapExpand === expand;
        snap = o; snapExpand = expand; snapAt = Date.now(); fetchFailed = false;
        return !same;
      })
      .catch(function () { fetchFailed = true; return false; })
      .then(function (changed) {
        if (abort) clearTimeout(abort);
        inFlight = false;
        if (changed) render(); else renderHeader(); // same tick and rows: nothing to repaint
        if (pendingRefresh) { pendingRefresh = false; poll(); } else schedule(POLL_MS);
      });
  }

  function init() {
    loadUi();
    $('filter').value = ui.filter;
    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (b) {
      b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
    });
    $('filter').addEventListener('input', function () {
      ui.filter = $('filter').value;
      saveUi();
      renderSessions();
      renderOrphans();
      if (wantedExpand().join(',') !== snapExpand) refreshSoon(); // the filter changed which rows are needed
    });
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'visible') { if (!inFlight) poll(); }
      else if (timer) { clearTimeout(timer); timer = null; }
    });
    // The stale badge must react even when no fetch completes (service hung or gone).
    setInterval(function () { if (document.visibilityState === 'visible') renderHeader(); }, 1000);
    showTab(ui.tab);
    render();
    poll();
  }
  init();
})();
