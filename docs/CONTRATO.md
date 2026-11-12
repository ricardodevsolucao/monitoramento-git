# Monitoramento — contrato entre partes

Monitor de processos para Windows: agrupa por aplicação, mostra uso da máquina (principalmente memória),
relação com git, pasta do processo, favoritos e processos salvos com Iniciar/Parar.

Stack: **.NET 10 (net10.0-windows) + WinForms host + WebView2**. UI em HTML/CSS/JS puro (sem framework,
sem build de front). Sem porta HTTP: a UI fala com o host por `window.chrome.webview.postMessage`.
Idioma da UI e mensagens de erro: português do Brasil. Código e identificadores: inglês.

## Estrutura

```
Monitoramento/
  Monitoramento.slnx                (ou .sln)
  src/Monitoramento.Core/           class library, net10.0-windows — coleta, git, config, launcher, BridgeRouter
  src/Monitoramento.App/            WinForms exe, net10.0-windows — janela, WebView2, bandeja, timer
  src/Monitoramento.App/wwwroot/    index.html, app.css, app.js (+ mock-bridge.js para abrir no navegador)
  tests/Monitoramento.Core.Tests/   xUnit
  docs/CONTRATO.md                  este arquivo
  README.md
```

Config do usuário: `%APPDATA%\Monitoramento\config.json` (escrita atômica: arquivo temporário + replace).

## Mensagens

### UI → host
```json
{ "id": "r12", "type": "toggleFavorite", "payload": { "key": "app:code.exe" } }
```
### host → UI (resposta)
```json
{ "replyTo": "r12", "ok": true, "data": { ... } }
{ "replyTo": "r12", "ok": false, "error": "Mensagem em português" }
{ "replyTo": "r12", "ok": false, "error": "Mensagem em português", "code": "unsafeRepo" }   // code só em alguns erros
```
### host → UI (push, a cada `settings.refreshIntervalMs`, padrão 2000)
```json
{ "type": "snapshot", "payload": Snapshot }
```
### host → UI (push dos terminais embutidos, a cada ~30 ms quando há saída; ver "Terminais embutidos")
```json
{ "type": "termOutput", "payload": { "id": "a1b2c3d4e5f6", "data": "texto com sequências VT", "seq": 1234 } }
{ "type": "termExit", "payload": { "id": "a1b2c3d4e5f6", "exitCode": 0 } }
```

**Códigos de erro** (`code` na resposta `ok: false`; só aparece nestes casos — os demais erros não trazem o campo, e a
UI deve decidir pelo `code`, nunca pelo texto). O Core propaga o código de qualquer resposta de erro que o tenha.

| code | mensagem / quando | o que a UI faz |
|---|---|---|
| `unsafeRepo` | `termOpen`/`termRunSavedIn`: a config git do repositório pode executar programas | confirma e repete com `confirmUnsafe: true` |
| `worktreeDirty` | `gitWorktreeRemove` sem `force` e o worktree tem alterações | confirma e repete com `force: true` |
| `worktreeInUse` | `gitWorktreeRemove` com terminal/processo rodando dentro do worktree | mostra a lista; o usuário fecha e tenta de novo |

Mensagens acima de **1 MB** (UTF-8) são recusadas com `{ replyTo: null, ok: false, error }`. Falha inesperada
vira mensagem genérica em português (`Falha ao processar "<type>". Tente de novo.`); o detalhe vai só para o log
(`Trace`), nunca para a UI.

### Tipos de requisição

| type | payload | data da resposta |
|---|---|---|
| `ready` | `{}` | `{ snapshot: Snapshot, config: AppConfig, configLoadError: string \| null }` (UI chama ao carregar; `configLoadError` = motivo, em português, de a configuração não ter sido lida) |
| `toggleFavorite` | `{ key }` | `{ favorites: string[] }` |
| `getIcons` | `{ keys: string[] }` (iconKey) | `{ icons: { [iconKey]: "data:image/png;base64,..." \| null } }` |
| `saveProcess` | `{ saved: SavedProcess }` (id vazio = novo) | `{ saved: SavedProcess }` |
| `deleteSaved` | `{ id }` | `{}` |
| `startSaved` | `{ id }` | `{ state: SavedProcessState }` (modo "terminal": `state.terminalId` = aba do terminal; reaproveita a aba parada do mesmo salvo) |
| `stopSaved` | `{ id }` | `{ state: SavedProcessState }` |
| `draftFromProcess` | `{ pid }` | `{ saved: SavedProcess, warning: string \| null }` (rascunho preenchido com comando e pasta; não grava. `warning` em português quando a linha de comando tem `& \| < > ^ %` fora de aspas) |
| `killProcess` | `{ pid, tree: bool, startTime?: string }` | `{ killed: number }` (`startTime` = `ProcessInfo.startTime` que a UI viu; confere a identidade) |
| `killGroup` | `{ key }` | `{ killed: number }` |
| `openFolder` | `{ path }` | `{}` (abre no Explorer) |
| `openTerminal` | `{ path }` | `{}` (abre Windows Terminal `wt -d`, senão `cmd` na pasta) |
| `setSettings` | `{ settings: Settings }` | `{ settings: Settings }` |
| `gitDetails` | `{ repoRoot: string, maxCommits?: number, force?: boolean }` (padrão 150, máx. 500; `force` = atualizar agora, ignora o cache de 5 s) | `{ details: GitDetails }` — só para `repoRoot` que o app já detectou num processo/salvo ou que está fixado por projeto git salvo/aba do workspace (não aceita caminho arbitrário) |
| `gitProjectDraft` | `{ path }` | `{ project: GitProject (id ""), repos: GitInfo[] }` — rascunho, não grava (ver "Projetos e workspace git") |
| `saveGitProject` | `{ project: { id, name, folder } }` (id vazio = novo) | `{ project: GitProject }` — `repos` recalculados pelo Core |
| `deleteGitProject` | `{ id }` | `{}` |
| `setGitWorkspace` | `{ workspace: GitWorkspace }` (UI chama com debounce) | `{ workspace: GitWorkspace }` saneado e gravado |
| `projectScripts` | `{ folder }` | `{ repos: { repoRoot, packageManager: "npm" \| "pnpm" \| "yarn" \| null, scripts: string[] }[] }` — só lê, nada é executado |
| `termOpen` | `{ folder, cols, rows, confirmUnsafe?: boolean }` | `{ terminal: TerminalInfo }` — shell padrão na pasta (mesma regra de pasta do `gitProjectDraft`); repositório com config que pode executar programas → erro `code: "unsafeRepo"` até `confirmUnsafe: true` |
| `termRunSavedIn` | `{ savedId, worktreePath, cols, rows, confirmUnsafe?: boolean }` | `{ terminal: TerminalInfo }` — comando do salvo num worktree do mesmo repositório (ver "Worktrees git") |
| `gitWorktreeAdd` | `{ repoRoot, branch?, newBranch?, startPoint?, path?, copyEnv? }` | `{ worktree: GitWorktree, details: GitDetails, copied: string[] }` |
| `gitWorktreeRemove` | `{ repoRoot, path, force: boolean }` | `{ details: GitDetails }` |
| `gitWorktreePrune` | `{ repoRoot }` | `{ details: GitDetails }` |
| `termInput` | `{ id, data: string }` (≤ 64 KB em UTF-8) | `{}` — só terminal existente e rodando |
| `termResize` | `{ id, cols, rows }` (ajustados a 20..500 × 5..200) | `{}` |
| `termAttach` | `{ id }` | `{ data: string, seq: number }` — conteúdo recente (anel) para redesenhar |
| `termRestart` | `{ id }` | `{ terminal: TerminalInfo }` — mesmo comando/shell na mesma aba (anel limpo) |
| `termClose` | `{ id }` | `{}` — encerra a árvore (se rodando) e remove o terminal |

