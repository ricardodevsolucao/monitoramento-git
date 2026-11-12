'use strict';
(() => {
  const MB = 1024 * 1024, GB = 1024 * MB;
  const $ = (s, r = document) => r.querySelector(s);
  const nf0 = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });
  const nf1 = new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const nf2 = new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const dtf = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'medium' });
  const tf = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  // ---------- formatação ----------
  const fmtBytes = (b) => b == null ? '—' : b >= GB ? (b >= 10 * GB ? nf1 : nf2).format(b / GB) + ' GB' : b >= MB ? nf0.format(b / MB) + ' MB' : nf0.format(b / 1024) + ' KB';
  const fmtPct = (v) => { v = v || 0; return v < 0.05 ? '0%' : (v >= 10 ? nf0 : nf1).format(v) + '%'; };
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const plural = (n, s, p) => `${nf0.format(n)} ${n === 1 ? s : p}`;
  // Mostra o FIM do caminho (o que identifica a pasta): "…\Projetos\gsintegra". O texto completo vai no tooltip.
  function shortPath(p, max = 52) {
    if (!p || p.length <= max) return p || '';
    const SEP = String.fromCharCode(92), ELL = String.fromCharCode(8230);
    const parts = p.split(new RegExp('[' + SEP + SEP + '/]')).filter(Boolean);
    let out = parts[parts.length - 1] || p;
    if (out.length >= max - 2) return ELL + out.slice(-(max - 2));
    for (let i = parts.length - 2; i >= 0; i--) {
      const next = parts[i] + SEP + out;
      if (next.length > max - 2) break;
      out = next;
    }
    return ELL + SEP + out;
  }
  // Caminho dentro de um elemento com direction:rtl (reticências à esquerda se ainda faltar espaço): o embutido LTR mantém a ordem.
  const pathText = (p) => String.fromCharCode(0x202A) + p + String.fromCharCode(0x202C);
  function since(iso) {
    if (!iso) return '—';
    const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
    const rel = m < 1 ? 'agora' : m < 60 ? `há ${m} min` : m < 1440 ? `há ${Math.floor(m / 60)} h ${m % 60} min` : `há ${Math.floor(m / 1440)} d`;
    return `${dtf.format(new Date(iso))} (${rel})`;
  }
  const hue = (s) => { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) % 360; return h; };
  const st = (el, v) => { if (el.textContent !== v) el.textContent = v; };
  const setAttr = (el, k, v) => { if (el.getAttribute(k) !== v) el.setAttribute(k, v); };

  // ---------- ícones SVG inline ----------
  const P = {
    chevron: '<path d="m9 6 6 6-6 6"/>',
    star: '<path d="m12 3.6 2.55 5.3 5.8.8-4.2 4.1 1 5.8L12 16.8l-5.15 2.8 1-5.8-4.2-4.1 5.8-.8z"/>',
    folder: '<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.3a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>',
    terminal: '<path d="m5 8 4 4-4 4M12 17h7"/>',
    bookmark: '<path d="M7 4.5h10v15l-5-3.6-5 3.6z"/>',
    stop: '<rect x="6.5" y="6.5" width="11" height="11" rx="2"/>',
    play: '<path d="M8.5 6v12l10-6z"/>',
    x: '<path d="M6 6l12 12M18 6 6 18"/>',
    copy: '<rect x="8.5" y="8.5" width="10" height="11" rx="2"/><path d="M15.5 8.5V6.5a2 2 0 0 0-2-2h-6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h1"/>',
    branch: '<circle cx="7" cy="6" r="2"/><circle cx="7" cy="18" r="2"/><circle cx="17" cy="9" r="2"/><path d="M7 8v8M17 11c0 3-4 3-8.5 5.2"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    edit: '<path d="M5 19l1-4L16.5 4.5a2 2 0 0 1 3 3L9 18z"/>',
    trash: '<path d="M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4"/>',
    moon: '<path d="M19 14.5A7.5 7.5 0 0 1 9.5 5 7.5 7.5 0 1 0 19 14.5z"/>',
    auto: '<rect x="3.5" y="5" width="17" height="11" rx="2"/><path d="M9 20h6M12 16v4"/>',
    alert: '<path d="M12 4 3 19h18z"/><path d="M12 10v4M12 16.8v.2"/>',
    lock: '<rect x="5.5" y="11" width="13" height="8.5" rx="2"/><path d="M8.5 11V8.5a3.5 3.5 0 0 1 7 0V11"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
    graph: '<circle cx="6" cy="6" r="2"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="12" r="2"/><path d="M6 8v8M8 6h3a5 5 0 0 1 5 5"/>',
    tree: '<path d="M6 4v8a3 3 0 0 0 3 3h9M6 4h0M18 15l-3-3M18 15l-3 3"/>',
    refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4"/>',
    eraser: '<path d="M5 19h14M8.5 15.5l7.5-9.5 3 2.4-7.5 9.5z"/>',
    wt: '<rect x="3.5" y="4.5" width="10" height="7" rx="1.5"/><rect x="10.5" y="12.5" width="10" height="7" rx="1.5"/>',
    dock: '<rect x="3.5" y="5" width="17" height="14" rx="2"/><path d="M14.5 5v14"/>',
    grid: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>',
  };
  const icon = (n) => `<svg class="ic" viewBox="0 0 24 24" aria-hidden="true">${P[n]}</svg>`;
  const ibtn = (act, ic, label, extra = '') => `<button type="button" class="ibtn ${extra}" data-act="${act}" aria-label="${esc(label)}" title="${esc(label)}">${icon(ic)}</button>`;

  // ---------- ponte com o host ----------
  function createBridge(wv) {
    let seq = 0;
    const pending = new Map();
    const pushHandlers = [];
    wv.addEventListener('message', (ev) => {
      let m = ev.data;
      if (typeof m === 'string') { try { m = JSON.parse(m); } catch { return; } }
      if (!m) return;
      if (m.replyTo) {
        const p = pending.get(m.replyTo);
        if (!p) return;
        pending.delete(m.replyTo);
        clearTimeout(p.timer);
        if (m.ok) p.resolve(m.data); else { const err = new Error(m.error || 'A operação falhou.'); err.code = m.code ?? null; p.reject(err); }
      } else if (m.type) pushHandlers.forEach((h) => h(m));
    });
    return {
      onPush(h) { pushHandlers.push(h); },
      request(type, payload = {}, timeoutMs = 15000) {
        return new Promise((resolve, reject) => {
          const id = 'r' + (++seq);
          const timer = setTimeout(() => { pending.delete(id); reject(new Error('O aplicativo não respondeu a tempo. Tente novamente.')); }, timeoutMs);
          pending.set(id, { resolve, reject, timer });
          wv.postMessage({ id, type, payload });
        });
      },
    };
  }
  function loadMock() {
    return new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = 'mock-bridge.js';
      s.onload = () => resolve(window.MockWebView);
      s.onerror = () => reject(new Error('Não foi possível carregar a ponte de simulação.'));
      document.head.appendChild(s);
    });
  }

  // ---------- estado ----------
  const state = {
    snap: null, config: null, favs: new Set(), tab: 'apps', query: '', sort: 'memory',
    expanded: new Set(), selected: null, icons: new Map(), iconPending: new Set(),
    pidIndex: new Map(), hovering: false, forceResort: false, order: new Map(), busy: new Set(),
    fatal: null, theme: 'system',
  };
  let bridge = null;
  const nodes = new Map();
  const listEl = $('#list'), wrapEl = $('#listwrap');

  // ---------- toasts / diálogos ----------
  function toast(msg, kind = 'info') {
    const t = document.createElement('div');
    t.className = 'toast ' + kind;
    t.innerHTML = icon(kind === 'error' ? 'alert' : 'check') + '<span></span>';
    t.lastChild.textContent = msg;
    const box = $('#toasts');
    while (box.children.length >= 3) box.firstChild.remove();
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'error' ? 6500 : 3200);
  }
  async function call(type, payload, okMsg) {
    try { const d = await bridge.request(type, payload); if (okMsg) toast(okMsg); return d; }
    catch (e) { toast(e.message, 'error'); return null; }
  }
  function confirmDialog({ title, body, ok, danger = true, check = null }) {
    return new Promise((resolve) => {
      const dlg = $('#confirmDlg');
      $('#cfTitle').textContent = title; $('#cfBody').textContent = body;
      const okb = $('#cfOk'); okb.textContent = ok; okb.className = 'btn ' + (danger ? 'danger solid' : 'primary');
      $('#cfCheckWrap').hidden = !check; $('#cfCheck').checked = false;
      if (check) $('#cfCheckLabel').textContent = check;
      const onClose = () => { dlg.removeEventListener('close', onClose); resolve(dlg.returnValue === 'ok' ? { checked: $('#cfCheck').checked } : null); };
      dlg.addEventListener('close', onClose);
      dlg.returnValue = 'cancel';
      dlg.showModal();
      $('#cfCancel').focus();
    });
  }
  async function copyText(text) {
    try { await navigator.clipboard.writeText(text); }
    catch {
      const ta = document.createElement('textarea'); ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta); ta.select();
      try { document.execCommand('copy'); } finally { ta.remove(); }
    }
    toast('Copiado para a área de transferência');
  }

  // ---------- git ----------
  function gitChip(g) {
    if (!g) return '';
    const n = g.dirtyCount;
    let s = '';
    if (n > 0) s += `<span class="gs dirty" title="${plural(n, 'arquivo alterado', 'arquivos alterados')}">●${nf0.format(n)}</span>`;
    if (g.ahead > 0) s += `<span class="gs ahead" title="${plural(g.ahead, 'commit', 'commits')} à frente do remoto">↑${g.ahead}</span>`;
    if (g.behind > 0) s += `<span class="gs behind" title="${plural(g.behind, 'commit', 'commits')} atrás do remoto">↓${g.behind}</span>`;
    const note = g.statusNote;
    if (note) s += `<span class="gs lock" title="${esc(note)}" aria-label="${esc(note)}" role="img">${icon('lock')}</span>`;
    const tip = `${g.mainRepoRoot ? 'Worktree' : 'Repositório'} ${g.repoName}\nBranch: ${g.branch ?? 'desconhecida'}\n${g.repoRoot}` + (note ? `\n${note}` : n != null ? `\n${n === 0 ? 'Sem alterações' : plural(n, 'arquivo alterado', 'arquivos alterados')}` : '');
    return `<span class="chip" role="button" tabindex="0" data-act="open-git" data-repo="${esc(g.repoRoot)}" title="${esc(tip + '\nClique para ver a árvore do git')}">${icon('branch')}<span class="rn">${esc(g.repoName)}</span><span class="br">${g.mainRepoRoot ? '⎇ ' : ''}${esc(g.branch ?? '—')}</span>${s}</span>`;
  }
  const MAX_CHIPS = 3, MAX_CHIPS_GROUP = 2;
  const moreChip = (list) => `<span class="chip more" title="${esc(list.map((r) => r.repoName + ' · ' + (r.branch ?? '—')).join('\n'))}">+${list.length}</span>`;
  // repositórios da instância: o da pasta de trabalho ou, se não houver, os das subpastas
  const procRepos = (p) => (p.git ? [p.git] : (p.subRepos || []));
  const chipsHtml = (repos, max = MAX_CHIPS) => repos.slice(0, max).map(gitChip).join("") + (repos.length > max ? moreChip(repos.slice(max)) : "");
  const reposSig = (repos, max = MAX_CHIPS) => repos.slice(0, max).map(gitSig).join("~") + "#" + repos.length;
  const gitSig = (g) => g ? [g.repoRoot, g.branch, g.dirtyCount, g.ahead, g.behind, g.statusNote].join('|') : '';
  function setHtml(n, key, el, sig, html) { if (n[key] !== sig) { n[key] = sig; el.innerHTML = html; } }

  // ---------- ícones de aplicativo (cache) ----------
  function requestIcons() {
    if (!state.snap) return;
    const need = [];
    for (const g of state.snap.groups) if (g.iconKey && !state.icons.has(g.iconKey) && !state.iconPending.has(g.iconKey)) need.push(g.iconKey);
    if (!need.length) return;
    need.forEach((k) => state.iconPending.add(k));
    for (let i = 0; i < need.length; i += 40) {
      const keys = need.slice(i, i + 40);
      bridge.request('getIcons', { keys }).then((d) => {
        keys.forEach((k) => { state.icons.set(k, d.icons?.[k] ?? null); state.iconPending.delete(k); });
        renderList();
      }).catch(() => keys.forEach((k) => { state.icons.set(k, null); state.iconPending.delete(k); }));
    }
  }

  // ---------- utilidades de dados ----------
  const isFav = (key) => state.favs.has(key);
  // grupo favoritado ou com algum processo favoritado sobe na lista
  const groupHasFav = (g) => isFav(g.key) || g.processes.some((p) => isFav(p.favoriteKey));
  function procMatches(p, q) {
    return [p.name, p.workingDirectory, p.commandLine, p.git?.repoName, p.git?.branch, String(p.pid)].some((v) => v && v.toLowerCase().includes(q)) || (p.subRepos || []).some((r) => (r.repoName + ' ' + (r.branch ?? '')).toLowerCase().includes(q));
  }
  function groupMatches(g, q) {
    return [g.name, g.exeName].some((v) => v.toLowerCase().includes(q)) || g.repos.some((r) => (r.repoName + ' ' + (r.branch ?? '')).toLowerCase().includes(q)) || g.processes.some((p) => procMatches(p, q));
  }
  function savedMatches(s, q) {
    return [s.name, s.command, s.workingDirectory, s.notes, s.git?.repoName, s.git?.branch].some((v) => v && v.toLowerCase().includes(q));
  }
  const cmp = {
    memory: (a, b) => b.memoryBytes - a.memoryBytes,
    cpu: (a, b) => b.cpuPercent - a.cpuPercent || b.memoryBytes - a.memoryBytes,
    name: (a, b) => a.name.localeCompare(b.name, 'pt-BR'),
  };
  function ordered(list, scope, keyFn, comparator) {
    const freeze = state.hovering && !state.forceResort && state.order.has(scope);
    let out;
    if (freeze) {
      const prev = state.order.get(scope);
      const idx = new Map(prev.map((k, i) => [k, i]));
      const known = list.filter((x) => idx.has(keyFn(x))).sort((a, b) => idx.get(keyFn(a)) - idx.get(keyFn(b)));
      const fresh = list.filter((x) => !idx.has(keyFn(x))).sort(comparator);
      out = known.concat(fresh);
    } else out = list.slice().sort(comparator);
    state.order.set(scope, out.map(keyFn));
    return out;
  }

  // ---------- fábricas de nós ----------
  function makeGroupNode(key) {
    const el = document.createElement('div');
    el.className = 'group';
    el.innerHTML = `
      <div class="row grow" role="treeitem" tabindex="0" aria-expanded="false" data-nav>
        <span class="chev">${icon('chevron')}</span>
        <span class="appicon"><img alt="" hidden><span class="lt"></span></span>
        <div class="main"><div class="name"><span class="t"></span><span class="badge git" hidden>git</span></div>
          <div class="sub"><span class="count-t"></span><span class="chips"></span></div></div>
        <div class="mem"><div class="memtop"><b></b><span class="pct"></span></div><div class="bar"><i></i></div></div>
        <div class="cpu"></div>
        <svg class="spark" viewBox="0 0 84 24" preserveAspectRatio="none" aria-hidden="true"><path class="a"/><path class="l"/></svg>
        <div class="acts"><button type="button" class="ibtn fav" data-act="fav"></button>${ibtn('kill-group', 'stop', 'Encerrar todos os processos do grupo', 'kill hov')}</div>
      </div>
      <div class="inst" role="group" hidden></div>`;
    const row = el.firstElementChild;
    const n = {
      kind: 'group', key, el, row, inst: el.lastElementChild, children: new Map(),
      t: $('.t', el), badge: $('.badge', el), countT: $('.count-t', el), chips: $('.chips', el), img: $('img', el), lt: $('.lt', el), icon: $('.appicon', el),
      memB: $('.memtop b', el), pct: $('.pct', el), bar: $('.bar i', el), cpu: $('.cpu', el), spA: $('.spark .a', el), spL: $('.spark .l', el),
      fav: $('.fav', el), killB: $('[data-act=kill-group]', el), sig: {}, iconSrc: undefined,
    };
    el.dataset.key = key;
    n.update = (it) => updateGroup(n, it);
    n.dispose = () => { n.children.clear(); };
    return n;
  }

  function sparkPaths(vals) {
    if (!vals || vals.length < 2) return ['', ''];
    const min = Math.min(...vals), max = Math.max(...vals), span = max - min || 1;
    const stepX = 84 / (vals.length - 1);
    let l = '';
    vals.forEach((v, i) => { l += (i ? 'L' : 'M') + (i * stepX).toFixed(1) + ' ' + (22 - ((v - min) / span) * 20).toFixed(1); });
    return [l + 'L84 24L0 24Z', l];
  }

  function updateGroup(n, { g, procs, maxMem }) {
    n.el.dataset.key = g.key;
    const open = state.expanded.has(g.key);
    st(n.t, g.name);
    setAttr(n.t, 'title', `${g.name}\n${g.exePath ?? g.exeName}`);
    const hasGit = g.processes.some((p) => p.isGitProcess);
    if (n.badge.hidden === hasGit) n.badge.hidden = !hasGit;
    const favProcs = g.processes.reduce((c, p) => c + (isFav(p.favoriteKey) ? 1 : 0), 0);
    st(n.countT, plural(g.processCount, 'processo', 'processos') + (favProcs ? ' · ' + plural(favProcs, 'favorito', 'favoritos') : ''));
    const repos = g.repos || [];
    setHtml(n, 'chipSig', n.chips, reposSig(repos, MAX_CHIPS_GROUP), chipsHtml(repos, MAX_CHIPS_GROUP));
    const src = (g.iconKey ? state.icons.get(g.iconKey) : null) ?? null;
    if (src !== n.iconSrc || n.iconName !== g.name) { n.iconName = g.name;
      n.iconSrc = src;
      if (src) { n.img.src = src; n.img.hidden = false; n.lt.hidden = true; n.icon.classList.add('has-img'); n.icon.style.background = ''; }
      else {
        n.img.hidden = true; n.lt.hidden = false; n.icon.classList.remove('has-img');
        n.lt.textContent = (g.name.trim()[0] || '?').toUpperCase();
        n.icon.style.background = `hsl(${hue(g.exeName)} 42% 38%)`;
      }
    }
    st(n.memB, fmtBytes(g.memoryBytes));
    st(n.pct, fmtPct(g.memoryPercent));
    n.bar.style.transform = `scaleX(${maxMem ? Math.max(0.01, g.memoryBytes / maxMem).toFixed(3) : 0})`;
    st(n.cpu, fmtPct(g.cpuPercent));
    n.cpu.classList.toggle('hot', g.cpuPercent >= 25);
    const [a, l] = sparkPaths(g.memoryHistory);
    if (n.spA.getAttribute('d') !== a) { n.spA.setAttribute('d', a); n.spL.setAttribute('d', l); }
    const fav = isFav(g.key);
    if (n.favState !== fav) {
      n.favState = fav;
      n.fav.className = 'ibtn fav ' + (fav ? 'on' : 'hov');
      n.fav.innerHTML = icon('star');
      const lbl = fav ? 'Remover dos favoritos' : 'Adicionar aos favoritos';
      n.fav.setAttribute('aria-label', lbl); n.fav.title = lbl; n.fav.setAttribute('aria-pressed', String(fav));
    }
    setAttr(n.row, 'aria-expanded', String(open));
    setAttr(n.row, 'aria-label', `${g.name}, ${plural(g.processCount, 'processo', 'processos')}, ${fmtBytes(g.memoryBytes)}`);
    n.inst.hidden = !open;
    if (open) {
      const items = procs.map((p) => ({ key: 'p' + p.pid, kind: 'proc', p, g, maxMem: g.processes[0]?.memoryBytes || 1, flat: false }));
      reconcile(n.inst, items, n.children);
    } else if (n.children.size) { n.inst.replaceChildren(); n.children.clear(); }
  }

  function makeProcNode(key, flat) {
    const el = document.createElement('div');
    el.className = 'row irow' + (flat ? ' flat' : '');
    el.setAttribute('role', 'treeitem'); el.tabIndex = 0; el.dataset.nav = '';
    el.innerHTML = `
      <span class="pid"></span>
      <div class="info"><div class="l1">${flat ? '<span class="name-pre"></span>' : ''}<span class="badge git" hidden>git</span><span class="folder"></span><span class="chipwrap"></span></div><div class="l2"></div></div>
      <div class="mem"><div class="memtop"><b></b></div><div class="bar"><i></i></div></div>
      <div class="cpu"></div>
      <div class="acts"><button type="button" class="ibtn fav" data-act="fav"></button>${ibtn('folder', 'folder', 'Abrir pasta no Explorer', 'hov')}${ibtn('terminal', 'terminal', 'Abrir terminal na pasta', 'hov')}${ibtn('save-as', 'bookmark', 'Salvar como processo', 'hov')}${ibtn('kill', 'stop', 'Encerrar processo', 'kill hov')}</div>`;
    const n = {
      kind: 'proc', key, el, flat, pid: $('.pid', el), pre: $('.name-pre', el), badge: $('.badge', el), folder: $('.folder', el), chipwrap: $('.chipwrap', el), l2: $('.l2', el),
      memB: $('.memtop b', el), bar: $('.bar i', el), cpu: $('.cpu', el), fav: $('.fav', el), fBtn: $('[data-act=folder]', el), tBtn: $('[data-act=terminal]', el), sBtn: $('[data-act=save-as]', el),
    };
    n.update = (it) => updateProc(n, it);
    return n;
  }

  function updateProc(n, { p, g, maxMem }) {
    n.el.dataset.pid = p.pid;
    st(n.pid, String(p.pid));
    if (n.pre) { st(n.pre, p.name); setAttr(n.pre, 'title', g.name); }
    if (n.badge.hidden === p.isGitProcess) n.badge.hidden = !p.isGitProcess;
    const wd = p.workingDirectory;
    st(n.folder, wd ? pathText(shortPath(wd)) : (p.accessDenied ? 'Sem acesso' : 'Pasta desconhecida'));
    n.folder.classList.toggle('dim', !wd); n.folder.classList.toggle('path', !!wd);
    setAttr(n.folder, 'title', wd || (p.accessDenied ? 'Sem acesso a este processo (executa com mais privilégios)' : ''));
    { const rs = procRepos(p); setHtml(n, 'gSig', n.chipwrap, reposSig(rs), chipsHtml(rs)); }
    const cmd = p.commandLine;
    st(n.l2, cmd || (p.accessDenied ? 'Sem acesso à linha de comando' : '—'));
    n.l2.classList.toggle('dim', !cmd);
    setAttr(n.l2, 'title', cmd || '');
    st(n.memB, fmtBytes(p.memoryBytes));
    n.bar.style.transform = `scaleX(${Math.max(0.01, p.memoryBytes / (maxMem || 1)).toFixed(3)})`;
    st(n.cpu, fmtPct(p.cpuPercent));
    n.cpu.classList.toggle('hot', p.cpuPercent >= 25);
    const fav = isFav(p.favoriteKey);
    if (n.favState !== fav) {
      n.favState = fav;
      n.fav.className = 'ibtn fav ' + (fav ? 'on' : 'hov');
      n.fav.innerHTML = icon('star');
      const lbl = fav ? 'Remover dos favoritos' : 'Adicionar aos favoritos';
      n.fav.setAttribute('aria-label', lbl); n.fav.title = lbl; n.fav.setAttribute('aria-pressed', String(fav));
    }
    n.fav.dataset.favkey = p.favoriteKey;
    n.fBtn.dataset.path = wd || ''; n.tBtn.dataset.path = wd || '';
    n.fBtn.disabled = !wd; n.tBtn.disabled = !wd; n.sBtn.disabled = p.accessDenied && !cmd;
    n.el.classList.toggle('sel', state.selected === p.pid);
    setAttr(n.el, 'aria-selected', String(state.selected === p.pid));
    setAttr(n.el, 'aria-label', `${p.name}, PID ${p.pid}, ${fmtBytes(p.memoryBytes)}`);
  }

  function makeSavedNode(key) {
    const el = document.createElement('div');
    el.className = 'row scard';
    el.setAttribute('role', 'treeitem'); el.tabIndex = 0; el.dataset.nav = '';
    el.innerHTML = `
      <span class="dot" aria-hidden="true"></span>
      <div class="main"><div class="name"><span class="t"></span></div><div class="scmd"></div>
        <div class="sdir"><span class="folder"></span><span class="chipwrap"></span></div>
        <div class="sub-stat"></div>
        <div class="serr" hidden>${icon('alert')}<span></span></div></div>
      <div class="sstat"><b></b><span></span></div>
      <div class="sacts"><button type="button" class="btn" data-act="toggle-run"></button>${ibtn('open-git', 'graph', 'Ver árvore do git', 'gitbtn')}${ibtn('saved-term', 'terminal', 'Abrir terminal na pasta')}${ibtn('folder-saved', 'folder', 'Abrir pasta no Explorer')}${ibtn('edit', 'edit', 'Editar')}${ibtn('delete', 'trash', 'Excluir', 'kill')}</div>`;
    const n = {
      kind: 'saved', key, el, dot: $('.dot', el), t: $('.t', el), cmd: $('.scmd', el), folder: $('.folder', el), chipwrap: $('.chipwrap', el), err: $('.serr', el), errT: $('.serr span', el),
      sb: $('.sstat b', el), ss: $('.sstat span', el), subStat: $('.sub-stat', el), run: $('[data-act=toggle-run]', el), tBtn: $('[data-act=saved-term]', el), fBtn: $('[data-act=folder-saved]', el), gBtn: $('.gitbtn', el),
    };
    n.update = (it) => updateSaved(n, it);
    return n;
  }

  function updateSaved(n, { s }) {
    n.el.dataset.id = s.id;
    st(n.t, s.name);
    st(n.cmd, s.command); setAttr(n.cmd, 'title', s.command);
    const wd = s.workingDirectory;
    st(n.folder, wd ? pathText(shortPath(wd, 44)) : 'Sem pasta definida'); n.folder.classList.toggle('path', !!wd); setAttr(n.folder, 'title', wd || '');
    setHtml(n, 'gSig', n.chipwrap, gitSig(s.git), gitChip(s.git));
    n.dot.classList.toggle('on', s.running);
    const status = s.running ? 'Rodando' : 'Parado';
    const detail = s.running ? `PID ${s.pids.join(', ')} · ${fmtBytes(s.memoryBytes)} · CPU ${fmtPct(s.cpuPercent)}` : (s.lastError ? 'Falhou ao iniciar' : 'Pronto para iniciar');
    st(n.sb, status); n.sb.classList.toggle('on', s.running); st(n.ss, detail); st(n.subStat, status + ' · ' + detail);
    n.err.hidden = !s.lastError; if (s.lastError) st(n.errT, s.lastError);
    const busy = state.busy.has(s.id);
    const label = busy ? (s.running ? 'Parando…' : 'Iniciando…') : (s.running ? 'Parar' : 'Iniciar');
    const sig = label + s.running;
    if (n.runSig !== sig) { n.runSig = sig; n.run.innerHTML = icon(s.running ? 'stop' : 'play') + `<span>${label}</span>`; n.run.className = 'btn ' + (s.running ? 'danger' : 'primary'); }
    n.run.disabled = busy;
    n.fBtn.dataset.path = wd || ''; n.fBtn.disabled = !wd;
    n.tBtn.disabled = !wd && !s.terminalId; n.tBtn.dataset.id = s.id;
    { const l = s.terminalId ? 'Mostrar o terminal deste processo' : 'Abrir terminal na pasta'; n.tBtn.title = l; n.tBtn.setAttribute('aria-label', l); }
    n.gBtn.hidden = !s.git; n.gBtn.dataset.repo = s.git?.repoRoot || '';
    setAttr(n.el, 'aria-label', `${s.name}, ${status}`);
  }

  function makeHeading(key) {
    const el = document.createElement('div'); el.className = 'heading'; el.setAttribute('role', 'presentation');
    const n = { kind: 'heading', key, el }; n.update = ({ text }) => st(el, text); return n;
  }
  function makeEmpty(key) {
    const el = document.createElement('div'); el.className = 'empty'; el.setAttribute('role', 'presentation');
    const n = { kind: 'empty', key, el, v: '' };
    n.update = ({ html, v }) => { if (n.v !== v) { n.v = v; el.innerHTML = html; } };
    return n;
  }
  function makeSkel(key) { const el = document.createElement('div'); el.className = 'skel'; el.setAttribute('role', 'presentation'); const n = { kind: 'skel', key, el }; n.update = () => {}; return n; }

  const factories = {
    group: (it) => makeGroupNode(it.key), proc: (it) => makeProcNode(it.key, it.flat), saved: (it) => makeSavedNode(it.key),
    heading: (it) => makeHeading(it.key), empty: (it) => makeEmpty(it.key), skel: (it) => makeSkel(it.key),
  };

  function reconcile(container, items, map) {
    const seen = new Set();
    let ref = container.firstElementChild;
    for (const it of items) {
      let n = map.get(it.key);
      if (!n || n.kind !== it.kind || (it.kind === 'proc' && n.flat !== it.flat)) {
        if (n) { n.el.remove(); n.dispose?.(); }
        n = factories[it.kind](it); map.set(it.key, n);
      }
      n.update(it);
      seen.add(it.key);
      if (n.el === ref) ref = ref.nextElementSibling; else container.insertBefore(n.el, ref);
    }
    for (const [k, n] of map) if (!seen.has(k)) { n.el.remove(); n.dispose?.(); map.delete(k); }
  }

  // ---------- montagem da lista ----------
  function emptyItem(variant) {
    const q = esc(state.query.trim());
    const E = {
      search: { h: 'Nada encontrado', p: `Nenhum resultado para “${q}”. Procure por parte do nome, da pasta, do comando ou do repositório.`, b: '<button class="btn" type="button" data-act="clear-q">Limpar busca</button>' },
      favs: { h: 'Nenhum favorito ainda', p: 'Clique na estrela de uma aplicação ou de um processo para fixá-lo aqui e acompanhá-lo de perto.', b: '' },
      saved: { h: 'Nenhum processo salvo', p: 'Guarde o comando de um servidor ou ferramenta e inicie com um clique. Você também pode usar o ícone de marcador em qualquer processo em execução.', b: '<button class="btn primary" type="button" data-act="new">' + icon('plus') + 'Criar processo salvo</button>' },
      apps: { h: 'Nenhum processo encontrado', p: 'Aguardando os dados do sistema.', b: '' },
    }[variant];
    return { key: 'empty:' + variant + (variant === 'search' ? state.query : ''), kind: 'empty', v: variant + state.query, html: `<h3>${E.h}</h3><p>${E.p}</p>${E.b}` };
  }

  function buildItems() {
    if (state.fatal) return [];
    const snap = state.snap;
    if (!snap) return [0, 1, 2, 3, 4, 5].map((i) => ({ key: 'skel' + i, kind: 'skel' }));
    const q = state.query.trim().toLowerCase();
    if (state.tab === 'saved') {
      let list = snap.saved.filter((s) => !q || savedMatches(s, q));
      list = ordered(list, 'saved', (s) => s.id, cmp.name);
      if (!list.length) return [emptyItem(q ? 'search' : 'saved')];
      return list.map((s) => ({ key: 's' + s.id, kind: 'saved', s }));
    }
    const maxMem = snap.groups.reduce((m, g) => Math.max(m, g.memoryBytes), 0);
    const procCmp = (a, b) => (isFav(b.favoriteKey) - isFav(a.favoriteKey)) || cmp[state.sort](a, b);
    const withProcs = (g) => {
      const base = !q || [g.name, g.exeName].some((v) => v.toLowerCase().includes(q)) ? g.processes : g.processes.filter((p) => procMatches(p, q));
      return { key: 'g' + g.key, kind: 'group', g, maxMem, procs: ordered(base, 'p:' + g.key, (p) => String(p.pid), procCmp) };
    };
    const comparator = (a, b) => (groupHasFav(b) - groupHasFav(a)) || cmp[state.sort](a, b);
    if (state.tab === 'apps') {
      const list = ordered(snap.groups.filter((g) => !q || groupMatches(g, q)), 'apps', (g) => g.key, comparator);
      return list.length ? list.map(withProcs) : [emptyItem(q ? 'search' : 'apps')];
    }
    const fg = ordered(snap.groups.filter((g) => isFav(g.key) && (!q || groupMatches(g, q))), 'favg', (g) => g.key, cmp[state.sort]);
    const fp = [];
    for (const g of snap.groups) for (const p of g.processes) if (isFav(p.favoriteKey) && (!q || procMatches(p, q))) fp.push({ p, g });
    const fpo = ordered(fp, 'favp', (x) => String(x.p.pid), (a, b) => (state.sort === 'name' ? a.p.name.localeCompare(b.p.name, 'pt-BR') : state.sort === 'cpu' ? b.p.cpuPercent - a.p.cpuPercent : b.p.memoryBytes - a.p.memoryBytes));
    if (!fg.length && !fpo.length) return [emptyItem(q ? 'search' : 'favs')];
    const items = [];
    if (fg.length) { items.push({ key: 'h:g', kind: 'heading', text: 'Aplicações' }, ...fg.map(withProcs)); }
    if (fpo.length) { items.push({ key: 'h:p', kind: 'heading', text: 'Processos' }, ...fpo.map(({ p, g }) => ({ key: 'f' + p.pid, kind: 'proc', p, g, maxMem: g.processes[0]?.memoryBytes || 1, flat: true }))); }
    return items;
  }

  function renderList() {
    if (state.tab === 'git') return;
    const tree = state.tab !== 'saved';
    const top = wrapEl.scrollTop;
    reconcile(listEl, buildItems(), nodes);
    if (wrapEl.scrollTop !== top) wrapEl.scrollTop = top;
    $('#colhead').hidden = !tree || !state.snap || !!state.fatal || !!listEl.querySelector('.empty,.skel');
    state.forceResort = false;
    if (state.fatal && !listEl.querySelector('.notice')) {
      const d = document.createElement('div'); d.className = 'notice';
      d.innerHTML = `${icon('alert')}<span></span><button class="btn small" type="button" data-act="retry">Tentar novamente</button>`;
      d.children[1].textContent = state.fatal;
      listEl.appendChild(d);
    } else if (!state.fatal) listEl.querySelector('.notice')?.remove();
  }

  // ---------- cabeçalho (máquina) ----------
  let lastHist = null;
  function renderMachine() {
    const m = state.snap.machine;
    st($('#memPct'), nf0.format(m.memoryPercent));
    st($('#memDetail'), `${fmtBytes(m.usedMemoryBytes)} de ${fmtBytes(m.totalMemoryBytes)} em uso`);
    const lvl = m.memoryPercent >= 90 ? 'crit' : m.memoryPercent >= 75 ? 'warn' : '';
    const meter = $('#memMeter');
    meter.className = 'meter ' + lvl; setAttr(meter, 'aria-valuenow', String(Math.round(m.memoryPercent)));
    setAttr(meter, 'aria-valuetext', `${nf0.format(m.memoryPercent)}% de ${fmtBytes(m.totalMemoryBytes)}`);
    $('#memBar').style.transform = `scaleX(${Math.min(1, m.memoryPercent / 100)})`;
    const stt = $('#memState'); stt.hidden = !lvl; stt.className = 'm-state ' + lvl; st(stt, lvl === 'crit' ? 'Crítico' : lvl ? 'Alto' : '');
    st($('#cpuPct'), nf0.format(m.cpuPercent));
    $('#cpuBar').style.transform = `scaleX(${Math.min(1, m.cpuPercent / 100)})`;
    st($('#procCount'), nf0.format(m.processCount));
    if (m.history !== lastHist) { lastHist = m.history; drawChart(m.history); }
  }
  function drawChart(h) {
    if (!h || h.length < 2) return;
    const t1 = h[h.length - 1].t, t0 = t1 - 600000;
    const X = (t) => Math.max(0, (t - t0) / 600000 * 300);
    const Y = (v) => 58 - Math.min(100, Math.max(0, v)) / 100 * 56;
    let mem = '', cpu = '';
    h.forEach((p, i) => { const x = X(p.t).toFixed(1); mem += (i ? 'L' : 'M') + x + ' ' + Y(p.memoryPercent).toFixed(1); cpu += (i ? 'L' : 'M') + x + ' ' + Y(p.cpuPercent).toFixed(1); });
    $('#lineMem').setAttribute('d', mem); $('#lineCpu').setAttribute('d', cpu);
    $('#areaMem').setAttribute('d', mem + `L${X(h[h.length - 1].t).toFixed(1)} 60L${X(h[0].t).toFixed(1)} 60Z`);
    const last = h[h.length - 1];
    $('#chart').setAttribute('aria-label', `Histórico dos últimos 10 minutos. Agora: memória ${nf0.format(last.memoryPercent)}%, CPU ${nf0.format(last.cpuPercent)}%.`);
  }
  function bindChart() {
    const chart = $('#chart'), cross = $('#cross'), tip = $('#tip');
    chart.addEventListener('pointermove', (e) => {
      const h = state.snap?.machine.history; if (!h || h.length < 2) return;
      const r = chart.getBoundingClientRect(), t1 = h[h.length - 1].t, t = t1 - 600000 + ((e.clientX - r.left) / r.width) * 600000;
      let best = h[0]; for (const p of h) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p;
      const x = Math.max(0, (best.t - (t1 - 600000)) / 600000) * r.width;
      cross.hidden = false; cross.style.left = x + 'px';
      tip.hidden = false; tip.textContent = `${tf.format(best.t)} · memória ${nf0.format(best.memoryPercent)}% · CPU ${nf0.format(best.cpuPercent)}%`;
      const w = tip.offsetWidth; tip.style.left = Math.min(Math.max(2, x - w / 2), r.width - w - 2) + 'px';
    });
    chart.addEventListener('pointerleave', () => { cross.hidden = true; tip.hidden = true; });
  }

  // ---------- abas, contadores ----------
  function renderTabs() {
    const snap = state.snap;
    st($('#cnt-apps'), snap ? nf0.format(snap.groups.length) : '0');
    let fav = 0;
    if (snap) for (const g of snap.groups) { if (isFav(g.key)) fav++; for (const p of g.processes) if (isFav(p.favoriteKey)) fav++; }
    st($('#cnt-favs'), nf0.format(fav));
    st($('#cnt-saved'), snap ? nf0.format(snap.saved.length) : '0');
    document.querySelectorAll('.tabs [role=tab]').forEach((b) => {
      const on = b.dataset.tab === state.tab;
      setAttr(b, 'aria-selected', String(on)); b.tabIndex = on ? 0 : -1;
    });
    const saved = state.tab === 'saved', docked = GitWorkspace.isDocked(), git = state.tab === 'git' && !docked;
    $('#tab-git').hidden = docked;
    $('#sortWrap').hidden = saved || git; $('.search').hidden = git;
    wrapEl.hidden = git; document.body.classList.toggle('tab-git', git);
    GitWorkspace.setPlacement(git);
    const nb = $('#btnNew'); nb.hidden = !saved;
    if (!nb.innerHTML) nb.innerHTML = icon('plus') + 'Novo processo';
    $('#pauseHint').hidden = !(state.hovering && state.snap && state.tab === 'apps' || state.hovering && state.snap && state.tab === 'favs');
    wrapEl.setAttribute('aria-labelledby', 'tab-' + state.tab);
  }

  function renderAll() {
    if (state.snap) { renderMachine(); }
    renderTabs(); renderList(); updateDrawer(); GitWorkspace.refresh(); Terminals.sync();
  }
  function showLoose() { state.tab = 'saved'; state.forceResort = true; renderAll(); }
  function knownRepos() {
    const m = new Map(), s = state.snap;
    if (!s) return [];
    // a chave ignora maiúsculas (C:\x e c:\x são o mesmo repositório)
    const put = (r) => m.set(r.repoRoot.toLowerCase(), r);
    for (const g of s.groups) { for (const r of g.repos || []) put(r); for (const p of g.processes) for (const r of procRepos(p)) put(r); }
    for (const x of s.saved) if (x.git) put(x.git);
    return [...m.values()].sort((a, b) => a.repoName.localeCompare(b.repoName, 'pt-BR'));
  }
  function openGit(root) {
    if (!root) return;
    closeDrawer();
    GitWorkspace.openRepo(root);
  }
  function selectGitTab() { state.tab = 'git'; state.forceResort = true; renderAll(); }
  function onDockChange(docked) {
    if (docked && state.tab === 'git') state.tab = 'apps'; else if (!docked) state.tab = 'git';
    state.forceResort = true; renderAll();
  }
  function filterProcesses(folder) {
    if (state.tab === 'git' || state.tab === 'saved') state.tab = 'apps';
    setQuery(folder); $('#q').focus();
  }

  function applySnapshot(s) {
    state.snap = s;
    state.pidIndex = new Map();
    for (const g of s.groups) for (const p of g.processes) state.pidIndex.set(p.pid, { p, g });
    requestIcons();
    renderAll();
    Terminals.onSnapshot(s);
  }

  // ---------- drawer de detalhes ----------
  const dw = $('#drawer');
  let dwPid = null, dwOpener = null;
  function openDrawer(pid, opener) {
    if (!state.pidIndex.has(pid)) return;
    const had = state.selected;
    GitWorkspace.collapse();
    state.selected = pid; dwOpener = opener || dwOpener;
    if (dwPid !== pid) { dwPid = pid; buildDrawer(); }
    dw.hidden = false; updateDrawer();
    markSelected(had);
    if (had == null) dw.querySelector('[data-act=close-drawer]').focus();
  }
  function closeDrawer() {
    if (dw.hidden) return;
    const had = state.selected;
    state.selected = null; dwPid = null; dw.hidden = true; dw.replaceChildren();
    markSelected(had);
    if (dwOpener?.isConnected) dwOpener.focus();
    dwOpener = null;
  }
  function markSelected() {
    for (const n of document.querySelectorAll('.irow')) {
      const on = String(state.selected) === n.dataset.pid;
      n.classList.toggle('sel', on); setAttr(n, 'aria-selected', String(on));
    }
  }
  function buildDrawer() {
    dw.dataset.pid = dwPid;
    dw.innerHTML = `
      <div class="dw-head"><div class="dw-tt"><h2 id="dwTitle" data-f="title"></h2><div class="sub2" data-f="sub"></div></div>
        <button type="button" class="ibtn" data-act="close-drawer" aria-label="Fechar detalhes" title="Fechar (Esc)">${icon('x')}</button></div>
      <div class="dw-body">
        <div class="dw-gone" data-f="gone" hidden>Este processo foi encerrado.</div>
        <section class="dw-sec"><h3>Comando</h3><div class="codebox" data-f="cmdbox"><code data-f="cmd"></code>${ibtn('copy-cmd', 'copy', 'Copiar comando')}</div></section>
        <section class="dw-sec"><h3>Pasta de trabalho</h3><div class="codebox" data-f="dirbox"><code data-f="dir"></code>${ibtn('copy-dir', 'copy', 'Copiar caminho')}</div></section>
        <section class="dw-sec" data-f="gitsec" hidden><h3>Git</h3><div class="gitbox" data-f="git"></div></section>
        <section class="dw-sec"><h3>Detalhes</h3><dl class="kv">
          <dt>Memória</dt><dd data-f="mem"></dd><dt>CPU</dt><dd data-f="cpu"></dd><dt>Início</dt><dd data-f="start"></dd>
          <dt>PID</dt><dd data-f="pid"></dd><dt>PID pai</dt><dd data-f="ppid"></dd><dt>Executável</dt><dd data-f="exe"></dd></dl></section>
      </div>
      <div class="dw-foot">
        <button type="button" class="btn small" data-act="fav" data-f="fav"></button>
        <button type="button" class="btn small" data-act="folder" data-f="bfolder">${icon('folder')}Abrir pasta</button>
        <button type="button" class="btn small" data-act="terminal" data-f="bterm">${icon('terminal')}Terminal</button>
        <button type="button" class="btn small" data-act="save-as" data-f="bsave">${icon('bookmark')}Salvar como processo</button>
        <button type="button" class="btn small danger" data-act="kill" data-f="bkill">${icon('stop')}Encerrar</button>
      </div>`;
    dw._f = {}; dw.querySelectorAll('[data-f]').forEach((e) => { dw._f[e.dataset.f] = e; });
    dw._sig = {};
  }
  const drawerRepo = (g) => `<div class="gitrepo">${gitChip(g)}<span class="hint">${esc(g.repoRoot)}</span><button type="button" class="btn small" data-act="open-git" data-repo="${esc(g.repoRoot)}">${icon('graph')}Ver árvore do git</button>${g.statusNote ? `<span class="hint git-note">${esc(g.statusNote)}</span>` : ''}</div>`;
  function updateDrawer() {
    if (dw.hidden || dwPid == null) return;
    const f = dw._f, hit = state.pidIndex.get(dwPid);
    f.gone.hidden = !!hit;
    if (!hit) { dw.querySelectorAll('.dw-foot .btn').forEach((b) => { b.disabled = true; }); return; }
    const { p, g } = hit;
    st(f.title, p.name); st(f.sub, `${g.name} · PID ${p.pid}`);
    const locked = (el, box, text, none) => { box.classList.toggle('dim', !text); st(el, text || none); };
    locked(f.cmd, f.cmdbox, p.commandLine, p.accessDenied ? 'Sem acesso' : 'Indisponível');
    locked(f.dir, f.dirbox, p.workingDirectory, p.accessDenied ? 'Sem acesso' : 'Indisponível');
    f.cmdbox.querySelector('[data-act=copy-cmd]').dataset.text = p.commandLine || ''; f.cmdbox.querySelector('[data-act=copy-cmd]').disabled = !p.commandLine;
    f.dirbox.querySelector('[data-act=copy-dir]').dataset.text = p.workingDirectory || ''; f.dirbox.querySelector('[data-act=copy-dir]').disabled = !p.workingDirectory;
    const drepos = procRepos(p);
    f.gitsec.hidden = !drepos.length && !p.isGitProcess;
    setHtml(dw._sig, 'g', f.git, drepos.map(gitSig).join('~') + p.isGitProcess, (p.isGitProcess ? '<span class="badge git">Processo do git</span>' : '') + drepos.map(drawerRepo).join(''));
    st(f.mem, `${fmtBytes(p.memoryBytes)} (${fmtPct(p.memoryPercent)} da máquina)`); st(f.cpu, fmtPct(p.cpuPercent));
    st(f.start, p.startTime ? since(p.startTime) : 'Sem acesso'); st(f.pid, String(p.pid)); st(f.ppid, String(p.parentPid));
    st(f.exe, p.exePath || 'Sem acesso');
    const fav = isFav(p.favoriteKey);
    f.fav.dataset.favkey = p.favoriteKey; f.fav.innerHTML = icon('star') + (fav ? 'Favorito' : 'Favoritar'); f.fav.setAttribute('aria-pressed', String(fav));
    f.bfolder.dataset.path = p.workingDirectory || ''; f.bterm.dataset.path = p.workingDirectory || '';
    f.bfolder.disabled = f.bterm.disabled = !p.workingDirectory; f.bsave.disabled = !p.commandLine; f.bkill.disabled = false;
  }

  // ---------- formulário de processo salvo ----------
  const formDlg = $('#formDlg'), form = $('#form');
  let formId = '';
  function envRow(k = '', v = '') {
    const r = document.createElement('div'); r.className = 'env-row';
    r.innerHTML = `<input type="text" name="envKey" aria-label="Nome da variável" placeholder="NOME" spellcheck="false" autocomplete="off"><input type="text" name="envValue" aria-label="Valor da variável" placeholder="valor" spellcheck="false" autocomplete="off">${ibtn('env-del', 'x', 'Remover variável')}`;
    r.children[0].value = k; r.children[1].value = v; return r;
  }
  function setSuggestions(cmds) {
    const box = $('#f-sugg');
    box.replaceChildren();
    (cmds || []).forEach((c) => { const b = document.createElement('button'); b.type = 'button'; b.className = 'btn small sugg-chip'; b.dataset.act = 'sugg'; b.dataset.cmd = c; b.textContent = c; box.appendChild(b); });
    box.hidden = !box.children.length;
  }
  function setLaunchMode(v) {
    const r = document.querySelector(`input[name=launchMode][value="${v}"]`) || document.querySelector('input[name=launchMode][value=terminal]');
    r.checked = true;
  }
  function openForm(saved, warning = null, suggestions = null) {
    if (formDlg.open) return;
    setSuggestions(suggestions);
    formId = saved?.id || '';
    $('#formTitle').textContent = formId ? 'Editar processo salvo' : 'Novo processo salvo';
    $('#f-name').value = saved?.name || ''; $('#f-cmd').value = saved?.command || ''; $('#f-dir').value = saved?.workingDirectory || '';
    setLaunchMode(saved?.launchMode ?? 'terminal'); $('#f-notes').value = saved?.notes || '';
    const rows = $('#envRows'); rows.replaceChildren();
    Object.entries(saved?.env || {}).forEach(([k, v]) => rows.appendChild(envRow(k, v)));
    const warn = $('#w-cmd'); warn.textContent = warning || ''; warn.hidden = !warning;
    ['e-name', 'e-cmd', 'e-env'].forEach((i) => { $('#' + i).textContent = ''; });
    document.querySelectorAll('#form .field.bad').forEach((x) => x.classList.remove('bad'));
    formDlg.showModal(); $('#f-name').focus();
  }
  async function submitForm(ev) {
    ev.preventDefault();
    const name = $('#f-name').value.trim(), command = $('#f-cmd').value.trim();
    const env = {}; let envErr = '';
    document.querySelectorAll('#envRows .env-row').forEach((r) => {
      const k = r.children[0].value.trim(), v = r.children[1].value;
      if (!k && !v) return;
      if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(k)) envErr = 'Nome de variável inválido. Use letras, números e sublinhado, sem começar por número.'; else env[k] = v;
    });
    $('#e-name').textContent = name ? '' : 'Informe um nome para o processo.';
    $('#e-cmd').textContent = command ? '' : 'Informe o comando que será executado.';
    $('#e-env').textContent = envErr;
    $('#f-name').closest('.field').classList.toggle('bad', !name); $('#f-cmd').closest('.field').classList.toggle('bad', !command);
    if (!name) return $('#f-name').focus(); if (!command) return $('#f-cmd').focus(); if (envErr) return;
    const saved = { id: formId, name, command, workingDirectory: $('#f-dir').value.trim() || null, env, launchMode: document.querySelector('input[name=launchMode]:checked')?.value || 'terminal', notes: $('#f-notes').value.trim() || null };
    const btn = $('#formSave'); btn.disabled = true;
    const d = await call('saveProcess', { saved }, formId ? 'Alterações salvas' : 'Processo salvo');
    btn.disabled = false;
    if (!d) return;
    formDlg.close();
    const s = d.saved, list = state.snap.saved, i = list.findIndex((x) => x.id === s.id);
    const merged = { running: false, pids: [], memoryBytes: 0, cpuPercent: 0, startedAt: null, git: null, lastError: null, ...(i >= 0 ? list[i] : {}), ...s };
    if (i >= 0) list[i] = merged; else list.push(merged);
    renderAll();
  }

  // ---------- ações ----------
  async function togglePath(act, path) {
    if (!path) return;
    await call(act, { path });
  }
  async function actToggleFav(key) {
    const d = await call('toggleFavorite', { key });
    if (!d) return;
    state.favs = new Set(d.favorites); state.forceResort = true; renderAll();
  }
  async function actKillProc(pid) {
    const hit = state.pidIndex.get(pid); if (!hit) return;
    const r = await confirmDialog({ title: `Encerrar ${hit.p.name}?`, body: `O processo ${hit.p.name} (PID ${pid}) será encerrado imediatamente. Alterações não salvas serão perdidas.`, ok: 'Encerrar', check: 'Encerrar também os processos filhos' });
    if (!r) return;
    const d = await call('killProcess', { pid, tree: r.checked, startTime: hit.p.startTime ?? null });
    if (d) toast(`${plural(d.killed, 'processo encerrado', 'processos encerrados')}`);
    else { state.forceResort = true; renderAll(); }
  }
  async function actKillGroup(key) {
    const g = state.snap.groups.find((x) => x.key === key); if (!g) return;
    const r = await confirmDialog({ title: `Encerrar ${g.name}?`, body: `Os ${plural(g.processCount, 'processo', 'processos')} de ${g.name} serão encerrados imediatamente. Alterações não salvas serão perdidas.`, ok: `Encerrar ${g.processCount > 1 ? 'todos' : ''}`.trim() });
    if (!r) return;
    const d = await call('killGroup', { key });
    if (d) toast(`${plural(d.killed, 'processo encerrado', 'processos encerrados')}`);
  }
  async function actSaveAs(pid) {
    const d = await call('draftFromProcess', { pid });
    if (d) openForm(d.saved, d.warning);
  }
  async function actToggleRun(id) {
    const s = state.snap.saved.find((x) => x.id === id); if (!s || state.busy.has(id)) return;
    state.busy.add(id); renderList(); GitWorkspace.refresh();
    const d = await call(s.running ? 'stopSaved' : 'startSaved', { id });
    state.busy.delete(id);
    if (d) {
      Object.assign(s, d.state);
      if (d.state.lastError) toast(d.state.lastError, 'error'); else toast(d.state.running ? `${s.name} iniciado` : `${s.name} parado`);
      if (d.state.running && d.state.terminalId) Terminals.revealSaved(s.id);
    }
    renderList(); GitWorkspace.refresh();
  }
  // ação explícita (não alterna): ignora quando o processo já está no estado pedido
  async function actSetRun(id, want) {
    const s = state.snap?.saved.find((x) => x.id === id);
    if (!s || s.running === want) return;
    await actToggleRun(id);
  }
  async function actDelete(id) {
    const s = state.snap.saved.find((x) => x.id === id); if (!s) return;
    const r = await confirmDialog({ title: 'Excluir processo salvo?', body: `“${s.name}” será removido da lista. O comando não será mais guardado. Se estiver rodando, ele continua em execução.`, ok: 'Excluir' });
    if (!r) return;
    if (await call('deleteSaved', { id }, 'Processo excluído')) { state.snap.saved = state.snap.saved.filter((x) => x.id !== id); renderAll(); }
  }

  function handleAction(act, btn) {
    const host = btn.closest('[data-pid],[data-key],[data-id]');
    const pid = Number(btn.closest('[data-pid]')?.dataset.pid);
    switch (act) {
      case 'fav': return actToggleFav(btn.dataset.favkey || btn.closest('[data-key]')?.dataset.key);
      case 'folder': return togglePath('openFolder', btn.dataset.path);
      case 'folder-saved': return togglePath('openFolder', btn.dataset.path);
      case 'terminal': return togglePath('openTerminal', btn.dataset.path);
      case 'save-as': return actSaveAs(pid);
      case 'kill': return actKillProc(pid);
      case 'kill-group': return actKillGroup(btn.closest('[data-key]').dataset.key);
      case 'toggle-run': return actToggleRun(btn.closest('[data-id]').dataset.id);
      case 'edit': return openForm(state.snap.saved.find((x) => x.id === btn.closest('[data-id]').dataset.id));
      case 'delete': return actDelete(btn.closest('[data-id]').dataset.id);
      case 'new': return openForm(null);
      case 'saved-term': return Terminals.openForSaved(state.snap.saved.find((x) => x.id === btn.closest('[data-id]').dataset.id));
      case 'sugg': { const f = $('#f-cmd'); f.value = btn.dataset.cmd; f.dispatchEvent(new Event('input', { bubbles: true })); return f.focus(); }
      case 'copy-cmd': case 'copy-dir': return btn.dataset.text && copyText(btn.dataset.text);
      case 'close-drawer': return closeDrawer();
      case 'open-git': return openGit(btn.dataset.repo);
      case 'clear-q': setQuery(''); return $('#q').focus();
      case 'retry': return init();
      case 'env-del': { const r = btn.closest('.env-row'); r.remove(); return; }
      default: return host;
    }
  }

  // ---------- eventos (delegados; registrados uma única vez) ----------
  function setQuery(v) {
    state.query = v; state.forceResort = true; $('#q').value = v;
    $('.search').classList.toggle('has-q', !!v);
    wrapEl.scrollTop = 0; renderAll();
  }
  function toggleGroup(key, force) {
    const open = force ?? !state.expanded.has(key);
    if (open) state.expanded.add(key); else state.expanded.delete(key);
    renderList();
  }
  function navMove(from, dir) {
    const items = [...listEl.querySelectorAll('[data-nav]')].filter((e) => e.offsetParent !== null);
    const i = items.indexOf(from); if (i < 0) return;
    const t = dir === 'home' ? items[0] : dir === 'end' ? items[items.length - 1] : items[i + dir];
    if (t) { t.focus(); t.scrollIntoView({ block: 'nearest' }); }
  }
  function bind() {
    document.addEventListener('click', (e) => {
      const b = e.target.closest('[data-act]');
      if (b && !b.closest('#form,#confirmDlg') ) { e.stopPropagation(); if (!b.disabled) handleAction(b.dataset.act, b); return; }
      if (b && b.closest('#form')) { handleAction(b.dataset.act, b); return; }
      const row = e.target.closest('#list [data-nav]');
      if (!row) return;
      if (row.classList.contains('grow')) toggleGroup(row.closest('.group').dataset.key);
      else if (row.classList.contains('irow')) openDrawer(Number(row.dataset.pid), row);
    });
    $('#list').addEventListener('keydown', (e) => {
      const row = e.target.closest('[data-nav]');
      if (!row || e.target !== row) return;
      if (e.key === 'ArrowDown') { e.preventDefault(); navMove(row, 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); navMove(row, -1); }
      else if (e.key === 'Home') { e.preventDefault(); navMove(row, 'home'); }
      else if (e.key === 'End') { e.preventDefault(); navMove(row, 'end'); }
      else if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        if (row.classList.contains('grow')) toggleGroup(row.closest('.group').dataset.key);
        else if (row.classList.contains('irow')) openDrawer(Number(row.dataset.pid), row);
        else if (row.classList.contains('scard')) openForm(state.snap.saved.find((x) => x.id === row.dataset.id));
      } else if (e.key === 'ArrowRight' && row.classList.contains('grow')) { e.preventDefault(); toggleGroup(row.closest('.group').dataset.key, true); }
      else if (e.key === 'ArrowLeft' && row.classList.contains('grow')) { e.preventDefault(); toggleGroup(row.closest('.group').dataset.key, false); }
      else if (e.key === 'ArrowLeft' && row.classList.contains('irow') && !row.classList.contains('flat')) { e.preventDefault(); const gr = row.closest('.group'); toggleGroup(gr.dataset.key, false); gr.querySelector('.grow').focus(); }
    });
    document.querySelector('.tabs').addEventListener('click', (e) => {
      const b = e.target.closest('[data-tab]'); if (!b) return;
      state.tab = b.dataset.tab; state.forceResort = true; wrapEl.scrollTop = 0; renderAll();
    });
    document.querySelector('.tabs').addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
      const tabs = [...document.querySelectorAll('.tabs [role=tab]')].filter((t) => !t.hidden), i = tabs.indexOf(document.activeElement); if (i < 0) return;
      const t = tabs[(i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]; t.click(); t.focus();
    });
    $('#q').addEventListener('input', (e) => setQuery(e.target.value));
    $('#q').addEventListener('keydown', (e) => { if (e.key === 'Escape') { if (state.query) { e.stopPropagation(); setQuery(''); } else e.target.blur(); } });
    $('#sort').addEventListener('change', (e) => { state.sort = e.target.value; state.forceResort = true; renderList(); });
    wrapEl.addEventListener('pointerenter', () => { state.hovering = true; renderTabs(); });
    wrapEl.addEventListener('pointerleave', () => { state.hovering = false; renderTabs(); if (state.snap) renderList(); });
    document.addEventListener('keydown', (e) => {
      const chip = e.target.closest?.('.chip[data-act]');
      if (chip && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); chip.click(); return; }
      const tag = e.target.tagName;
      if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT') { e.preventDefault(); $('#q').focus(); $('#q').select(); }
      else if (e.key === 'Escape' && !formDlg.open && !$('#confirmDlg').open && !dw.hidden && !e.target.closest?.('#gitwrap')) { e.preventDefault(); closeDrawer(); }
    });
    $('#cfgWarnClose').addEventListener('click', () => { $('#cfgWarn').hidden = true; });
    $('#btnNew').addEventListener('click', () => openForm(null));
    $('#btnEnvAdd').addEventListener('click', () => { const r = envRow(); $('#envRows').appendChild(r); r.children[0].focus(); });
    $('#formCancel').addEventListener('click', () => formDlg.close());
    form.addEventListener('input', (e) => { const f = e.target.closest('.field'); if (f && e.target.value.trim()) { f.classList.remove('bad'); const er = f.querySelector('.err'); if (er) er.textContent = ''; } });
    form.addEventListener('submit', submitForm);
    $('#btnTheme').addEventListener('click', async () => {
      const next = { system: 'light', light: 'dark', dark: 'system' }[state.theme];
      const d = await call('setSettings', { settings: { ...(state.config?.settings || { refreshIntervalMs: 2000, minimizeToTray: false }), theme: next } });
      if (d) { state.config.settings = d.settings; applyTheme(d.settings.theme); }
    });
    bindChart();
  }
  function applyTheme(t) {
    state.theme = t || 'system';
    if (state.theme === 'system') document.documentElement.removeAttribute('data-theme'); else document.documentElement.dataset.theme = state.theme;
    const b = $('#btnTheme'), lbl = { system: 'Tema: automático (do Windows)', light: 'Tema: claro', dark: 'Tema: escuro' }[state.theme];
    b.innerHTML = icon({ system: 'auto', light: 'sun', dark: 'moon' }[state.theme]); b.title = lbl; b.setAttribute('aria-label', lbl + '. Clique para alternar');
  }

  // ---------- inicialização ----------
  function showConfigError(err) {
    const box = $('#cfgWarn');
    if (!err) { box.hidden = true; return; }
    $('#cfgWarnText').textContent = `Não foi possível ler o arquivo de configuração; o aplicativo iniciou com a configuração padrão. ${err}`;
    box.hidden = false;
  }
  async function init() {
    state.fatal = null; renderList();
    try {
      const d = await bridge.request('ready', {}, 20000);
      showConfigError(d.configLoadError);
      state.config = d.config; state.favs = new Set(d.config.favorites); applyTheme(d.config.settings.theme);
      GitWorkspace.load(d.config);
      applySnapshot(d.snapshot);
    } catch (e) { state.fatal = e.message || 'Não foi possível conectar ao serviço de monitoramento.'; renderTabs(); renderList(); }
  }
  async function boot() {
    bind(); applyTheme('system'); renderTabs(); renderList();
    let wv = window.chrome?.webview;
    if (!wv) { try { wv = await loadMock(); } catch (e) { state.fatal = e.message; renderList(); return; } }
    bridge = createBridge(wv);
    bridge.onPush((m) => { if (m.type === 'snapshot' && m.payload) applySnapshot(m.payload); else if (m.type === 'termOutput' || m.type === 'termExit') Terminals.onPush(m); });
    const request = (t, p, to) => bridge.request(t, p, to), openPath = (t, p) => call(t, { path: p });
    GitView.init({ request, toast, copy: copyText, openPath, confirm: confirmDialog, grid: $('#gitgrid'), onUpdate: (root, d) => GitWorkspace.onDetails(root, d), worktreeCreate: (root, br) => GitWorkspace.worktreeDialog(root, br), worktreeOpen: (path) => GitWorkspace.openWorktreePath(path) });
    GitWorkspace.init({
      request, toast, openPath, confirm: confirmDialog, knownRepos, icon, snapshot: () => state.snap, onCount: (n) => { st($('#cnt-git'), String(n)); st($('#dockHandleCnt'), String(n)); },
      isBusy: (id) => state.busy.has(id), toggleRun: actToggleRun, setRun: actSetRun, filterProcesses, selectGitTab, onDockChange,
      openSavedForm: (saved, sugg) => openForm(saved, null, sugg),
      u: { esc, fmtBytes, fmtPct, plural, shortPath, pathText, nf0 },
    });
    Terminals.init({
      request, toast, confirm: confirmDialog, icon, u: { esc },
      tabs: () => GitWorkspace.tabs(), wtFolders: () => GitWorkspace.wtFolders(), projectCtx: () => GitWorkspace.ctx(), projectVisible: () => GitWorkspace.panelVisible(), showTab: (f) => GitWorkspace.showTab(f),
      showLoose, looseVisible: () => state.tab === 'saved' && !wrapEl.hidden,
    });
    init();
  }
  boot();
})();
