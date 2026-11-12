'use strict';
/* Painéis de repositório (controle de código estilo VS Code + árvore de commits).
   Quem decide quais repositórios aparecem é o espaço de trabalho (git-workspace.js); aqui só se desenha.
   O layout de trilhas (lane/edges/passThrough) vem pronto do Core. */
(() => {
  const ROW = 26, LW = 14, VIS_LANES = 10, STEP = 150, HARD = 500, WIDE = 760, POLL = 10000;
  const nf0 = new Intl.NumberFormat('pt-BR');
  const dtf = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'medium', timeStyle: 'short' });
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const BS = String.fromCharCode(92), LRE = String.fromCharCode(0x202A), PDF = String.fromCharCode(0x202C), ARR = String.fromCharCode(8594);

  const IC = {
    branch: '<circle cx="7" cy="6" r="2"/><circle cx="7" cy="18" r="2"/><circle cx="17" cy="9" r="2"/><path d="M7 8v8M17 11c0 3-4 3-8.5 5.2"/>',
    sync: '<path d="M20 11a8 8 0 0 0-14-4.5L4 9M4 4v5h5M4 13a8 8 0 0 0 14 4.5L20 15M20 20v-5h-5"/>',
    fetch: '<path d="M12 4v11M7.5 10.5 12 15l4.5-4.5M5 19.5h14"/>',
    merge: '<circle cx="6" cy="6" r="2"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="15" r="2"/><path d="M6 8v8M8 6c5 0 10 1 10 7"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
    graph: '<circle cx="6" cy="6" r="2"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="12" r="2"/><path d="M6 8v8M8 6h3a5 5 0 0 1 5 5"/>',
    refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4"/>',
    more: '<circle cx="5.5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="18.5" cy="12" r="1.3"/>',
    max: '<path d="M5 9V5h4M19 9V5h-4M5 15v4h4M19 15v4h-4"/>',
    min: '<path d="M9 5v4H5M15 5v4h4M9 19v-4H5M15 19v-4h4"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    wt: '<rect x="3.5" y="4.5" width="10" height="7" rx="1.5"/><rect x="10.5" y="12.5" width="10" height="7" rx="1.5"/>',
    minus: '<path d="M5 12h14"/>',
    undo: '<path d="M9 8H5V4M5.5 8A8 8 0 1 1 5 15"/>',
    folder: '<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.3a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>',
    terminal: '<path d="m5 8 4 4-4 4M12 17h7"/>',
    chev: '<path d="m9 6 6 6-6 6"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    lock: '<rect x="5.5" y="11" width="13" height="8.5" rx="2"/><path d="M8.5 11V8.5a3.5 3.5 0 0 1 7 0V11"/>',
    alert: '<path d="M12 4 3 19h18z"/><path d="M12 10v4M12 16.8v.2"/>',
    tag: '<path d="M4 4h7l9 9-7 7-9-9z"/><circle cx="8.5" cy="8.5" r="1"/>',
    cloud: '<path d="M7 18a4 4 0 0 1-.5-8A5.5 5.5 0 0 1 17 8.5a4.8 4.8 0 0 1 0 9.5z"/>',
    copy: '<rect x="8.5" y="8.5" width="10" height="11" rx="2"/><path d="M15.5 8.5V6.5a2 2 0 0 0-2-2h-6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h1"/>',
    switch: '<path d="M4 8h13M13.5 4.5 17 8l-3.5 3.5M20 16H7M10.5 12.5 7 16l3.5 3.5"/>',
  };
  const ic = (n) => `<svg class="ic" viewBox="0 0 24 24" aria-hidden="true">${IC[n]}</svg>`;
  const ib = (act, icon, label, extra = '') => `<button type="button" class="ibtn ${extra}" data-ga="${act}" aria-label="${esc(label)}" title="${esc(label)}">${ic(icon)}</button>`;

  // ícones de arquivo: selo colorido com a sigla (SVG próprio, sem biblioteca)
  const FT = { ts: ['TS', '#2f6fdd', '#fff'], tsx: ['TS', '#2f6fdd', '#fff'], js: ['JS', '#e8c53a', '#2a2300'], mjs: ['JS', '#e8c53a', '#2a2300'], cjs: ['JS', '#e8c53a', '#2a2300'], md: ['MD', '#5b6b7c', '#fff'], json: ['{}', '#c98a1a', '#fff'], css: ['#', '#8a4fd0', '#fff'], scss: ['#', '#cf3b6a', '#fff'], html: ['<>', '#d9531e', '#fff'], cs: ['C#', '#2a9d6e', '#fff'], csproj: ['C#', '#2a9d6e', '#fff'], sql: ['DB', '#0b7a86', '#fff'], prisma: ['P', '#3b4a5a', '#fff'], yml: ['YM', '#8a6f2a', '#fff'], yaml: ['YM', '#8a6f2a', '#fff'] };
  function fileIcon(path) {
    const ext = (path.split('.').pop() || '').toLowerCase(), t = path.includes('.') ? FT[ext] : null;
    if (!t) return '<svg class="fi-svg" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 1.8h5.2L12.2 5v9.2H4z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/><path d="M9 1.8V5h3.2" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>';
    return `<svg class="fi-svg" viewBox="0 0 16 16" aria-hidden="true"><rect x="1" y="1" width="14" height="14" rx="3.2" fill="${t[1]}"/><text x="8" y="11.2" font-size="7.2" font-weight="700" font-family="Segoe UI,Arial,sans-serif" text-anchor="middle" fill="${t[2]}">${esc(t[0])}</text></svg>`;
  }
  const STATUS = { M: 'Modificado', A: 'Adicionado', D: 'Excluído', R: 'Renomeado', C: 'Copiado', T: 'Tipo alterado', U: 'Conflito (não mesclado)', '?': 'Não rastreado' };

  function rel(iso) {
    const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
    if (m < 1) return 'agora';
    if (m < 60) return `há ${m} min`;
    if (m < 1440) return `há ${Math.floor(m / 60)} h`;
    const d = Math.floor(m / 1440);
    if (d < 14) return `há ${d} d`;
    if (d < 60) return `há ${Math.floor(d / 7)} sem`;
    if (d < 700) return `há ${Math.floor(d / 30)} ${Math.floor(d / 30) === 1 ? 'mês' : 'meses'}`;
    return `há ${Math.floor(d / 365)} anos`;
  }
  const baseName = (p) => p.slice(p.lastIndexOf('/') + 1), dirName = (p) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '');
  const sum = (out) => String(out || '').split('\n').map((s) => s.trim()).filter(Boolean).slice(0, 2).join(' · ').slice(0, 200);

  // ---------- estado ----------
  let env = null, gridEl = null, visible = false, maxRoot = null, splitPct = 42, tick30 = 0, stacked = false;
  const cache = new Map();   // raiz → painel (mantém rascunho de commit e abas ao trocar de projeto)
  let shown = [];            // painéis na grade agora

  // ---------- painel ----------
  function makePanel(root, name) {
    const el = document.createElement('section');
    el.className = 'gp'; el.tabIndex = -1; el.dataset.root = root; el.dataset.mode = 'narrow'; el.dataset.tab = 'changes';
    el.setAttribute('role', 'region'); el.setAttribute('aria-label', `Repositório ${name}`);
    el.innerHTML = `
      <header class="gp-head">
        <div class="gp-l1">
          <h3 class="gp-name" data-r="name"></h3><span class="gp-git">Git</span>
          <button type="button" class="gp-branch" data-ga="branch-pick" data-popper aria-haspopup="dialog" data-r="branchBtn"></button>
          <span class="gp-sync" data-r="sync"></span><span class="gp-lock" data-r="lock" hidden></span>
          <span class="gp-sp"></span>
          <span class="gp-win">${ib('max', 'max', 'Maximizar (Esc volta)', 'maxb')}</span>
        </div>
        <div class="gp-l2">
          <span class="gp-path" data-r="path"></span><span class="gp-upd" data-r="upd"></span><span class="gp-sp"></span>
          <span class="gp-acts">${ib('sync', 'sync', 'Sincronizar (pull e push)', 'lk')}${ib('fetch', 'fetch', 'Buscar do remoto (fetch)', 'lk')}${ib('branch-pick', 'switch', 'Trocar ou criar branch', 'lk').replace('class="ibtn', 'data-popper class="ibtn')}${ib('merge-pick', 'merge', 'Mesclar na branch atual…', 'lk').replace('class="ibtn', 'data-popper class="ibtn')}${ib('commit', 'check', 'Commit (Ctrl+Enter na mensagem)', 'lk')}${ib('show-tree', 'graph', 'Ver histórico (árvore)')}${ib('refresh', 'refresh', 'Atualizar agora')}${ib('menu', 'more', 'Mais ações', 'lk').replace('class="ibtn', 'data-popper class="ibtn')}</span>
        </div>
      </header>
      <div class="gp-banner merge" data-r="merge" role="alert" hidden>${ic('alert')}<span>Merge em andamento — resolva os conflitos e faça o commit, ou aborte.</span><button type="button" class="btn small danger lk" data-ga="merge-abort">Abortar merge</button></div>
      <div class="gp-banner err" data-r="err" role="alert" hidden>${ic('alert')}<span data-r="errT"></span><button type="button" class="btn small" data-ga="refresh">Tentar de novo</button></div>
      <div class="gp-ntabs" role="tablist" aria-label="Seções do painel">
        <button type="button" role="tab" data-tab="changes">Mudanças <span class="count" data-r="cChanges">0</span></button><button type="button" role="tab" data-tab="tree">Árvore</button><button type="button" role="tab" data-tab="branches">Branches <span class="count" data-r="cBr">0</span></button>
      </div>
      <div class="gp-body" data-r="body">
        <div class="gp-left">
          <div class="scm-commit">
            <textarea class="scm-msg" name="message" rows="1" data-r="msg" aria-label="Mensagem do commit" spellcheck="true"></textarea>
            <div class="scm-btns"><button type="button" class="btn primary scm-go lk" data-ga="commit" data-r="goBtn">${ic('check')}<span>Commit</span></button><button type="button" class="btn primary scm-dd lk" data-ga="cmenu" data-popper aria-haspopup="menu" aria-label="Mais opções de commit" title="Mais opções de commit">${ic('down')}</button></div>
            <div class="pop cm-menu" data-r="cm" role="menu" hidden>
              <button type="button" role="menuitem" data-ga="commit">Commit</button><button type="button" role="menuitem" data-ga="commit-push">Commit e push</button><button type="button" role="menuitem" data-ga="commit-amend">Commit (amend — refazer o último)</button>
            </div>
          </div>
          <div class="scm-list" data-r="scm"></div>
        </div>
        <div class="gp-split" data-r="split" role="separator" aria-orientation="vertical" aria-label="Redimensionar seções" tabindex="0"></div>
        <div class="gp-right">
          <div class="gp-rtabs" role="tablist" aria-label="Histórico"><button type="button" role="tab" data-tab="tree">Árvore</button><button type="button" role="tab" data-tab="branches">Branches</button></div>
          <div class="gp-pane" data-pane="tree" data-r="tree"></div>
          <div class="gp-pane" data-pane="branches" data-r="branches"></div>
        </div>
      </div>
      <div class="pop gp-pop" data-r="pop" hidden></div>`;
    const P = { root, name, el, gen: 0, data: null, err: null, loading: false, busy: null, lastLoad: 0, maxCommits: STEP, tab: 'changes', wide: false, pop: null, collapsed: {}, sig: {}, r: {} };
    el.querySelectorAll('[data-r]').forEach((n) => { P.r[n.dataset.r] = n; });
    P.r.msg.placeholder = 'Mensagem (Ctrl+Enter para commit)';
    P.ro = new ResizeObserver((es) => { const w = es[0].contentRect.width; const wide = w >= WIDE; if (wide !== P.wide) { P.wide = wide; el.dataset.mode = wide ? 'wide' : 'narrow'; applyTabs(P); } });
    P.ro.observe(el);
    bindPanel(P); bindPop(P);
    renderAll(P); applyTabs(P); updateWin(P);
    return P;
  }

  function bindPanel(P) {
    const el = P.el;
    el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-ga],[data-tab]');
      if (!b || !el.contains(b) || b.disabled) return;
      if (b.dataset.tab) { P.tab = b.dataset.tab; applyTabs(P); return; }
      e.stopPropagation(); act(P, b.dataset.ga, b);
    });
    P.r.msg.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doCommit(P, {}); } });
    P.r.msg.addEventListener('input', () => { const t = P.r.msg; t.style.height = 'auto'; t.style.height = Math.min(t.scrollHeight, 110) + 'px'; });
    el.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && P.pop) { e.stopPropagation(); closePop(P, true); return; }
      if (P.pop && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
        const items = [...el.querySelectorAll('.pop:not([hidden]) [role=option],.pop:not([hidden]) [role=menuitem]')];
        const i = items.indexOf(document.activeElement);
        if (items.length && (i >= 0 || e.target.classList.contains('pk-q'))) { e.preventDefault(); items[Math.max(0, Math.min(items.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))].focus(); }
      }
    });
    const sp = P.r.split;
    sp.addEventListener('pointerdown', (e) => {
      e.preventDefault(); sp.setPointerCapture(e.pointerId);
      const box = P.r.body.getBoundingClientRect();
      const mv = (ev) => { splitPct = Math.max(25, Math.min(70, ((ev.clientX - box.left) / box.width) * 100)); document.querySelectorAll('.gp-body').forEach((b) => b.style.setProperty('--split', splitPct + '%')); };
      const up = () => { for (const t of ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture']) sp.removeEventListener(t, t === 'pointermove' ? mv : up); };
      sp.addEventListener('pointermove', mv); sp.addEventListener('pointerup', up); sp.addEventListener('pointercancel', up); sp.addEventListener('lostpointercapture', up);
    });
    sp.addEventListener('keydown', (e) => { if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return; e.preventDefault(); splitPct = Math.max(25, Math.min(70, splitPct + (e.key === 'ArrowRight' ? 3 : -3))); document.querySelectorAll('.gp-body').forEach((b) => b.style.setProperty('--split', splitPct + '%')); });
    P.r.body.style.setProperty('--split', splitPct + '%');
  }

  function applyTabs(P) {
    const eff = P.wide && P.tab === 'changes' ? 'changes' : P.tab;
    P.el.dataset.tab = eff;
    P.el.querySelectorAll('.gp-ntabs [role=tab]').forEach((t) => { const on = t.dataset.tab === P.tab; t.setAttribute('aria-selected', String(on)); t.tabIndex = on ? 0 : -1; });
    P.el.querySelectorAll('.gp-rtabs [role=tab]').forEach((t) => { const on = (t.dataset.tab === 'branches') === (P.tab === 'branches'); t.setAttribute('aria-selected', String(on)); });
  }
  function updateWin(P) {
    const mx = P.el.querySelector('.maxb');
    const m = maxRoot === P.root;
    mx.innerHTML = ic(m ? 'min' : 'max'); mx.title = mx.ariaLabel = m ? 'Restaurar (Esc)' : 'Maximizar (Esc volta)';
    P.el.classList.toggle('maxed', m);
  }

  // ---------- carregamento ----------
  async function refresh(P, force) {
    if (P.loading || P.busy) return;
    if (!force && Date.now() - P.lastLoad < 2500) return;
    P.loading = true; P.el.classList.add('loading');
    const gen = ++P.gen;   // uma ação (commit, push…) invalida leituras que ainda estão no ar
    try {
      const d = await env.request('gitDetails', { repoRoot: P.root, maxCommits: P.maxCommits, force: !!force });
      if (gen === P.gen) apply(P, d.details);
    } catch (e) {
      if (gen === P.gen) { P.err = e.message || 'Não foi possível ler o repositório.'; P.lastLoad = Date.now(); renderAll(P); }
    } finally { P.loading = false; P.el.classList.remove('loading'); }
  }
  function apply(P, d) {
    P.data = d; P.err = null; P.lastLoad = Date.now();
    if (d.repo?.repoName && d.repo.repoName !== P.name) { P.name = d.repo.repoName; P.el.setAttribute('aria-label', `Repositório ${P.name}`); }
    renderAll(P);
    env.onUpdate?.(P.root, d);
  }

  // ---------- render ----------
  function renderAll(P) { renderHead(P); renderScm(P); renderTree(P); renderBranches(P); renderPop(P); }

  function renderHead(P) {
    const d = P.data, r = P.r;
    r.name.textContent = P.name;
    const note = d?.repo?.statusNote;
    r.path.textContent = LRE + P.root + PDF; r.path.title = P.root;
    const loadingFirst = !d && !P.err;
    const sig = JSON.stringify([d && [d.head, d.repo, d.merging, d.error, d.stagedCount, d.unstagedCount, d.untrackedCount, d.conflictCount, d.rebasing], P.err, P.busy]);
    if (P.sig.head === sig) return;
    P.sig.head = sig;
    if (!d) { r.branchBtn.innerHTML = loadingFirst ? 'Carregando…' : '—'; r.branchBtn.disabled = true; r.sync.innerHTML = ''; r.lock.hidden = true; }
    else {
      const dirty = d.changes ? d.changes.length : d.repo.dirtyCount;
      const bn = d.head.detached || !d.head.branch ? `HEAD destacado @${esc((d.head.sha || '').slice(0, 7))}` : esc(d.head.branch);
      r.branchBtn.disabled = false;
      r.branchBtn.innerHTML = `${ic('branch')}<span class="bn">${bn}${dirty ? '<b aria-hidden="true">*</b>' : ''}</span>${ic('down')}`;
      r.branchBtn.title = `Branch atual: ${d.head.branch || 'HEAD destacado'}${dirty ? ' (com alterações)' : ''}${d.head.upstream ? `\nUpstream: ${d.head.upstream}` : ''}\nClique para trocar de branch`;
      const a = d.repo.ahead, b = d.repo.behind;
      r.sync.innerHTML = (d.head.upstream ? `<span class="gs ahead" title="${a || 0} commit(s) à frente de ${esc(d.head.upstream)}">↑${a || 0}</span><span class="gs behind" title="${b || 0} commit(s) atrás de ${esc(d.head.upstream)}">↓${b || 0}</span>` : '<span class="gs none" title="A branch não tem upstream">sem upstream</span>')
        + (dirty != null ? `<span class="gs dirty" title="${nf0.format(dirty)} arquivo(s) alterado(s)">●${nf0.format(dirty)}</span>` : '');
      r.lock.hidden = !note;
      if (note) { r.lock.innerHTML = `${ic('lock')}<span>${esc(note)}</span>`; r.lock.title = note; }
    }
    const merging = !!d?.merging;
    r.merge.hidden = !merging;
    const errMsg = P.err || d?.error;
    r.err.hidden = !errMsg; if (errMsg) r.errT.textContent = errMsg;
    P.el.classList.toggle('failed', !d && !!P.err);
    r.msg.placeholder = 'Mensagem (Ctrl+Enter para commit)'; r.msg.title = `O commit será feito em "${d?.head?.branch ?? 'HEAD'}"`;
    r.goBtn.lastElementChild.textContent = merging ? 'Concluir merge' : 'Commit';
    r.cChanges.textContent = d?.changes ? String(d.changes.length) : '0'; r.cBr.textContent = String(d?.branches?.length || 0);
    P.el.setAttribute('aria-busy', String(!!P.busy));
    P.el.classList.toggle('is-busy', !!P.busy);
    P.el.querySelectorAll('.lk').forEach((b) => { b.disabled = !!P.busy || !d || (d.changes === null && b.matches('[data-ga=commit],[data-ga=cmenu]')); });
    P.el.querySelectorAll('.spin').forEach((b) => b.classList.remove('spin'));
    if (P.busy) P.el.querySelector(`[data-ga="${P.busy}"]`)?.classList.add('spin');
  }

  function fileRow(c, kind) {
    const letter = kind === 'conf' ? 'U' : kind === 'staged' ? c.index : c.worktree;
    const dir = dirName(c.path), name = baseName(c.path);
    const renamed = c.origPath && kind !== 'unst';
    const label = renamed ? `${baseName(c.origPath)} ${ARR} ${name}` : name;
    const title = `${c.path}${renamed ? `\n(antes: ${c.origPath})` : ''}\n${STATUS[letter] || ''}`;
    const folder = ib('folder', 'folder', 'Abrir pasta do arquivo');
    const acts = kind === 'staged' ? ib('unstage', 'minus', 'Tirar da preparação') + folder
      : kind === 'conf' ? ib('stage', 'plus', 'Marcar como resolvido (preparar)') + folder
        : ib('stage', 'plus', 'Preparar') + ib('discard', 'undo', 'Descartar alterações') + folder;
    return `<li class="sc-f ${kind}" data-path="${esc(c.path)}" data-kind="${kind}" title="${esc(title)}"><span class="fi">${fileIcon(c.path)}</span><span class="fn"><span class="fnn">${esc(label)}</span>${dir ? `<span class="fd">${esc(dir)}</span>` : ''}</span><span class="fa">${acts}</span><span class="fl L${letter === '?' ? 'Q' : letter}" aria-label="${esc(STATUS[letter] || '')}">${letter === '?' ? '?' : esc(letter)}</span></li>`;
  }
  function section(P, key, title, list, kind, acts) {
    if (!list.length) return '';
    const open = !P.collapsed[key];
    return `<section class="sc-sec ${key}" data-sec="${key}"><div class="sc-h"><button type="button" class="sc-t" data-ga="toggle" data-sec="${key}" aria-expanded="${open}">${ic('chev')}<span>${title}</span><span class="count">${nf0.format(list.length)}</span></button><span class="sc-ha">${acts}</span></div>${open ? `<ul class="sc-files" role="list">${list.map((c) => fileRow(c, kind)).join('')}</ul>` : ''}</section>`;
  }
  function renderScm(P) {
    const d = P.data, box = P.r.scm;
    const sig = JSON.stringify([d && [d.changes, d.merging, d.repo.statusNote], P.collapsed, !d, !!P.err]);
    if (P.sig.scm === sig) return; P.sig.scm = sig;
    const keep = document.activeElement && box.contains(document.activeElement) ? { path: document.activeElement.closest('[data-path]')?.dataset.path, kind: document.activeElement.closest('[data-kind]')?.dataset.kind, act: document.activeElement.dataset.ga, sec: document.activeElement.dataset.sec } : null;
    if (!d) { box.innerHTML = P.err ? '' : '<div class="skl"><i></i><i></i><i></i><i></i></div>'; return; }
    if (d.changes === null) { box.innerHTML = `<div class="gp-empty">${ic('lock')}<p>${esc(d.repo.statusNote || 'O status deste repositório não foi lido por segurança.')}</p></div>`; return; }
    const by = (a, b) => a.path.localeCompare(b.path, 'pt-BR');
    const conf = d.changes.filter((c) => c.conflict).sort(by), staged = d.changes.filter((c) => c.index && !c.conflict).sort(by);
    const un = d.changes.filter((c) => !c.conflict && c.worktree).sort((a, b) => (a.worktree === '?') - (b.worktree === '?') || by(a, b));
    const html = section(P, 'conf', 'Conflitos', conf, 'conf', ib('stage-all-conf', 'plus', 'Marcar todos como resolvidos'))
      + section(P, 'staged', 'Mudanças preparadas', staged, 'staged', ib('unstage-all', 'minus', 'Tirar todas da preparação'))
      + section(P, 'unst', 'Mudanças', un, 'unst', ib('stage-all', 'plus', 'Preparar todas') + ib('discard-all', 'undo', 'Descartar todas'));
    box.innerHTML = html || `<div class="gp-empty"><p>Nenhuma mudança.</p><span class="hint">A árvore de trabalho está limpa.</span></div>`;
    if (keep) {
      const sel = keep.path ? `[data-path="${CSS.escape(keep.path)}"][data-kind="${keep.kind}"] [data-ga="${keep.act}"]` : keep.sec ? `[data-sec="${keep.sec}"] [data-ga="${keep.act}"]` : null;
      (sel && box.querySelector(sel))?.focus();
    }
  }

  // gráfico: cada segmento liga o centro de uma linha ao da seguinte; passThrough que não segue reto converge no nó da próxima linha
  function graphSvg(cs, lanes) {
    const w = Math.max(1, lanes) * LW, hgt = cs.length * ROW, X = (l) => l * LW + LW / 2, Y = (i) => i * ROW + ROW / 2, C = (l) => l % 8;
    const paths = Array.from({ length: 8 }, () => '');
    const seg = (c, x1, y1, x2, y2) => { paths[c] += x1 === x2 ? `M${x1} ${y1}V${y2}` : `M${x1} ${y1}C${x1} ${(y1 + y2) / 2} ${x2} ${(y1 + y2) / 2} ${x2} ${y2}`; };
    let nodes = '';
    cs.forEach((c, i) => {
      const nx = cs[i + 1], y1 = Y(i), y2 = nx ? Y(i + 1) : hgt, seen = new Set();
      for (const e of c.edges) { const k = e.fromLane + '>' + e.toLane; if (seen.has(k)) continue; seen.add(k); seg(C(e.toLane), X(e.fromLane), y1, nx ? X(e.toLane) : X(e.fromLane), y2); }
      for (const p of c.passThrough) {
        const straight = !nx || p === nx.lane || nx.passThrough.includes(p);
        seg(C(p), X(p), y1, straight ? X(p) : X(nx.lane), y2);
      }
      const col = C(c.lane), isHead = c.refs.some((r) => r.kind === 'head');
      nodes += `<circle class="gn ${c.parents.length > 1 ? 'gm' : 'gc'} c${col}" cx="${X(c.lane)}" cy="${y1}" r="${c.parents.length > 1 ? 4.2 : 4.6}"/>`;
      if (isHead) nodes += `<circle class="gh" cx="${X(c.lane)}" cy="${y1}" r="7.4"/>`;
    });
    return `<svg width="${w}" height="${hgt}" viewBox="0 0 ${w} ${hgt}" aria-hidden="true" focusable="false">${paths.map((d, i) => (d ? `<path class="gl c${i}" d="${d}"/>` : '')).join('')}${nodes}</svg>`;
  }
  function refPill(r) {
    const k = r.kind;
    return `<span class="rf rf-${k}" title="${esc(({ head: 'HEAD (commit atual)', branch: 'Branch local ', remote: 'Branch remota ', tag: 'Tag ' }[k]) + (k === 'head' ? '' : r.name))}">${k === 'tag' ? ic('tag') : k === 'remote' ? ic('cloud') : ''}${esc(r.name)}</span>`;
  }
  function renderTree(P) {
    const d = P.data, box = P.r.tree;
    if (!d) { if (P.sig.tree !== 'x') { P.sig.tree = 'x'; box.innerHTML = '<div class="skl"><i></i><i></i><i></i><i></i><i></i></div>'; } return; }
    const sig = d.commits.map((c) => c.sha + c.refs.map((r) => r.kind[0] + r.name).join(',')).join('|') + `#${d.truncated}#${d.graph.lanes}#${P.maxCommits}#${d.error ? 1 : 0}`;
    if (P.sig.tree === sig) return; P.sig.tree = sig;
    if (!d.commits.length) { box.innerHTML = `<div class="gp-empty"><p>${d.error ? 'Histórico indisponível.' : 'Nenhum commit ainda.'}</p>${d.error ? `<span class="hint">${esc(d.error)}</span>` : ''}</div>`; return; }
    const lanes = Math.max(1, d.graph.lanes), gw = Math.min(lanes, VIS_LANES) * LW + 6;
    const rows = d.commits.map((c) => {
      const shown = c.refs.slice(0, 3), more = c.refs.length - shown.length;
      const full = dtf.format(new Date(c.date));
      return `<div class="gt-row${c.refs.some((r) => r.kind === 'head') ? ' head' : ''}" role="listitem"><span class="gt-main">${shown.map(refPill).join('')}${more > 0 ? `<span class="rf rf-more" title="${esc(c.refs.slice(3).map((r) => r.name).join('\n'))}">+${more}</span>` : ''}<span class="gt-subj" title="${esc(c.subject)}">${esc(c.subject)}</span></span><span class="gt-author" title="${esc(c.author)}">${esc(c.author)}</span><time class="gt-date" datetime="${esc(c.date)}" data-d="${esc(c.date)}" title="${esc(full)}">${rel(c.date)}</time><button type="button" class="gt-sha" data-ga="copy-sha" data-sha="${esc(c.sha)}" title="Copiar ${esc(c.sha)}" aria-label="Copiar o hash ${esc(c.shortSha)}">${esc(c.shortSha)}</button></div>`;
    }).join('');
    const more = d.truncated ? (P.maxCommits >= HARD ? `<div class="gt-more"><span class="hint">Mostrando os ${HARD} commits mais recentes (limite).</span></div>` : `<div class="gt-more"><button type="button" class="btn small" data-ga="more-commits">Mostrar mais commits</button><span class="hint">${nf0.format(d.commits.length)} exibidos</span></div>`) : '';
    box.innerHTML = `<div class="gt"><div class="gt-graph" tabindex="${lanes > VIS_LANES ? 0 : -1}" aria-label="Gráfico de trilhas">${graphSvg(d.commits, lanes)}</div><div class="gt-rows" role="list" aria-label="Commits">${rows}</div></div>${more}`;
    box.firstElementChild.style.setProperty('--gw', gw + 'px');
  }
  // worktree: selo de "em uso" e ação (criar worktree desta branch / abrir o worktree dela)
  const useBadge = (b) => (b.worktreePath ? `<span class="wt-use" title="${esc('Em uso no worktree ' + b.worktreePath)}">${ic('wt')}em worktree</span>` : '');
  const wtAction = (b) => (b.current ? '' : b.worktreePath
    ? `<button type="button" class="ibtn" data-ga="wt-open" data-path="${esc(b.worktreePath)}" aria-label="${esc('Abrir o worktree de ' + b.name)}" title="Abrir o worktree desta branch">${ic('wt')}</button>`
    : `<button type="button" class="ibtn" data-ga="wt-create" data-br="${esc(b.name)}" aria-label="${esc('Criar worktree da branch ' + b.name)}" title="Criar worktree desta branch">${ic('wt')}</button>`);
  function renderBranches(P) {
    const d = P.data, box = P.r.branches;
    if (!d) { box.innerHTML = '<div class="skl"><i></i><i></i><i></i></div>'; return; }
    const sig = JSON.stringify(d.branches.map((b) => [b.name, b.current, b.ahead, b.behind, b.sha, b.worktreePath ?? null]));
    if (P.sig.br === sig) return; P.sig.br = sig;
    const row = (b) => `<li class="br-r${b.current ? ' cur' : ''}"><span class="br-n" title="${esc(b.name)}">${b.current ? ic('check') : ''}<span>${esc(b.name)}</span>${useBadge(b)}</span><span class="br-ab">${b.ahead ? `<span class="gs ahead">↑${b.ahead}</span>` : ''}${b.behind ? `<span class="gs behind">↓${b.behind}</span>` : ''}</span><time class="br-d" title="${esc(dtf.format(new Date(b.lastCommitDate)))}" data-d="${esc(b.lastCommitDate)}">${rel(b.lastCommitDate)}</time><span class="br-a">${(b.current ? '' : (b.worktreePath ? ib('checkout', 'switch', `Trocar para ${b.name} (em uso em outro worktree)`).replace('<button ', '<button aria-disabled="true" ') : ib('checkout', 'switch', `Trocar para ${b.name}`)) + ib('merge', 'merge', `Mesclar ${b.name} na atual`)) + wtAction(b)}</span></li>`.replace('<li ', `<li data-br="${esc(b.name)}" `);
    const loc = d.branches.filter((b) => !b.remote), rem = d.branches.filter((b) => b.remote);
    box.innerHTML = (loc.length ? `<h4 class="br-h">Locais <span class="count">${loc.length}</span></h4><ul class="br-l" role="list">${loc.map(row).join('')}</ul>` : '') + (rem.length ? `<h4 class="br-h">Remotas <span class="count">${rem.length}</span></h4><ul class="br-l" role="list">${rem.map(row).join('')}</ul>` : '') || '<div class="gp-empty"><p>Nenhuma branch.</p></div>';
  }

  // ---------- popovers ----------
  function openPop(P, type) {
    if (P.pop?.type === type) { closePop(P, true); return; }
    P.pop = { type }; renderPop(P);
    const first = P.el.querySelector('.pop:not([hidden]) .pk-q, .pop:not([hidden]) [role=menuitem], .pop:not([hidden]) .pk-name');
    first?.focus();
  }
  function closePop(P, back) {
    const t = P.pop?.type; P.pop = null; renderPop(P);
    if (back) P.el.querySelector(t === 'cmenu' ? '.scm-dd' : t === 'menu' ? '[data-ga=menu]' : t === 'merge' ? '[data-ga=merge-pick]' : '[data-ga=branch-pick]')?.focus();
  }
  const branchRows = (P, filter, mode) => {
    const d = P.data, f = filter.trim().toLowerCase();
    const list = d.branches.filter((b) => !b.name.endsWith('/HEAD') && (mode !== 'merge' || !b.current) && (!f || b.name.toLowerCase().includes(f)));
    const one = (b) => `<div class="pk-row"><button type="button" role="option" class="pk-o${b.current ? ' cur' : ''}"${b.worktreePath && mode !== 'merge' ? ' aria-disabled="true"' : ''} data-ga="${mode === 'merge' ? 'do-merge' : 'do-checkout'}" data-br="${esc(b.name)}" aria-selected="${b.current}">${b.current ? ic('check') : b.remote ? ic('cloud') : ic('branch')}<span class="pk-bn">${esc(b.name)}</span>${useBadge(b)}${b.ahead ? `<span class="gs ahead">↑${b.ahead}</span>` : ''}${b.behind ? `<span class="gs behind">↓${b.behind}</span>` : ''}<time class="pk-d" data-d="${esc(b.lastCommitDate)}">${rel(b.lastCommitDate)}</time></button>${mode === 'merge' ? '' : wtAction(b)}</div>`;
    const loc = list.filter((b) => !b.remote), rem = list.filter((b) => b.remote);
    return (loc.length ? `<div class="pk-g">Locais</div>${loc.map(one).join('')}` : '') + (rem.length ? `<div class="pk-g">Remotas</div>${rem.map(one).join('')}` : '') || '<div class="pk-none">Nenhuma branch encontrada.</div>';
  };
  function renderPop(P) {
    const pop = P.r.pop, cm = P.r.cm, d = P.data, t = P.pop?.type;
    cm.hidden = t !== 'cmenu';
    pop.hidden = !t || t === 'cmenu';
    P.el.querySelectorAll('[data-popper]').forEach((b) => b.setAttribute('aria-expanded', String(!!t && b.dataset.ga === ({ branch: 'branch-pick', create: 'branch-pick', merge: 'merge-pick', menu: 'menu', cmenu: 'cmenu' }[t]))));
    if (!t || t === 'cmenu' || !d) { if (!t) pop.innerHTML = ''; return; }
    pop.className = 'pop gp-pop ' + t;
    if (t === 'menu') {
      pop.innerHTML = `<div role="menu"><button type="button" role="menuitem" data-ga="pull">Pull (somente avanço)</button><button type="button" role="menuitem" data-ga="push">Push</button><button type="button" role="menuitem" data-ga="fetch">Fetch</button>${d.merging ? '<button type="button" role="menuitem" class="danger" data-ga="merge-abort">Abortar merge</button>' : ''}<hr><button type="button" role="menuitem" data-ga="folder">Abrir pasta</button><button type="button" role="menuitem" data-ga="terminal">Abrir terminal</button><button type="button" role="menuitem" data-ga="copy-path">Copiar caminho</button></div>`;
    } else if (t === 'create') {
      pop.innerHTML = `<form class="pk-form" data-form="create"><h4>Criar branch</h4><label>Nome<input class="pk-name" name="name" type="text" autocomplete="off" spellcheck="false" placeholder="feat/minha-mudanca" required></label><label>A partir de<select name="from">${d.branches.filter((b) => !b.name.endsWith('/HEAD')).map((b) => `<option value="${esc(b.name)}"${b.current ? ' selected' : ''}>${esc(b.name)}${b.current ? ' (atual)' : ''}</option>`).join('')}</select></label><div class="pk-err" role="alert"></div><div class="pk-foot"><button type="button" class="btn small" data-ga="back-pick">Voltar</button><button type="submit" class="btn small primary">Criar e trocar</button></div></form>`;
    } else {
      const q = P.pop.q || '';
      pop.innerHTML = `<div class="pk"><div class="pk-top"><input class="pk-q" name="q" type="search" placeholder="${t === 'merge' ? 'Mesclar qual branch na atual?' : 'Trocar para a branch…'}" aria-label="Buscar branch" value="${esc(q)}" autocomplete="off" spellcheck="false"></div>${t === 'branch' ? `<button type="button" class="pk-create" data-ga="create-pick">${ic('plus')}Criar branch…</button>` : `<div class="pk-hint">Mesclar em <b>${esc(d.head.branch || 'HEAD')}</b></div>`}<div class="pk-list" role="listbox" aria-label="Branches">${branchRows(P, q, t)}</div></div>`;
    }
  }
  function bindPop(P) {
    P.r.pop.addEventListener('input', (e) => { if (!e.target.classList.contains('pk-q')) return; P.pop.q = e.target.value; P.r.pop.querySelector('.pk-list').innerHTML = branchRows(P, P.pop.q, P.pop.type); });
    P.r.pop.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.classList.contains('pk-q')) { e.preventDefault(); P.r.pop.querySelector('.pk-o:not(.cur)')?.click(); } });
    P.r.pop.addEventListener('submit', async (e) => {
      e.preventDefault();
      const f = e.target, name = f.elements.name.value.trim(), from = f.elements.from.value;
      if (!name) { f.querySelector('.pk-err').textContent = 'Informe o nome da branch.'; return; }
      if (await run(P, 'create', 'gitCheckout', { repoRoot: P.root, branch: name, create: true, startPoint: from }, `Branch "${name}" criada`)) closePop(P);
    });
  }

  // ---------- ações ----------
  async function run(P, key, type, payload, okMsg) {
    if (P.busy) return false;
    P.gen++;
    P.busy = key; P.sig.head = null; renderHead(P);
    try {
      const d = await env.request(type, payload, 200000);
      P.busy = null; apply(P, d.details);
      if (key === 'merge' && d.details.merging && d.details.conflictCount) env.toast(`Merge com ${d.details.conflictCount} conflito(s): resolva os arquivos e conclua o merge, ou aborte.`, 'error');
      else env.toast(sum(d.output) || okMsg || 'Concluído');
      return true;
    } catch (e) {
      P.busy = null; P.sig.head = null; renderHead(P);
      env.toast(e.message || 'A operação git falhou.', 'error');
      return false;
    }
  }
  const files = (P, kind) => {
    const ch = P.data?.changes || [];
    return ch.filter((c) => (kind === 'conf' ? c.conflict : kind === 'staged' ? c.index && !c.conflict : !c.conflict && c.worktree)).map((c) => c.path);
  };
  async function discard(P, paths) {
    const ch = P.data?.changes || [], sel = ch.filter((c) => paths.includes(c.path));
    if (!sel.length) return;
    const untracked = sel.filter((c) => c.worktree === '?').length;
    const one = sel.length === 1 ? `"${sel[0].path}"` : `${sel.length} arquivos`;
    const body = sel.length === 1 && untracked ? `O arquivo não rastreado ${one} será excluído do disco. Esta ação não tem volta.`
      : `${sel.length === 1 ? 'As alterações de ' : 'As alterações de '}${one} serão descartadas${untracked ? ` (${untracked} não rastreado${untracked > 1 ? 's' : ''} será excluído do disco)` : ''}. Esta ação não tem volta.`;
    const ok = await env.confirm({ title: 'Descartar alterações?', body, ok: 'Descartar' });
    if (ok) await run(P, 'discard', 'gitDiscard', { repoRoot: P.root, paths }, 'Alterações descartadas');
  }
  async function doCommit(P, { push = false, amend = false }) {
    const d = P.data; if (!d || P.busy) return;
    if (d.changes === null) { env.toast('O status deste repositório não foi lido por segurança; não é possível fazer commit por aqui.', 'error'); return; }
    const message = P.r.msg.value;
    if (!message.trim()) { env.toast('Digite a mensagem do commit.', 'error'); P.r.msg.focus(); return; }
    if (d.conflictCount) { env.toast(`Resolva os ${d.conflictCount} conflito(s) e prepare os arquivos antes do commit.`, 'error'); return; }
    let all = false;
    if (!amend && !d.merging && !d.stagedCount) {
      if (!d.changes.length) { env.toast('Não há mudanças para fazer commit.', 'error'); return; }
      const ok = await env.confirm({ title: 'Não há mudanças preparadas', body: 'Preparar todas as mudanças e fazer o commit?', ok: 'Preparar tudo e commit', danger: false });
      if (!ok) return;
      all = true;
    }
    if (amend && !(await env.confirm({ title: 'Refazer o último commit?', body: 'O commit atual será substituído (amend) com a mensagem e as mudanças preparadas. Se ele já foi enviado, será preciso forçar o push.', ok: 'Refazer commit' }))) return;
    if (await run(P, 'commit', 'gitCommit', { repoRoot: P.root, message, all, amend, push }, push ? 'Commit feito e enviado' : 'Commit feito')) { P.r.msg.value = ''; P.r.msg.style.height = ''; }
  }
  function act(P, a, btn) {
    const row = btn.closest('[data-path]'), path = row?.dataset.path;
    switch (a) {
      case 'max': maxRoot = maxRoot === P.root ? null : P.root; layout(); P.el.focus(); return;
      case 'refresh': refresh(P, true); return;
      case 'show-tree': P.tab = 'tree'; applyTabs(P); P.r.tree.scrollTop = 0; return;
      case 'branch-pick': if (P.pop?.type === 'create') { P.pop = null; } openPop(P, 'branch'); return;
      case 'merge-pick': openPop(P, 'merge'); return;
      case 'create-pick': P.pop = { type: 'create' }; renderPop(P); P.el.querySelector('.pk-name')?.focus(); return;
      case 'back-pick': P.pop = { type: 'branch' }; renderPop(P); P.el.querySelector('.pk-q')?.focus(); return;
      case 'menu': openPop(P, 'menu'); return;
      case 'cmenu': openPop(P, 'cmenu'); return;
      case 'toggle': { const s = btn.dataset.sec; P.collapsed[s] = !P.collapsed[s]; renderScm(P); return; }
      case 'stage': run(P, 'stage', 'gitStage', { repoRoot: P.root, paths: [path] }); return;
      case 'unstage': run(P, 'unstage', 'gitUnstage', { repoRoot: P.root, paths: [path] }); return;
      case 'discard': discard(P, [path]); return;
      case 'stage-all': run(P, 'stage-all', 'gitStage', { repoRoot: P.root, paths: files(P, 'unst') }); return;
      case 'stage-all-conf': run(P, 'stage-all-conf', 'gitStage', { repoRoot: P.root, paths: files(P, 'conf') }); return;
      case 'unstage-all': run(P, 'unstage-all', 'gitUnstage', { repoRoot: P.root, paths: files(P, 'staged') }); return;
      case 'discard-all': discard(P, files(P, 'unst')); return;
      case 'folder': {
        if (path) { const dir = dirName(path); env.openPath('openFolder', P.root + (dir ? BS + dir.split('/').join(BS) : '')); } else env.openPath('openFolder', P.root);
        closePop(P); return;
      }
      case 'terminal': env.openPath('openTerminal', P.root); closePop(P); return;
      case 'copy-path': env.copy(P.root); closePop(P); return;
      case 'copy-sha': env.copy(btn.dataset.sha); return;
      case 'more-commits': P.maxCommits = Math.min(HARD, P.maxCommits + (P.maxCommits < 300 ? STEP : 200)); P.sig.tree = null; refresh(P, true); return;
      case 'commit': closePop(P); doCommit(P, {}); return;
      case 'commit-push': closePop(P); doCommit(P, { push: true }); return;
      case 'commit-amend': closePop(P); doCommit(P, { amend: true }); return;
      case 'fetch': closePop(P); run(P, 'fetch', 'gitFetch', { repoRoot: P.root }, 'Fetch concluído'); return;
      case 'pull': closePop(P); run(P, 'pull', 'gitPull', { repoRoot: P.root }, 'Pull concluído'); return;
      case 'push': closePop(P); run(P, 'push', 'gitPush', { repoRoot: P.root }, 'Push concluído'); return;
      case 'sync': syncRepo(P); return;
      case 'merge-abort': closePop(P); env.confirm({ title: 'Abortar o merge?', body: 'O merge em andamento será cancelado e as resoluções de conflito feitas até agora serão perdidas.', ok: 'Abortar merge' }).then((ok) => { if (ok) run(P, 'merge-abort', 'gitMergeAbort', { repoRoot: P.root }, 'Merge abortado'); }); return;
      case 'wt-create': { const br = btn.dataset.br; closePop(P); env.worktreeCreate?.(P.root, br); return; }
      case 'wt-open': closePop(P); env.worktreeOpen?.(btn.dataset.path); return;
      case 'checkout': case 'do-checkout': {
        if (btn.getAttribute('aria-disabled') === 'true') { env.toast('Esta branch está em uso em outro worktree. Abra o worktree dela.', 'error'); return; } const br = btn.dataset.br || btn.closest('[data-br]')?.dataset.br; closePop(P); run(P, 'checkout', 'gitCheckout', { repoRoot: P.root, branch: br, create: false, startPoint: null }, `Alternado para "${br}"`); return; }
      case 'merge': case 'do-merge': {
        const br = btn.dataset.br || btn.closest('[data-br]')?.dataset.br, cur = P.data?.head?.branch || 'HEAD';
        closePop(P);
        env.confirm({ title: 'Mesclar branches?', body: `Mesclar "${br}" em "${cur}"?`, ok: 'Mesclar', danger: false }).then((ok) => { if (ok) run(P, 'merge', 'gitMerge', { repoRoot: P.root, branch: br }, 'Merge concluído'); });
        return;
      }
      default:
    }
  }
  async function syncRepo(P) {
    const d = P.data; if (!d || P.busy) return;
    const a = d.repo.ahead || 0, b = d.repo.behind || 0;
    if (b > 0 && !(await run(P, 'sync', 'gitPull', { repoRoot: P.root }, 'Pull concluído'))) return;
    if (a > 0 || !d.head.upstream) { await run(P, 'sync', 'gitPush', { repoRoot: P.root }, 'Push concluído'); return; }
    if (b === 0) await run(P, 'sync', 'gitFetch', { repoRoot: P.root }, 'Já está sincronizado');
  }

  // ---------- grade e seleção de repositórios ----------
  function layout() {
    if (!gridEl) return;
    const n = shown.length;
    gridEl.dataset.n = stacked ? 's' : String(n);
    if (maxRoot && !shown.some((p) => p.root === maxRoot)) maxRoot = null;
    gridEl.classList.toggle('has-max', !!maxRoot);
    for (const e of [...gridEl.children]) if (!shown.some((p) => p.el === e)) e.remove();
    let ref = gridEl.firstElementChild;
    for (const p of shown) { if (p.el === ref) ref = ref.nextElementSibling; else gridEl.insertBefore(p.el, ref); updateWin(p); }
    gridEl.hidden = n === 0;
  }
  // list = [{ root, name }] (até 4); stack = um painel embaixo do outro (modo fixado à direita)
  function setRepos(list, opts = {}) {
    stacked = !!opts.stack;
    shown = list.slice(0, 4).map(({ root, name }) => {
      let P = cache.get(root);
      if (!P) { P = makePanel(root, name || root); cache.set(root, P); }
      return P;
    });
    layout();
    if (visible) shown.forEach((p) => refresh(p, false));
  }
  // descarta os painéis de repositórios que não estão em nenhuma aba
  function prune(roots) {
    for (const [root, P] of cache) {
      if (roots.has(root) || shown.includes(P)) continue;
      P.ro.disconnect(); P.el.remove(); cache.delete(root);
    }
  }
  function focusRepo(root) {
    const P = cache.get(root);
    if (!P || !shown.includes(P)) return;
    P.el.scrollIntoView({ block: 'nearest' }); P.el.focus({ preventScroll: true });
  }
  // dado do painel só vale enquanto ele está na tela e visível (senão o snapshot é mais novo)
  const liveInfo = (root) => { const P = cache.get(root); return visible && P && shown.includes(P) ? P.data?.repo ?? null : null; };
  const draftOf = (roots) => roots.filter((r) => (cache.get(r)?.r.msg.value || '').trim()).map((r) => cache.get(r).name);

  function setVisible(v) {
    if (v === visible) return;
    visible = v;
    if (v) { layout(); shown.forEach((p) => refresh(p, false)); }
  }
  function poll() {
    if (!visible || document.visibilityState !== 'visible') return;
    const now = Date.now(); tick30++;
    for (const p of shown) {
      if (p.r.upd) p.r.upd.textContent = p.lastLoad ? `atualizado há ${Math.max(0, Math.round((now - p.lastLoad) / 1000))} s` : '';
      if (now - p.lastLoad >= POLL) refresh(p, true);
    }
    if (tick30 % 30 === 0) document.querySelectorAll('#gitwrap [data-d]').forEach((t) => { const v = rel(t.dataset.d); if (t.textContent !== v) t.textContent = v; });
  }

  function init(e) {
    env = e; gridEl = env.grid;
    layout();
    setInterval(poll, 1000);
    document.addEventListener('visibilitychange', () => { if (visible && document.visibilityState === 'visible') shown.forEach((p) => refresh(p, false)); });
    document.addEventListener('pointerdown', (ev) => {
      for (const p of shown) if (p.pop && !ev.target.closest('.pop') && !ev.target.closest('[data-popper]')) closePop(p);
    });
    document.addEventListener('keydown', (ev) => {
      if (!visible || document.querySelector('dialog[open]')) return;
      if (ev.ctrlKey && !ev.altKey && !ev.shiftKey && /^[1-4]$/.test(ev.key)) { const p = shown[Number(ev.key) - 1]; if (p) { ev.preventDefault(); p.el.focus(); } return; }
      if (ev.key === 'Escape') {
        const withPop = shown.find((p) => p.pop);
        if (withPop) { closePop(withPop, true); ev.stopPropagation(); }
        else if (maxRoot) { maxRoot = null; layout(); ev.preventDefault(); ev.stopPropagation(); }
      }
    }, true);
  }

  const details = (root) => cache.get(root)?.data ?? null;
  const applyDetails = (root, d) => { const P = cache.get(root); if (P && d) { P.gen++; apply(P, d); } };
  window.GitView = { details, applyDetails, init, setRepos, setVisible, prune, focusRepo, liveInfo, draftOf };
})();