## Visão Git (árvore, mudanças e branch)

Ao clicar num processo/salvo/chip com git, o repositório abre na aba **Git**, que mostra **até 4 repositórios ao
mesmo tempo** em painéis (1 = tela cheia; 2 = lado a lado; 3 = dois em cima e um embaixo ocupando a largura, ou 3
colunas em tela larga; 4 = grade 2×2). Abrir um 5º substitui o painel aberto há mais tempo que não esteja fixado
(o usuário pode fixar um painel). As abas abertas e o painel fixado à direita persistem no `config.json` via
`setGitWorkspace` (ver "Projetos e workspace git"); a página "Projetos" é estado só da UI (não persiste).
Cada painel atualiza a cada 10 s enquanto a aba Git está visível (não a cada snapshot) e tem botão de atualizar.

```ts
type GitDetails = {
  repo: GitInfo;                     // mesmo formato do snapshot (branch, dirtyCount, ahead, behind, statusNote)
  head: { branch: string | null; detached: boolean; sha: string | null; upstream: string | null };
  branches: { name: string; current: boolean; remote: boolean; upstream: string | null;
              ahead: number | null; behind: number | null; sha: string; lastCommitDate: string;
              worktreePath: string | null }[];   // branch local em uso noutro worktree (ver "Worktrees git")
  worktrees: GitWorktree[];          // ver "Worktrees git"
  changes: GitChange[] | null;       // null quando o status não pode ser lido com segurança (ver statusNote)
  commits: GitCommit[];              // ordem topológica, mais novo primeiro (branches locais + remotas + HEAD)
  graph: { lanes: number };          // nº máximo de trilhas, para a UI dimensionar a coluna do gráfico
  truncated: boolean;                // há mais commits além de maxCommits
  error: string | null;              // falha ao ler o log (português); painel mostra o que tiver
};

type GitChange = {
  path: string; origPath: string | null;   // origPath em renomeação
  index: "M" | "A" | "D" | "R" | "C" | "U" | "T" | null;     // área de preparação (staged)
  worktree: "M" | "D" | "U" | "T" | "?" | null;              // árvore de trabalho; "?" = não rastreado
  conflict: boolean;
};

type GitCommit = {
  sha: string; shortSha: string; parents: string[];
  subject: string; author: string; date: string;             // ISO (data do autor)
  refs: { name: string; kind: "head" | "branch" | "remote" | "tag" }[];
  lane: number;                                              // trilha do nó (0 = mais à esquerda)
  edges: { fromLane: number; toLane: number; toSha: string; kind: "parent" | "merge" }[]; // linhas até a próxima linha
  passThrough: number[];                                     // trilhas que só atravessam esta linha
};
```

Regras da visão Git:
- Mesmas proteções do status: caminho local (`LocalPath`), `GitRepoSafety` e a lista de `-c` seguros. Para o log,
  além disso: `--no-pager`, `-c log.showSignature=false --no-show-signature`, `-c core.pager=`, `--no-textconv`,
  `--no-ext-diff`, `-c gpg.program=`, `-c core.alternateRefsCommand=`, `GIT_TERMINAL_PROMPT=0`, timeout 5 s,
  saída limitada (ex.: 2 MB). Repositório marcado inseguro → `changes = null`, `statusNote` preenchido; o log
  ainda é lido se a checagem estendida (`gpg`, `log.showSignature`, `alternateRefsCommand`) também passar.
  A checagem estendida também recusa `[diff "x"]` com `textconv`/`command`, `diff.external` e `[include]`/
  `[includeIf]` (conteúdo não verificado). Se ela falhar **nenhum git roda** (nem status): `commits = []`,
  `branches = []`, `changes = null`, `error` = "Histórico não lido: …" e `statusNote` = "Status não lido: …";
  `head` vem do arquivo `HEAD`. `filter`/`fsmonitor` só bloqueiam o status, não o log.
- `refs`: a branch atual vem como `{ name: "main", kind: "head" }` (não repete como `branch`); HEAD destacado vem
  como `{ name: "HEAD", kind: "head" }`; `origin/HEAD` e refs fora de heads/remotes/tags são omitidas.
- O layout das trilhas (`lane`, `edges`, `passThrough`) é calculado no Core (testável), não na UI. Convenção:
  - `edges[].toLane` = trilha em que o pai (`toSha`) estará na **linha seguinte**: o nó, se o pai for o próximo
    commit, ou uma trilha de `passThrough` dessa linha. Pai fora da janela (truncado) continua descendo até o fim.
  - `passThrough` da linha N = trilhas que atravessam a linha N sem nó e seguem retas para a linha N+1 (como nó ou
    como `passThrough`).
  - Cada sha fica reservado em **uma** trilha só: quando dois filhos têm o mesmo pai, a aresta do segundo já aponta
    (`toLane`) para a trilha reservada, então o Core nunca manda duas trilhas para o mesmo sha. A UI ainda tolera o
    caso (trilha de `passThrough` da linha N que some na N+1 converge no nó da N+1).
  - Trilha livre mais à esquerda é reaproveitada; `graph.lanes` = maior trilha usada + 1.
