'use strict';
/* Terminais embutidos (xterm.js). Uma faixa na aba do projeto (e outra, para terminais sem projeto aberto, na aba Salvos).
   O host guarda o buffer; aqui só existe um xterm montado por faixa visível: ao voltar, termAttach reconstrói a tela. */
(() => {
  const UI_KEY = 'monitoramento.term.ui.v1', CHUNK = 16000, MIN_H = 120, DEF_H = 260, BS = String.fromCharCode(92);
  const FONT = '"Cascadia Mono", Consolas, "Courier New", monospace';
  const $ = (s, r = document) => r.querySelector(s);
  const norm = (p) => String(p || '').split('/').join(BS).replace(/\\+$/, '').toLowerCase();
  const within = (path, folder) => { if (!path || !folder) return false; const a = norm(path), b = norm(folder); return a === b || a.startsWith(b + BS); };
  const baseOf = (p) => { const s = String(p || '').split('/').join(BS).replace(/\\+$/, ''); return s.slice(s.lastIndexOf(BS) + 1) || s; };

  let env = null, u = null, ic = null;
  let terms = [];                  // TerminalInfo[]: último snapshot + o que respostas locais acrescentaram
  const local = new Map();         // id → { info, at } (ainda não veio no snapshot)
  const strips = [];
  let pendingReveal = null;

  // ---------- preferências da faixa (altura e aberta/recolhida por projeto) ----------
  function loadUi() { try { const j = JSON.parse(localStorage.getItem(UI_KEY) || '{}'); return j && typeof j === 'object' ? j : {}; } catch { return {}; } }
  function saveUi(key, patch) {
    const all = loadUi(); all[key] = { ...(all[key] || {}), ...patch };
    try { localStorage.setItem(UI_KEY, JSON.stringify(all)); } catch { /* sem persistência */ }
  }
  const getUi = (key) => { const v = loadUi()[key] || {}; return { h: Number(v.h) >= MIN_H ? Number(v.h) : DEF_H, open: !!v.open }; };

  // ---------- dados ----------
  const byId = (id) => terms.find((t) => t.id === id) || null;
  // termExit já recebido: um snapshot atrasado não pode voltar a dizer "rodando" para a mesma execução (mesmo pid e startedAt)
  const exited = new Map();
  function setTerms(incoming) {
    const list = incoming.map((t) => {
      const x = exited.get(t.id);
      if (!x) return t;
      if (x.startedAt !== t.startedAt || x.pid !== t.pid) { exited.delete(t.id); return t; }
      return t.running ? { ...t, running: false, exitCode: x.exitCode } : t;
    });
    const ids = new Set(list.map((t) => t.id));
    const now = Date.now();
    for (const [id, v] of local) if (ids.has(id) || now - v.at > 6000) local.delete(id);
    terms = list.concat([...local.values()].map((v) => v.info));
  }
  function upsert(info) {
    local.set(info.id, { info, at: Date.now() });
    const i = terms.findIndex((t) => t.id === info.id);
    if (i >= 0) terms[i] = info; else terms.push(info);
  }
  const owns = (folder, t) => (t.projectFolder && norm(t.projectFolder) === norm(folder)) || within(t.folder, folder);
  const ownerTab = (t) => env.tabs().filter((f) => owns(f, t)).sort((a, b) => b.length - a.length)[0] || null;   // a pasta mais específica vence (worktree dentro do projeto)
  const stateOf = (t) => (t.running ? 'on' : t.exitCode ? 'bad' : 'off');
  const stateTxt = (t) => (t.running ? 'rodando' : t.exitCode == null ? 'parado' : t.exitCode === 0 ? 'saiu (código 0)' : `saiu com código ${t.exitCode}`);

  // ---------- tema pelos tokens ----------
  function theme() {
    const cs = getComputedStyle(document.documentElement), v = (n) => cs.getPropertyValue(n).trim();
    const o = { background: v('--term-bg'), foreground: v('--term-fg'), cursor: v('--term-cursor'), cursorAccent: v('--term-bg'), selectionBackground: v('--term-sel') };
    ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white', 'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite']
      .forEach((n, i) => { o[n] = v('--a' + i); });
    return o;
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); return; } catch { /* cai para o método antigo */ }
    const ta = document.createElement('textarea'); ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    try { document.execCommand('copy'); } finally { ta.remove(); }
  }

  // ---------- faixa ----------
  function createStrip(host, cfg) {
    const S = { host, cfg, active: new Map(), m: null, menuOpen: false, tabSig: '', ctxKey: null, focusNext: false };
    host.className = 'ts'; host.hidden = true;
    host.innerHTML = `
      <div class="ts-resize" role="separator" aria-orientation="horizontal" aria-label="Altura dos terminais" tabindex="0"></div>
      <div class="ts-bar">
        <span class="ts-label">Terminais</span>
        <div class="ts-tabs" role="tablist" aria-label="Terminais"></div>
        ${cfg.canNew ? `<button type="button" class="ibtn" data-t="new" aria-haspopup="menu" aria-expanded="false" aria-label="Novo terminal" title="Novo terminal">${ic('plus')}</button>` : ''}
        <span class="gp-sp"></span>
        <button type="button" class="ibtn" data-t="restart" aria-label="Reiniciar terminal" title="Reiniciar terminal">${ic('refresh')}</button>
        <button type="button" class="ibtn" data-t="clear" aria-label="Limpar a tela do terminal" title="Limpar a tela">${ic('eraser')}</button>
        <button type="button" class="ibtn" data-t="close-active" aria-label="Fechar terminal atual" title="Fechar terminal atual">${ic('x')}</button>
        <button type="button" class="ibtn" data-t="toggle" aria-expanded="false" aria-label="Expandir terminais" title="Expandir terminais">${ic('chevron')}</button>
      </div>
      <div class="pop ts-menu" role="menu" aria-label="Novo terminal" hidden></div>
      <div class="ts-body" role="tabpanel"><div class="ts-term"></div><div class="ts-empty" hidden></div></div>`;
    S.el = { resize: $('.ts-resize', host), tabs: $('.ts-tabs', host), menu: $('.ts-menu', host), body: $('.ts-body', host), term: $('.ts-term', host), empty: $('.ts-empty', host), toggle: $('[data-t=toggle]', host), restart: $('[data-t=restart]', host), clear: $('[data-t=clear]', host), closeA: $('[data-t=close-active]', host) };
    host.addEventListener('click', (e) => onClick(S, e));
    S.el.tabs.addEventListener('keydown', (e) => onTabKey(S, e));
    S.el.menu.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      const items = [...S.el.menu.querySelectorAll('[role=menuitem]')], i = items.indexOf(document.activeElement);
      e.preventDefault(); items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
    });
    host.addEventListener('keydown', (e) => { if (e.key === 'Escape' && S.menuOpen) { e.stopPropagation(); setMenu(S, false, true); } });
    bindResize(S);
    strips.push(S);
    return S;
  }

  function keepFocus(box, fn) {
    const a = document.activeElement, k = a && box.contains(a) ? a.dataset.fk : null;
    fn();
    if (k) box.querySelector(`[data-fk="${CSS.escape(k)}"]`)?.focus();
  }

  function render(S) {
    const ctx = S.cfg.ctx(), vis = !!ctx && S.cfg.visible();
    S.host.hidden = !vis;
    if (!vis) { unmount(S); S.ctxKey = null; return; }
    if (S.ctxKey !== ctx.key) { setMenu(S, false); unmount(S); S.ctxKey = ctx.key; S.tabSig = ''; }
    const ui = getUi(ctx.key), list = terms.filter((t) => S.cfg.belongs(t, ctx));
    let act = S.active.get(ctx.key);
    if (!list.some((t) => t.id === act)) act = list[0]?.id ?? null;
    S.active.set(ctx.key, act);
    const cur = list.find((t) => t.id === act) || null;
    // abas
    const sig = JSON.stringify([list.map((t) => [t.id, t.title, t.running, t.exitCode]), act, ui.open]);
    if (sig !== S.tabSig) {
      S.tabSig = sig;
      keepFocus(S.el.tabs, () => {
        S.el.tabs.innerHTML = list.map((t, i) => `<button type="button" role="tab" class="ts-tab${t.id === act ? ' on' : ''}" data-t="tab" data-id="${u.esc(t.id)}" data-fk="tt-${u.esc(t.id)}" aria-selected="${t.id === act}" tabindex="${t.id === act || (!act && i === 0) ? 0 : -1}" title="${u.esc(t.title + ' — ' + t.folder + ' — ' + stateTxt(t))}"><i class="ts-dot ${stateOf(t)}" aria-hidden="true"></i><span class="ts-tn">${u.esc(t.title)}</span>${!t.running && t.exitCode ? `<span class="ts-code" aria-hidden="true">${u.esc(t.exitCode)}</span>` : ''}<span class="sr"> ${u.esc(stateTxt(t))}</span><span class="ts-x" data-t="close" data-id="${u.esc(t.id)}" aria-hidden="true" title="Fechar terminal">${ic('x')}</span></button>`).join('');
      });
    }
    const label = ui.open ? 'Recolher terminais' : 'Expandir terminais';
    S.host.dataset.open = String(ui.open);
    S.el.toggle.setAttribute('aria-expanded', String(ui.open)); S.el.toggle.title = label; S.el.toggle.setAttribute('aria-label', label);
    S.el.restart.disabled = !cur || cur.running; S.el.clear.disabled = !cur || !ui.open; S.el.closeA.disabled = !cur;
    if (cur) S.el.body.setAttribute('aria-labelledby', S.el.tabs.querySelector('[aria-selected=true]')?.id || ''); else S.el.body.removeAttribute('aria-labelledby');
    if (ui.open) {
      const max = Math.max(MIN_H, (S.host.parentElement?.clientHeight || 600) - 150);
      S.host.style.height = Math.min(ui.h, Math.max(MIN_H, max)) + 'px';
      S.el.resize.setAttribute('aria-valuenow', String(Math.round(ui.h)));
    } else S.host.style.removeProperty('height');
    S.el.empty.hidden = !!cur || !ui.open;
    if (!cur && ui.open) S.el.empty.innerHTML = S.cfg.canNew ? '<p>Nenhum terminal neste projeto.</p><p class="hint">Use o botão + para abrir um shell na pasta do projeto ou de um repositório, ou inicie um processo salvo em modo terminal.</p>' : '<p>Nenhum terminal aberto.</p>';
    if (ui.open && cur) mount(S, cur.id); else unmount(S);
  }

  // ---------- xterm montado (só o ativo, só se visível) ----------
  function unmount(S) {
    const m = S.m; if (!m) return;
    m.disposed = true; clearTimeout(m.t); m.ro?.disconnect();
    try { m.webgl?.dispose(); } catch { /* já liberado */ }
    try { m.term.dispose(); } catch { /* já liberado */ }
    S.m = null; S.el.term.replaceChildren();
  }
  // Partes de no máximo CHUNK unidades UTF-16, sem partir um par substituto (surrogate)
  function chunksOf(data) {
    const out = []; let i = 0;
    while (i < data.length) {
      let end = Math.min(i + CHUNK, data.length);
      if (end < data.length) { const c = data.charCodeAt(end - 1); if (c >= 0xD800 && c <= 0xDBFF) end--; }
      out.push(data.slice(i, end)); i = end;
    }
    return out;
  }
  let lastInputErr = 0;
  const send = (m, id, data) => {
    const parts = chunksOf(data);
    // em ordem; se uma parte falhar, as seguintes não são enviadas (e o usuário é avisado)
    m.q = m.q.then(async () => {
      for (const part of parts) {
        try { await env.request('termInput', { id, data: part }); }
        catch (e) {
          if (parts.length > 1) env.toast('Parte do texto colado não foi enviada; nada mais foi enviado.', 'error');
          else if (Date.now() - lastInputErr > 3000) { lastInputErr = Date.now(); env.toast(e.message || 'Não foi possível enviar ao terminal.', 'error'); }
          return;
        }
      }
    });
  };
  // Abrir/rodar pode ser recusado por repositório não verificado: o Core pergunta ("… Abrir o terminal mesmo assim?") e aceita confirmUnsafe
  async function requestSafe(type, payload, failMsg) {
    try { return await env.request(type, payload); }
    catch (e) {
      const msg = e.message || failMsg;
      if (e.code !== 'unsafeRepo') { env.toast(msg, 'error'); return null; }
      const ok = await env.confirm({ title: 'Abrir o terminal neste repositório?', body: msg, ok: 'Abrir mesmo assim' });
      if (!ok) return null;
      try { return await env.request(type, { ...payload, confirmUnsafe: true }); }
      catch (e2) { env.toast(e2.message || failMsg, 'error'); return null; }
    }
  }
  function mount(S, id) {
    if (S.m?.id === id && !S.m.disposed) return;
    unmount(S);
    const info = byId(id); if (!info) return;
    const host = S.el.term;
    const term = new window.Terminal({ fontFamily: FONT, fontSize: 13, lineHeight: 1.15, cursorBlink: true, scrollback: 5000, theme: theme(), allowProposedApi: false, rightClickSelectsWord: false });
    const fit = new window.FitAddon.FitAddon();
    term.loadAddon(fit); term.open(host);
    term.textarea?.setAttribute('name', 'terminal-input');
    const m = S.m = { id, term, fit, webgl: null, pending: [], attaching: true, lastSeq: -1, ro: null, t: 0, q: Promise.resolve(), cols: info.cols, rows: info.rows, disposed: false };
    try {
      const gl = new window.WebglAddon.WebglAddon();
      gl.onContextLoss(() => { try { gl.dispose(); } catch { /* ok */ } m.webgl = null; });
      term.loadAddon(gl); m.webgl = gl;
    } catch { m.webgl = null; /* sem WebGL: fica o renderizador de DOM */ }
    const doFit = () => {
      if (m.disposed || host.clientWidth < 20 || host.clientHeight < 20) return;
      try { fit.fit(); } catch { return; }
      if (term.cols !== m.cols || term.rows !== m.rows) { m.cols = term.cols; m.rows = term.rows; env.request('termResize', { id, cols: term.cols, rows: term.rows }).catch(() => {}); }
    };
    doFit();
    m.ro = new ResizeObserver(() => { clearTimeout(m.t); m.t = setTimeout(doFit, 80); });
    m.ro.observe(host);
    term.onData((d) => send(m, id, d));
    term.attachCustomKeyEventHandler((ev) => {
      if (ev.type !== 'keydown' || !ev.ctrlKey || ev.altKey) return true;
      const k = ev.key.toLowerCase();
      if (k === 'c' && term.hasSelection()) { copy(term.getSelection()); term.clearSelection(); return false; }   // sem seleção, ^C segue para o shell
      if (k === 'v') return false;   // o evento paste do navegador cola (sem pedir permissão)
      return true;
    });
    host.oncontextmenu = (e) => {
      e.preventDefault();
      if (term.hasSelection()) { copy(term.getSelection()); term.clearSelection(); return; }
      navigator.clipboard.readText().then((t) => { if (t) term.paste(t); }).catch(() => env.toast('Para colar, use Ctrl+V.'));
    };
    if (S.focusNext) { term.focus(); S.focusNext = false; }
    attach(m);
  }
  // Lê o buffer do host e redesenha. Saída que chegar durante a leitura espera em m.pending.
  function attach(m) {
    m.attaching = true; m.pending = [];
    m.term.reset();
    env.request('termAttach', { id: m.id }).then((d) => {
      if (m.disposed) return;
      if (d?.data) m.term.write(d.data);
      m.lastSeq = Number(d?.seq ?? -1); m.attaching = false;
      const queued = m.pending; m.pending = [];
      for (const p of queued) applyOutput(m, p);
    }).catch((e) => { if (!m.disposed) { m.attaching = false; m.term.write(`\r\n\x1b[31m${(e.message || 'Não foi possível ler o terminal.')}\x1b[0m\r\n`); } });
  }
  // seq = total de caracteres produzidos até o fim de data (só cresce). Descarta o já aplicado; buraco = perda → reanexa.
  function applyOutput(m, p) {
    const end = Number(p.seq), start = end - p.data.length;
    if (end <= m.lastSeq) return;
    if (start > m.lastSeq) { attach(m); return; }
    m.term.write(start < m.lastSeq ? p.data.slice(m.lastSeq - start) : p.data);
    m.lastSeq = end;
  }
  const stripWith = (id) => strips.find((S) => S.m?.id === id && !S.m.disposed) || null;

  // ---------- eventos da faixa ----------
  function setOpen(S, key, v) { saveUi(key, { open: v }); render(S); }
  function setMenu(S, v, back) {
    if (S.menuOpen === v) return;
    S.menuOpen = v; S.el.menu.hidden = !v;
    S.host.querySelector('[data-t=new]')?.setAttribute('aria-expanded', String(v));
    if (v) {
      const ctx = S.cfg.ctx(); if (!ctx) return;
      const items = [{ label: ctx.wt ? 'Shell na pasta do worktree' : 'Shell na pasta do projeto', folder: ctx.folder }, ...ctx.repos.map((r) => ({ label: `Shell em ${r.name}`, folder: r.root }))];
      S.el.menu.innerHTML = items.map((it) => `<button type="button" role="menuitem" data-t="open" data-folder="${u.esc(it.folder)}" title="${u.esc(it.folder)}">${ic('terminal')}<span>${u.esc(it.label)}</span></button>`).join('');
      S.el.menu.querySelector('[role=menuitem]')?.focus();
    } else { S.el.menu.innerHTML = ''; if (back) S.host.querySelector('[data-t=new]')?.focus(); }
  }
  async function openShell(S, folder) {
    const ctx = S.cfg.ctx(); if (!ctx) return;
    const m = S.m;
    const d = await requestSafe('termOpen', { folder, cols: m?.term.cols || 80, rows: m?.term.rows || 24 }, 'Não foi possível abrir o terminal.');
    if (!d?.terminal) return;
    upsert(d.terminal); S.active.set(ctx.key, d.terminal.id); saveUi(ctx.key, { open: true }); S.focusNext = true; render(S);
  }
  async function closeTerm(S, id) {
    const t = byId(id); if (!t) return;
    if (t.running) {
      const r = await env.confirm({ title: 'Encerrar o terminal?', body: `“${t.title}” está em execução. Encerra o terminal e tudo que foi aberto a partir dele.`, ok: 'Encerrar e fechar' });
      if (!r) return;
    }
    try { await env.request('termClose', { id }); } catch (e) { env.toast(e.message || 'Não foi possível fechar o terminal.', 'error'); return; }
    terms = terms.filter((x) => x.id !== id); local.delete(id); exited.delete(id);
    if (S.m?.id === id) unmount(S);
    render(S);
  }
  async function restartTerm(S, id) {
    try {
      const d = await env.request('termRestart', { id });
      if (d?.terminal) { upsert(d.terminal); if (S.m?.id === id) unmount(S); S.focusNext = true; render(S); }   // remontar = tela limpa + novo attach (o Core limpou o buffer)
    } catch (e) {
      // ex.: o processo salvo deste terminal foi excluído
      const r = await env.confirm({ title: 'Não foi possível reiniciar', body: `${e.message || 'O terminal não pôde ser reiniciado.'} Quer fechar a aba deste terminal?`, ok: 'Fechar aba', danger: false });
      if (r) closeTerm(S, id);
    }
  }
  function onClick(S, e) {
    const b = e.target.closest('[data-t]'); if (!b || !S.host.contains(b) || b.disabled) return;
    const ctx = S.cfg.ctx(), key = ctx?.key, d = b.dataset;
    switch (d.t) {
      case 'tab': S.active.set(key, d.id); S.tabSig = ''; S.focusNext = true; render(S); break;
      case 'close': closeTerm(S, d.id); break;
      case 'close-active': { const id = S.active.get(key); if (id) closeTerm(S, id); break; }
      case 'new': setMenu(S, !S.menuOpen, true); break;
      case 'open': setMenu(S, false); openShell(S, d.folder); break;
      case 'restart': { const id = S.active.get(key); if (id) restartTerm(S, id); break; }
      case 'clear': S.m?.term.clear(); S.m?.term.focus(); break;
      case 'toggle': S.focusNext = !getUi(key).open; setOpen(S, key, !getUi(key).open); break;
      default:
    }
  }
  function onTabKey(S, e) {
    const tabs = [...S.el.tabs.querySelectorAll('[role=tab]')], i = tabs.indexOf(document.activeElement); if (i < 0) return;
    const key = S.ctxKey;
    if (['ArrowRight', 'ArrowLeft', 'Home', 'End'].includes(e.key)) {
      e.preventDefault();
      const n = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
      S.active.set(key, tabs[n].dataset.id); S.tabSig = ''; render(S); S.el.tabs.querySelectorAll('[role=tab]')[n]?.focus();
    } else if (e.key === 'Delete') { e.preventDefault(); closeTerm(S, tabs[i].dataset.id); }
  }
  function bindResize(S) {
    const rz = S.el.resize;
    const apply = (h) => { const key = S.ctxKey; if (!key) return; const max = Math.max(MIN_H, (S.host.parentElement?.clientHeight || 600) - 150); const v = Math.max(MIN_H, Math.min(max, Math.round(h))); saveUi(key, { h: v }); S.host.style.height = v + 'px'; rz.setAttribute('aria-valuenow', String(v)); };
    rz.addEventListener('pointerdown', (e) => {
      e.preventDefault(); rz.setPointerCapture(e.pointerId); rz.classList.add('drag');
      const y0 = e.clientY, h0 = S.host.getBoundingClientRect().height;
      const mv = (ev) => apply(h0 + (y0 - ev.clientY));
      const up = () => { rz.classList.remove('drag'); for (const t of ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture']) rz.removeEventListener(t, t === 'pointermove' ? mv : up); };
      rz.addEventListener('pointermove', mv); rz.addEventListener('pointerup', up); rz.addEventListener('pointercancel', up); rz.addEventListener('lostpointercapture', up);
    });
    rz.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
      e.preventDefault(); apply(S.host.getBoundingClientRect().height + (e.key === 'ArrowUp' ? 24 : -24));
    });
  }

  // ---------- API ----------
  function sync() { if (env) strips.forEach(render); }
  function onSnapshot(snap) {
    if (!env) return;
    setTerms(Array.isArray(snap?.terminals) ? snap.terminals : []);
    if (pendingReveal) {
      const p = pendingReveal, t = p.id ? byId(p.id) : terms.find((x) => x.savedId === p.savedId);
      if (t) { pendingReveal = null; reveal(t.id); } else if (Date.now() - p.at > 6000) pendingReveal = null;
    }
    sync();
  }
  function onPush(m) {
    const p = m.payload || {};
    if (m.type === 'termOutput') {
      const S = stripWith(p.id); if (!S) return;
      const mm = S.m, item = { data: String(p.data ?? ''), seq: Number(p.seq) };
      if (mm.attaching) mm.pending.push(item); else applyOutput(mm, item);
    } else if (m.type === 'termExit') {
      const t = byId(p.id); if (!t) return;
      exited.set(t.id, { startedAt: t.startedAt, pid: t.pid, exitCode: p.exitCode ?? null });
      upsert({ ...t, running: false, exitCode: p.exitCode ?? null });
      stripWith(p.id)?.m.term.write(`\r\n\x1b[2m[processo finalizado com código ${p.exitCode ?? '?'}]\x1b[0m\r\n`);
      sync();
    }
  }
  // mostra o terminal na faixa do projeto dono (abrindo a aba) ou na faixa de terminais avulsos
  function reveal(id) {
    const t = byId(id);
    if (!t) { pendingReveal = { id, at: Date.now() }; return; }
    const tab = ownerTab(t);
    if (tab) {
      env.showTab(tab);
      const S = strips[0], key = norm(tab);
      S.active.set(key, id); saveUi(key, { open: true }); S.focusNext = true;
      sync();
      S.host.scrollIntoView({ block: 'nearest' });
    } else {
      env.showLoose();
      const S = strips[1];
      S.active.set('loose', id); saveUi('loose', { open: true }); S.focusNext = true;
      sync();
    }
  }
  // "Abrir terminal" do card de processo salvo
  async function openForSaved(s) {
    if (s.terminalId && byId(s.terminalId)) { reveal(s.terminalId); return; }
    if (!s.workingDirectory) { env.toast('Este processo salvo não tem pasta de trabalho.', 'error'); return; }
    const d = await requestSafe('termOpen', { folder: s.workingDirectory, cols: 80, rows: 24 }, 'Não foi possível abrir o terminal.');
    if (d?.terminal) { upsert(d.terminal); reveal(d.terminal.id); }
  }
  // shell numa pasta (ex.: worktree) e mostra na aba dona
  async function openShellIn(folder) {
    const d = await requestSafe('termOpen', { folder, cols: 80, rows: 24 }, 'Não foi possível abrir o terminal.');
    if (d?.terminal) { upsert(d.terminal); reveal(d.terminal.id); }
  }
  // roda o comando salvo do repo principal dentro de um worktree
  async function runSavedIn(savedId, worktreePath) {
    const d = await requestSafe('termRunSavedIn', { savedId, worktreePath, cols: 80, rows: 24 }, 'Não foi possível rodar o comando no worktree.');
    if (d?.terminal) { upsert(d.terminal); reveal(d.terminal.id); }
  }
  function revealSaved(savedId) {
    const t = terms.find((x) => x.savedId === savedId);
    if (t) reveal(t.id); else pendingReveal = { id: null, savedId, at: Date.now() };
  }

  function init(e) {
    env = e; u = e.u; ic = e.icon;
    createStrip($('#termDock'), { canNew: true, ctx: () => env.projectCtx(), visible: () => env.projectVisible(), belongs: (t, ctx) => owns(ctx.folder, t) && !env.wtFolders().some((f) => norm(f) !== norm(ctx.folder) && within(t.folder, f)) });
    createStrip($('#termLoose'), { canNew: false, ctx: () => ({ key: 'loose', folder: '', repos: [] }), visible: () => env.looseVisible() && terms.some((t) => !ownerTab(t)), belongs: (t) => !ownerTab(t) });
    // tema claro/escuro (atributo data-theme ou preferência do sistema)
    const retheme = () => strips.forEach((S) => { if (S.m && !S.m.disposed) S.m.term.options.theme = theme(); });
    new MutationObserver(retheme).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', retheme);
    document.addEventListener('pointerdown', (ev) => { for (const S of strips) if (S.menuOpen && !ev.target.closest('.ts-menu') && !ev.target.closest('[data-t=new]')) setMenu(S, false); });
  }
  const countIn = (path) => terms.filter((t) => t.running && within(t.folder, path)).length;
  window.Terminals = { countIn, init, sync, onSnapshot, onPush, reveal, revealSaved, openForSaved, openShellIn, runSavedIn };
})();
