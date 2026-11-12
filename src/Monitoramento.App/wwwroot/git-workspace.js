'use strict';
/* Espaço de trabalho git: uma aba por projeto (pasta com repositórios), cabeçalho do projeto com processos
   salvos por repositório, página "Projetos" e modo fixado à direita. A fonte da verdade é config.gitWorkspace
   (gravado pelo host via setGitWorkspace); a UI só desenha. */
(() => {
  const MAX_TABS = 12, MAX_SHOWN = 4, DEFAULT_PCT = 42, MIN_PCT = 25, MAX_PCT = 70, DEBOUNCE = 400, NARROW_PX = 760, DOCK_MIN_PX = 440, LIST_MIN_PX = 320, SAFE_CMD = /^[\w:.@\/-]+$/, BS = String.fromCharCode(92);
  const $ = (s, r = document) => r.querySelector(s);
  const clampPct = (v) => Math.max(MIN_PCT, Math.min(MAX_PCT, Math.round(Number(v) * 10) / 10 || DEFAULT_PCT));

  let env = null, u = null, ic = null;
  const el = {};
  let ws = { docked: false, dockWidthPct: DEFAULT_PCT, tabs: [], active: null };
  let projects = [];
  const shownByTab = new Map();   // pasta normalizada → raízes exibidas (só quando o projeto tem mais de 4 repositórios)
  const draftInfo = new Map();    // raiz → GitInfo vindo do rascunho (repositório ainda sem processo)
  let timer = null, dirty = false, flushing = false, retry = 0, overlayOpen = false, tabGit = false, shownNow = null, menuOpen = false;
  let pageMode = false;           // a página "Projetos" é só estado da UI: nunca vai para o host
  let dlgCtx = null, addBusy = false;
  const bulk = new Set();         // pastas com "Iniciar tudo"/"Parar tudo" em andamento
  const sigs = { bar: '', foot: '', l1: '', repos: '', page: '' };
  const narrowMq = window.matchMedia('(max-width: ' + (NARROW_PX - 1) + 'px)');

  // ---------- caminhos ----------
  const norm = (p) => String(p || '').split('/').join(BS).replace(/\\+$/, '').toLowerCase();
  const within = (path, folder) => { if (!path || !folder) return false; const a = norm(path), b = norm(folder); return a === b || a.startsWith(b + BS); };
  const trimEnd = (p) => String(p || '').split('/').join(BS).replace(/\\+$/, '');
  const baseOf = (p) => { const s = trimEnd(p); return s.slice(s.lastIndexOf(BS) + 1) || s; };
  const parentOf = (p) => { const s = trimEnd(p), i = s.lastIndexOf(BS); return i > 0 ? s.slice(0, i) : ''; };

  // ---------- dados ----------
  const tabOf = (folder) => ws.tabs.find((t) => norm(t.folder) === norm(folder)) || null;
  const activeTab = () => (pageMode || !ws.tabs.length ? null : tabOf(ws.active) || ws.tabs[0]);
  const hasRepo = (list, root) => list.some((r) => norm(r) === norm(root));
  const repoKeys = (list) => new Set(list.map((r) => norm(r)));
  const uniqRepos = (list) => { const seen = new Set(); return list.filter((r) => { const k = norm(r.repoRoot); if (seen.has(k)) return false; seen.add(k); return true; }); };
  const savedOf = (t) => (t ? projects.find((p) => p.id === t.projectId) || null : null);
  const displayName = (t) => (t.worktreeOf ? t.name : savedOf(t)?.name || t.name || baseOf(t.folder));
  function projectForRepo(root) {
    return projects.filter((p) => hasRepo(p.repos, root)).sort((a, b) => b.folder.length - a.folder.length)[0] || null;
  }
  const knownMap = () => new Map(env.knownRepos().map((r) => [norm(r.repoRoot), r]));
  const repoInfo = (root, km) => window.GitView.liveInfo(root) || km.get(norm(root)) || draftInfo.get(root) || null;
  const repoName = (root, km) => repoInfo(root, km)?.repoName || baseOf(root);
  const tabDirty = (t, km) => t.repos.reduce((n, r) => n + (repoInfo(r, km)?.dirtyCount || 0), 0);
  const allRoots = () => new Set(ws.tabs.flatMap((t) => t.repos));

  function visibleRoots(t) {
    if (t.repos.length <= MAX_SHOWN) return t.repos.slice();
    const key = norm(t.folder);
    let sel = (shownByTab.get(key) || []).filter((r) => t.repos.includes(r));
    if (!sel.length) {
      sel = t.repos.slice(0, MAX_SHOWN);
      if (t.activeRepo && t.repos.includes(t.activeRepo) && !sel.includes(t.activeRepo)) sel = [t.activeRepo, ...sel.slice(0, MAX_SHOWN - 1)];
    }
    shownByTab.set(key, sel);
    return sel;
  }

  // processos salvos e em execução dentro de uma pasta
  function savedIn(folder) {
    const snap = env.snapshot();
    return snap ? snap.saved.filter((s) => within(s.workingDirectory, folder)) : [];
  }
  function runningIn(folder) {
    const snap = env.snapshot();
    let count = 0, mem = 0, cpu = 0;
    if (snap) for (const g of snap.groups) for (const p of g.processes) if (within(p.workingDirectory, folder)) { count++; mem += p.memoryBytes; cpu += p.cpuPercent; }
    return { count, mem, cpu };
  }
  // Agrupa os salvos do projeto por repositório (o mais específico vence); o resto vai para "extras".
  function groupSaved(repos, folder) {
    const byRepo = new Map(repos.map((r) => [r, []])), extras = [];
    for (const s of savedIn(folder)) {
      const owner = repos.filter((r) => within(s.workingDirectory, r)).sort((a, b) => b.length - a.length)[0];
      if (owner) byRepo.get(owner).push(s); else extras.push(s);
    }
    return { byRepo, extras };
  }

  // ---------- persistência (com debounce) ----------
  const serialize = () => ({
    docked: ws.docked, dockWidthPct: ws.dockWidthPct,
    tabs: ws.tabs.map((t) => ({ folder: t.folder, name: t.name, repos: t.repos.slice(), projectId: t.projectId, activeRepo: t.activeRepo, worktreeOf: t.worktreeOf || null })),
    active: ws.active,
  });
  function sane(w) {
    const tabs = Array.isArray(w?.tabs) ? w.tabs.filter((t) => t && typeof t.folder === 'string' && t.folder).map((t) => ({
      folder: t.folder, name: String(t.name || baseOf(t.folder)), repos: Array.isArray(t.repos) ? t.repos.filter((r) => typeof r === 'string') : [],
      projectId: t.projectId || null, activeRepo: t.activeRepo || null, worktreeOf: t.worktreeOf || null,
    })) : [];
    const active = typeof w?.active === 'string' && tabs.some((t) => norm(t.folder) === norm(w.active)) ? w.active : (tabs[0]?.folder ?? null);
    return { docked: !!w?.docked, dockWidthPct: clampPct(w?.dockWidthPct ?? DEFAULT_PCT), tabs, active };
  }
  // Um envio em voo por vez; mudança durante o voo reenvia depois; erro mantém "sujo" e tenta de novo com espera crescente.
  function persist() { dirty = true; clearTimeout(timer); timer = setTimeout(flush, DEBOUNCE); }
  async function flush() {
    if (flushing || !dirty) return;
    flushing = true; dirty = false;
    const sent = serialize();
    let d = null;
    try { d = await env.request('setGitWorkspace', { workspace: sent }); }
    catch (e) {
      dirty = true; retry++;
      if (retry === 1) env.toast('Não foi possível guardar as abas do git. Vou tentar de novo. ' + (e.message || ''), 'error');
    }
    flushing = false;
    if (dirty) { clearTimeout(timer); timer = setTimeout(flush, retry ? Math.min(8000, DEBOUNCE * 2 ** retry) : DEBOUNCE); return; }
    retry = 0;
    if (!d?.workspace) return;
    const next = sane(d.workspace);
    const gone = sent.tabs.filter((t) => !next.tabs.some((x) => norm(x.folder) === norm(t.folder)));
    if (gone.length) env.toast(gone.length === 1 ? `A aba "${gone[0].name}" foi removida: o repositório não está disponível.` : `${gone.length} abas foram removidas: os repositórios não estão disponíveis.`, 'error');
    if (JSON.stringify(next) !== JSON.stringify(serialize())) { ws = next; applyDock(); renderAll(); }
  }
  function changed() { renderAll(); persist(); }

  // ---------- abertura de projetos ----------
  function addTab(tab) {
    if (ws.tabs.length >= MAX_TABS) { env.toast(`Limite de ${MAX_TABS} abas atingido. Feche uma aba para abrir outro projeto.`, 'error'); return false; }
    pageMode = false; ws.tabs.push(tab); ws.active = tab.folder;
    return true;
  }
  function openProject(proj, activeRepo) {
    let t = tabOf(proj.folder);
    pageMode = false;
    if (!t) {
      if (!addTab({ folder: proj.folder, name: proj.name, repos: proj.repos.slice(), projectId: proj.id, activeRepo: activeRepo || proj.repos[0] || null })) return false;
    } else {
      t.projectId = proj.id; t.name = proj.name; t.repos = proj.repos.slice();
      if (activeRepo) t.activeRepo = activeRepo;
      ws.active = t.folder;
    }
    changed(); reveal(); focusActive(activeRepo);
    return true;
  }
  function openDraft(d, root) {
    const p = d?.project;
    if (!p?.folder) { env.toast('Não foi possível identificar o projeto desta pasta.', 'error'); return false; }
    (d.repos || []).forEach((r) => draftInfo.set(r.repoRoot, r));
    const known = p.id ? projects.find((x) => x.id === p.id) : null;
    if (known) return openProject(known, root);
    const t = tabOf(p.folder);
    if (t) { pageMode = false; ws.active = t.folder; if (root) t.activeRepo = root; changed(); reveal(); focusActive(root); return true; }
    const repos = p.repos?.length ? p.repos.slice() : (d.repos || []).map((r) => r.repoRoot);
    if (!addTab({ folder: p.folder, name: p.name || baseOf(p.folder), repos, projectId: null, activeRepo: root || repos[0] || null })) return false;
    changed(); reveal(); focusActive(root);
    return true;
  }
  async function draftFor(path) {
    try { return await env.request('gitProjectDraft', { path }); }
    catch (e) { env.toast(e.message || 'Não foi possível abrir esta pasta como projeto.', 'error'); return null; }
  }
  async function openRepo(root) {
    if (!root) return false;
    const wtInfo = repoInfo(root, knownMap());
    if (wtInfo?.mainRepoRoot) return openWorktreeTab({ path: root, branch: wtInfo.branch, mainRepoRoot: wtInfo.mainRepoRoot });
    const proj = projectForRepo(root);
    if (proj) return openProject(proj, root);
    const t = ws.tabs.find((x) => hasRepo(x.repos, root));
    if (t) { pageMode = false; ws.active = t.folder; t.activeRepo = root; changed(); reveal(); focusActive(root); return true; }
    const d = await draftFor(root);
    return d ? openDraft(d, root) : false;
  }
  async function openFolder(path) {
    const d = await draftFor(path);
    return d ? openDraft(d, null) : false;
  }
  function focusActive(root) {
    requestAnimationFrame(() => {
      const t = activeTab(); const r = root || t?.activeRepo;
      if (r) window.GitView.focusRepo(r); else el.panel.focus({ preventScroll: true });
    });
  }
  function activate(folder) {
    const t = tabOf(folder); if (!t) return;
    if (!pageMode && activeTab() === t) return;
    pageMode = false; ws.active = t.folder; changed();
  }
  async function closeTab(folder) {
    const i = ws.tabs.findIndex((t) => norm(t.folder) === norm(folder)); if (i < 0) return;
    const drafts = window.GitView.draftOf(ws.tabs[i].repos);
    if (drafts.length) {
      const r = await env.confirm({ title: 'Fechar a aba?', body: `Há mensagem de commit ainda não enviada em ${drafts.join(', ')}. Se fechar a aba, o texto será perdido.`, ok: 'Fechar aba' });
      if (!r) return;
    }
    const j = ws.tabs.findIndex((t) => norm(t.folder) === norm(folder)); if (j < 0) return;
    const wasActive = activeTab() === ws.tabs[j];
    const hadFocus = el.tabs.contains(document.activeElement) || document.activeElement === el.close;
    ws.tabs.splice(j, 1); shownByTab.delete(norm(folder));
    if (wasActive) ws.active = ws.tabs[Math.min(j, ws.tabs.length - 1)]?.folder ?? null;
    changed();
    if (hadFocus) (el.tabs.querySelector('[role=tab][aria-selected=true]') || el.tabs.querySelector('[role=tab]') || el.add).focus();
  }

  // ---------- fixar à direita ----------
  function applyDock() {
    const narrow = narrowMq.matches, wide = ws.docked && !narrow, overlay = ws.docked && narrow;
    const b = document.body;
    b.classList.toggle('dock', ws.docked); b.classList.toggle('dock-narrow', overlay);
    b.style.setProperty('--dock', ws.dockWidthPct + '%');
    const show = ws.docked ? (!narrow || overlayOpen) : tabGit;
    el.wrap.hidden = !show;
    el.split.hidden = !wide; el.split.setAttribute('aria-valuenow', String(Math.round(ws.dockWidthPct)));
    el.split.setAttribute('aria-valuetext', `Painel git com ${Math.round(ws.dockWidthPct)}% da largura`);
    el.handle.hidden = !(overlay && !overlayOpen); el.handle.setAttribute('aria-expanded', String(overlayOpen));
    el.collapse.hidden = !overlay;
    const lbl = ws.docked ? 'Soltar' : 'Fixar à direita';
    const tip = (ws.docked ? 'Soltar o painel git (volta a ser a aba Git)' : 'Fixar o painel git à direita, ao lado da lista') + ' (Alt+G)';
    const sig = lbl + tip;
    if (el.dock._sig !== sig) { el.dock._sig = sig; el.dock.innerHTML = ic('dock') + `<span>${lbl}</span>`; el.dock.title = tip; el.dock.setAttribute('aria-label', tip); }
    el.dock.setAttribute('aria-pressed', String(ws.docked));
    if (shownNow !== show) { shownNow = show; window.GitView.setVisible(show); if (show) refresh(); }
  }
  function toggleDocked() {
    ws.docked = !ws.docked;
    overlayOpen = ws.docked && narrowMq.matches;
    env.onDockChange(ws.docked);
    sigs.bar = '';   // força rolar até a aba ativa na nova largura
    applyDock(); renderAll(); persist();
    if (ws.docked) el.dock.focus();
  }
  function setOverlay(v, focusBack) {
    if (overlayOpen === v) return;
    overlayOpen = v; applyDock();
    if (v) focusActive(); else if (focusBack) el.handle.focus();
  }
  function reveal() {
    if (ws.docked) { if (narrowMq.matches) setOverlay(true); else applyDock(); } else env.selectGitTab();
  }
  function bindSplit() {
    const sp = el.split, root = el.workarea;
    sp.addEventListener('pointerdown', (e) => {
      e.preventDefault(); sp.setPointerCapture(e.pointerId); sp.classList.add('drag');
      const box = root.getBoundingClientRect();
      const lo = Math.max(MIN_PCT, (DOCK_MIN_PX / box.width) * 100), hi = Math.min(MAX_PCT, ((box.width - LIST_MIN_PX) / box.width) * 100);
      const mv = (ev) => { ws.dockWidthPct = Math.max(lo, Math.min(hi, clampPct(((box.right - ev.clientX) / box.width) * 100))); applyDock(); };
      const up = () => { sp.classList.remove('drag'); for (const t of ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture']) sp.removeEventListener(t, t === 'pointermove' ? mv : up); persist(); };
      sp.addEventListener('pointermove', mv); sp.addEventListener('pointerup', up); sp.addEventListener('pointercancel', up); sp.addEventListener('lostpointercapture', up);
    });
    sp.addEventListener('keydown', (e) => {
      const step = e.shiftKey ? 8 : 3;
      if (e.key === 'ArrowLeft') ws.dockWidthPct = clampPct(ws.dockWidthPct + step);
      else if (e.key === 'ArrowRight') ws.dockWidthPct = clampPct(ws.dockWidthPct - step);
      else if (e.key === 'Home') ws.dockWidthPct = MAX_PCT;
      else if (e.key === 'End') ws.dockWidthPct = MIN_PCT;
      else return;
      e.preventDefault(); applyDock(); persist();
    });
  }

  // ---------- barra de abas ----------
  function renderBar() {
    const km = knownMap();
    const items = ws.tabs.map((t) => ({ folder: t.folder, name: displayName(t), dirty: tabDirty(t, km), saved: !!savedOf(t), wt: !!t.worktreeOf }));
    const cur = activeTab();
    const activeKey = cur ? norm(cur.folder) : null;
    const sig = JSON.stringify([items, activeKey]);
    if (sig === sigs.bar) return; sigs.bar = sig;
    const a = document.activeElement;
    const keep = el.tabs.contains(a) ? a.dataset.folder : null;
    el.tabs.innerHTML = items.map((t, i) => {
      const on = norm(t.folder) === activeKey, tab0 = on || (!activeKey && i === 0);
      const dirtyTxt = t.dirty ? u.plural(t.dirty, 'mudança', 'mudanças') : '';
      return `<button type="button" role="tab" id="wst-${i}" class="ws-tab${on ? ' on' : ''}${t.wt ? ' wt' : ''}" data-wa="tab" data-folder="${u.esc(t.folder)}" aria-selected="${on}" aria-controls="wsPanel" tabindex="${tab0 ? 0 : -1}" title="${u.esc(t.name + ' — ' + t.folder)}">${t.wt ? ic('branch') : t.saved ? ic('star') : ''}<span class="ws-tn">${u.esc(t.name)}</span>${t.dirty ? `<span class="ws-dirty" title="${u.esc(dirtyTxt)}"><span aria-hidden="true">●${u.nf0.format(t.dirty)}</span><span class="sr"> ${u.esc(dirtyTxt)}</span></span>` : ''}<span class="ws-x" data-wa="close" data-folder="${u.esc(t.folder)}" aria-hidden="true" title="Fechar aba">${ic('x')}</span></button>`;
    }).join('');
    if (keep) el.tabs.querySelector(`[data-folder="${CSS.escape(keep)}"]`)?.focus();
    el.close.hidden = !cur;
    const sel = el.tabs.querySelector('[aria-selected=true]');
    sel?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    if (sel) el.panel.setAttribute('aria-labelledby', sel.id); else el.panel.removeAttribute('aria-labelledby');
  }

  // ---------- trechos de HTML compartilhados (aba e página) ----------
  const stateTxt = (s) => (s.running ? `Rodando · ${u.fmtBytes(s.memoryBytes)}` : s.lastError ? 'Falhou ao iniciar' : 'Parado');
  function svHtml(s) {
    const busy = env.isBusy(s.id);
    const lbl = busy ? (s.running ? 'Parando…' : 'Iniciando…') : (s.running ? 'Parar' : 'Iniciar');
    const tip = `${s.command}${s.lastError ? '\n' + s.lastError : ''}`;
    return `<span class="pj-sv${s.running ? ' on' : ''}" title="${u.esc(tip)}"><span class="dot${s.running ? ' on' : ''}" aria-hidden="true"></span><span class="pj-svn">${u.esc(s.name)}</span><span class="pj-svst${s.lastError && !s.running ? ' bad' : ''}">${u.esc(stateTxt(s))}</span><button type="button" class="ibtn pj-run${s.running ? ' kill' : ''}" data-wa="run" data-id="${u.esc(s.id)}" data-fk="run-${u.esc(s.id)}" aria-label="${lbl} ${u.esc(s.name)}" title="${lbl} ${u.esc(s.name)}"${busy ? ' aria-disabled="true"' : ''}>${ic(s.running ? 'stop' : 'play')}</button></span>`;
  }
  const addCmdBtn = (root, pname, folder, compact, label) => `<button type="button" class="btn small ghost pj-add${compact ? ' compact' : ''}" data-wa="add-cmd" data-fk="add-${u.esc(root)}" data-root="${u.esc(root)}" data-pname="${u.esc(pname)}" data-folder="${u.esc(folder)}" aria-label="${u.esc(label)}" title="${u.esc(label)}">${ic('plus')}<span>${compact ? 'Comando' : 'Adicionar comando'}</span></button>`;
  function repoChipHtml(root, km) {
    const g = repoInfo(root, km), name = g?.repoName || baseOf(root);
    let s = '';
    if (g) {
      if (g.dirtyCount > 0) s += `<span class="gs dirty" title="${u.plural(g.dirtyCount, 'arquivo alterado', 'arquivos alterados')}">●${u.nf0.format(g.dirtyCount)}</span>`;
      if (g.ahead > 0) s += `<span class="gs ahead" title="${u.plural(g.ahead, 'commit à frente', 'commits à frente')}">↑${g.ahead}</span>`;
      if (g.behind > 0) s += `<span class="gs behind" title="${u.plural(g.behind, 'commit atrás', 'commits atrás')}">↓${g.behind}</span>`;
      if (g.statusNote) s += `<span class="gs lock" title="${u.esc(g.statusNote)}" role="img" aria-label="${u.esc(g.statusNote)}">${ic('lock')}</span>`;
    }
    return `${ic('branch')}<span class="rn">${u.esc(name)}</span><span class="br">${u.esc(g?.branch ?? '—')}</span>${s}`;
  }
  const repoSummaryTxt = (root, km) => {
    const g = repoInfo(root, km);
    return `${repoName(root, km)}, branch ${g?.branch ?? 'desconhecida'}${g?.dirtyCount ? ', ' + u.plural(g.dirtyCount, 'mudança', 'mudanças') : ''}`;
  };
  // linha de um repositório: chip + processos salvos dentro dele (ou "Adicionar comando")
  function repoRowHtml(root, list, ctx) {
    const { km, pname, folder, tabMode, shown, many } = ctx;
    const chip = tabMode
      ? `<button type="button" class="pj-repo${shown ? ' on' : ''}" data-wa="repo" data-root="${u.esc(root)}" data-fk="repo-${u.esc(root)}"${many ? ` aria-pressed="${shown}"` : ''} aria-label="${u.esc(repoSummaryTxt(root, km))}${many ? (shown ? ', exibido' : ', oculto') : ''}" title="${u.esc(root + (many ? (shown ? '\nClique para ocultar o painel' : '\nClique para exibir o painel') : '\nClique para ir ao painel'))}">${repoChipHtml(root, km)}</button>`
      : `<span class="pj-repo static" title="${u.esc(root)}">${repoChipHtml(root, km)}</span>`;
    const svs = list.map(svHtml).join('') + addCmdBtn(root, pname, folder, tabMode || list.length > 0, `Adicionar comando em ${baseOf(root)}`);
    return `<div class="pj-rrow">${chip}<div class="pj-svs">${svs}</div></div>`;
  }
  function allSavedActions(folder, saved) {
    if (!saved.length) return '';
    const stopped = saved.filter((s) => !s.running).length, running = saved.length - stopped;
    const f = u.esc(folder), lock = bulk.has(norm(folder));
    return `<span class="pj-all"><button type="button" class="btn small primary" data-wa="run-all" data-folder="${f}" data-fk="all-run-${f}"${stopped && !lock ? '' : ' aria-disabled="true"'} title="Inicia os ${u.plural(stopped, 'processo salvo parado', 'processos salvos parados')} deste projeto">${ic('play')}Iniciar tudo</button><button type="button" class="btn small danger" data-wa="stop-all" data-folder="${f}" data-fk="all-stop-${f}"${running && !lock ? '' : ' aria-disabled="true"'} title="Para os ${u.plural(running, 'processo salvo em execução', 'processos salvos em execução')} deste projeto">${ic('stop')}Parar tudo</button></span>`;
  }
  function runSummary(folder) {
    const r = runningIn(folder);
    return r.count
      ? `<span class="pj-runsum"><i class="dot on" aria-hidden="true"></i>${u.plural(r.count, 'processo rodando', 'processos rodando')} · ${u.fmtBytes(r.mem)} · CPU ${u.fmtPct(r.cpu)}</span>`
      : '<span class="pj-runsum none">Nenhum processo rodando nesta pasta.</span>';
  }

  // ---------- cabeçalho do projeto ----------
  function keepFocus(box, fn) {
    const a = document.activeElement, key = a && box.contains(a) ? a.dataset.fk : null;
    fn();
    if (key) box.querySelector(`[data-fk="${CSS.escape(key)}"]`)?.focus();
  }
  function renderProject(t) {
    if (t.worktreeOf) { renderWorktreeHead(t); return; }
    const km = knownMap(), proj = savedOf(t), name = displayName(t);
    // linha 1: identidade
    const l1 = `<h2 class="pj-name" title="${u.esc(name)}">${u.esc(name)}</h2>${proj ? `<span class="pj-badge saved" title="Projeto salvo">${ic('star')}Salvo</span>` : '<span class="pj-badge">Não salvo</span>'}<span class="pj-path" title="${u.esc(t.folder)}">${u.esc(u.pathText(u.shortPath(t.folder, 70)))}</span><span class="gp-sp"></span><span class="pj-acts">${proj
      ? `<button type="button" class="ibtn" data-wa="rename" data-fk="rename" aria-label="Renomear projeto" title="Renomear projeto">${ic('edit')}</button><button type="button" class="ibtn kill" data-wa="unsave" data-fk="unsave" aria-label="Remover dos salvos" title="Remover dos salvos">${ic('trash')}</button>`
      : `<button type="button" class="btn small primary" data-wa="save" data-fk="save">${ic('star')}Salvar projeto</button>`}<button type="button" class="ibtn" data-wa="folder" data-fk="folder" aria-label="Abrir pasta do projeto no Explorer" title="Abrir pasta no Explorer">${ic('folder')}</button><button type="button" class="ibtn" data-wa="terminal" data-fk="term" aria-label="Abrir terminal na pasta do projeto" title="Abrir terminal na pasta">${ic('terminal')}</button></span>`;
    if (sigs.l1 !== l1) { sigs.l1 = l1; keepFocus(el.l1, () => { el.l1.innerHTML = l1; }); }
    // linhas por repositório
    const groups = groupSaved(t.repos, t.folder), many = t.repos.length > MAX_SHOWN, sel = visibleRoots(t);
    const note = many ? `<p class="pj-hint">Este projeto tem ${t.repos.length} repositórios. Escolha até ${MAX_SHOWN} para exibir os painéis (${sel.length} exibidos).</p>` : '';
    const rows = t.repos.map((r) => repoRowHtml(r, groups.byRepo.get(r) || [], { km, pname: name, folder: t.folder, tabMode: true, shown: sel.includes(r), many }) + wtSection(r)).join('');
    const extras = groups.extras.length ? `<div class="pj-rrow extra"><span class="pj-repo static">${ic('folder')}<span class="rn">Pasta do projeto</span></span><div class="pj-svs">${groups.extras.map(svHtml).join('')}</div></div>` : '';
    const body = note + rows + extras || '<p class="pj-hint">Nenhum repositório git encontrado nesta pasta.</p>';
    if (sigs.repos !== body) { sigs.repos = body; keepFocus(el.repos, () => { el.repos.innerHTML = body; }); }
    // rodapé: execução
    const all = savedIn(t.folder);
    const foot = `${runSummary(t.folder)}<button type="button" class="btn small ghost" data-wa="list" data-folder="${u.esc(t.folder)}" data-fk="list" title="Filtra a lista de processos por esta pasta">Ver na lista</button><span class="gp-sp"></span>${allSavedActions(t.folder, all)}`;
    if (sigs.foot !== foot) { sigs.foot = foot; keepFocus(el.foot, () => { el.foot.innerHTML = foot; }); }
  }

  // ---------- página "Projetos" ----------
  function cardHtml(p, km) {
    const open = !!tabOf(p.folder);
    const groups = groupSaved(p.repos, p.folder);
    const ctx = { km, pname: p.name, folder: p.folder, tabMode: false };
    const rows = p.repos.map((r) => repoRowHtml(r, groups.byRepo.get(r) || [], ctx)).join('');
    const extras = groups.extras.length ? `<div class="pj-rrow extra"><span class="pj-repo static">${ic('folder')}<span class="rn">Pasta do projeto</span></span><div class="pj-svs">${groups.extras.map(svHtml).join('')}</div></div>` : '';
    const all = savedIn(p.folder);
    return `<article class="pg-card${open ? ' open' : ''}" data-id="${u.esc(p.id)}"><header class="pg-ch"><h4><button type="button" class="pg-title" data-wa="open-project" data-id="${u.esc(p.id)}" data-fk="open-${u.esc(p.id)}" title="Abrir em uma aba">${u.esc(p.name)}</button></h4>${open ? '<span class="pj-badge on">Aberto</span>' : ''}<span class="gp-sp"></span><button type="button" class="ibtn" data-wa="rename-id" data-id="${u.esc(p.id)}" data-fk="ren-${u.esc(p.id)}" aria-label="Renomear ${u.esc(p.name)}" title="Renomear">${ic('edit')}</button><button type="button" class="ibtn kill" data-wa="unsave-id" data-id="${u.esc(p.id)}" data-fk="del-${u.esc(p.id)}" aria-label="Remover ${u.esc(p.name)} dos salvos" title="Remover dos salvos">${ic('trash')}</button></header><div class="pg-path" title="${u.esc(p.folder)}">${u.esc(u.pathText(u.shortPath(p.folder, 60)))}</div><div class="pg-repos">${rows || extras ? rows + extras : '<p class="pj-hint">Nenhum repositório git na pasta.</p>'}</div><footer class="pg-foot">${runSummary(p.folder)}<span class="gp-sp"></span>${allSavedActions(p.folder, all)}</footer></article>`;
  }
  function renderPage() {
    const km = knownMap();
    const byParent = new Map();
    for (const p of projects.slice().sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'))) {
      const parent = parentOf(p.folder) || p.folder;
      if (!byParent.has(norm(parent))) byParent.set(norm(parent), { parent, list: [] });
      byParent.get(norm(parent)).list.push(p);
    }
    const groups = [...byParent.values()].sort((a, b) => a.parent.localeCompare(b.parent, 'pt-BR'));
    const covered = repoKeys(projects.flatMap((p) => p.repos));
    const loose = uniqRepos(env.knownRepos()).filter((r) => !covered.has(norm(r.repoRoot)));
    const html = (groups.length
      ? groups.map((g) => `<section class="pg-group" aria-label="Pasta ${u.esc(g.parent)}"><h3 class="pg-gh" title="${u.esc(g.parent)}">${ic('folder')}<span class="pg-gp">${u.esc(u.pathText(u.shortPath(g.parent, 56)))}</span><span class="count">${g.list.length}</span></h3><div class="pg-cards">${g.list.map((p) => cardHtml(p, km)).join('')}</div></section>`).join('')
      : '<div class="empty"><h3>Nenhum projeto salvo</h3><p>Abra um repositório pelo chip git de um processo (ou pelo botão “Abrir projeto” acima) e use “Salvar projeto” para guardá-lo aqui, agrupado pela pasta onde ele fica.</p></div>')
      + (loose.length ? `<section class="pg-group" aria-label="Repositórios detectados"><h3 class="pg-gh">${ic('branch')}<span class="pg-gp">Detectados nos processos, ainda sem projeto</span><span class="count">${loose.length}</span></h3><div class="pg-loose">${loose.map((r) => `<button type="button" class="btn small" data-wa="open-repo" data-root="${u.esc(r.repoRoot)}" data-fk="loose-${u.esc(r.repoRoot)}" title="${u.esc(r.repoRoot)}">${ic('branch')}${u.esc(r.repoName)}</button>`).join('')}</div></section>` : '');
    if (sigs.page === html) return;
    sigs.page = html; keepFocus(el.pgBody, () => { el.pgBody.innerHTML = html; });
  }
  function buildPageHead() {
    el.page.innerHTML = `<div class="pg-head"><div><h2>Projetos</h2><p class="hint">Projetos salvos, agrupados pela pasta onde ficam. Clique num projeto para abri-lo em uma aba.</p></div><form class="pg-add" id="pgAdd" novalidate><label class="sr" for="pgPath">Pasta do projeto</label><input id="pgPath" type="text" autocomplete="off" spellcheck="false" placeholder="Abrir uma pasta (ex.: C:\\projetos\\meu-app)"><button type="submit" class="btn small">Abrir pasta</button></form></div><div id="pgBody"></div>`;
    el.pgBody = $('#pgBody');
    $('#pgAdd').addEventListener('submit', async (e) => {
      e.preventDefault();
      const input = $('#pgPath'), v = input.value.trim();
      if (!v) { env.toast('Informe a pasta do projeto.', 'error'); input.focus(); return; }
      if (await openFolder(v)) input.value = '';
    });
  }

  // ---------- menu "Abrir projeto" ----------
  function menuHtml() {
    const open = new Set(ws.tabs.map((t) => norm(t.folder)));
    const saved = projects.filter((p) => !open.has(norm(p.folder))).sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
    const covered = repoKeys([...projects.flatMap((p) => p.repos), ...allRoots()]);
    const loose = uniqRepos(env.knownRepos()).filter((r) => !covered.has(norm(r.repoRoot)));
    return `<button type="button" role="menuitem" data-wa="page">${ic('grid')}Todos os projetos</button>`
      + (saved.length ? '<div class="pk-g" role="presentation">Projetos salvos</div>' + saved.map((p) => `<button type="button" role="menuitem" data-wa="open-project" data-id="${u.esc(p.id)}" title="${u.esc(p.folder)}">${ic('star')}<span class="mi-t">${u.esc(p.name)}</span><span class="mi-s">${u.plural(p.repos.length, 'repo', 'repos')}</span></button>`).join('') : '')
      + (loose.length ? '<div class="pk-g" role="presentation">Repositórios detectados</div>' + loose.slice(0, 12).map((r) => `<button type="button" role="menuitem" data-wa="open-repo" data-root="${u.esc(r.repoRoot)}" title="${u.esc(r.repoRoot)}">${ic('branch')}<span class="mi-t">${u.esc(r.repoName)}</span><span class="mi-s">${u.esc(r.branch ?? '—')}</span></button>`).join('') : '');
  }
  function setMenu(v, back) {
    if (menuOpen === v) return;
    menuOpen = v; el.menu.hidden = !v; el.add.setAttribute('aria-expanded', String(v));
    if (v) {
      el.menu.innerHTML = menuHtml();
      const wr = el.wrap.getBoundingClientRect(), br = el.add.getBoundingClientRect();
      el.menu.style.top = (br.bottom - wr.top + 2) + 'px';
      el.menu.style.left = Math.max(4, Math.min(br.left - wr.left, wr.width - el.menu.offsetWidth - 8)) + 'px';
      el.menu.querySelector('[role=menuitem]')?.focus();
    } else { el.menu.innerHTML = ''; if (back) el.add.focus(); }
  }

  // ---------- diálogo salvar/renomear ----------
  // ctx = { proj (salvo ou null), folder, name, repos } — funciona sem aba aberta (renomear pela página)
  function openProjectDialog(ctx) {
    const dlg = $('#projDlg'), proj = ctx.proj;
    dlgCtx = { projectId: proj?.id || '', folder: proj?.folder || ctx.folder };
    $('#pjTitle').textContent = proj ? 'Renomear projeto' : 'Salvar projeto';
    $('#projSave').textContent = proj ? 'Salvar nome' : 'Salvar projeto';
    $('#pj-name').value = proj?.name || ctx.name || baseOf(ctx.folder);
    $('#pj-folder').textContent = dlgCtx.folder;
    const km = knownMap(), repos = proj?.repos || ctx.repos || [];
    $('#pj-repos').textContent = repos.length ? repos.map((r) => repoName(r, km)).join(', ') : 'Nenhum repositório git na pasta';
    $('#e-pjname').textContent = ''; $('#pj-name').closest('.field').classList.remove('bad');
    dlg.showModal(); $('#pj-name').focus(); $('#pj-name').select();
  }
  async function submitProject(ev) {
    ev.preventDefault();
    if (!dlgCtx) { $('#projDlg').close(); return; }
    const name = $('#pj-name').value.trim();
    $('#e-pjname').textContent = name ? '' : 'Informe um nome para o projeto.';
    $('#pj-name').closest('.field').classList.toggle('bad', !name);
    if (!name) { $('#pj-name').focus(); return; }
    const { projectId, folder } = dlgCtx, btn = $('#projSave'); btn.disabled = true;
    let d = null;
    try { d = await env.request('saveGitProject', { project: { id: projectId, name, folder } }); }
    catch (e) { env.toast(e.message || 'Não foi possível salvar o projeto.', 'error'); }
    btn.disabled = false;
    if (!d?.project) return;
    $('#projDlg').close();
    upsertProject(d.project);
    const cur = tabOf(d.project.folder);
    if (cur) { cur.projectId = d.project.id; cur.name = d.project.name; if (d.project.repos?.length) cur.repos = d.project.repos.slice(); }
    env.toast(projectId ? 'Projeto renomeado' : 'Projeto salvo');
    changed();
  }
  function upsertProject(p) {
    const i = projects.findIndex((x) => x.id === p.id);
    const full = { id: p.id, name: p.name, folder: p.folder, repos: Array.isArray(p.repos) ? p.repos.slice() : [] };
    if (i >= 0) projects[i] = full; else projects.push(full);
  }
  async function unsave(proj) {
    const r = await env.confirm({ title: 'Remover dos salvos?', body: `“${proj.name}” deixa de ser um projeto salvo. A pasta e os repositórios não são alterados e a aba, se aberta, continua aberta.`, ok: 'Remover' });
    if (!r) return;
    try { await env.request('deleteGitProject', { id: proj.id }); } catch (e) { env.toast(e.message || 'Não foi possível remover o projeto.', 'error'); return; }
    projects = projects.filter((p) => p.id !== proj.id);
    ws.tabs.forEach((x) => { if (x.projectId === proj.id) x.projectId = null; });
    env.toast('Projeto removido dos salvos');
    changed();
  }

  // ---------- iniciar e parar processos salvos do projeto ----------
  // Cada item é reavaliado na hora (setRun ignora quem já está no estado pedido); a trava desabilita os dois botões.
  async function bulkRun(folder, want) {
    const key = norm(folder);
    if (bulk.has(key)) return;
    let ids = savedIn(folder).filter((x) => x.running !== want).map((x) => x.id);
    if (!want) {
      if (!ids.length) return;
      const names = savedIn(folder).filter((x) => ids.includes(x.id)).map((x) => x.name);
      const r = await env.confirm({ title: 'Parar tudo?', body: `${u.plural(names.length, 'processo salvo será parado', 'processos salvos serão parados')}: ${names.join(', ')}. Alterações não salvas nesses processos serão perdidas.`, ok: 'Parar tudo' });
      if (!r || bulk.has(key)) return;
      ids = savedIn(folder).filter((x) => x.running).map((x) => x.id);   // refiltra: o estado pode ter mudado durante a confirmação
    }
    bulk.add(key); refresh();
    try { for (const id of ids) await env.setRun(id, want); }
    finally { bulk.delete(key); refresh(); }
  }
  const runAll = (folder) => bulkRun(folder, true);
  const stopAll = (folder) => bulkRun(folder, false);
  const PM_FIRST = ['start', 'start:dev', 'dev', 'serve', 'watch'];
  function suggestions(entry) {
    const scripts = Array.isArray(entry?.scripts) ? entry.scripts.filter((x) => typeof x === 'string' && SAFE_CMD.test(x)) : [];
    if (!scripts.length) return [];
    const pm = entry.packageManager || 'npm';
    const rank = (s) => { const i = PM_FIRST.indexOf(s); return i < 0 ? 99 : i; };
    return scripts.slice().sort((a, b) => rank(a) - rank(b) || a.localeCompare(b)).slice(0, 8)
      .map((s) => (pm === 'npm' ? (s === 'start' || s === 'test' ? `npm ${s}` : `npm run ${s}`) : `${pm} ${s}`));
  }
  async function addCommand(root, pname, folder) {
    if (addBusy || $('#formDlg').open) return;
    addBusy = true;
    let sugg = [];
    try { const d = await env.request('projectScripts', { folder }, 3000); sugg = suggestions((d?.repos || []).find((r) => norm(r.repoRoot) === norm(root))); }
    catch { /* sem sugestões: o formulário funciona do mesmo jeito */ }
    finally { addBusy = false; }
    if ($('#formDlg').open) return;
    env.openSavedForm({ id: '', name: `${pname} · ${baseOf(root)}`, command: '', workingDirectory: root, env: {}, launchMode: 'terminal', notes: null }, sugg);
  }

  // ---------- renderização ----------
  function syncRepos(t) {
    const km = knownMap();
    const list = visibleRoots(t).map((r) => ({ root: r, name: repoName(r, km) }));
    window.GitView.setRepos(list, { stack: ws.docked });
    el.empty.hidden = list.length > 0;
    if (!list.length) el.empty.innerHTML = '<div class="empty"><h3>Sem repositórios git</h3><p>Nenhum repositório git foi encontrado nesta pasta. Se você criou um repositório agora, abra o projeto de novo.</p></div>';
  }
  function renderAll() {
    renderBar();
    const t = activeTab();
    el.panel.hidden = !t; el.page.hidden = !!t;
    if (t) { renderProject(t); syncRepos(t); } else { window.GitView.setRepos([]); renderPage(); }
    window.GitView.prune(allRoots());
    const open = new Set(ws.tabs.map((t) => norm(t.folder)));
    for (const k of shownByTab.keys()) if (!open.has(k)) shownByTab.delete(k);
    const roots = allRoots();
    for (const k of draftInfo.keys()) if (!roots.has(k)) draftInfo.delete(k);
    env.onCount(ws.tabs.length);
    window.Terminals?.sync();
  }
  // chamado a cada snapshot e a cada atualização dos painéis: só redesenha o que mudou
  function refresh() {
    if (!env) return;
    window.Terminals?.sync();
    if (el.wrap.hidden) return;
    renderBar();
    const t = activeTab();
    if (t) renderProject(t); else if (!el.page.hidden) renderPage();
    if (menuOpen) { const keep = document.activeElement?.dataset?.wa; el.menu.innerHTML = menuHtml(); if (keep) el.menu.querySelector(`[data-wa="${keep}"]`)?.focus(); }
  }

  // ---------- eventos ----------
  const tabButtons = () => [...el.tabs.querySelectorAll('[role=tab]')];
  function cycleTab(dir) {
    const tabs = ws.tabs; if (!tabs.length) return;
    const i = ws.active ? tabs.findIndex((t) => norm(t.folder) === norm(ws.active)) : -1;
    const next = tabs[(i + dir + tabs.length) % tabs.length];
    activate(next.folder);
  }
  function onClick(e) {
    const b = e.target.closest('[data-wa]');
    if (b && el.wrap.contains(b)) {
      if (b.disabled || b.getAttribute('aria-disabled') === 'true') return;
      const d = b.dataset, t = activeTab();
      switch (d.wa) {
        case 'tab': activate(d.folder); break;
        case 'close': closeTab(d.folder); break;
        case 'close-active': if (t) closeTab(t.folder); break;
        case 'dock': toggleDocked(); break;
        case 'collapse': setOverlay(false, true); break;
        case 'page': setMenu(false); pageMode = true; renderAll(); break;
        case 'open-project': { const p = projects.find((x) => x.id === d.id); setMenu(false); if (p) openProject(p); break; }
        case 'open-repo': setMenu(false); openRepo(d.root); break;
        case 'repo': repoClick(d.root); break;
        case 'save': case 'rename': if (t) openProjectDialog({ proj: savedOf(t), folder: t.folder, name: t.name, repos: t.repos }); break;
        case 'unsave': { const p = savedOf(t); if (p) unsave(p); break; }
        case 'rename-id': case 'unsave-id': {
          const p = projects.find((x) => x.id === d.id); if (!p) break;
          if (d.wa === 'unsave-id') unsave(p); else openProjectDialog({ proj: p, folder: p.folder, name: p.name, repos: p.repos });
          break;
        }
        case 'folder': if (t) env.openPath('openFolder', t.folder); break;
        case 'terminal': if (t) env.openPath('openTerminal', t.folder); break;
        case 'run': env.toggleRun(d.id); break;
        case 'run-all': runAll(d.folder); break;
        case 'stop-all': stopAll(d.folder); break;
        case 'add-cmd': addCommand(d.root, d.pname, d.folder); break;
        case 'wt-toggle': wtOpen.set(d.root, !(wtOpen.get(d.root) ?? wtLinked(d.root) > 0)); sigs.repos = ''; refresh(); break;
        case 'wt-new': openWtDialog(d.root, null); break;
        case 'wt-prune': pruneWorktrees(d.root); break;
        case 'wt-open': openWorktreeTab({ path: d.path, branch: d.branch || null, mainRepoRoot: d.root }); break;
        case 'wt-term': openWorktreeTab({ path: d.path, branch: d.branch || null, mainRepoRoot: d.root }).then(() => window.Terminals.openShellIn(d.path)); break;
        case 'wt-rm': removeWorktree(d.root, d.path); break;
        case 'wt-run': window.Terminals.runSavedIn(d.id, d.path); break;
        case 'wt-parent': openParent(d.root); break;
        case 'list': env.filterProcesses(d.folder); break;
        default:
      }
      return;
    }
    const card = e.target.closest('.pg-card');
    if (card && el.wrap.contains(card)) { const p = projects.find((x) => x.id === card.dataset.id); if (p) openProject(p); }
  }
  function repoClick(root) {
    const t = activeTab(); if (!t) return;
    t.activeRepo = root;
    if (t.repos.length > MAX_SHOWN) {
      let sel = visibleRoots(t);
      if (sel.includes(root)) { if (sel.length > 1) sel = sel.filter((r) => r !== root); }
      else { sel = [...sel, root]; if (sel.length > MAX_SHOWN) sel = sel.slice(sel.length - MAX_SHOWN); }
      shownByTab.set(norm(t.folder), sel);
      if (!sel.includes(root)) t.activeRepo = sel[0];
      changed(); if (sel.includes(root)) window.GitView.focusRepo(root);
    } else { persist(); window.GitView.focusRepo(root); }
  }
  function onKey(e) {
    const dialogOpen = !!document.querySelector('dialog[open]');
    if (!dialogOpen && e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey && e.key.toLowerCase() === 'g') { e.preventDefault(); toggleDocked(); return; }
    if (dialogOpen) return;
    if (e.ctrlKey && !e.altKey && !e.shiftKey && (e.key === 'PageDown' || e.key === 'PageUp') && !el.wrap.hidden && ws.tabs.length) { e.preventDefault(); cycleTab(e.key === 'PageDown' ? 1 : -1); return; }
    if (e.key === 'Escape') {
      if (menuOpen) { e.preventDefault(); setMenu(false, true); }
      else if (ws.docked && narrowMq.matches && overlayOpen && el.wrap.contains(document.activeElement)) { e.preventDefault(); setOverlay(false, true); }
    }
  }
  function bind() {
    el.wrap.addEventListener('click', onClick);
    el.add.addEventListener('click', () => setMenu(!menuOpen, true));
    el.handle.addEventListener('click', () => setOverlay(true));
    el.tabs.addEventListener('keydown', (e) => {
      const tabs = tabButtons(), i = tabs.indexOf(document.activeElement); if (i < 0) return;
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft' || e.key === 'Home' || e.key === 'End') {
        e.preventDefault();
        const n = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
        activate(tabs[n].dataset.folder); tabButtons()[n]?.focus();
      } else if (e.key === 'Delete') { e.preventDefault(); closeTab(tabs[i].dataset.folder); }
    });
    el.tabs.addEventListener('auxclick', (e) => { const b = e.target.closest('[data-wa=tab]'); if (b && e.button === 1) { e.preventDefault(); closeTab(b.dataset.folder); } });
    el.menu.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      const items = [...el.menu.querySelectorAll('[role=menuitem]')], i = items.indexOf(document.activeElement);
      e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
    });
    document.addEventListener('pointerdown', (e) => { if (menuOpen && !e.target.closest('#wsMenu') && !e.target.closest('#wsAdd')) setMenu(false); });
    document.addEventListener('keydown', onKey);
    narrowMq.addEventListener('change', () => { if (!narrowMq.matches) overlayOpen = false; applyDock(); renderAll(); });
    $('#projForm').addEventListener('submit', submitProject);
    $('#projCancel').addEventListener('click', () => $('#projDlg').close());
    bindSplit();
    bindWt();
  }

  // ---------- worktrees ----------
  const wtCache = new Map();       // raiz do repo principal → { list: GitWorktree[], at }
  const wtOpen = new Map();        // raiz → seção aberta? (padrão: aberta quando há worktree vinculado)
  const wtFetchAt = new Map();
  const wtList = (root) => wtCache.get(root)?.list || [];
  const wtLinked = (root) => wtList(root).filter((w) => !w.isMain).length;
  function onDetails(root, d) {
    if (!d?.worktrees) return;
    const main = d.repo?.mainRepoRoot || root;
    wtCache.set(main, { list: d.worktrees, at: Date.now() });
    refresh();
  }
  // lista de worktrees dos repos da aba (leitura leve; os painéis visíveis já atualizam sozinhos)
  function ensureWorktrees(roots) {
    for (const r of roots) {
      if (Date.now() - (wtFetchAt.get(r) || 0) < 15000) continue;
      wtFetchAt.set(r, Date.now());
      env.request('gitDetails', { repoRoot: r, maxCommits: 1 }).then((d) => onDetails(r, d.details)).catch(() => {});
    }
  }
  const slugOf = (b) => String(b || '').replace(/^(origin|upstream)\//, '').replace(/[\\/:*?"<>|\s]+/g, '-').replace(/^-+|-+$/g, '');
  const projectFolderFor = (root) => ws.tabs.find((t) => !t.worktreeOf && hasRepo(t.repos, root))?.folder || projectForRepo(root)?.folder || parentOf(root);
  const parentName = (main) => { const t = ws.tabs.find((x) => !x.worktreeOf && hasRepo(x.repos, main)); return t ? displayName(t) : (projectForRepo(main)?.name || baseOf(projectFolderFor(main))); };
  const wtRunning = (path) => runningIn(path).count > 0 || window.Terminals.countIn(path) > 0;
  function relPath(path, root) {
    const proj = projectFolderFor(root);
    if (within(path, proj)) return u.pathText(trimEnd(path).slice(trimEnd(proj).length + 1) || baseOf(path));
    return u.pathText(trimEnd(path).split(BS).slice(-2).join(BS));
  }
  function wtRowHtml(w, root) {
    const r = u.esc(root), p = u.esc(w.path), br = w.branch || (w.detached ? 'HEAD destacado' : '—'), run = !w.prunable && wtRunning(w.path);
    const lock = w.locked ? '<span class="wt-badge lock" title="' + u.esc(w.lockReason || 'Worktree bloqueado') + '">' + ic('lock') + 'bloqueado</span>' : '';
    const badges = (run ? '<span class="wt-badge on">rodando</span>' : '') + lock + (w.prunable ? '<span class="wt-badge bad" title="A pasta deste worktree não existe mais">órfão</span>' : '') + (w.isMain ? '<span class="wt-badge">principal</span>' : '');
    const dirty = w.dirtyCount > 0 ? '<span class="gs dirty" title="' + u.plural(w.dirtyCount, 'arquivo alterado', 'arquivos alterados') + '">●' + u.nf0.format(w.dirtyCount) + '</span>' : '';
    let acts = '';
    if (!w.isMain) {
      const base = ' data-root="' + r + '" data-path="' + p + '" data-branch="' + u.esc(w.branch || '') + '"';
      if (w.prunable) acts += '<button type="button" class="ibtn" aria-disabled="true" data-fk="wto-' + p + '" aria-label="Abrir a aba do worktree ' + u.esc(br) + ' (indisponível)" title="A pasta não existe mais; use Limpar órfãos">' + ic('wt') + '</button>';
      else {
        acts += '<button type="button" class="ibtn" data-wa="wt-open"' + base + ' data-fk="wto-' + p + '" aria-label="Abrir a aba do worktree ' + u.esc(br) + '" title="Abrir em uma aba">' + ic('wt') + '</button>';
        acts += '<button type="button" class="ibtn" data-wa="wt-term"' + base + ' data-fk="wtt-' + p + '" aria-label="Abrir terminal no worktree ' + u.esc(br) + '" title="Abrir terminal">' + ic('terminal') + '</button>';
      }
      acts += '<button type="button" class="ibtn kill" data-wa="wt-rm"' + base + ' data-fk="wtr-' + p + '" aria-label="Remover o worktree ' + u.esc(br) + '" title="Remover worktree">' + ic('trash') + '</button>';
    }
    return '<div class="wt-row' + (w.isMain ? ' main' : '') + '" title="' + p + '"><span class="wt-b">' + ic('branch') + '<b>' + u.esc(br) + '</b></span><span class="wt-path">' + u.esc(relPath(w.path, root)) + '</span>' + dirty + badges + '<span class="wt-acts">' + acts + '</span></div>';
  }
  function wtSection(root) {
    ensureWorktrees([root]);
    const list = wtList(root); if (!list.length) return '';
    const open = wtOpen.get(root) ?? wtLinked(root) > 0, orphans = list.filter((w) => w.prunable).length, r = u.esc(root);
    let head = '<div class="wt-head"><button type="button" class="wt-tg" data-wa="wt-toggle" data-root="' + r + '" data-fk="wtg-' + r + '" aria-expanded="' + open + '">' + ic('chevron') + '<span>Worktrees (' + list.length + ')</span></button>';
    head += '<button type="button" class="btn small ghost" data-wa="wt-new" data-root="' + r + '" data-fk="wtn-' + r + '">' + ic('plus') + 'Novo worktree</button>';
    if (orphans) head += '<button type="button" class="btn small ghost" data-wa="wt-prune" data-root="' + r + '" data-fk="wtp-' + r + '" title="Remove o registro de worktrees cujas pastas não existem mais">Limpar órfãos (' + orphans + ')</button>';
    head += '</div>';
    return '<div class="wt-sec">' + head + (open ? '<div class="wt-list">' + list.map((w) => wtRowHtml(w, root)).join('') + '</div>' : '') + '</div>';
  }

  async function openWorktreeTab({ path, branch, mainRepoRoot }) {
    if (wtList(mainRepoRoot).find((w) => norm(w.path) === norm(path))?.prunable) { env.toast('A pasta deste worktree não existe mais; use Limpar órfãos.', 'error'); return false; }
    pageMode = false;
    let t = tabOf(path);
    if (!t) {
      if (ws.tabs.length >= MAX_TABS) { env.toast(`Limite de ${MAX_TABS} abas atingido. Feche uma aba para abrir outro worktree.`, 'error'); return false; }
      const km = knownMap(), br = branch || repoInfo(path, km)?.branch || 'HEAD destacado';
      t = { folder: path, name: `${parentName(mainRepoRoot)} · ${repoName(mainRepoRoot, km)} ⎇ ${br}`, repos: [path], projectId: null, activeRepo: path, worktreeOf: mainRepoRoot };
      const pi = ws.tabs.findIndex((x) => !x.worktreeOf && hasRepo(x.repos, mainRepoRoot));
      if (pi < 0) ws.tabs.push(t);
      else { let j = pi + 1; while (ws.tabs[j]?.worktreeOf && hasRepo(ws.tabs[pi].repos, ws.tabs[j].worktreeOf)) j++; ws.tabs.splice(j, 0, t); }
    }
    ws.active = t.folder; changed(); reveal(); focusActive(path);
    return true;
  }
  async function openParent(main) {
    const t = ws.tabs.find((x) => !x.worktreeOf && hasRepo(x.repos, main));
    if (t) { activate(t.folder); return; }
    const proj = projectForRepo(main);
    if (proj) { openProject(proj, main); return; }
    const d = await draftFor(main); if (d) openDraft(d, main);
  }
  function renderWorktreeHead(t) {
    const km = knownMap(), main = t.worktreeOf, info = repoInfo(t.folder, km);
    const w = wtList(main).find((x) => norm(x.path) === norm(t.folder));
    ensureWorktrees([main]);
    const br = w?.branch ?? info?.branch ?? 'HEAD destacado', rn = repoName(main, km), pn = parentName(main), m = u.esc(main), f = u.esc(t.folder);
    const lock = w?.locked ? '<span class="wt-badge lock" title="' + u.esc(w.lockReason || 'Worktree bloqueado') + '">' + ic('lock') + 'bloqueado</span>' : '';
    const l1 = '<h2 class="pj-name" title="' + u.esc(t.name) + '">Worktree de ' + u.esc(rn) + ' · ' + u.esc(pn) + '</h2>'
      + '<span class="pj-repo static" title="Branch ' + u.esc(br) + '">' + ic('branch') + '<span class="rn">' + u.esc(br) + '</span></span>' + lock
      + '<span class="pj-path" title="' + f + '">' + u.esc(u.pathText(u.shortPath(t.folder, 70))) + '</span><span class="gp-sp"></span><span class="pj-acts">'
      + '<button type="button" class="btn small ghost" data-wa="wt-parent" data-root="' + m + '" data-fk="wtparent">Ir para o projeto</button>'
      + '<button type="button" class="btn small danger" data-wa="wt-rm" data-root="' + m + '" data-path="' + f + '" data-fk="wtrm">' + ic('trash') + 'Remover worktree</button>'
      + '<button type="button" class="ibtn" data-wa="folder" data-fk="folder" aria-label="Abrir pasta do worktree no Explorer" title="Abrir pasta no Explorer">' + ic('folder') + '</button>'
      + '<button type="button" class="ibtn" data-wa="terminal" data-fk="term" aria-label="Abrir terminal na pasta do worktree" title="Abrir terminal na pasta">' + ic('terminal') + '</button></span>';
    if (sigs.l1 !== l1) { sigs.l1 = l1; keepFocus(el.l1, () => { el.l1.innerHTML = l1; }); }
    const runs = savedIn(main).filter((x) => within(x.workingDirectory, main));
    const btns = runs.length
      ? runs.map((x) => '<button type="button" class="btn small" data-wa="wt-run" data-id="' + u.esc(x.id) + '" data-path="' + f + '" data-fk="wtrun-' + u.esc(x.id) + '" title="' + u.esc(x.command) + '">' + ic('play') + u.esc(x.name) + '</button>').join('')
      : '<span class="pj-hint">Nenhum comando salvo no repositório principal. Salve um comando lá para rodá-lo neste worktree.</span>';
    const body = '<div class="pj-rrow"><span class="wt-runl">Rodar aqui</span><div class="pj-svs">' + btns + '</div></div>';
    if (sigs.repos !== body) { sigs.repos = body; keepFocus(el.repos, () => { el.repos.innerHTML = body; }); }
    const foot = runSummary(t.folder) + '<button type="button" class="btn small ghost" data-wa="list" data-folder="' + f + '" data-fk="list" title="Filtra a lista de processos por esta pasta">Ver na lista</button>';
    if (sigs.foot !== foot) { sigs.foot = foot; keepFocus(el.foot, () => { el.foot.innerHTML = foot; }); }
  }

  async function pruneWorktrees(root) {
    const n = wtList(root).filter((w) => w.prunable).length;
    const r = await env.confirm({ title: 'Limpar worktrees órfãos?', body: `${u.plural(n, 'registro de worktree cuja pasta não existe mais será removido', 'registros de worktrees cujas pastas não existem mais serão removidos')}. As branches não são apagadas.`, ok: 'Limpar órfãos', danger: false });
    if (!r) return;
    try {
      const d = await env.request('gitWorktreePrune', { repoRoot: root });
      afterWtChange(root, d?.details); env.toast('Worktrees órfãos removidos');
    } catch (e) { env.toast(e.message || 'Não foi possível limpar os worktrees órfãos.', 'error'); }
  }
  function afterWtChange(root, details) {
    if (details) { onDetails(root, details); window.GitView.applyDetails(root, details); }
    sigs.repos = ''; refresh();
  }
  async function removeWorktree(root, path) {
    const w = wtList(root).find((x) => norm(x.path) === norm(path)), label = w?.branch || baseOf(path);
    const r = await env.confirm({ title: 'Remover o worktree?', body: `“${label}” (${path}) será removido do disco. A branch não é apagada.`, ok: 'Remover worktree' });
    if (!r) return;
    let d = null;
    try { d = await env.request('gitWorktreeRemove', { repoRoot: root, path, force: false }, 60000); }
    catch (e) {
      const msg = e.message || 'Não foi possível remover o worktree.';
      if (e.code !== 'worktreeDirty') { env.toast(msg, 'error'); return; }   // worktreeInUse e demais: mostra a mensagem do Core
      const f = await env.confirm({ title: 'Remover mesmo com mudanças?', body: `${msg} Remover descarta essas mudanças.`, ok: 'Remover e descartar' });
      if (!f) return;
      try { d = await env.request('gitWorktreeRemove', { repoRoot: root, path, force: true }, 60000); }
      catch (e2) { env.toast(e2.message || 'Não foi possível remover o worktree.', 'error'); return; }
    }
    const t = tabOf(path);
    if (t) {
      ws.tabs.splice(ws.tabs.indexOf(t), 1);
      if (ws.active && norm(ws.active) === norm(path)) ws.active = (ws.tabs.find((x) => !x.worktreeOf && hasRepo(x.repos, root)) || ws.tabs[0])?.folder ?? null;
      changed();
    }
    afterWtChange(root, d?.details); env.toast('Worktree removido');
  }

  // ---------- diálogo "Novo worktree" ----------
  const wt = { root: null, roots: [], branches: [], sel: null, touched: false, mode: 'existing', preview: '' };
  const wtEl = (id) => $('#' + id);
  const branchOptions = () => wt.branches.filter((b) => !b.name.endsWith('/HEAD'));
  const needsPath = () => !ws.tabs.some((t) => !t.worktreeOf && hasRepo(t.repos, wt.root)) && !projectForRepo(wt.root) && /^[A-Za-z]:$/.test(parentOf(wt.root));
  function wtDefaultPath() {
    if (needsPath()) return '';
    const name = wt.mode === 'new' ? wtEl('wt-name').value.trim() : wt.sel || '';
    return trimEnd(projectFolderFor(wt.root)) + '.worktrees' + BS + repoName(wt.root, knownMap()) + '-' + (slugOf(name) || '<branch>');
  }
  function wtSyncPath() {
    wt.preview = wtDefaultPath();
    if (!wt.touched) wtEl('wt-path').value = wt.preview;
    wtEl('wt-path-h').textContent = needsPath() ? 'Este repositório está na raiz da unidade e não pertence a um projeto salvo: informe a pasta do worktree.' : wt.touched ? 'Pasta personalizada.' : 'Prévia da pasta padrão; edite se quiser outra.';
  }
  function wtRenderList() {
    const q = wtEl('wt-q').value.trim().toLowerCase(), match = (b) => !q || b.name.toLowerCase().includes(q);
    const loc = branchOptions().filter((b) => !b.remote && match(b)), rem = branchOptions().filter((b) => b.remote && match(b));
    const one = (b) => {
      const dis = !!b.worktreePath;
      return '<button type="button" role="option" class="pk-o' + (wt.sel === b.name ? ' cur' : '') + '" data-b="' + u.esc(b.name) + '" aria-selected="' + (wt.sel === b.name) + '"' + (dis ? ' aria-disabled="true"' : '') + ' title="' + u.esc(dis ? 'Em uso em ' + b.worktreePath : b.name) + '">' + ic(b.remote ? 'plus' : 'branch') + '<span class="pk-bn">' + u.esc(b.name) + '</span>' + (dis ? '<span class="wt-use">em uso em ' + u.esc(u.pathText(baseOf(b.worktreePath))) + '</span>' : '') + '</button>';
    };
    wtEl('wt-list').innerHTML = (loc.length ? '<div class="pk-g" role="presentation">Locais</div>' + loc.map(one).join('') : '') + (rem.length ? '<div class="pk-g" role="presentation">Remotas</div>' + rem.map(one).join('') : '') || '<div class="pk-none">Nenhuma branch encontrada.</div>';
  }
  async function wtLoadBranches() {
    wtEl('wt-list').innerHTML = '<div class="pk-none">Carregando branches…</div>';
    let d = window.GitView.details(wt.root);
    if (!d?.branches) {
      try { d = (await env.request('gitDetails', { repoRoot: wt.root, maxCommits: 1 })).details; }
      catch (e) { wtEl('e-wt').textContent = e.message || 'Não foi possível ler as branches.'; return; }
    }
    wt.branches = d.branches || [];
    if (d.worktrees) onDetails(wt.root, d);
    wtEl('wt-from').innerHTML = branchOptions().map((b) => '<option value="' + u.esc(b.name) + '"' + (b.current ? ' selected' : '') + '>' + u.esc(b.name) + (b.current ? ' (atual)' : '') + '</option>').join('');
    const runs = savedIn(wt.root).filter((x) => within(x.workingDirectory, wt.root));
    wtEl('wt-run').innerHTML = '<option value="">Nenhum</option>' + runs.map((x) => '<option value="' + u.esc(x.id) + '">' + u.esc(x.name) + ' — ' + u.esc(x.command) + '</option>').join('');
    wtRenderList(); wtSyncPath();
  }
  function wtSetMode(m) {
    wt.mode = m; wtEl('wt-ex').hidden = m !== 'existing'; wtEl('wt-nw').hidden = m !== 'new';
    document.querySelector('input[name=wtMode][value=' + m + ']').checked = true; wtSyncPath();
  }
  function openWtDialog(root, branch) {
    if (wtEl('wtDlg').open) return;
    const t = activeTab();
    const roots = t && !t.worktreeOf && hasRepo(t.repos, root) ? t.repos.slice() : [root];
    Object.assign(wt, { root, roots, branches: [], sel: branch || null, touched: false, mode: 'existing' });
    const km = knownMap();
    wtEl('wt-repo').innerHTML = roots.map((r) => '<option value="' + u.esc(r) + '"' + (r === root ? ' selected' : '') + '>' + u.esc(repoName(r, km)) + '</option>').join('');
    wtEl('wt-repo-f').hidden = roots.length < 2;
    wtEl('wt-q').value = ''; wtEl('wt-name').value = ''; wtEl('wt-env').checked = true; wtEl('wt-tab').checked = true; wtEl('e-wt').textContent = '';
    wtSetMode('existing');
    wtEl('wtDlg').showModal(); wtEl(branch ? 'wt-path' : 'wt-q').focus();
    wtLoadBranches();
  }
  async function submitWt(ev) {
    ev.preventDefault();
    const err = wtEl('e-wt'); err.textContent = '';
    const payload = { repoRoot: wt.root, copyEnv: wtEl('wt-env').checked };
    if (wt.mode === 'existing') {
      const b = branchOptions().find((x) => x.name === wt.sel);
      if (!b) { err.textContent = 'Escolha uma branch.'; return; }
      if (b.worktreePath) { err.textContent = `Esta branch já está em uso em ${b.worktreePath}.`; return; }
      if (b.remote) { payload.newBranch = b.name.replace(/^[^/]+\//, ''); payload.startPoint = b.name; } else payload.branch = b.name;
    } else {
      const n = wtEl('wt-name').value.trim();
      if (!n) { err.textContent = 'Informe o nome da nova branch.'; wtEl('wt-name').focus(); return; }
      payload.newBranch = n; payload.startPoint = wtEl('wt-from').value || null;
    }
    const typed = wtEl('wt-path').value.trim();
    if (needsPath() && !typed) { wtEl('e-wtpath').textContent = 'Informe a pasta do worktree.'; wtEl('wt-path').focus(); return; }
    if ((wt.touched || needsPath()) && typed) payload.path = typed;
    const btn = wtEl('wtSave'); btn.disabled = true;
    let d = null;
    wtEl('e-wtpath').textContent = '';
    try { d = await env.request('gitWorktreeAdd', payload, 120000); }
    catch (e) {
      const msg = e.message || 'Não foi possível criar o worktree.';
      if (msg.startsWith('Caminho do worktree inválido: ')) { wtEl('e-wtpath').textContent = msg; wtEl('wt-path').focus(); } else err.textContent = msg;
    }
    btn.disabled = false;
    if (!d?.worktree) return;
    wtEl('wtDlg').close();
    afterWtChange(wt.root, d.details);
    const copied = d.copied || [];
    env.toast(copied.length ? `Worktree criado. ${u.plural(copied.length, 'arquivo copiado', 'arquivos copiados')}: ${copied.join(', ')}` : 'Worktree criado.');
    const runId = wtEl('wt-run').value;
    if (wtEl('wt-tab').checked || runId) await openWorktreeTab({ path: d.worktree.path, branch: d.worktree.branch, mainRepoRoot: wt.root });
    if (runId) window.Terminals.runSavedIn(runId, d.worktree.path);
  }
  function bindWt() {
    wtEl('wtForm').addEventListener('submit', submitWt);
    wtEl('wtCancel').addEventListener('click', () => wtEl('wtDlg').close());
    wtEl('wt-repo').addEventListener('change', (e) => { wt.root = e.target.value; wt.sel = null; wt.touched = false; wtLoadBranches(); });
    wtEl('wt-q').addEventListener('input', wtRenderList);
    wtEl('wt-name').addEventListener('input', wtSyncPath);
    wtEl('wt-path').addEventListener('input', (e) => { wt.touched = e.target.value !== wt.preview; wtEl('wt-path-h').textContent = wt.touched ? 'Pasta personalizada.' : 'Prévia da pasta padrão; edite se quiser outra.'; });
    for (const r of document.querySelectorAll('input[name=wtMode]')) r.addEventListener('change', () => wtSetMode(r.value));
    wtEl('wt-list').addEventListener('click', (e) => {
      const b = e.target.closest('[data-b]');
      if (!b || b.getAttribute('aria-disabled') === 'true') return;
      wt.sel = b.dataset.b; wtRenderList(); wtSyncPath();
    });
  }

  // ---------- API ----------
  function init(e) {
    env = e; u = e.u; ic = e.icon;
    Object.assign(el, { wrap: $('#gitwrap'), tabs: $('#wsTabs'), add: $('#wsAdd'), dock: $('#wsDock'), collapse: $('#wsCollapse'), close: $('#wsClose'), menu: $('#wsMenu'), panel: $('#wsPanel'), page: $('#wsPage'), empty: $('#wsEmpty'), split: $('#dockSplit'), handle: $('#dockHandle'), workarea: $('#workarea') });
    el.proj = $('#wsProj');
    el.proj.innerHTML = '<div class="pj-l1" id="pjL1"></div><div class="pj-repos" id="pjRepos"></div><div class="pj-foot" id="pjFoot"></div>';
    el.l1 = $('#pjL1'); el.repos = $('#pjRepos'); el.foot = $('#pjFoot');
    el.add.innerHTML = ic('plus'); el.collapse.innerHTML = ic('x'); el.close.innerHTML = ic('x');
    buildPageHead();
    new ResizeObserver(() => el.tabs.querySelector('.ws-tab.on')?.scrollIntoView({ block: 'nearest', inline: 'nearest' })).observe(el.tabs);
    bind(); applyDock(); renderAll();
  }
  function load(config) {
    projects = Array.isArray(config?.gitProjects) ? config.gitProjects.filter((p) => p && p.id).map((p) => ({ id: p.id, name: p.name, folder: p.folder, repos: Array.isArray(p.repos) ? p.repos.slice() : [] })) : [];
    ws = sane(config?.gitWorkspace);
    ws.tabs.forEach((t) => { if (t.projectId && !projects.some((p) => p.id === t.projectId)) t.projectId = null; });
    overlayOpen = false; pageMode = false;
    applyDock(); renderAll();
  }
  window.GitWorkspace = {
    init, load, refresh, openRepo,
    isDocked: () => ws.docked,
    setPlacement(gitTabSelected) { tabGit = gitTabSelected; if (env) applyDock(); },
    toggleDocked, collapse: () => setOverlay(false),
    onDetails, worktreeDialog: (root, branch) => openWtDialog(root, branch),
    openWorktreePath(path) {
      for (const [main, v] of wtCache) { const w = v.list.find((x) => norm(x.path) === norm(path)); if (w) return openWorktreeTab({ path: w.path, branch: w.branch, mainRepoRoot: main }); }
      env.toast('Worktree não encontrado. Atualize o repositório e tente de novo.', 'error'); return false;
    },
    wtFolders: () => ws.tabs.filter((t) => t.worktreeOf).map((t) => t.folder),
    // usados pelos terminais embutidos
    tabs: () => ws.tabs.map((t) => t.folder),
    panelVisible: () => !el.wrap.hidden && !el.panel.hidden,
    ctx() {
      const t = activeTab(); if (!t) return null;
      const km = knownMap();
      return { key: norm(t.folder), folder: t.folder, name: displayName(t), wt: !!t.worktreeOf, repos: t.worktreeOf ? [] : t.repos.map((r) => ({ root: r, name: repoName(r, km) })) };
    },
    showTab(folder) { const t = tabOf(folder); if (!t) return; pageMode = false; ws.active = t.folder; changed(); reveal(); },
  };
})();