- `gitDetails` com cache de 5 s por repositório e no máximo 2 leituras simultâneas (chamadas simultâneas ao mesmo
  repositório compartilham a mesma leitura). Repositórios aceitos: vistos nos snapshots (processos ou salvos) nos
  últimos 30 min, ou fixados por projeto git salvo/aba do workspace (sem expirar), comparando o caminho normalizado
  sem diferenciar maiúsculas. `maxCommits` fora de 1..500 é ajustado.
- `changes[]`: conflito (`u`) vem com `index = "U"`, `worktree = "U"`, `conflict = true`; contagens: `stagedCount` =
  `index` ≠ null sem conflito; `unstagedCount` = `worktree` M/D/T sem conflito; `untrackedCount` = `"?"`.

### Repositórios nas subpastas (workspace com vários repos)

Quando a pasta de trabalho do processo **não** está dentro de um repositório, o Core procura repositórios nas
**subpastas imediatas** (1 nível; no máximo 12; ignora pastas ocultas, `node_modules`, `bin`, `obj`, `dist`;
mesma validação `LocalPath`; cache de 60 s por pasta), como o VS Code faz ao abrir uma pasta com vários repos
(ex.: `gsintegra\app` e `gsintegra\api`). Eles vão em `ProcessInfo.subRepos: GitInfo[]` (vazio quando há `git`
ou nada foi achado), entram em `AppGroup.repos`, no `KnownGitRepos` (liberando `gitDetails`/ações) e na busca.
A UI mostra um chip por repo (até 3 + "+N").
Detalhes do Core: só a pasta de trabalho (não caminhos da linha de comando); olha no máximo 256 subpastas; além das
ocultas (nome com `.` ou atributo oculto), pula pastas de sistema e links/junções; ordem por nome; o status de cada
repo usa o mesmo cache/segurança do snapshot (`statusNote` quando bloqueado).

### Ações git (controle de código, estilo painel "Source Control" do VS Code)

Todas recebem `repoRoot` (mesma regra do `gitDetails`: só repositório já detectado, caminho local) e devolvem
`{ details: GitDetails, output: string | null }` — `details` relido logo após a ação (cache invalidado);
`output` = saída relevante do git (ex.: resumo do push/pull), cortada em 4 KB. Erro → `ok:false` com título em
português + a mensagem do git (stderr, cortada em 1 KB), ex.: `"Falha ao trocar de branch: error: Your local
changes … would be overwritten by checkout"`.

| type | payload | observação |
|---|---|---|
| `gitStage` | `{ repoRoot, paths: string[] }` | `paths` vazio = todos (`git add -A`) |
| `gitUnstage` | `{ repoRoot, paths: string[] }` | vazio = todos (`git restore --staged`) |
| `gitDiscard` | `{ repoRoot, paths: string[] }` | rastreado → `git restore --worktree`; não rastreado → `git clean -f`; a UI SEMPRE confirma antes |
| `gitCommit` | `{ repoRoot, message, all: bool, amend: bool, push: bool }` | mensagem via stdin (`-F -`); `all` = preparar tudo antes (a UI pergunta quando nada está preparado, como o VS Code); `push` = commit e push |
| `gitCheckout` | `{ repoRoot, branch, create: bool, startPoint: string \| null }` | branch remota (`origin/x`) sem local → `git switch --track`; `create` = `git switch -c branch [startPoint]` |
| `gitMerge` | `{ repoRoot, branch }` | merge na branch atual; conflito → ok:true com `details.merging = true` e as mudanças `conflict` |
| `gitMergeAbort` | `{ repoRoot }` | `git merge --abort` |
| `gitFetch` | `{ repoRoot }` | `git fetch --prune` |
| `gitPull` | `{ repoRoot }` | `git pull --ff-only` (não cria merge implícito; falha → mensagem pedindo merge manual) |
| `gitPush` | `{ repoRoot }` | sem upstream e com 1 remoto → `git push -u <remoto> HEAD` |

`GitDetails` ganha: `merging: boolean` (existe `MERGE_HEAD`), `rebasing: boolean`, `remotes: string[]`,
`stagedCount: number`, `unstagedCount: number`, `untrackedCount: number`, `conflictCount: number`.

Regras das ações:
- **Leitura continua blindada** (seção anterior). **Ações disparadas pelo usuário rodam como o git do terminal**
  (hooks do projeto ativos — husky/lint-staged —, assinatura se configurada), porque são intenção explícita, igual
  ao VS Code. Mantém `--no-pager`, `core.pager=`, `GIT_TERMINAL_PROMPT=0` (o Git Credential Manager com janela
  continua funcionando) e `ArgumentList` (nada concatenado; caminhos depois de `--`).
