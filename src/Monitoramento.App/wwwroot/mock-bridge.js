'use strict';
/* Simula o host (WebView2) para abrir a interface direto no navegador. Não é usado dentro do app. */
(() => {
  const MB = 1024 * 1024, GB = 1024 * MB, TOTAL = 32 * GB, BASE_USED = 6.2 * GB;
  const U = 'C:\\Users\\ricardo.martinez\\';
  const R = (min, max) => min + Math.random() * (max - min);
  const hash = (s) => { let h = 5381; for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0; return h.toString(36); };

  const WS = U + 'Desktop\\Projetos\\workspace-gsintegra';
  const WTROOT = WS + '.worktrees', WT_X = WTROOT + '\\api-feature-x', WT_OLD = WTROOT + '\\api-old-spike', WT_LOCK = WTROOT + '\\api-hotfix';
  const repos = [
    { repoRoot: U + 'Desktop\\Projetos\\gsintegra', repoName: 'gsintegra', branch: 'chore/vitest', dirtyCount: 14, ahead: 2, behind: 0 },
    { repoRoot: U + 'Documents\\Projetos\\Monitoramento', repoName: 'Monitoramento', branch: 'main', dirtyCount: 3, ahead: 0, behind: 0 },
    { repoRoot: U + 'Projetos\\dfc', repoName: 'dfc', branch: 'feat/fluxo-realizado', dirtyCount: 0, ahead: 0, behind: 3 },
    { repoRoot: U + 'Projetos\\leadia', repoName: 'leadia', branch: 'a1b2c3d', dirtyCount: 6, ahead: null, behind: null },
    { repoRoot: U + 'Projetos\\vida-ativa', repoName: 'vida-ativa', branch: 'main', dirtyCount: 0, ahead: 0, behind: 0 },
    { repoRoot: U + 'Projetos\\quebrado', repoName: 'quebrado', branch: 'main', dirtyCount: null, ahead: null, behind: null },
    { repoRoot: WS + '\\app', repoName: 'app', branch: 'chore/vitest', dirtyCount: 5, ahead: 1, behind: 0 },
    { repoRoot: WS + '\\api', repoName: 'api', branch: 'main', dirtyCount: 0, ahead: 0, behind: 2 },
    { repoRoot: WT_X, repoName: 'api', branch: 'feature/x', dirtyCount: 2, ahead: 1, behind: 0, mainRepoRoot: WS + '\\api' },
    { repoRoot: WT_LOCK, repoName: 'api', branch: 'hotfix/vt', dirtyCount: 0, ahead: 0, behind: 0, mainRepoRoot: WS + '\\api' },
    { repoRoot: U + 'Projetos\\protegido', repoName: 'protegido', branch: 'main', dirtyCount: null, ahead: null, behind: null, statusNote: 'Status do git não lido por segurança: o repositório tem filtro, fsmonitor ou include na configuração.' },
  ];
  const gitFor = (cwd) => { if (!cwd) return null; const r = repos.find((x) => cwd.toLowerCase().startsWith(x.repoRoot.toLowerCase())); return r ? { mainRepoRoot: null, ...r } : null; };

  // workspace que não é repo, com repositórios nas subpastas (como gsintegra → app, api)
  const subReposFor = (cwd) => (cwd && cwd.toLowerCase() === WS.toLowerCase() ? repos.filter((r) => r.repoRoot.startsWith(WS + '\\')).map((r) => ({ ...r })) : []);

  // ---------- git simulado (estado em memória; layout de trilhas calculado aqui, como o Core faria) ----------
  const shaOf = (seed) => { let h = 2166136261, out = ''; for (let r = 0; r < 5; r++) { for (let i = 0; i < seed.length; i++) { h ^= seed.charCodeAt(i) + r; h = Math.imul(h, 16777619) >>> 0; } out += h.toString(16).padStart(8, '0'); } return out; };
  const rng = (a) => () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; };
  const HOUR = 3600e3;
  const AUTH = ['Ricardo Martinez', 'Ana Souza', 'Bruno Lima', 'Carla Dias'];
  const SUBJ = ['feat(api): endpoint de relatório por competência', 'fix(app): corrige foco no modal de confirmação', 'refactor(api): extrai cálculo de VT para o calculator', 'test(api): cobre desconto de benefício por sindicato', 'chore: atualiza dependências', 'docs: atualiza BUSINESS_RULES do faturamento', 'fix(api): escopo companyId na listagem de postos', 'feat(app): coluna de situação na grade', 'perf(api): cache dos totais pendentes', 'style(app): ajusta densidade da tabela', 'fix(rdm): fila de pendente de envio', 'feat(mobile): tela de cobertura'];
  const HASH7 = (sha) => sha.slice(0, 7);
  function buildModel(def) {
    const idmap = new Map(), commits = [];
    def.list.forEach((e) => idmap.set(e[0], shaOf(def.name + e[0])));
    def.list.forEach(([id, ps, subject, author, hours]) => commits.push({ sha: idmap.get(id), parents: ps.map((p) => idmap.get(p)).filter(Boolean), subject, author, date: new Date(Date.now() - hours * HOUR).toISOString() }));
    const at = (id) => idmap.get(id);
    const m = { name: def.name, commits, branches: {}, remoteBranches: {}, tags: {}, upstream: {}, remotes: ['origin'], changes: def.changes, merging: false, mergeHead: null, conflictOn: def.conflictOn || [], strictOn: def.strictOn || null, head: { branch: def.head.branch, detached: !def.head.branch, sha: at(def.head.id) ?? null }, fetched: false, error: def.error || null, statusNote: def.statusNote || null };
    for (const [k, v] of Object.entries(def.branches || {})) m.branches[k] = at(v);
    for (const [k, v] of Object.entries(def.remoteBranches || {})) m.remoteBranches[k] = at(v);
    for (const [k, v] of Object.entries(def.tags || {})) m.tags[k] = at(v);
    Object.assign(m.upstream, def.upstream || {});
    if (def.merging) { m.merging = true; m.mergeHead = at(def.merging); }
    return m;
  }
  const chg = (path, index, worktree, extra = {}) => ({ path, origPath: null, index, worktree, conflict: false, ...extra });
  function gsiDef() {
    const rnd = rng(7), list = [
      ['c01', ['c02'], 'test(app): migra specs de fakeAsync para vi.useFakeTimers', AUTH[0], 0.3], ['c02', ['c04'], 'chore(app): remove Karma e jasmine do projeto', AUTH[0], 1.5],
      ['c03', ['c05', 'c09'], 'Merge pull request #212 de feat/dfc-fluxo', AUTH[1], 2.2], ['c04', ['c06'], 'chore(app): configura Vitest com Chromium', AUTH[0], 3],
      ['c05', ['c07'], 'fix(api): escopo companyId no relatório de faturas', AUTH[2], 4], ['c09', ['c10'], 'feat(dfc): fluxo realizado por filial', AUTH[3], 5],
      ['c06', ['c08'], 'chore(app): atualiza Angular para 22.0.1', AUTH[0], 6], ['c07', ['c08'], 'feat(api): cache Redis dos totais pendentes', AUTH[2], 7],
      ['c10', ['c12'], 'feat(dfc): sincronização do DW (fase 7)', AUTH[3], 9], ['c08', ['m2'], 'perf(app): lista de faturamento em duas fases', AUTH[1], 11],
      ['m2', ['c14', 'f1'], "Merge branch 'fix/billing-vt' into main", AUTH[1], 13], ['f1', ['c15'], 'fix(api): desconto VT por linha usa fração', AUTH[2], 15],
      ['c14', ['c12'], 'docs: regras do RDM atualizadas', AUTH[1], 17], ['c15', ['c12'], 'test(api): regressão do VT negativo', AUTH[2], 19],
      ['c12', ['c16'], 'feat(app): design system etapa 2', AUTH[0], 24], ['r1', ['c16'], 'chore(release): 2.8.0', AUTH[1], 26], ['c16', ['c17'], 'fix(api): migration de billing_date', AUTH[0], 30],
      ['c17', ['c18'], 'chore(release): 2.7.3', AUTH[1], 40], ['c18', ['c19'], 'fix(app): grade do faturamento recalculava tudo', AUTH[2], 52], ['c19', ['s0'], 'feat(api): MCP do Integra (OAuth 2.1)', AUTH[0], 70],
    ];
    let h = 80, i = 0;
    const s = (n) => 's' + n, subj = () => SUBJ[Math.floor(rnd() * SUBJ.length)], au = () => AUTH[Math.floor(rnd() * AUTH.length)];
    while (i < 700) {
      h += 0.5 + rnd() * 18;
      if (i % 11 === 5 && i + 3 < 700) {
        list.push([s(i), [s(i + 1), s(i + 2)], `Merge branch 'topic/${i}' into main`, au(), h], [s(i + 1), [s(i + 3)], subj(), au(), h + 1], [s(i + 2), [s(i + 3)], subj(), au(), h + 2]); i += 3;
      } else { list.push([s(i), [s(i + 1)], subj(), au(), h]); i++; }
    }
    return {
      name: 'gsintegra', list, head: { branch: 'chore/vitest', id: 'c01' },
      branches: { 'chore/vitest': 'c01', main: 'c03', 'feat/dfc-fluxo': 'c09', 'fix/billing-vt': 'f1', 'release/2026-11': 'r1', 'chore/lint-api': 'c06' },
      remoteBranches: { 'origin/main': 'c03', 'origin/feat/dfc-fluxo': 'c09', 'origin/fix/billing-vt': 'f1', 'origin/release/2026-11': 'r1', 'origin/chore/vitest': 'c04' },
      tags: { 'v2.8.0': 'r1', 'v2.7.3': 'c17' },
      upstream: { 'chore/vitest': 'origin/chore/vitest', main: 'origin/main', 'feat/dfc-fluxo': 'origin/feat/dfc-fluxo', 'fix/billing-vt': 'origin/fix/billing-vt', 'release/2026-11': 'origin/release/2026-11' },
      conflictOn: ['feat/dfc-fluxo'], strictOn: 'release/2026-11',
      changes: [
        chg('app/src/app/pages/faturamento/faturamento.component.ts', 'M', 'M'), chg('api/src/invoices/invoice.service.ts', 'M', null), chg('api/src/invoices/dto/create-invoice.dto.ts', 'A', null),
        chg('app/src/app/pages/faturamento/BUSINESS_RULES.md', null, 'M'), chg('app/src/styles.scss', null, 'M'), chg('app/package.json', null, 'M'), chg('api/prisma/schema.prisma', null, 'M'),
        chg('api/src/legacy/old-report.ts', null, 'D'), chg('app/src/app/core/services/base.service.ts', 'R', null, { origPath: 'app/src/app/core/base.service.ts' }), chg('docs/API.md', null, 'M'),
        chg('app/src/app/pages/vendido/vendido.component.html', null, 'M'), chg('app/src/app/shared/ui/tabela.component.css', null, 'M'), chg('scratch/notas.txt', null, '?'), chg('api/src/invoices/invoice.calculator.spec.ts', null, '?'),
      ],
    };
  }
  const lin = (name, subs, extra) => ({ name, list: subs.map((t, i) => ['k' + i, i + 1 < subs.length ? ['k' + (i + 1)] : [], t, AUTH[i % 4], 1 + i * 9]), ...extra });
  function otherDefs() {
    const mon = lin('Monitoramento', ['feat(ui): aba Git no monitor', 'fix(core): cache de ícones por exePath', 'feat(core): GitInfo com statusNote', 'chore: CONTRATO.md revisado', 'feat(core): salvos com Iniciar e Parar', 'feat(ui): drawer de detalhes', 'chore: estrutura da solução', 'Commit inicial']);
    Object.assign(mon, { head: { branch: 'main', id: 'k0' }, branches: { main: 'k0' }, remoteBranches: { 'origin/main': 'k0' }, upstream: { main: 'origin/main' }, tags: { 'v0.1.0': 'k6' }, changes: [chg('src/Monitoramento.App/wwwroot/app.js', null, 'M'), chg('src/Monitoramento.App/wwwroot/git-view.js', null, '?'), chg('docs/CONTRATO.md', null, 'M')] });
    const dfc = lin('dfc', ['feat(fluxo): realizado por filial', 'feat(dw): sincronização incremental', 'fix(api): fuso horário das datas', 'feat(app): tela de fluxo previsto', 'chore: Prisma 7', 'feat(api): módulo de contas', 'chore: Angular + Nest', 'Commit inicial']);
    dfc.list.unshift(['r2', ['r1'], 'fix(dw): fecha conexão ao fim da carga', AUTH[1], 0.5], ['r1', ['r0'], 'feat(fluxo): agrupa por conta', AUTH[3], 1], ['r0', ['k0'], 'chore(dw): ajusta tamanho do lote', AUTH[3], 1.5]);
    Object.assign(dfc, { head: { branch: 'feat/fluxo-realizado', id: 'k0' }, branches: { 'feat/fluxo-realizado': 'k0', main: 'k3' }, remoteBranches: { 'origin/feat/fluxo-realizado': 'r2', 'origin/main': 'k3' }, upstream: { 'feat/fluxo-realizado': 'origin/feat/fluxo-realizado', main: 'origin/main' }, changes: [] });
    const lead = { name: 'leadia', list: [['a1', ['a2'], 'feat(auth): login com Google', AUTH[0], 2], ['a2', ['a4'], 'feat(leads): funil por etapa', AUTH[0], 6], ['m1', ['a3', 'a5'], "Merge branch 'fix/import' into main", AUTH[1], 9], ['a3', ['a6'], 'refactor(leads): renomeia serviço de importação', AUTH[1], 12], ['a5', ['a6'], 'fix(import): CSV com BOM', AUTH[2], 14], ['a4', ['a6'], 'chore: Prisma generate no postinstall', AUTH[0], 15], ['a6', ['a7'], 'feat(leads): listagem paginada', AUTH[3], 30], ['a7', ['a8'], 'chore: Docker compose local', AUTH[3], 50], ['a8', [], 'Commit inicial', AUTH[3], 80]] };
    Object.assign(lead, { head: { id: 'a1', branch: null }, branches: { main: 'm1', 'feat/auth': 'a1' }, remoteBranches: { 'origin/main': 'm1', 'origin/feat/auth': 'a2' }, upstream: { main: 'origin/main' }, merging: 'a5',
      changes: [chg('src/auth/google.strategy.ts', 'U', 'U', { conflict: true }), chg('src/leads/leads.service.ts', 'U', 'U', { conflict: true }), chg('src/leads/import.service.ts', 'R', null, { origPath: 'src/leads/importer.service.ts' }), chg('prisma/schema.prisma', 'M', null), chg('.env.local', null, '?'), chg('tmp/amostra.csv', null, '?')] });
    const vida = lin('vida-ativa', ['feat: agenda de aulas', 'fix: fuso do check-in', 'feat: cadastro de alunos', 'chore: estrutura inicial']);
    Object.assign(vida, { head: { branch: 'main', id: 'k0' }, branches: { main: 'k0', develop: 'k1' }, remoteBranches: { 'origin/main': 'k0', 'origin/develop': 'k1' }, upstream: { main: 'origin/main', develop: 'origin/develop' }, changes: [] });
    const prot = lin('protegido', ['feat: relatório mensal', 'chore: scripts de build', 'Commit inicial']);
    Object.assign(prot, { head: { branch: 'main', id: 'k0' }, branches: { main: 'k0' }, remoteBranches: { 'origin/main': 'k0' }, upstream: { main: 'origin/main' }, changes: null, statusNote: 'Status do git não lido por segurança: o repositório tem filtro, fsmonitor ou include na configuração.' });
    const bad = { name: 'quebrado', list: [], head: { branch: 'main', id: 'zz' }, changes: null, error: 'Não foi possível ler o histórico: o git excedeu o tempo limite de 5 s (repositório muito grande ou disco lento).' };
    return [mon, dfc, lead, vida, prot, bad];
  }
  const models = new Map();
  const wsApp = lin('app', ['feat(vitest): migra specs do Karma', 'chore: Angular 22', 'feat(faturamento): grade em duas fases', 'Commit inicial']);
  Object.assign(wsApp, { head: { branch: 'chore/vitest', id: 'k0' }, branches: { 'chore/vitest': 'k0', main: 'k2' }, remoteBranches: { 'origin/main': 'k2', 'origin/chore/vitest': 'k1' }, upstream: { 'chore/vitest': 'origin/chore/vitest', main: 'origin/main' }, changes: [chg('src/app/app.config.ts', null, 'M'), chg('vitest.config.ts', 'A', null), chg('src/test-setup.ts', null, '?'), chg('package.json', null, 'M'), chg('angular.json', null, 'M')] });
  const wsApi = lin('api', ['fix(invoices): arredonda centavos', 'feat(mcp): catálogo de consultas', 'chore: Prisma 7', 'Commit inicial']);
  Object.assign(wsApi, { head: { branch: 'main', id: 'k2' }, branches: { main: 'k2', 'feature/x': 'k0', 'hotfix/vt': 'k1', 'feature/rdm': 'k1' }, remoteBranches: { 'origin/main': 'k0' }, upstream: { main: 'origin/main' }, changes: [] });
  const rootOf = (name) => repos.find((r) => r.repoName === name).repoRoot;
  [gsiDef(), ...otherDefs()].forEach((d) => models.set(rootOf(d.name), buildModel(d)));
  models.set(WS + '\\app', buildModel(wsApp)); models.set(WS + '\\api', buildModel(wsApi));
  const wtModel = (name, branch, changes) => { const d = lin('api', ['feat(vt): desconto por linha', 'fix(invoices): arredonda centavos', 'Commit inicial']); Object.assign(d, { head: { branch, id: 'k0' }, branches: { [branch]: 'k0', main: 'k1' }, remoteBranches: { 'origin/main': 'k1' }, upstream: {}, changes }); return buildModel(d); };
  models.set(WT_X, wtModel('api', 'feature/x', [chg('src/invoices/invoice.calculator.ts', null, 'M'), chg('.env.local', null, '?')]));
  models.set(WT_LOCK, wtModel('api', 'hotfix/vt', []));
  const mainEntry = (root) => ({ path: root, branch: (models.get(root)?.head.branch) ?? null, head: (models.get(root)?.head.sha) ?? '', isMain: true, detached: false, locked: false, lockReason: null, prunable: false, dirtyCount: null });
  const WTREES = new Map([[WS + '\\api', [
    mainEntry(WS + '\\api'),
    { path: WT_X, branch: 'feature/x', head: 'a1b2c3d', isMain: false, detached: false, locked: false, lockReason: null, prunable: false, dirtyCount: 2 },
    { path: WT_LOCK, branch: 'hotfix/vt', head: 'e4f5a6b', isMain: false, detached: false, locked: true, lockReason: 'Em uso pelo CI local', prunable: false, dirtyCount: 0 },
    { path: WT_OLD, branch: 'old/spike', head: '9c8d7e6', isMain: false, detached: false, locked: false, lockReason: null, prunable: true, dirtyCount: null },
  ]]]);

  const byS = (m) => new Map(m.commits.map((c) => [c.sha, c]));
  function reach(m, tip) { const idx = byS(m), seen = new Set(), st = [tip]; while (st.length) { const s = st.pop(); if (!s || seen.has(s) || !idx.has(s)) continue; seen.add(s); st.push(...idx.get(s).parents); } return seen; }
  const tipOf = (m, name) => m.branches[name] ?? m.remoteBranches[name] ?? null;
  const countAB = (m, a, b) => { if (!a || !b) return [null, null]; const ra = reach(m, a), rb = reach(m, b); let ah = 0, be = 0; ra.forEach((x) => { if (!rb.has(x)) ah++; }); rb.forEach((x) => { if (!ra.has(x)) be++; }); return [ah, be]; };
  function layout(list) {
    const lanes = []; let max = 0;
    const rows = list.map((c) => {
      let L = lanes.indexOf(c.sha); const exp = []; lanes.forEach((e, i) => { if (e === c.sha) exp.push(i); });
      if (L < 0) { L = lanes.indexOf(null); if (L < 0) { L = lanes.length; lanes.push(null); } }
      const pass = []; lanes.forEach((e, i) => { if (e !== null && e !== c.sha) pass.push(i); });
      exp.forEach((i) => { lanes[i] = null; });
      const edges = [];
      c.parents.forEach((p, pi) => {
        let t = lanes.indexOf(p);
        if (t < 0) { if (pi === 0 && lanes[L] === null) t = L; else { t = lanes.indexOf(null); if (t < 0) { t = lanes.length; lanes.push(null); } } lanes[t] = p; }
        edges.push({ fromLane: L, toLane: t, toSha: p, kind: pi ? 'merge' : 'parent' });
      });
      while (lanes.length && lanes[lanes.length - 1] === null) lanes.pop();
      max = Math.max(max, lanes.length, L + 1);
      return { lane: L, edges, passThrough: pass };
    });
    return { rows, lanes: max };
  }
  function details(m, repoRoot, maxCommits) {
    const info = repos.find((r) => r.repoRoot === repoRoot);
    if (m.error) return { repo: { ...info, statusNote: null }, head: { branch: m.head.branch, detached: false, sha: null, upstream: null }, branches: [], changes: null, commits: [], graph: { lanes: 0 }, truncated: false, error: m.error, merging: false, rebasing: false, remotes: m.remotes, stagedCount: 0, unstagedCount: 0, untrackedCount: 0, conflictCount: 0 };
    const idx = byS(m), max = Math.min(Math.max(maxCommits || 150, 1), 500);
    const refsBy = new Map(), addRef = (sha, r) => { if (!refsBy.has(sha)) refsBy.set(sha, []); refsBy.get(sha).push(r); };
    addRef(m.head.sha, { name: 'HEAD', kind: 'head' });
    Object.entries(m.branches).forEach(([n, s]) => addRef(s, { name: n, kind: 'branch' }));
    Object.entries(m.remoteBranches).forEach(([n, s]) => addRef(s, { name: n, kind: 'remote' }));
    Object.entries(m.tags).forEach(([n, s]) => addRef(s, { name: n, kind: 'tag' }));
    const all = m.commits, slice = all.slice(0, max), lay = layout(slice);
    const commits = slice.map((c, i) => ({ sha: c.sha, shortSha: HASH7(c.sha), parents: c.parents, subject: c.subject, author: c.author, date: c.date, refs: refsBy.get(c.sha) || [], ...lay.rows[i] }));
    const upName = m.head.branch ? m.upstream[m.head.branch] ?? null : null;
    const [ah, be] = upName && m.head.branch ? countAB(m, m.head.sha, m.remoteBranches[upName]) : [null, null];
    const branches = [];
    Object.entries(m.branches).forEach(([n, s]) => { const u = m.upstream[n] ?? null; const [a, b] = u ? countAB(m, s, m.remoteBranches[u]) : [null, null]; branches.push({ name: n, current: n === m.head.branch, remote: false, worktreePath: wtPathOf(repoRoot, n), upstream: u, ahead: a, behind: b, sha: s, lastCommitDate: idx.get(s)?.date ?? new Date().toISOString() }); });
    Object.entries(m.remoteBranches).forEach(([n, s]) => branches.push({ name: n, current: false, remote: true, upstream: null, ahead: null, behind: null, sha: s, lastCommitDate: idx.get(s)?.date ?? new Date().toISOString() }));
    const ch = m.changes;
    return {
      repo: { mainRepoRoot: info?.mainRepoRoot ?? null, repoRoot, repoName: m.name, branch: m.head.branch ?? HASH7(m.head.sha), dirtyCount: ch ? ch.length : null, ahead: ah, behind: be, statusNote: m.statusNote },
      head: { branch: m.head.branch, detached: m.head.detached, sha: m.head.sha, upstream: upName }, branches, changes: ch ? ch.map((c) => ({ ...c })) : null, commits, graph: { lanes: lay.lanes },
      truncated: all.length > max, error: null, merging: m.merging, rebasing: false, remotes: m.remotes.slice(), worktrees: worktreesFor(repoRoot),
      stagedCount: ch ? ch.filter((c) => c.index && !c.conflict).length : 0, unstagedCount: ch ? ch.filter((c) => c.worktree && c.worktree !== '?' && !c.conflict).length : 0,
      untrackedCount: ch ? ch.filter((c) => c.worktree === '?').length : 0, conflictCount: ch ? ch.filter((c) => c.conflict).length : 0,
    };
  }
  function syncRepos() {
    models.forEach((m, root) => {
      if (m.error || m.changes === null) return;
      const r = repos.find((x) => x.repoRoot === root), up = m.head.branch ? m.upstream[m.head.branch] : null;
      const [a, b] = up ? countAB(m, m.head.sha, m.remoteBranches[up]) : [null, null];
      r.branch = m.head.branch ?? HASH7(m.head.sha); r.dirtyCount = m.changes.length; r.ahead = a; r.behind = b;
    });
  }
  const gitModel = (repoRoot) => { const m = models.get(repoRoot); if (!m) throw new Error('Repositório não reconhecido: só é possível abrir repositórios detectados num processo ou salvo.'); return m; };
  const need = (m, what) => { if (m.error) throw new Error(`${what}: não foi possível ler o repositório.`); if (m.changes === null) throw new Error(`${what}: o status deste repositório não foi lido por segurança (ver aviso do painel).`); };
  const advance = (m, sha) => { m.head.sha = sha; if (m.head.branch) m.branches[m.head.branch] = sha; };
  function newCommit(m, subject, parents) {
    const c = { sha: shaOf(m.name + subject + Date.now() + Math.random()), parents, subject, author: AUTH[0], date: new Date().toISOString() };
    m.commits.unshift(c); return c;
  }
  const pathsSel = (m, paths) => (paths && paths.length ? m.changes.filter((c) => paths.includes(c.path)) : m.changes.slice());
  const done = (m, repoRoot, output) => { syncRepos(); return { details: details(m, repoRoot, 150), output }; };
  const gitActions = {
    gitStage: ({ repoRoot, paths }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao preparar');
      pathsSel(m, paths).forEach((c) => { if (c.conflict) { c.conflict = false; c.index = 'M'; c.worktree = null; return; } if (!c.worktree) return; c.index = c.worktree === '?' ? 'A' : c.worktree === 'D' ? 'D' : (c.index === 'R' ? 'R' : 'M'); c.worktree = null; });
      return done(m, repoRoot, null);
    },
    gitUnstage: ({ repoRoot, paths }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao tirar da preparação');
      pathsSel(m, paths).forEach((c) => { if (!c.index || c.conflict) return; const w = c.index === 'A' ? '?' : c.index === 'D' ? 'D' : 'M'; c.worktree = c.worktree || w; c.index = null; });
      return done(m, repoRoot, null);
    },
    gitDiscard: ({ repoRoot, paths }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao descartar');
      pathsSel(m, paths).filter((c) => c.worktree).forEach((c) => { c.worktree = null; if (!c.index) m.changes.splice(m.changes.indexOf(c), 1); });
      return done(m, repoRoot, null);
    },
    gitCommit: ({ repoRoot, message, all, amend, push }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao fazer o commit');
      if (!String(message || '').trim()) throw new Error('A mensagem do commit está vazia.');
      if (all) gitActions.gitStage({ repoRoot, paths: [] });
      if (m.changes.some((c) => c.conflict)) throw new Error('Falha ao fazer o commit: resolva os conflitos antes (U) e prepare os arquivos.');
      if (!m.changes.some((c) => c.index) && !amend && !m.merging) throw new Error('Falha ao fazer o commit: não há mudanças preparadas.');
      const subject = message.trim().split('\n')[0];
      m.changes.forEach((c) => { c.index = null; }); m.changes = m.changes.filter((c) => c.worktree);
      let c;
      if (m.merging) { c = newCommit(m, subject, [m.head.sha, m.mergeHead]); m.merging = false; m.mergeHead = null; }
      else if (amend) { const cur = m.commits.find((x) => x.sha === m.head.sha); m.commits.splice(m.commits.indexOf(cur), 1); c = newCommit(m, subject, cur.parents.slice()); Object.entries(m.branches).forEach(([k, v]) => { if (v === cur.sha) m.branches[k] = c.sha; }); }
      else c = newCommit(m, subject, [m.head.sha]);
      advance(m, c.sha);
      let out = `[${m.head.branch ?? 'HEAD destacado'} ${HASH7(c.sha)}] ${subject}`;
      if (push) out += '\n' + gitActions.gitPush({ repoRoot }).output;
      return done(m, repoRoot, out);
    },
    gitCheckout: ({ repoRoot, branch, create, startPoint }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao trocar de branch');
      if (m.merging) throw new Error('Falha ao trocar de branch: há um merge em andamento. Conclua ou aborte o merge antes.');
      if (create) {
        if (!/^[\w][\w./-]*$/.test(branch || '') || branch.endsWith('/') || branch.includes('..')) throw new Error(`Falha ao criar a branch: "${branch}" não é um nome de branch válido.`);
        if (m.branches[branch]) throw new Error(`Falha ao criar a branch: já existe uma branch chamada "${branch}".`);
        const from = startPoint ? tipOf(m, startPoint) : m.head.sha; if (!from) throw new Error(`Falha ao criar a branch: "${startPoint}" não existe.`);
        m.branches[branch] = from; m.head = { branch, detached: false, sha: from };
        return done(m, repoRoot, `Alternado para uma nova branch "${branch}"`);
      }
      let name = branch, tip = m.branches[name];
      if (tip === undefined && m.remoteBranches[branch]) { name = branch.replace(/^[^/]+\//, ''); if (!m.branches[name]) { m.branches[name] = m.remoteBranches[branch]; m.upstream[name] = branch; } tip = m.branches[name]; }
      if (tip === undefined) throw new Error(`Falha ao trocar de branch: "${branch}" não existe.`);
      if (m.strictOn && name === m.strictOn && m.changes.some((c) => c.worktree && c.worktree !== '?')) {
        const f = m.changes.filter((c) => c.worktree && c.worktree !== '?').slice(0, 2).map((c) => '\t' + c.path).join('\n');
        throw new Error(`Falha ao trocar de branch: error: Your local changes to the following files would be overwritten by checkout:\n${f}\nPlease commit your changes or stash them before you switch branches.`);
      }
      m.head = { branch: name, detached: false, sha: tip };
      return done(m, repoRoot, `Alternado para a branch "${name}"`);
    },
    gitMerge: ({ repoRoot, branch }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao mesclar');
      const tip = tipOf(m, branch); if (!tip) throw new Error(`Falha ao mesclar: "${branch}" não existe.`);
      if (m.merging) throw new Error('Falha ao mesclar: já há um merge em andamento.');
      if (reach(m, m.head.sha).has(tip)) return done(m, repoRoot, 'Já está atualizado.');
      if (reach(m, tip).has(m.head.sha)) { advance(m, tip); return done(m, repoRoot, `Fast-forward para ${HASH7(tip)}`); }
      if (m.conflictOn.includes(branch) || m.conflictOn.includes(branch.replace(/^origin\//, ''))) {
        m.merging = true; m.mergeHead = tip;
        ['api/src/invoices/invoice.service.ts', 'app/src/app/pages/faturamento/faturamento.component.ts'].forEach((p) => { const c = m.changes.find((x) => x.path === p); if (c) { c.conflict = true; c.index = 'U'; c.worktree = 'U'; } else m.changes.unshift(chg(p, 'U', 'U', { conflict: true })); });
        return done(m, repoRoot, `CONFLITO (conteúdo): conflito de mesclagem em ${m.changes.filter((c) => c.conflict).map((c) => c.path).join(', ')}\nA mesclagem automática falhou; corrija os conflitos e faça o commit do resultado.`);
      }
      if (m.changes.some((c) => c.index)) throw new Error('Falha ao mesclar: há mudanças preparadas; faça commit ou tire-as da preparação.');
      const c = newCommit(m, `Merge branch '${branch}' into ${m.head.branch ?? 'HEAD'}`, [m.head.sha, tip]); advance(m, c.sha);
      return done(m, repoRoot, "Merge feito pela estratégia 'ort'.");
    },
    gitMergeAbort: ({ repoRoot }) => {
      const m = gitModel(repoRoot); if (!m.merging) throw new Error('Falha ao abortar o merge: não há merge em andamento.');
      m.changes = m.changes.filter((c) => !c.conflict); m.merging = false; m.mergeHead = null; return done(m, repoRoot, 'Merge abortado.');
    },
    gitFetch: ({ repoRoot }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao buscar');
      if (!m.fetched && m.name === 'gsintegra') { m.fetched = true; const c = newCommit(m, 'fix(api): ajuste de timeout do relatório (de origin/main)', [m.remoteBranches['origin/main']]); m.remoteBranches['origin/main'] = c.sha; return done(m, repoRoot, `From origin\n   ${HASH7(c.parents[0])}..${HASH7(c.sha)}  main -> origin/main`); }
      return done(m, repoRoot, 'Nenhuma novidade no remoto.');
    },
    gitPull: ({ repoRoot }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao atualizar');
      const up = m.head.branch && m.upstream[m.head.branch]; if (!up) throw new Error('Falha ao atualizar: a branch atual não tem upstream.');
      const [a, b] = countAB(m, m.head.sha, m.remoteBranches[up]);
      if (!b) return done(m, repoRoot, 'Já está atualizado.');
      if (a) throw new Error('Falha ao atualizar: fatal: Not possible to fast-forward, aborting. Faça um merge manual de ' + up + '.');
      advance(m, m.remoteBranches[up]); return done(m, repoRoot, `Fast-forward: ${b} commit(s) trazido(s) de ${up}.`);
    },
    gitPush: ({ repoRoot }) => {
      const m = gitModel(repoRoot); need(m, 'Falha ao enviar');
      if (!m.head.branch) throw new Error('Falha ao enviar: HEAD destacado; crie uma branch antes.');
      const up = m.upstream[m.head.branch] ?? `origin/${m.head.branch}`;
      const [, b] = m.remoteBranches[up] ? countAB(m, m.head.sha, m.remoteBranches[up]) : [0, 0];
      if (b) throw new Error('Falha ao enviar: ! [rejected] (non-fast-forward). Atualize (pull) antes de enviar.');
      const first = !m.upstream[m.head.branch]; m.upstream[m.head.branch] = up; m.remoteBranches[up] = m.head.sha;
      return done(m, repoRoot, (first ? `branch '${m.head.branch}' configurada para acompanhar '${up}'.\n` : '') + `To origin\n   ${HASH7(m.head.sha)}  ${m.head.branch} -> ${m.head.branch}`);
    },
  };

  let pidSeq = 1000;
  const nextPid = () => (pidSeq += Math.floor(R(4, 60)) * 4);
  const groups = [];
  function mk(exe, name, path, list, opts = {}) {
    const g = { exe, name, path, procs: [], hist: [], denied: !!opts.denied };
    list.forEach(([mb, cpu, cwd, args, o = {}]) => g.procs.push(mkProc(g, mb, cpu, cwd, args, o)));
    groups.push(g); return g;
  }
  function mkProc(g, mb, cpu, cwd, args, o = {}) {
    const denied = g.denied || o.denied;
    const cmd = args === null ? null : `"${g.path}"${args ? ' ' + args : ''}`;
    return { pid: o.pid ?? nextPid(), ppid: o.ppid ?? 1000, name: g.exe, exePath: g.path, cmd: denied ? null : cmd, cwd: denied ? null : cwd, mem: mb * MB, cpu, denied: !!denied, isGit: !!o.isGit, start: Date.now() - R(60, 9000) * 1000, savedId: null };
  }
  const P = (s) => U + s;
  const gsi = P('Desktop\\Projetos\\gsintegra'), mon = P('Documents\\Projetos\\Monitoramento'), dfc = P('Projetos\\dfc'), lead = P('Projetos\\leadia');
  const NODE = 'C:\\Program Files\\nodejs\\node.exe';

  mk('Code.exe', 'Visual Studio Code', P('AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe'), [
    [520, 4, gsi, gsi], [410, 6, gsi, '--type=extensionHost'], [260, 2, gsi, '--type=renderer'], [210, 1, mon, mon], [180, 3, dfc, dfc], [150, 1, gsi, '--type=gpu-process'], [120, 0.4, null, '--type=utility'], [90, 0.2, mon, '--type=fileWatcher'], [70, 0.1, gsi, '--type=ptyHost']]);
  mk('node.exe', 'Node.js JavaScript Runtime', NODE, [
    [640, 9, gsi + '\\api', 'node_modules\\@nestjs\\cli\\bin\\nest.js start --watch'], [480, 5, gsi + '\\app', 'node_modules\\@angular\\cli\\bin\\ng.js serve'],
    [310, 2, gsi + '\\mobile', 'node_modules\\expo\\bin\\cli start'], [260, 1, dfc, 'node_modules\\vite\\bin\\vite.js'], [190, 3, lead, 'server.js'], [120, 1, P('Projetos\\vida-ativa'), 'node_modules\\vite\\bin\\vite.js'], [70, 0.1, P('Projetos\\quebrado'), 'index.js'], [95, 0.1, mon, 'tools\\watch.mjs & echo ok'], [150, 2, WS, 'tools\\dev-all.mjs'], [210, 4, WT_X, 'node_modules\\@nestjs\\cli\\bin\\nest.js start --watch']]);
  mk('chrome.exe', 'Google Chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', [
    [620, 3, null, '--profile-directory=Default'], [420, 5, null, '--type=renderer'], [330, 2, null, '--type=renderer'], [280, 1, null, '--type=gpu-process'], [210, 0.6, null, '--type=renderer'], [190, 0.4, null, '--type=renderer'], [160, 0.2, null, '--type=renderer'], [140, 0.9, null, '--type=utility'], [120, 0.1, null, '--type=renderer'], [100, 0.1, null, '--type=renderer'], [90, 0.1, null, '--type=renderer'], [80, 0, null, '--type=renderer'], [60, 0, null, '--type=crashpad'], [55, 0, null, '--type=utility']]);
  mk('msedgewebview2.exe', 'Microsoft Edge WebView2', 'C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\msedgewebview2.exe', [
    [210, 1, null, '--webview-exe-name=Teams.exe'], [180, 0.6, null, '--type=renderer'], [140, 0.4, null, '--type=gpu-process'], [120, 0.2, null, '--type=renderer'], [100, 0.1, null, '--type=utility'], [80, 0.1, null, '--type=renderer'], [70, 0, null, '--type=renderer'], [50, 0, null, '--type=crashpad']]);
  mk('Teams.exe', 'Microsoft Teams', P('AppData\\Local\\Microsoft\\Teams\\current\\Teams.exe'), [[380, 2, null, ''], [220, 1, null, '--type=renderer'], [150, 0.5, null, '--type=gpu']]);
  mk('docker desktop.exe', 'Docker Desktop', 'C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe', [[310, 1.2, null, ''], [240, 0.8, null, '--backend']]);
  mk('vmmemWSL', 'Máquina virtual do WSL', 'C:\\Windows\\System32\\vmmemWSL', [[2100, 3, null, null]], { denied: true });
  mk('postgres.exe', 'PostgreSQL Server', 'C:\\Program Files\\PostgreSQL\\16\\bin\\postgres.exe', [[110, 0.4, 'C:\\Program Files\\PostgreSQL\\16\\data', '-D "C:\\Program Files\\PostgreSQL\\16\\data"'], [60, 0, 'C:\\Program Files\\PostgreSQL\\16\\data', ': checkpointer'], [48, 0, 'C:\\Program Files\\PostgreSQL\\16\\data', ': walwriter']]);
  mk('dotnet.exe', '.NET Host', 'C:\\Program Files\\dotnet\\dotnet.exe', [[230, 4, mon + '\\src\\Monitoramento.App', 'watch run'], [180, 1, mon + '\\src\\Monitoramento.Core', 'build-server']]);
  mk('Rider.exe', 'JetBrains Rider', 'C:\\Program Files\\JetBrains\\Rider\\Rider.exe', [[900, 3, P('Projetos\\protegido'), '']]);
  mk('git.exe', 'Git', 'C:\\Program Files\\Git\\cmd\\git.exe', [[14, 1.4, gsi, 'status --porcelain=v2 --branch', { isGit: true }], [9, 0, dfc, 'fetch --all', { isGit: true }]]);
  mk('WindowsTerminal.exe', 'Windows Terminal', 'C:\\Program Files\\WindowsApps\\Microsoft.WindowsTerminal\\WindowsTerminal.exe', [[190, 0.8, null, '']]);
  mk('powershell.exe', 'Windows PowerShell', 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe', [[95, 0, gsi, '-NoLogo'], [88, 0, mon, '-NoLogo']]);
  mk('OUTLOOK.EXE', 'Microsoft Outlook', 'C:\\Program Files\\Microsoft Office\\root\\Office16\\OUTLOOK.EXE', [[430, 1, null, '']]);
  mk('EXCEL.EXE', 'Microsoft Excel', 'C:\\Program Files\\Microsoft Office\\root\\Office16\\EXCEL.EXE', [[360, 0.4, P('Documents'), '"relatorio-faturamento.xlsx"']]);
  mk('Spotify.exe', 'Spotify', P('AppData\\Roaming\\Spotify\\Spotify.exe'), [[250, 1.2, null, ''], [150, 0.3, null, '--type=renderer'], [80, 0, null, '--type=gpu']]);
  mk('Ssms.exe', 'SQL Server Management Studio', 'C:\\Program Files (x86)\\Microsoft SQL Server Management Studio 20\\Common7\\IDE\\Ssms.exe', [[520, 0.5, null, '']]);
  mk('explorer.exe', 'Windows Explorer', 'C:\\Windows\\explorer.exe', [[190, 0.8, null, '']]);
  mk('svchost.exe', 'Processo host para Serviços do Windows', 'C:\\Windows\\System32\\svchost.exe', [[85, 0.4, null, '-k netsvcs -p'], [64, 0.1, null, '-k LocalService'], [52, 0, null, '-k DcomLaunch'], [47, 0.1, null, '-k NetworkService'], [40, 0, null, '-k utcsvc'], [33, 0, null, '-k wsappx']], { denied: true });
  mk('MsMpEng.exe', 'Antimalware Service Executable', 'C:\\ProgramData\\Microsoft\\Windows Defender\\Platform\\MsMpEng.exe', [[310, 2.5, null, null]], { denied: true });
  mk('Slack.exe', 'Slack', P('AppData\\Local\\slack\\slack.exe'), [[290, 1, null, ''], [130, 0.2, null, '--type=renderer']]);
  mk('Figma.exe', 'Figma', P('AppData\\Local\\Figma\\Figma.exe'), [[270, 0.8, null, '']]);
  mk('claude.exe', 'Claude Code', P('.local\\bin\\claude.exe'), [[240, 6, gsi, '--resume'], [210, 1, mon, '']]);
  mk('System', 'Sistema', 'C:\\Windows\\System32\\ntoskrnl.exe', [[12, 0.5, null, null, { pid: 4, ppid: 0 }]], { denied: true });
  mk('OneDrive.exe', 'Microsoft OneDrive', 'C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe', [[130, 0.2, null, '/background']]);

  // índice de pids para os salvos
  const find = (exe, cwdPart) => groups.find((g) => g.exe === exe).procs.find((p) => (p.cwd || '').includes(cwdPart));
  const saved = [
    { id: 'a1', name: 'API Integra (dev)', command: 'npm run start:dev', workingDirectory: gsi + '\\api', env: { NODE_ENV: 'development', PORT: '3001' }, launchMode: 'terminal', notes: 'Sobe a API NestJS com recarga automática.', running: true, pids: [find('node.exe', 'api').pid], startedAt: null, lastError: null },
    { id: 'b2', name: 'Front Angular', command: 'npm start', workingDirectory: gsi + '\\app', env: {}, launchMode: 'window', notes: null, running: false, pids: [], startedAt: null, lastError: null },
    { id: 'c3', name: 'Banco local (Docker)', command: 'docker compose up -d postgres', workingDirectory: gsi, env: {}, launchMode: 'terminal', notes: null, running: false, pids: [], startedAt: null, lastError: 'Não foi possível iniciar: o Docker Desktop não está em execução.' },
    { id: 'f6', name: 'API (workspace)', command: 'npm run start:dev', workingDirectory: WS + '\\api', env: {}, launchMode: 'terminal', notes: null, running: false, pids: [], startedAt: null, lastError: null },
    { id: 'g7', name: 'Front (workspace)', command: 'npm start', workingDirectory: WS + '\\app', env: {}, launchMode: 'window', notes: null, running: false, pids: [], startedAt: null, lastError: null },
    { id: 'e5', name: 'Dev de tudo (workspace)', command: 'node tools/dev-all.mjs', workingDirectory: WS, env: {}, launchMode: 'terminal', notes: null, running: false, pids: [], startedAt: null, lastError: null },
    { id: 'd4', name: 'Monitoramento (watch)', command: 'dotnet watch run --project src/Monitoramento.App', workingDirectory: mon, env: { DOTNET_ENVIRONMENT: 'Development' }, launchMode: 'window', notes: 'Observações de teste.', running: true, pids: [find('dotnet.exe', 'App').pid], startedAt: null, lastError: null },
  ];
  saved.forEach((s) => s.pids.forEach((pid) => { for (const g of groups) for (const p of g.procs) if (p.pid === pid) { p.savedId = s.id; s.startedAt = new Date(p.start).toISOString(); } }));
  const config = { favorites: ['app:code.exe'], saved: [], settings: { refreshIntervalMs: 2000, minimizeToTray: true, theme: 'system' } };
  const firstNode = groups.find((g) => g.exe === 'node.exe').procs[1];
  config.favorites.push('cmd:' + hash((firstNode.exePath + firstNode.cmd).toLowerCase()));

  // histórico da máquina (10 min)
  const mhist = []; let mp = 58, cp = 14;
  for (let i = 300; i > 0; i--) { mp = Math.min(92, Math.max(40, mp + R(-1.2, 1.3))); cp = Math.min(80, Math.max(3, cp + R(-6, 6))); mhist.push({ t: Date.now() - i * 2000, memoryPercent: mp, cpuPercent: cp }); }

  const favKey = (p) => 'cmd:' + hash(((p.exePath || '') + (p.cmd || p.name)).toLowerCase());
  const savedMem = (s) => { let m = 0, c = 0; for (const g of groups) for (const p of g.procs) if (s.pids.includes(p.pid)) { m += p.mem; c += p.cpu; } return { memoryBytes: m, cpuPercent: c }; };

  function tick(quiet) {
    for (const g of groups) {
      for (const p of g.procs) { p.mem = Math.max(4 * MB, p.mem * (1 + R(-0.012, 0.014))); p.base = p.base ?? p.cpu; p.cpu = Math.max(0, p.base * R(0.3, 1.7) * (p.base > 1 ? 1 : 0.8) + R(-0.1, 0.2)); }
      g.hist.push(g.procs.reduce((a, p) => a + p.mem, 0)); if (g.hist.length > 60) g.hist.shift();
    }
    const used = BASE_USED + groups.reduce((a, g) => a + g.procs.reduce((b, p) => b + p.mem, 0), 0);
    mp = Math.min(100, (used / TOTAL) * 100); cp = Math.max(1, Math.min(95, cp + R(-5, 5)));
    if (!quiet) { mhist.push({ t: Date.now(), memoryPercent: mp, cpuPercent: cp }); if (mhist.length > 300) mhist.shift(); }
    syncRepos();
  }
  for (let i = 0; i < 60; i++) tick(true);

  function snapshot() {
    initTerms();
    const favs = new Set(config.favorites);
    const gs = groups.filter((g) => g.procs.length).map((g) => {
      const procs = g.procs.slice().sort((a, b) => b.mem - a.mem).map((p) => ({
        pid: p.pid, parentPid: p.ppid, name: p.name, exePath: p.exePath, commandLine: p.cmd, workingDirectory: p.cwd, memoryBytes: p.mem, memoryPercent: (p.mem / TOTAL) * 100, cpuPercent: p.cpu,
        startTime: p.denied ? null : new Date(p.start).toISOString(), accessDenied: p.denied, isGitProcess: p.isGit, git: gitFor(p.cwd), subRepos: gitFor(p.cwd) ? [] : subReposFor(p.cwd), favoriteKey: favKey(p), favorite: favs.has(favKey(p)), savedId: p.savedId,
      }));
      const key = 'app:' + g.exe.toLowerCase(), mem = procs.reduce((a, p) => a + p.memoryBytes, 0), seen = new Map();
      procs.forEach((p) => { if (p.git && !seen.has(p.git.repoRoot)) seen.set(p.git.repoRoot, p.git); (p.subRepos || []).forEach((r) => { if (!seen.has(r.repoRoot)) seen.set(r.repoRoot, r); }); });
      return { key, name: g.name, exeName: g.exe, exePath: g.path, iconKey: g.path.toLowerCase(), memoryBytes: mem, memoryPercent: (mem / TOTAL) * 100, cpuPercent: procs.reduce((a, p) => a + p.cpuPercent, 0), processCount: procs.length, favorite: favs.has(key), repos: [...seen.values()], memoryHistory: g.hist.slice(), processes: procs };
    }).sort((a, b) => b.memoryBytes - a.memoryBytes);
    return {
      timestamp: new Date().toISOString(),
      machine: { totalMemoryBytes: TOTAL, usedMemoryBytes: (mp / 100) * TOTAL, memoryPercent: mp, cpuPercent: cp, logicalCores: 16, processCount: groups.reduce((a, g) => a + g.procs.length, 0), history: mhist.slice() },
      groups: gs, saved: saved.map(savedState), terminals: [...TERMS.values()].map((t) => ({ ...t.info, ...(window.__mockStale === t.info.id ? { running: true, exitCode: null } : {}) })),   // gancho de teste: snapshot atrasado
    };
  }
  const savedState = (s) => ({ ...s, terminalId: savedTerm(s.id)?.info.id ?? null, env: { ...s.env }, ...savedMem(s), git: gitFor(s.workingDirectory), subRepos: gitFor(s.workingDirectory) ? [] : subReposFor(s.workingDirectory) });

  const iconColors = ['#2f6fdd', '#d9531e', '#2a9d6e', '#8a4fd0', '#c4912b', '#cf3b6a'];
  function iconFor(key) {
    if (/node\.exe|svchost|msmpeng|system|vmmem|git\.exe/.test(key)) return null;
    const c = iconColors[parseInt(hash(key), 36) % iconColors.length], ch = (key.split('\\').pop()[0] || '?').toUpperCase();
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="7" fill="${c}"/><text x="16" y="22" font-family="Segoe UI,Arial" font-size="17" font-weight="700" text-anchor="middle" fill="#fff">${ch}</text></svg>`;
    return 'data:image/svg+xml;utf8,' + encodeURIComponent(svg);
  }
  const procByPid = (pid) => { for (const g of groups) for (const p of g.procs) if (p.pid === pid) return { g, p }; return null; };
  function removeProc(pid) { for (const g of groups) { const i = g.procs.findIndex((p) => p.pid === pid); if (i >= 0) { g.procs.splice(i, 1); } } saved.forEach((s) => { s.pids = s.pids.filter((x) => x !== pid); if (!s.pids.length) { s.running = false; s.startedAt = null; } }); }

  // ---------- projetos e espaço de trabalho git ----------
  const BSL = String.fromCharCode(92);
  const norm = (p) => String(p || '').split('/').join(BSL).replace(/\+$/, '').toLowerCase();
  const within = (path, folder) => { const a = norm(path), b = norm(folder); return !!a && (a === b || a.startsWith(b + BSL)); };
  const parentOf = (p) => { const t = String(p).replace(/\+$/, ''); return t.slice(0, t.lastIndexOf(BSL)); };
  const reposUnder = (folder) => repos.filter((r) => within(r.repoRoot, folder)).map((r) => r.repoRoot);
  const projectsDb = [
    { id: 'p1', name: 'workspace-gsintegra', folder: WS }, { id: 'p2', name: 'gsintegra', folder: gsi }, { id: 'p3', name: 'Projetos pessoais', folder: U + 'Projetos' },
  ];
  const projOut = (p) => ({ id: p.id, name: p.name, folder: p.folder, repos: reposUnder(p.folder) });
  const wsDb = { docked: false, dockWidthPct: 42, tabs: [], active: null };
  const infosOf = (roots) => roots.map((r) => ({ ...repos.find((x) => x.repoRoot === r) }));
  const baseOf = (p) => String(p).slice(String(p).lastIndexOf(BSL) + 1);
  function draft(path) {
    const saved = projectsDb.find((p) => norm(p.folder) === norm(path));
    if (saved) { const o = projOut(saved); return { project: o, repos: infosOf(o.repos) }; }
    const repo = repos.find((r) => norm(r.repoRoot) === norm(path));
    let folder = null;
    if (repo) { const parent = parentOf(repo.repoRoot); folder = reposUnder(parent).length >= 2 ? parent : repo.repoRoot; }
    else if (reposUnder(path).length) folder = path;
    else if (groups.some((g) => g.procs.some((p) => p.cwd && norm(p.cwd) === norm(path)))) { const r = repos.find((x) => within(path, x.repoRoot)); folder = r ? r.repoRoot : null; }
    if (!folder) throw new Error('Esta pasta não é um repositório git detectado nem contém repositórios. Abra um repositório que apareça nos processos.');
    const roots = reposUnder(folder);
    return { project: { id: '', name: baseOf(folder), folder, repos: roots }, repos: infosOf(roots) };
  }
  function cleanWorkspace(w) {
    const known = new Set(repos.map((r) => r.repoRoot));
    const tabs = [];
    for (const t of (Array.isArray(w?.tabs) ? w.tabs : [])) {
      if (tabs.length >= 12 || !t?.folder || tabs.some((x) => norm(x.folder) === norm(t.folder))) continue;
      const rs = (t.repos || []).filter((r) => known.has(r));
      tabs.push({ folder: t.folder, name: String(t.name || baseOf(t.folder)), repos: rs, projectId: projectsDb.some((p) => p.id === t.projectId) ? t.projectId : null, activeRepo: rs.includes(t.activeRepo) ? t.activeRepo : (rs[0] ?? null), worktreeOf: t.worktreeOf && known.has(t.worktreeOf) ? t.worktreeOf : null });
    }
    return { docked: !!w?.docked, dockWidthPct: Math.max(25, Math.min(70, Number(w?.dockWidthPct) || 42)), tabs, active: tabs.some((t) => norm(t.folder) === norm(w?.active)) ? w.active : (tabs[0]?.folder ?? null) };
  }

  // ---------- terminais simulados ----------
  const ESC = String.fromCharCode(27), TERMS = new Map();
  let termN = 0, termsInit = false;
  const col = (n, t) => ESC + '[' + n + 'm' + t + ESC + '[0m';
  const projectFolderOf = (folder) => (projectsDb.find((p) => within(folder, p.folder)) || {}).folder ?? null;
  const promptOf = (t) => col('32', 'PS') + ' ' + col('36', t.info.folder) + col('90', '>') + ' ';
  // seq = total de caracteres produzidos até o fim de data; nunca volta a zero (nem no restart)
  function termPush(t, data) {
    for (let i = 0; i < data.length; i += 32768) {
      const part = data.slice(i, i + 32768);
      t.seq += part.length; t.buf = (t.buf + part).slice(-40000);
      if (window.__mockDropOutput > 0) { window.__mockDropOutput--; continue; }   // gancho de teste: simula perda de mensagens
      emit({ type: 'termOutput', payload: { id: t.info.id, data: part, seq: t.seq } });
    }
  }
  const clampSize = (cols, rows) => [Math.max(20, Math.min(500, Number(cols) || 120)), Math.max(5, Math.min(200, Number(rows) || 30))];
  const LOGS = [['32', 'INFO', 'GET /api/invoices?competence=2026-10 200'], ['32', 'INFO', 'GET /api/postos 200'], ['33', 'WARN', 'cache de totais pendentes expirou; recalculando'], ['32', 'INFO', 'POST /api/rdm/coberturas 201'], ['31', 'ERRO', 'Nexti: tempo limite excedido (tentativa 2/3)'], ['36', 'DEBUG', 'prisma:query SELECT "id" FROM "invoice" WHERE "company_id" = $1']];
  function startDev(t) {
    let n = 0;
    clearInterval(t.timer);
    t.timer = setInterval(() => { const [c, lvl, msg] = LOGS[n++ % LOGS.length]; termPush(t, col('90', new Date().toLocaleTimeString('pt-BR')) + ' ' + col(c, lvl.padEnd(5)) + ' ' + msg + ' ' + col('90', Math.round(R(2, 180)) + 'ms') + '\r\n'); }, 1100);
  }
  function exitTerm(t, code) {
    if (!t.info.running) return;
    clearInterval(t.timer); t.info.running = false; t.info.exitCode = code;
    emit({ type: 'termExit', payload: { id: t.info.id, exitCode: code } });
  }
  function makeTerm(folder, o = {}) {
    const info = { id: 't' + (++termN), title: o.title || 'powershell · ' + baseOf(folder), folder, projectFolder: projectFolderOf(folder), savedId: o.savedId || null, pid: nextPid(), running: true, exitCode: null, startedAt: new Date().toISOString(), cols: 80, rows: 24 };
    const t = { info, buf: '', seq: 0, line: '', timer: null, dev: !!o.dev };
    TERMS.set(info.id, t);
    boot(t);
    return t;
  }
  function boot(t) {
    t.buf = ''; t.line = '';
    if (t.dev) { termPush(t, col('1', '> ' + (t.info.title)) + '\r\n' + col('90', 'iniciando em ' + t.info.folder) + '\r\n'); startDev(t); }
    else termPush(t, col('90', 'Windows PowerShell (simulado)') + '\r\n' + promptOf(t));
  }
  function runLine(t, line) {
    const [cmd, ...args] = line.trim().split(/\s+/);
    if (!cmd) return t.dev ? '' : promptOf(t);
    if (cmd === 'exit') { exitTerm(t, Number(args[0]) || 0); return ''; }
    if (t.dev) return '';
    if (cmd === 'clear') return ESC + '[2J' + ESC + '[H' + promptOf(t);
    if (cmd === 'ls' || cmd === 'dir') return col('34;1', 'app') + '  ' + col('34;1', 'api') + '  ' + col('34;1', 'docs') + '  package.json  ' + col('32', 'build.ps1') + '\r\n' + promptOf(t);
    if (cmd === 'echo') return args.join(' ') + '\r\n' + promptOf(t);
    return col('31', cmd + ': comando não encontrado') + '\r\n' + promptOf(t);
  }
  function termInput(t, data) {
    if (!t.info.running) return;
    let out = '';
    for (const ch of data) {
      if (ch === '\r') { out += '\r\n' + runLine(t, t.line); t.line = ''; if (!t.info.running) break; }
      else if (ch === '\n') continue;
      else if (ch === String.fromCharCode(3)) { out += '^C\r\n'; t.line = ''; if (t.dev) { termPush(t, out); exitTerm(t, 130); return; } out += promptOf(t); }
      else if (ch === String.fromCharCode(127)) { if (t.line) { t.line = t.line.slice(0, -1); out += '\b \b'; } }
      else if (ch >= ' ') { t.line += ch; out += ch; }
    }
    if (out) termPush(t, out);
  }
  const savedTerm = (id) => [...TERMS.values()].find((t) => t.info.savedId === id) || null;
  function ensureSavedTerm(s) {
    let t = savedTerm(s.id);
    if (!t) t = makeTerm(s.workingDirectory || U, { title: s.name, savedId: s.id, dev: true });
    else if (!t.info.running) { t.info.running = true; t.info.exitCode = null; boot(t); }
    return t;
  }
  function initTerms() {
    if (termsInit) return; termsInit = true;
    saved.filter((x) => x.running && x.launchMode === 'terminal').forEach((x) => ensureSavedTerm(x));
  }
  const termById = (id) => { const t = TERMS.get(id); if (!t) throw new Error('Terminal não encontrado (já foi fechado).'); return t; };
  const termHandlers = {
    termOpen: ({ folder, cols, rows, confirmUnsafe }) => { if (!folder) throw new Error('Informe a pasta do terminal.'); unsafeCheck(folder, confirmUnsafe); const t = makeTerm(folder); [t.info.cols, t.info.rows] = clampSize(cols, rows); return { terminal: { ...t.info } }; },
    termInput: ({ id, data }) => { if (String(data).length > 65536) throw new Error('Entrada grande demais.'); termInput(termById(id), String(data)); return {}; },
    termResize: ({ id, cols, rows }) => { const t = termById(id); [t.info.cols, t.info.rows] = clampSize(cols, rows); return {}; },
    termAttach: ({ id }) => { const t = termById(id); return { data: t.buf, seq: t.seq }; },
    termRestart: ({ id }) => { const t = termById(id); if (t.info.savedId && !saved.some((x) => x.id === t.info.savedId)) throw new Error('O processo salvo deste terminal foi excluído; não dá para reiniciar.'); if (t.info.running) exitTerm(t, 0); t.info.running = true; t.info.exitCode = null; t.info.startedAt = new Date().toISOString(); boot(t); return { terminal: { ...t.info } }; },
    termClose: ({ id }) => { const t = termById(id); clearInterval(t.timer); TERMS.delete(id); return {}; },
  };

  // ---------- worktrees simulados ----------
  const mainOf = (root) => repos.find((r) => r.repoRoot === root)?.mainRepoRoot || root;
  function worktreesFor(root) {
    const main = mainOf(root), list = WTREES.get(main) || [mainEntry(main)];
    return list.map((w) => { const r = repos.find((x) => x.repoRoot === w.path); return { ...w, dirtyCount: w.prunable ? null : (r ? r.dirtyCount : w.dirtyCount) }; });
  }
  const wtPathOf = (root, branch) => (worktreesFor(root).find((w) => w.branch === branch && w.path !== root) || {}).path ?? null;
  const inside = (path) => [...groups.flatMap((g) => g.procs.filter((p) => p.cwd && within(p.cwd, path)).map((p) => p.name + ' (PID ' + p.pid + ')')), ...[...TERMS.values()].filter((t) => t.info.running && within(t.info.folder, path)).map((t) => t.info.title)];
  const codeErr = (msg, code) => Object.assign(new Error(msg), { code });
  const unsafeCheck = (folder, confirmUnsafe) => { if (!confirmUnsafe && within(folder, U + 'Projetos\\protegido')) throw codeErr('O repositório "protegido" tem configuração que pode executar programas (filtro, fsmonitor ou include). Abrir o terminal mesmo assim?', 'unsafeRepo'); };
  const defaultWtPath = (main, name) => { const proj = (projectsDb.find((p) => within(main, p.folder)) || {}).folder || parentOf(main); return proj + '.worktrees' + BSL + baseOf(main) + '-' + String(name).replace(/^(origin|upstream)\//, '').replace(/[^\w.-]+/g, '-'); };
  const wtHandlers = {
    gitWorktreeAdd: ({ repoRoot, branch, newBranch, startPoint, path, copyEnv }) => {
      const m = gitModel(repoRoot), main = mainOf(repoRoot), list = WTREES.get(main) || [mainEntry(main)];
      if (branch && newBranch) throw new Error('Informe só a branch existente ou o nome da nova branch.');
      const name = newBranch || branch; if (!name) throw new Error('Informe a branch do worktree.');
      if (startPoint && !tipOf(m, startPoint)) throw new Error('O ponto de partida "' + startPoint + '" não existe entre as branches do repositório.');
      if (path && !/^[A-Za-z]:\\/.test(path)) throw new Error('Caminho do worktree inválido: use um caminho absoluto de uma unidade local.');
      if (!path && !projectsDb.some((p) => within(main, p.folder)) && /^[A-Za-z]:$/.test(parentOf(main))) throw new Error('Caminho do worktree inválido: informe a pasta (o repositório está na raiz da unidade e não pertence a um projeto salvo).');
      const used = list.find((w) => w.branch === name);
      if (used) throw new Error('A branch "' + name + '" já está em uso no worktree ' + used.path + '.');
      if (newBranch && m.branches[newBranch]) throw new Error('Já existe uma branch chamada "' + newBranch + '".');
      if (branch && !m.branches[branch]) throw new Error('A branch "' + branch + '" não existe.');
      const dest = path || defaultWtPath(main, name);
      if (list.some((w) => norm(w.path) === norm(dest))) throw new Error('Já existe um worktree nessa pasta.');
      const c = { ...m, commits: m.commits.slice(), branches: { ...m.branches }, remoteBranches: { ...m.remoteBranches }, tags: { ...m.tags }, upstream: {}, changes: [], merging: false, mergeHead: null };
      if (newBranch) { const tip = startPoint ? tipOf(m, startPoint) : m.head.sha; if (!tip) throw new Error('"' + startPoint + '" não existe.'); c.branches[newBranch] = tip; }
      c.head = { branch: name, detached: false, sha: c.branches[name] };
      repos.push({ repoRoot: dest, repoName: baseOf(main), branch: name, dirtyCount: 0, ahead: 0, behind: 0, mainRepoRoot: main });
      models.set(dest, c);
      const w = { path: dest, branch: name, head: HASH7(c.head.sha), isMain: false, detached: false, locked: false, lockReason: null, prunable: false, dirtyCount: 0 };
      list.push(w); WTREES.set(main, list);
      if (newBranch) m.branches[newBranch] = c.branches[newBranch];
      return { worktree: { ...w }, details: details(m, main, 1), copied: copyEnv ? ['.env', 'config/.env.local'] : [] };
    },
    gitWorktreeRemove: ({ repoRoot, path, force }) => {
      if (mainOf(repoRoot) !== repoRoot) throw new Error('Remova o worktree pelo repositório principal (repoRoot deve ser o repositório principal).');
      const main = repoRoot, list = WTREES.get(main) || [], w = list.find((x) => norm(x.path) === norm(path));
      if (!w) throw new Error('Worktree não encontrado.');
      if (w.isMain) throw new Error('O worktree principal não pode ser removido.');
      if (w.locked) throw new Error('O worktree está bloqueado (' + (w.lockReason || 'sem motivo') + '). Desbloqueie antes de remover.');
      const run = inside(w.path); if (run.length) throw codeErr('Há processos em uso dentro deste worktree. Em uso por: ' + run.join(', ') + '.', 'worktreeInUse');
      const r = repos.find((x) => x.repoRoot === w.path);
      if (!force && r && r.dirtyCount > 0) throw codeErr('O worktree tem ' + r.dirtyCount + ' arquivo(s) com mudanças não confirmadas. Remover descarta essas mudanças.', 'worktreeDirty');
      WTREES.set(main, list.filter((x) => x !== w));
      const i = repos.findIndex((x) => x.repoRoot === w.path); if (i >= 0) repos.splice(i, 1);
      models.delete(w.path);
      return { details: details(gitModel(main), main, 1) };
    },
    gitWorktreePrune: ({ repoRoot }) => {
      const main = mainOf(repoRoot), list = WTREES.get(main) || [];
      WTREES.set(main, list.filter((w) => !w.prunable));
      return { details: details(gitModel(main), main, 1), pruned: list.filter((w) => w.prunable).length };
    },
    termRunSavedIn: ({ savedId, worktreePath, cols, rows, confirmUnsafe }) => {
      const sv = saved.find((x) => x.id === savedId); if (!sv) throw new Error('Processo salvo não encontrado (foi excluído?).');
      unsafeCheck(worktreePath, confirmUnsafe);
      const w = worktreesFor(worktreePath).find((x) => norm(x.path) === norm(worktreePath));
      const t = makeTerm(worktreePath, { title: sv.name + ' \u2387 ' + (w?.branch || 'worktree'), dev: true });
      [t.info.cols, t.info.rows] = clampSize(cols, rows);
      return { terminal: { ...t.info } };
    },
  };

  const handlers = {
    ...termHandlers,
    ...wtHandlers,
    gitProjectDraft: ({ path }) => draft(path),
    saveGitProject: ({ project }) => {
      const name = String(project?.name || '').trim();
      if (!name) throw new Error('Informe um nome para o projeto.');
      if (!project.folder || !reposUnder(project.folder).length) throw new Error('A pasta do projeto não contém repositórios git detectados.');
      if (!project.id) { const dup = projectsDb.find((p) => norm(p.folder) === norm(project.folder)); if (dup) throw new Error('Já existe um projeto salvo para esta pasta.'); const p = { id: 'p' + Math.random().toString(16).slice(2, 8), name, folder: project.folder }; projectsDb.push(p); return { project: projOut(p) }; }
      const p = projectsDb.find((x) => x.id === project.id); if (!p) throw new Error('Projeto não encontrado.');
      p.name = name; return { project: projOut(p) };
    },
    deleteGitProject: ({ id }) => { const i = projectsDb.findIndex((p) => p.id === id); if (i < 0) throw new Error('Projeto não encontrado.'); projectsDb.splice(i, 1); return {}; },
    setGitWorkspace: ({ workspace }) => { Object.assign(wsDb, cleanWorkspace(workspace)); return { workspace: JSON.parse(JSON.stringify(wsDb)) }; },
    projectScripts: ({ folder }) => ({ repos: reposUnder(folder).map((r) => {
      const n = baseOf(r);
      if (n === 'app') return { repoRoot: r, packageManager: 'pnpm', scripts: ['build', 'lint', 'start', 'test'] };
      if (n === 'api') return { repoRoot: r, packageManager: 'npm', scripts: ['build', 'start', 'start:dev', 'test', 'lint'] };
      if (n === 'protegido') return { repoRoot: r, packageManager: null, scripts: [] };
      return { repoRoot: r, packageManager: 'npm', scripts: ['build', 'dev'] };
    }) }),
    ready: () => ({ configLoadError: new URLSearchParams(location.search).has('configError') ? 'Unexpected end of JSON input (config.json, linha 12).' : null, snapshot: snapshot(), config: { ...config, gitProjects: projectsDb.map(projOut), gitWorkspace: JSON.parse(JSON.stringify(wsDb)), saved: saved.map(({ id, name, command, workingDirectory, env, launchMode, notes }) => ({ id, name, command, workingDirectory, env, launchMode, notes })) } }),
    toggleFavorite: ({ key }) => { const i = config.favorites.indexOf(key); if (i >= 0) config.favorites.splice(i, 1); else config.favorites.push(key); return { favorites: config.favorites.slice() }; },
    getIcons: ({ keys }) => ({ icons: Object.fromEntries(keys.map((k) => [k, iconFor(k)])) }),
    saveProcess: ({ saved: s }) => {
      if (!s.name?.trim() || !s.command?.trim()) throw new Error('Informe o nome e o comando do processo.');
      if (!s.id) { s.id = Math.random().toString(16).slice(2, 10); saved.push({ ...s, running: false, pids: [], startedAt: null, lastError: null }); }
      else { const t = saved.find((x) => x.id === s.id); if (!t) throw new Error('Processo salvo não encontrado.'); Object.assign(t, s); }
      return { saved: { ...s } };
    },
    deleteSaved: ({ id }) => { const i = saved.findIndex((x) => x.id === id); if (i < 0) throw new Error('Processo salvo não encontrado.'); saved.splice(i, 1); return {}; },
    startSaved: ({ id }) => {
      const s = saved.find((x) => x.id === id); if (!s) throw new Error('Processo salvo não encontrado.');
      if (s.running) return { state: savedState(s) };
      if (id === 'c3') { s.lastError = 'Não foi possível iniciar: o Docker Desktop não está em execução.'; return { state: savedState(s) }; }
      const g = groups.find((x) => x.exe === (s.command.startsWith('dotnet') ? 'dotnet.exe' : 'node.exe'));
      const p = mkProc(g, R(80, 200), 3, s.workingDirectory, s.command); p.savedId = s.id; g.procs.push(p);
      s.running = true; s.pids = [p.pid]; s.startedAt = new Date().toISOString(); s.lastError = null;
      if (s.launchMode === 'terminal') ensureSavedTerm(s);
      return { state: savedState(s) };
    },
    stopSaved: ({ id }) => {
      const s = saved.find((x) => x.id === id); if (!s) throw new Error('Processo salvo não encontrado.');
      s.pids.slice().forEach(removeProc); s.running = false; s.pids = []; s.startedAt = null;
      const st = savedTerm(s.id); if (st) exitTerm(st, 0);
      return { state: savedState(s) };
    },
    draftFromProcess: ({ pid }) => {
      const h = procByPid(pid); if (!h) throw new Error('O processo não existe mais.');
      if (!h.p.cmd) throw new Error('Sem acesso à linha de comando deste processo.');
      const leaf = (h.p.cwd || '').split('\\').pop();
      const risky = /[&|<>^%]/.test(h.p.cmd.replace(/"[^"]*"/g, ''));
      return { warning: risky ? 'Este comando contém & | < > ^ ou % fora de aspas; revise antes de salvar, pois o shell pode interpretá-los de outra forma.' : null, saved: { id: '', name: leaf ? `${h.g.name} (${leaf})` : h.g.name, command: h.p.cmd, workingDirectory: h.p.cwd, env: {}, launchMode: 'terminal', notes: null } };
    },
    killProcess: ({ pid, startTime }) => {
      const cur = procByPid(pid); if (cur && startTime && Math.abs(new Date(startTime) - cur.p.start) > 2000) throw new Error('O PID foi reutilizado por outro processo; nada foi encerrado.');
      if (pid === 0 || pid === 4) throw new Error('Este processo é crítico para o Windows e não pode ser encerrado.');
      if (!procByPid(pid)) throw new Error('O processo já foi encerrado.');
      removeProc(pid); return { killed: 1 };
    },
    killGroup: ({ key }) => {
      const g = groups.find((x) => 'app:' + x.exe.toLowerCase() === key); if (!g) throw new Error('A aplicação já foi encerrada.');
      if (g.exe === 'System' || g.exe === 'explorer.exe') throw new Error(`${g.name} é essencial para o Windows e não pode ser encerrado.`);
      const pids = g.procs.map((p) => p.pid); pids.forEach(removeProc); return { killed: pids.length };
    },
    openFolder: ({ path }) => { if (!path) throw new Error('Pasta não informada.'); console.info('[mock] abrir pasta', path); return {}; },
    openTerminal: ({ path }) => { if (!path) throw new Error('Pasta não informada.'); console.info('[mock] abrir terminal em', path); return {}; },
    gitDetails: ({ repoRoot, maxCommits }) => ({ details: details(gitModel(repoRoot), repoRoot, maxCommits) }),
    ...gitActions,
    setSettings: ({ settings }) => { Object.assign(config.settings, settings); return { settings: { ...config.settings } }; },
  };

  const listeners = new Set();
  const emit = (data) => listeners.forEach((fn) => fn({ data: JSON.parse(JSON.stringify(data)) }));
  let timer = null;
  window.MockWebView = {
    addEventListener(type, fn) { if (type === 'message') listeners.add(fn); },
    removeEventListener(type, fn) { listeners.delete(fn); },
    postMessage(msg) {
      const h = handlers[msg.type];
      const extra = msg.type === 'setGitWorkspace' ? Number(new URLSearchParams(location.search).get('latency')) || 0 : 0;   // ?latency=ms simula host lento
      setTimeout(() => {
        if (!h) return emit({ replyTo: msg.id, ok: false, error: `Requisição desconhecida: ${msg.type}` });
        try { const data = h(msg.payload || {}); emit({ replyTo: msg.id, ok: true, data }); }
        catch (e) { emit({ replyTo: msg.id, ok: false, error: e.message, ...(e.code ? { code: e.code } : {}) }); }
        if (msg.type === 'ready' && !timer) timer = setInterval(() => { tick(); emit({ type: 'snapshot', payload: snapshot() }); }, 2000);
      }, extra + (/^git/.test(msg.type) && msg.type !== 'gitDetails' ? R(350, 700) : R(30, 120)));
    },
  };
})();
