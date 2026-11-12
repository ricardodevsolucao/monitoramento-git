# Monitoramento

Monitor de processos para Windows. Agrupa os processos por aplicação, mostra o uso da máquina
(principalmente memória), a relação de cada processo com repositórios git, a pasta de trabalho,
favoritos e **processos salvos** que podem ser iniciados e parados com um clique.

Interface em HTML/CSS/JS puro dentro de um WebView2; o núcleo (coleta, git, configuração, launcher)
é uma biblioteca .NET. O contrato entre as partes está em [`docs/CONTRATO.md`](docs/CONTRATO.md).

## Requisitos

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (não é necessário se publicar com `-SelfContained`)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (já vem no Windows 11 e no Edge atualizado)
- Para desenvolver: .NET 10 SDK

## Como rodar

```powershell
dotnet run --project src\Monitoramento.App
```

Em Debug o DevTools do WebView2 e o menu de contexto ficam habilitados (F12).

## Testes

```powershell
dotnet build Monitoramento.slnx
dotnet test Monitoramento.slnx
```

## Publicar

```powershell
.\publicar.ps1                 # dependente do framework, arquivo único, saída em .\publish\
.\publicar.ps1 -SelfContained  # embute o runtime do .NET (executável bem maior)
```

Mantenha a pasta `wwwroot` ao lado de `Monitoramento.App.exe`.

## Onde ficam os dados