- Uma ação por repositório por vez (lock por repo; segunda ação simultânea → erro "Já há uma operação git em
  andamento neste repositório."). Timeout: 60 s locais (hooks podem rodar lint), 180 s para rede.
- `paths` precisam ser relativos, dentro do repositório (sem `..`, sem absoluto) e estar na lista de mudanças atual.
- Nome de branch validado com `git check-ref-format --branch`. `startPoint`/`branch` do merge precisam existir
  em `branches`.
- Mensagem de commit vazia (só espaços) → erro em português, sem chamar o git.
- Detalhes do Core: `gitDiscard` com `paths` vazio → erro (não descarta tudo); `gitUnstage` de renomeação leva
  junto o `origPath`; caminhos usam `--literal-pathspecs` (sem glob/magia); `gitCommit` com `all` = `git add -A`
  (inclui não rastreados) antes do commit; `push` após commit que falha → erro "O commit foi feito, mas o envio
  (push) falhou: …"; push com HEAD destacado ou sem upstream com 0 ou 2+ remotos → erro em português; ações usam
  `GIT_EDITOR=:` (merge `--no-edit`, nunca abre editor).

## Projetos e workspace git

**Projeto git salvo** = uma pasta que agrupa 1+ repositórios (ex.: `gsintegra` → `api` e `app`). **Workspace git**
= abas abertas na visão Git e o painel fixado à direita; persiste no `config.json` (não no `localStorage`).

```ts
type GitProject = { id: string /* guid */; name: string; folder: string;
                    repos: string[] /* raízes, calculadas pelo Core, ordenadas por nome */ };
type GitTab = { folder: string; name: string; repos: string[]; projectId: string | null; activeRepo: string | null;
                worktreeOf: string | null /* aba de worktree: raiz do repo principal; folder = repos[0] = worktree */ };
type GitWorkspace = { docked: boolean /* padrão false */; dockWidthPct: number /* 25..70, padrão 42 */;
                      tabs: GitTab[]; active: string | null /* folder da aba ativa */ };
```

**Regra de aceitação de pasta** (`gitProjectDraft.path`, `saveGitProject.project.folder`, `projectScripts.folder`):
caminho local (`LocalPath`: recusa UNC, `\\?\`, rede etc.), não pode ser link/junção, **não pode ser pasta ampla**, e
precisa ser (a) raiz de repositório conhecido (`KnownGitRepos`), (b) pasta de trabalho de um processo do último
snapshot, (c) pasta-mãe **direta** de um repositório conhecido ou (d) pasta de um projeto salvo. Fora disso → erro
`"Pasta não reconhecida: abra-a a partir de um processo ou repositório já detectado."`. Assim a UI não consegue fazer
o app olhar (nem rodar git) num caminho arbitrário.

**Pasta ampla** (recusada mesmo que caia em a–d; erro `"Pasta ampla demais para virar projeto (raiz da unidade,
pasta do usuário ou do sistema). Escolha a pasta do projeto ou de um repositório."`): raiz de unidade (`C:\`),
`%USERPROFILE%`, `C:\Users`, Área de Trabalho, Documentos, Downloads, raiz do OneDrive (`%OneDrive%`,
`%OneDriveConsumer%`, `%OneDriveCommercial%`), Windows, Program Files, Program Files (x86) e ProgramData — só a
própria pasta; subpastas (ex.: `Documents\Projetos`) não são amplas.

**Forma dos caminhos:** todo caminho devolvido (projetos, abas, `GitInfo.repoRoot`, `subRepos`) vem canônico — letra
de unidade **maiúscula**, nome longo no lugar do 8.3 (`RICARD~1.MAR` → nome real, quando o caminho existe) e sem
barra final. A UI pode comparar caminhos sem diferenciar maiúsculas, mas o Core já não manda duas grafias do mesmo
repo.

**Dados não confiáveis:** nomes de projeto/aba e nomes de script vêm do usuário ou de arquivos do repositório. A UI
deve exibi-los com `textContent` (ou escapar), nunca como HTML. O Core recusa nomes de script com caracteres de
controle.

**Repositórios da pasta** (save, rescan, `projectScripts`): se a pasta é raiz de repositório → só ela; senão os
repositórios das subpastas imediatas (mesma varredura de "Repositórios nas subpastas": 1 nível, máx. 12, sem
ocultas/links/`node_modules`…). Nenhum → erro `"Nenhum repositório git nessa pasta."`.

- `gitProjectDraft { path }` → `{ project, repos: GitInfo[] }` com `project.id = ""` e `name` = nome da pasta
  sugerida — ou, se a pasta sugerida já é de um projeto salvo, o `id` e o `name` dele. Sugestão: se `path` é raiz de
  repo e a pasta-mãe tem **de 2 a 4** repos filhos, não é ampla **e** também passa na regra de aceitação
  (normalmente sim, pela regra c), sugere a mãe (workspace); com 5 ou mais repos (coleção, ex.: `Documents\Projetos`)
  ou mãe ampla, sugere o próprio repo. Se `path` não é raiz de repo (inclusive subpasta de um repo), `repos` = repos
  das subpastas imediatas de `path`. **O rascunho não roda git** em repo que o app ainda não conhece: esses vêm só com
  `repoName` e `branch` lida do `HEAD` (`dirtyCount`/`ahead`/`behind`/`statusNote` `null`); repo já conhecido usa o
  mesmo cache de status do snapshot.
- `saveGitProject { project: { id, name, folder } }` → `{ project }`. `repos` enviados pela UI são **ignorados**:
  o Core recalcula pela pasta. `name` aparado; vazio → nome da pasta; mais de 80 caracteres → erro `"O nome do
  projeto pode ter no máximo 80 caracteres."`. Id vazio = novo (guid); se a pasta já é de um projeto salvo (sem
  diferenciar maiúsculas/barra final), **atualiza** esse projeto (mesmo id) em vez de duplicar — e, se o `name`
  enviado for o padrão (vazio ou igual ao nome da pasta), mantém o nome atual do projeto. Id preenchido =
  renomear/atualizar; id inexistente → `"Projeto não encontrado."`; mudar para a pasta de **outro** projeto →
  `"Já existe outro projeto salvo com essa pasta."`. Máximo de 50 projetos (`"Limite de 50 projetos salvos
  atingido. Apague um para salvar outro."`). Gravação atômica; em modo somente leitura → o erro de configuração de
  sempre.
- `deleteGitProject { id }` → `{}`; id inexistente → `"Projeto não encontrado."`. Abas com esse `projectId` ficam
  com `projectId: null` (a aba continua).
- `setGitWorkspace { workspace }` → `{ workspace }` saneado e gravado: `dockWidthPct` limitado a 25..70 (aceita
  decimal); até 12 abas e 12 repos por aba (excedentes descartados, na ordem enviada); cada repo precisa estar em
  `KnownGitRepos` **ou já estar numa aba gravada** (ex.: repo num pendrive ausente continua na aba; só não é fixado
  até voltar a ser válido) — os outros são descartados; aba sem repos sai; aba com pasta não local (e que não estava
  gravada) sai; `folder` da aba precisa ser a raiz ou a mãe direta de um repo da aba, sem link/junção e sem ser
  pasta ampla — senão vira a raiz do primeiro repo; pasta repetida → vale a primeira; `name` aparado e cortado em 80
  (vazio → nome da pasta); `projectId` que não existe → `null`; `activeRepo` inválido → primeiro repo da aba;
  `active` inválido ou `null` → pasta da primeira aba (`null` sem abas). Caminhos voltam na forma canônica. Igual
  por valor ao já gravado → responde o mesmo workspace sem regravar o arquivo nem refazer a fixação (a UI pode
  chamar com debounce sem custo). Tipo errado (ex.: `dockWidthPct: "x"`) → `ok:false`.
- `projectScripts { folder }` → `{ repos: [{ repoRoot, packageManager, scripts }] }`, um item por repositório da
  pasta (ordem por nome). Lê só `package.json` na raiz de cada repo, aberto sem seguir link/junção e conferido pelo
  próprio handle (acima de 1 MB ou JSON inválido → `scripts: []`; nomes vazios, com mais de 200 caracteres ou com
  caracteres de controle são descartados). `scripts` = **nomes** das chaves de `"scripts"` (nunca os comandos): `dev`, `start`,
  `start:dev`, `serve` primeiro (nessa ordem), o resto em ordem alfabética, máx. 30. `packageManager` pelo campo
  `"packageManager"` (`"pnpm@9.1.0"` → `"pnpm"`; só npm/pnpm/yarn) ou, sem ele, pelo lockfile presente
  (`pnpm-lock.yaml` → pnpm, `yarn.lock` → yarn, `package-lock.json` → npm); sem `package.json` → `null` e `[]`.
  Nada é executado.

**Fixação (pin) em `KnownGitRepos`:** os repositórios dos projetos salvos e das abas do workspace ficam conhecidos
sem expirar (os vistos em processos continuam expirando em 30 min), então `gitDetails` e as ações git funcionam
para eles mesmo sem processo rodando. O conjunto fixado é recalculado ao carregar a config, a cada
`saveGitProject`/`deleteGitProject`/`setGitWorkspace` (quando mudou algo) e no `ready`; só entram caminhos que passam
em `LocalPath`, não são link/junção e são raiz de repositório de verdade (`.git` não pode ser link). O recálculo é
serializado pela geração da configuração: alterações simultâneas nunca deixam a fixação de uma config mais velha.
Os repositórios vistos em processos têm teto de 2.048 entradas (cheio só de entradas frescas → sai a mais antiga).

**Rescan:** no `ready` (no máximo a cada 30 s) o Core varre de novo a pasta de cada projeto salvo (repo clonado
depois aparece; repo apagado some) e grava só se algo mudou. Pasta que não passa em `LocalPath` (ex.: unidade
removível ausente) fica como está. A config é do usuário (confiável), mas o carregamento só faz saneamento estrutural
(nulos, duplicados, letra de unidade, limites 50/12): os caminhos são validados na fixação, não apagados. Projeto sem
`id` ganha um guid novo (não é descartado); o que passar dos limites é descartado e vira aviso em `configLoadError`
(ex.: `"Configuração carregada com ajustes: Havia mais de 50 projetos git salvos; só os 50 primeiros foram
mantidos."`) — a configuração continua editável (não é o modo somente leitura).

## Terminais embutidos (ConPTY)

Terminais como o integrado do VS Code, dentro do app, mostrados na aba do projeto (faixa embaixo — UI). O Core usa o
pseudo-console do Windows (ConPTY, Windows 10 1809+) seguindo a documentação oficial: "Creating a Pseudoconsole
session" (https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session), `CreatePseudoConsole`,
`ResizePseudoConsole` e `ClosePseudoConsole` (https://learn.microsoft.com/en-us/windows/console/createpseudoconsole,
…/resizepseudoconsole, …/closepseudoconsole) e `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`
(https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).

```ts
type LaunchMode = "terminal" | "window" | "hidden";
type TerminalInfo = {
  id: string; title: string;          // title = nome do salvo, ou nome da pasta do shell
  folder: string;
  projectFolder: string | null;       // pasta do projeto git salvo que contém folder (ela ou subpasta), se houver
  savedId: string | null;             // processo salvo que roda nele (null = shell do termOpen)
  pid: number | null; running: boolean; exitCode: number | null;
  startedAt: string;                  // ISO do último início
  cols: number; rows: number;
};
```

- **Comandos:** a UI nunca define o comando. `termOpen` abre o shell padrão — `pwsh.exe -NoLogo` se estiver no PATH
  (só pastas locais do PATH), senão `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo`. Processo
  salvo em modo "terminal" roda `cmd.exe /s /k "<comando>"` (a mesma linha do modo "window") na pasta do salvo (sem
  pasta: o perfil do usuário) com as variáveis dele.
- **Pasta do `termOpen`:** mesma regra do `gitProjectDraft` (LocalPath, não ampla, sem link; projeto salvo, repo
  conhecido, pasta de processo do snapshot ou mãe direta de repo conhecido). Erros iguais aos de lá.
- **Ids:** `termInput`/`termResize`/`termAttach`/`termRestart`/`termClose` só aceitam ids criados pelo app (senão
  `"Terminal não encontrado."`); `termInput` só com o terminal rodando (`"O terminal não está rodando."`) e até 64 KB
  (`"Entrada grande demais para o terminal (limite de 64 KB)."`). `termOpen` e `termResize` ajustam o tamanho a
  20..500 colunas × 5..200 linhas (ausente → 120 × 30).
- **Limite:** 16 terminais (rodando ou parados) → `"Limite de 16 terminais abertos. Feche um terminal para abrir
  outro."`. ConPTY indisponível (Windows anterior ao 10 1809) → `termOpen` responde `"Terminais embutidos precisam do
  Windows 10 versão 1809 ou mais recente."` e o modo "terminal" do salvo cai para "window".
- **Saída e `seq`:** a saída (UTF-8 com sequências VT) é decodificada em fluxo — caractere dividido entre duas
  leituras não vira lixo — e guardada num anel de 1 Mi caracteres (~1 MB) por terminal. `seq` = total de caracteres
  produzidos pelo terminal até o fim do `data` (só cresce, inclusive depois de reiniciar). `termOutput` sai em lotes a
  cada ~30 ms, com até 32 Ki caracteres por mensagem (abaixo de 256 KB mesmo com escapes JSON). `termAttach` devolve o
  conteúdo do anel e o `seq` do último caractere incluído: a UI descarta `termOutput` com `seq` ≤ esse valor. Se
  `seq - data.length` de um `termOutput` for maior que o último `seq` recebido, houve perda (host sem WebView ou com
  mais de 2 Mi caracteres pendentes): chame `termAttach` de novo.
- **Sem WebView** (janela na bandeja/liberada) o host não empurra nada; o anel continua guardando. Ao recriar a página,
  a UI chama `termAttach` para cada terminal do snapshot.
- **`termExit`** chega depois de toda a saída daquele processo. O terminal continua na lista com `running: false` e
  `exitCode` até `termClose`.
- **`termRestart`:** shell (ou terminal de `termRunSavedIn`) → encerra a árvore (se rodando), limpa o anel e roda o
  mesmo comando na mesma aba (mesmo `id`). Terminal de processo salvo → Parar + Iniciar do salvo (o rastreio de
  "rodando" continua valendo), na mesma aba. A UI deve limpar a tela ao receber a resposta. Se o salvo foi excluído
  (`deleteSaved` desvincula a aba: `savedId` vira null e ela não reinicia mais) → `"O processo salvo foi excluído;
  feche este terminal."`.
- **Repositório com config que pode executar programas** (`termOpen` e `termRunSavedIn`): se a config local do
  repositório da pasta — ou de algum repositório nas subpastas imediatas dela — define `core.fsmonitor`, `filter.*`,
  `include`/`includeIf` (ou não pôde ser lida), a mesma checagem do status do snapshot, a resposta é `ok: false`,
  `code: "unsafeRepo"` e `error` = `"A configuração git deste repositório pode executar programas (<motivo>). Abrir o
  terminal mesmo assim?"`. A UI confirma com o usuário e repete a mensagem com `confirmUnsafe: true`. Sem git
  instalado a checagem não acontece.
- **Processo salvo em modo "terminal":** `startSaved` cria o terminal (ou reaproveita, limpo, a aba parada do mesmo
  salvo) e devolve `state.terminalId`; `snapshot.saved[].terminalId` aponta para a aba enquanto ela existir. "Rodando" e
  `pids` continuam pelo pid do cmd + descendentes. Já rodando → `"Este processo salvo já está em execução."`.
  `stopSaved` encerra os processos do salvo (identidade pid + CreateTime, como antes) e a árvore do terminal, que fica
  com `running: false` e `exitCode` (1 quando encerrado pelo app).
- **Árvore:** cada terminal roda num Job Object com "encerrar ao fechar": encerrar o terminal (`termClose`,
  `stopSaved`, `termRestart`) ou o app mata o shell/cmd **e todos os descendentes**, inclusive programas abertos a
  partir dele — também os que sobreviveram ao shell. Um terminal cujo shell saiu mas que ainda tem descendentes vivos
  conta como rodando na confirmação de saída e na recusa de `gitWorktreeRemove`. Na saída do app há um prazo único
  (3 s) para todas as árvores terminarem.
- **Encerramento do app:** "Sair" no menu da bandeja com terminais rodando pede confirmação nativa ("N terminais
  rodando serão encerrados. Sair mesmo assim?"); o mesmo vale para fechar a janela quando "minimizar para a bandeja"
  está desligado. O X com bandeja ligada continua só escondendo a janela. Desligamento do Windows encerra sem
  perguntar. Em qualquer saída o host fecha todos os pseudo-consoles e mata as árvores.
- **Entrega dos pushes:** o host só recolhe o próximo lote de saída depois de postar o anterior (contrapressão); se a
  página não acompanhar, a saída espera no gerenciador (teto de 2 Mi caracteres, depois perde a mais antiga e a UI
  usa `termAttach`).

## Worktrees git

Ver os worktrees de um repositório (inclusive quais estão "rodando" — a UI cruza `worktrees[].path` com as pastas de
processos/terminais), criar a partir de branches e abrir cada um numa aba "projeto · repo ⎇ branch".

```ts
type GitWorktree = {
  path: string;                       // canônico (unidade maiúscula, sem barra final)
  branch: string | null;              // null quando destacado
  head: string;                       // sha curto (7)
  isMain: boolean; detached: boolean;
  locked: boolean; lockReason: string | null;
  prunable: boolean;                  // pasta sumiu: aparece para ser limpo (gitWorktreePrune); nunca é registrado
  dirtyCount: number | null;          // só para worktree que já é repositório conhecido (cache de status); senão null
};
```

- **Leitura:** `gitDetails` traz `worktrees` (`git worktree list --porcelain -z`, pelo mesmo runner endurecido de
  leitura; nada roda se a checagem estendida da config bloquear o histórico). O primeiro é o principal. Cada
  `branches[]` local em uso noutro worktree traz `worktreePath`. `repo.mainRepoRoot` vem preenchido quando o painel é
  de um worktree vinculado.
- **Segurança:** os caminhos vêm de `.git/worktrees/*/gitdir` (dado do repositório, não confiável). Um worktree
  vinculado só aparece/é aceito se o caminho passar em `LocalPath`, não for link/junção e o arquivo `.git` dele
  (arquivo, não pasta) apontar (`gitdir:`) de volta para `<pasta git comum>\worktrees\<nome>` do mesmo repositório.
  O principal precisa ser a raiz do repositório principal. Os que passam viram repositórios conhecidos (a UI pode
  abri-los). Worktree vinculado de um repo irmão **não** entra em `subRepos`/repos do projeto (aparece só como
  worktree). Processo rodando num worktree: `git.repoRoot` = worktree e `git.mainRepoRoot` = principal.
- **`gitWorktreeAdd`** (ação, hooks ativos como nas outras): `branch` existente (local; ou remota `origin/x` sem local
  `x` → cria `x` acompanhando a remota) **ou** `newBranch` (validado com `check-ref-format`) a partir de `startPoint`
  (uma das branches; padrão HEAD) — só um dos dois. Branch já em uso (atual ou noutro worktree) → `"A branch "x" já
  está em uso no worktree "<caminho>"."`. Caminho padrão: `<pasta do projeto salvo que contém o repo, senão pasta-mãe
  do repo>.worktrees\<repo>-<slug da branch>` (slug: minúsculas, `[a-z0-9._-]`, `/` e o resto viram `-`; já existe →
  `-2`, `-3`…). `path` informado: local, não amplo, pasta-mãe existente, destino inexistente ou vazio, sem link/junção
  no caminho, fora da working tree de qualquer repositório — erros começam com `"Caminho do worktree inválido: "`.
  `copyEnv: true` copia da raiz do repositório principal os `.env` e `.env.*` **não versionados** (exceto
  `.env.example`/`.env.sample`), até 1 MB, sem link, para a raiz do worktree, sem sobrescrever; `copied` = nomes
  copiados. O worktree criado é validado e registrado/fixado (não expira) em `KnownGitRepos`.
- **`gitWorktreeRemove`:** recusa o principal (`"O worktree principal do repositório não pode ser removido."`). Com
  terminal do app (vivo) ou processo (último snapshot, coleta nova antes) com a pasta dentro do worktree → recusa:
  `"Feche os terminais/processos rodando no worktree antes de remover. Em uso por: terminal "…", node.exe (pid 123)."`
  com `code: "worktreeInUse"` (o app não encerra nada sozinho). Sem `force` e com mudanças → `"O worktree tem
  alterações não salvas (arquivos modificados ou não rastreados). Remover mesmo assim descarta essas alterações."` com
  `code: "worktreeDirty"` — a UI pede confirmação e repete com `force: true` (`--force`; trancado: `--force --force`). Removido → sai da fixação. Remover pelo painel do próprio
  worktree → erro pedindo para abrir o principal.
- **`gitWorktreePrune`:** `git worktree prune`; os prunable deixam de ser fixados.
- **`termRunSavedIn`:** roda o comando do salvo (`cmd /s /k`, variáveis do salvo) num terminal com pasta = worktree +
  (pasta de trabalho do salvo relativa à raiz do repositório dele); título `"<nome do salvo> ⎇ <branch>"`; `savedId`
  do terminal = null (o salvo não muda e o terminal não conta como ele rodando). Só se `worktreePath` for o principal
  ou um worktree vinculado válido do **mesmo** repositório (`"A pasta não é um worktree do repositório deste processo
  salvo."`); salvo sem pasta dentro de um repositório → `"O processo salvo não está dentro de um repositório git
  (defina a pasta de trabalho dele)."`; pasta relativa ausente no worktree → erro. Mesma confirmação `unsafeRepo`.
- **Abas de worktree** (`setGitWorkspace`): `worktreeOf` = raiz do repositório principal; `folder` = `repos[0]` =
  caminho do worktree. Aceita se o worktree for conhecido/fixado; aba de worktree removido segue a regra das abas
  gravadas (fica, só não é fixada).

## Formatos (JSON camelCase)

```ts
type Snapshot = {
  timestamp: string;                 // ISO
  machine: {
    totalMemoryBytes: number; usedMemoryBytes: number; memoryPercent: number; // 0..100
    cpuPercent: number;              // 0..100, normalizado por núcleos
    logicalCores: number; processCount: number;
    history: { t: number; memoryPercent: number; cpuPercent: number }[]; // últimos 10 min, t = epoch ms
  };
  groups: AppGroup[];                // ordenado por memória desc; favoritos a UI coloca no topo
  saved: SavedProcessState[];
  terminals: TerminalInfo[];         // terminais embutidos abertos (ver "Terminais embutidos")
};

type AppGroup = {
  key: string;                       // "app:<exe em minúsculas>", ex.: "app:code.exe"
  name: string;                      // nome amigável (FileDescription/ProductName; senão exe sem extensão)
  exeName: string; exePath: string | null;
  iconKey: string | null;            // = exePath em minúsculas; UI pede via getIcons e guarda em cache
  memoryBytes: number; memoryPercent: number; cpuPercent: number;
  processCount: number;
  favorite: boolean;
  repos: GitInfo[];                  // repositórios distintos das instâncias
  memoryHistory: number[];           // memoryBytes, últimos 60 pontos (sparkline)
  processes: ProcessInfo[];          // ordenado por memória desc
};

type ProcessInfo = {
  pid: number; parentPid: number; name: string;
  exePath: string | null;
  commandLine: string | null;        // null = sem acesso
  workingDirectory: string | null;   // null = sem acesso
  memoryBytes: number;               // private working set (como o Gerenciador de Tarefas)
  memoryPercent: number; cpuPercent: number;
  startTime: string | null;
  accessDenied: boolean;
  isGitProcess: boolean;             // git.exe, git-remote-*, ssh do git etc.
  git: GitInfo | null;               // repo da pasta de trabalho (ou do caminho no comando)
  favoriteKey: string;               // "cmd:<hash curto de exe+commandLine normalizados>"; sem commandLine
                                     // (sem acesso) = chave do grupo "app:<exe>" (senão todos colidiriam no mesmo hash)
  favorite: boolean;
  savedId: string | null;            // processo salvo correspondente, se houver
};

type GitInfo = {
  repoRoot: string; repoName: string;
  branch: string | null;             // HEAD destacado → sha curto
  dirtyCount: number | null;         // arquivos alterados (git status --porcelain), cache ~15s
  ahead: number | null; behind: number | null;
  statusNote: string | null;         // motivo (pt-BR) de o status não ter sido lido por segurança, ex.:
                                     // "Status não lido: a config do repositório define core.fsmonitor."
  mainRepoRoot: string | null;       // repoRoot é worktree vinculado → raiz do repo principal; senão null
};

type SavedProcess = {
  id: string;                        // guid
  name: string;
  command: string;                   // linha de comando completa, ex.: "npm run dev"
  workingDirectory: string | null;
  env: Record<string, string>;
  launchMode: LaunchMode;            // "terminal" (padrão: aba embutida, cmd /s /k) | "window" (console separado,
                                     // cmd /s /k) | "hidden" (cmd /s /c oculto). Config antiga (sem o campo, com
                                     // showWindow) → "terminal"; showWindow é ignorado. Valor desconhecido → "terminal".
  notes: string | null;
};

type SavedProcessState = SavedProcess & {
  running: boolean; pids: number[];
  memoryBytes: number; cpuPercent: number;
  startedAt: string | null;
  git: GitInfo | null;               // da workingDirectory
  lastError: string | null;
  terminalId: string | null;         // aba do terminal embutido deste salvo (modo "terminal"), se existir
};

type Settings = { refreshIntervalMs: number; minimizeToTray: boolean; theme: "system" | "light" | "dark" };

type AppConfig = { favorites: string[]; saved: SavedProcess[]; settings: Settings;
                   gitProjects: GitProject[]; gitWorkspace: GitWorkspace }; // config antiga sem esses campos → [] e padrões
```

## Regras de comportamento

- **Coleta:** uma chamada `NtQuerySystemInformation(SystemProcessInformation)` por ciclo (memória privada,
  tempos de CPU, pid/ppid, nome) — funciona até para processo elevado. Linha de comando e pasta de trabalho
  lidas do PEB (`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`), em cache por `(pid, startTime)`;
  pasta de trabalho re-lida no máximo a cada 10 s. Sem acesso (OpenProcess falhou) → `null` + `accessDenied: true`,
  nunca exceção. Abriu mas leu `null` (processo recém-criado, ainda sem ProcessParameters) → tenta de novo nas
  próximas amostras (até 5 leituras, espera crescente a partir de 250 ms); esgotadas, conta como sem acesso.
  Falha da chamada de coleta → o ciclo anterior é reaproveitado (não derruba o rastreio dos salvos).
- **CPU %:** delta de (kernel+user) / (delta de tempo × núcleos) × 100. Amostras a menos de 250 ms da anterior
  não recalculam (mantêm o último valor) para não saturar em 100%.
- **Caminho local (validação única):** todo caminho vindo de dado não confiável (pasta do PEB, linha de comando,
  `gitdir:`/`commondir` de arquivos do git, `iconKey`, `openFolder`/`openTerminal`, pasta de trabalho do salvo) só
  é usado se for absoluto `X:\…` numa unidade **fixa ou removível**. Recusa UNC (`\\host`, `//host`), `\\?\`,
  `\\.\`, `\??\`, `GLOBALROOT`, nomes reservados (`CON`, `NUL`, `COM1`…), fluxos alternativos (`a:b`) e unidades de
  rede/CD/RAM/inexistentes — antes de qualquer acesso ao disco (evita autenticação SMB e dispositivos). O caminho
  aceito sai canônico: letra de unidade maiúscula e, se tiver segmento 8.3 (`~`) e existir, o nome longo
  (`GetLongPathNameW`, só depois da validação).
- **Git:** sobe pastas a partir da `workingDirectory` (ou de caminho absoluto citado na linha de comando) procurando
  `.git` (pasta ou arquivo de worktree); branch lida de `HEAD` sem chamar git; `dirtyCount`/`ahead`/`behind` via
  `git status` em segundo plano, cache 15 s por repo (repos não vistos há 10 min saem do cache), timeout 3 s;
  falha/timeout → `null` (não mantém o valor anterior).
- **Git seguro:** o status é sempre `git -c core.fsmonitor=false -c core.hooksPath=NUL -c core.untrackedCache=false
  --no-optional-locks -C <repo> status --porcelain=v2 --branch --ignore-submodules=all` com `GIT_TERMINAL_PROMPT=0`
  e `GIT_OPTIONAL_LOCKS=0`, e só em caminho local. Antes de rodar, lê a config local do repositório (`config` e
  `config.worktree` da pasta git e da `commondir`): se houver seção `[filter …]`, `[include]`, `[includeIf …]` ou
  `fsmonitor` (não falso) em `[core]` **não roda**: `branch` continua, `dirtyCount`/`ahead`/`behind` = `null` e
  `statusNote` explica o motivo. `filter=` no `.gitattributes` não bloqueia (o comando do driver vem da config;
  a local já foi checada e a global é do usuário — ex.: Git LFS).
  A config é interpretada pelo **parser do próprio git**, que não executa nada: `git config --file <arquivo>
  --no-includes --null --list`, sem `-C`, numa pasta neutra, com `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL=NUL`,
  sem as variáveis `GIT_*` herdadas, timeout 3 s; cache por (caminho, tamanho, data de modificação). Bloqueio por
  nome de chave (sem diferenciar maiúsculas): status → `filter.*`, `include.*`, `includeIf.*`, `core.fsmonitor` não
  falso; log (checagem estendida) → `gpg.*`, `include*`, `log.showSignature` não falso, `core.alternateRefsCommand`,
  `diff.*.textconv`, `diff.*.command`, `diff.external`. Config que o git recusa (inválida/ilegível) → não roda
  ("Status não lido: a config do repositório é inválida ou não pôde ser lida."). `.git`, `commondir`, o destino de
  `gitdir:`, `HEAD` e os arquivos de config que forem link/junção não são seguidos (repositório ignorado ou não
  lido). Os processos git (status, visão Git, ações) não herdam `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`,
  `GIT_CONFIG_PARAMETERS`, `GIT_PAGER` etc. Ações passam nomes de ref depois de `--end-of-options`; `gitDiscard`
  recusa caminho que passe por junção/link simbólico.
- **Processo salvo rodando:** pids iniciados pelo app (e descendentes) **ou** processo cuja pasta de trabalho
  é igual e cuja linha de comando contém o comando salvo **em fronteira de token** (antes: início/espaço/aspas;
  depois: fim/espaço/aspas; sem maiúsculas/espaços extras) — `npm run dev` não casa com `npm run dev:api`.
  Cada processo tem um único dono: o salvo com início registrado pelo app; depois, o de comando mais longo.
- **Iniciar:** sem cmd intermediário — "terminal" roda `cmd.exe /s /k "<comando>"` num terminal embutido (ConPTY;
  sem ConPTY cai para "window"); "window" lança `cmd.exe /s /k "<comando>"` direto num console novo
  (`CREATE_NEW_CONSOLE`, título = nome); "hidden" lança `cmd.exe /s /c "<comando>"` sem janela; o pid rastreado é o do
  próprio cmd. Iniciar duas vezes o mesmo salvo (cliques/mensagens em paralelo) é recusado de forma atômica
  enquanto houver início em andamento ou registro vivo/em carência.
- **Parar / encerrar:** só encerra processos cuja identidade (pid + CreateTime) confere, abrindo o processo e
  encerrando pelo mesmo handle; pid reaproveitado nunca é encerrado (`killProcess`: erro "O processo terminou e o
  PID foi reutilizado; atualize a lista"; em `killGroup`/Parar o pid é pulado). Parar faz uma coleta nova e encerra
  os processos do salvo presentes nela + o pid raiz iniciado pelo app (conferido pelo CreateTime do registro).
  `killProcess` sem `startTime` usa o CreateTime do último snapshot (pid fora dele é recusado). A checagem de
  protegidos usa o nome **atual** do processo (e o do snapshot).
- **Configuração:** arquivo inexistente → padrões. Arquivo existente que não pôde ser lido (em uso, sem permissão)
  → 3 novas tentativas; persistindo, modo **somente leitura**: alterações retornam erro em português até uma
  releitura funcionar, e o arquivo nunca é sobrescrito. JSON corrompido → cópia `.bak` e padrões. Variáveis de
  ambiente com chave repetida (sem diferenciar maiúsculas, após aparar) → a última vence.
- **Proteção:** recusar encerrar pid 0/4, o próprio app e processos críticos (`csrss`, `wininit`, `winlogon`,
  `services`, `lsass`, `smss`, `System`, `Registry`, `dwm`) com erro em português.
- **WebView2:** conteúdo servido por mapeamento de host virtual `https://monitoramento.app/` → `wwwroot`;
  bloquear navegação externa; aceitar mensagens só dessa origem; DevTools só em Debug.