| O quê | Onde |
|---|---|
| Configuração (favoritos, processos salvos, tema, intervalo) | `%APPDATA%\Monitoramento\config.json` |
| Tamanho, posição e estado da janela | `%LOCALAPPDATA%\Monitoramento\window.json` |
| Logs de erro (mantidos por 14 dias) | `%LOCALAPPDATA%\Monitoramento\logs\` |
| Dados do WebView2 (cache, perfil) | `%LOCALAPPDATA%\Monitoramento\WebView2\` |

## Uso

- **Bandeja:** fechar a janela (X) com "minimizar para a bandeja" ligado só esconde o app. Duplo clique no
  ícone abre; o menu tem *Abrir*, *Processos salvos* (▶ Iniciar / ■ Parar) e *Sair*. Sair de verdade é só pelo menu.
- **Instância única:** abrir o programa de novo traz a janela existente para a frente.
- **Economia:** com a janela minimizada ou na bandeja a coleta pesada para (1 amostra a cada 10 s para o histórico e o tooltip).
- **Bandeja leve:** depois de 30 s escondido na bandeja o app libera a interface (os processos `msedgewebview2` encerram) e a recria ao abrir; a coleta segue a cada 10 s. Minimizado na barra de tarefas a interface é mantida, para a troca ser instantânea.
- **Fechar por fora:** `CloseMainWindow` e pedidos de fechamento de outros programas também vão para a bandeja (com a opção ligada); só o menu *Sair*, o desligamento do Windows e o encerramento forçado do processo (*Finalizar tarefa* com força, `taskkill /f`) encerram.
- **Avisos de segurança do git:** em repositórios cuja configuração tem filtro, `fsmonitor` ou `include` (que executam programas), o status **não é lido**; o chip mostra a branch com um cadeado e o motivo, sem contagem de alterações. O mesmo vale para pastas que não são locais.
- **Avisos na interface:** erro ao ler a configuração aparece como faixa no topo; comando copiado de um processo com `& | < > ^ %` fora de aspas gera aviso amarelo no formulário; encerrar um PID reutilizado por outro processo é recusado.
- **Favoritos no topo:** em Aplicações, o grupo favoritado e o grupo que contém algum processo favoritado sobem; dentro do grupo, os processos favoritados vêm primeiro (depois, a ordem escolhida: memória, CPU ou nome). Favoritar ou desfavoritar reordena na hora; com o mouse sobre a lista a ordem fica congelada.
- **Git por projeto:** o chip git (ou *Ver árvore do git*) abre uma aba do projeto: a pasta que reúne os repositórios (ex.: `gsintegra` com `app` e `api`). Cada aba tem o cabeçalho do projeto (pasta, resumo dos repositórios, processos rodando na pasta) e os painéis de git dos repositórios (até 4 por vez; com mais, escolha quais exibir). O `+` lista projetos salvos e repositórios detectados; *Todos os projetos* abre a página com os projetos salvos agrupados pela pasta onde ficam. *Salvar projeto* guarda a aba; as abas abertas são lembradas entre execuções.
- **Iniciar back e front:** no cabeçalho do projeto, cada repositório mostra os processos salvos dentro dele com ▶ Iniciar / ■ Parar. *Iniciar tudo* inicia os salvos parados do projeto; *Parar tudo* pede confirmação. Repositório sem processo salvo tem *Adicionar comando* (abre o formulário já preenchido, com sugestões a partir dos scripts do `package.json`).
- **Fixar à direita:** o botão *Fixar à direita* (ou `Alt+G`) divide a janela: lista de processos à esquerda, git à direita. Arraste a divisória (ou use `←` `→` nela) para mudar a largura; a aba *Git* da barra principal some. Em janela estreita (menos de 760 px) o painel vira uma sobreposição recolhível, aberta pela aba *Git* na borda direita (o painel fixado tem no mínimo 440 px e a lista 320 px) e fechada com `Esc`.
- **Terminais embutidos:** na aba do projeto há uma faixa **Terminais** (abaixo dos painéis de git) com uma aba por terminal, bolinha de estado (rodando, parado, saiu com código), × (pede confirmação se estiver rodando), reiniciar, limpar a tela e recolher. O `+` abre um shell na pasta do projeto ou em cada repositório dele. Arraste a borda de cima da faixa (ou use `↑` `↓` nela) para mudar a altura; altura e estado aberta/recolhida são lembrados por projeto. Processos salvos em modo *Terminal no app* abrem (ou focam) a aba deles ao iniciar; os que não pertencem a nenhum projeto aberto aparecem numa faixa na aba *Salvos* (botão *Abrir terminal* do card). Só o terminal visível fica montado; ao trocar de projeto e voltar, a tela é reconstruída a partir do buffer do host. `Ctrl+C` copia quando há seleção (senão envia ^C), `Ctrl+V` cola, clique direito copia a seleção ou cola (se o sistema permitir ler a área de transferência).
- **Modo de abertura do processo salvo:** *Terminal no app* (padrão), *Janela de console separada* ou *Oculto*.
- **Worktrees git:** em cada repositório do projeto há a seção recolhível *Worktrees (N)*, com branch, pasta, mudanças e selos *rodando*, *bloqueado* e *órfão*. Cada worktree abre numa aba própria, logo depois da aba do projeto, com o rótulo `projeto · repo ⎇ branch`; ela tem o painel git do worktree, a faixa de terminais própria e a lista *Rodar aqui* com os comandos salvos do repositório principal (o comando roda dentro do worktree). *Novo worktree* cria a partir de uma branch existente (local ou remota; branch em uso em outro worktree aparece desabilitada) ou de uma nova branch, com prévia editável da pasta (`<projeto>.worktrees\<repo>-<branch>`), cópia dos `.env` do repositório principal, abertura em nova aba e comando salvo opcional. Também dá para criar o worktree de uma branch pelo popup de branches (ou abrir o worktree dela, se já estiver em uso). *Remover worktree* pergunta de novo se houver mudanças e recusa se houver processo ou terminal rodando nele; *Limpar órfãos* faz o prune. O chip git de um processo que roda dentro de um worktree abre a aba do worktree (e mostra `⎇ branch`).
- **Tema:** o botão no topo alterna automático / claro / escuro; a barra de título acompanha.

### Atalhos de teclado

| Tecla | Ação |
|---|---|
| `/` | Foca a busca |
| `Esc` | Limpa a busca / fecha o painel de detalhes |
| `↑` `↓` `Home` `End` | Navega pela lista |
| `→` `←` | Expande / recolhe o grupo |
| `Enter` ou `Espaço` | Abre os detalhes da linha |
| `←` `→` nas abas | Troca entre Aplicações, Favoritos e Salvos |
| `Alt+G` | Fixa / solta o painel git à direita |
| `Ctrl+PgDn` / `Ctrl+PgUp` | Próxima / anterior aba de projeto (com o git visível) |
| `←` `→` `Home` `End` `Delete` nas abas de projeto | Troca de aba / fecha a aba (clique do meio também fecha) |
| `←` `→` na divisória | Ajusta a largura do painel fixado (`Shift` = passo maior) |
| `Ctrl+1` a `Ctrl+4` | Foca o painel de repositório 1 a 4 |
| `↑` `↓` na borda da faixa de terminais | Ajusta a altura da faixa |
| `Ctrl+C` / `Ctrl+V` no terminal | Copia a seleção (ou envia ^C) / cola |

## Componentes de terceiros

Empacotados em `wwwroot/vendor/xterm/` (sem CDN, sem scripts de instalação); a licença MIT está em `wwwroot/vendor/xterm/LICENSE`.

| Pacote | Versão | Licença |
|---|---|---|
| @xterm/xterm | 5.5.0 | MIT |
| @xterm/addon-fit | 0.10.0 | MIT |
| @xterm/addon-webgl | 0.18.0 | MIT (com volta para o renderizador de DOM se o WebGL falhar) |

## Segurança

O conteúdo é servido por `https://monitoramento.app/` (mapeamento de host virtual para `wwwroot`), com CSP
restritiva (`script-src` só `'self'`; `style-src` aceita `'self'` e o nonce fixo de `style-nonce.js`, que o xterm.js exige para os `<style>` que cria em tempo de execução; não há `unsafe-inline`). O host cancela qualquer navegação para outra origem, bloqueia novas janelas, downloads, permissões,
arrastar-e-soltar externo e objetos de host, e só aceita mensagens da UI vindas dessa origem e com até 1 MB.
O app roda com o nível de acesso de quem o abriu (`asInvoker`); não pede elevação.

## Limitações

- **Processos elevados:** memória, CPU e nome aparecem para todos, mas a **linha de comando e a pasta de trabalho**
  de processos executados como administrador (e de alguns serviços do sistema) só podem ser lidas se o
  Monitoramento também rodar como administrador. Sem isso a UI mostra "Sem acesso".
- Encerrar processos protegidos (`System`, `csrss`, `lsass`, …) e o próprio app é recusado de propósito.
- O WebView2 mantém alguns processos filhos (`msedgewebview2`) enquanto o app está aberto, inclusive na bandeja.
- Os arquivos da interface ficam na pasta `wwwroot` ao lado do executável (não são embutidos no `.exe`).
