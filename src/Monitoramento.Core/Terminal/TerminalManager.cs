using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Monitoramento.Core.Actions;
using Monitoramento.Core.Git;
using Monitoramento.Core.Launcher;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Terminal;

/// <summary>Evento para empurrar à UI (<c>termOutput</c> / <c>termExit</c>).</summary>
internal abstract record TerminalEvent(string Id);

/// <param name="Seq">Total de caracteres produzidos pelo terminal até o fim de <paramref name="Data"/>.</param>
internal sealed record TerminalOutputEvent(string Id, string Data, long Seq) : TerminalEvent(Id);

internal sealed record TerminalExitEvent(string Id, int ExitCode) : TerminalEvent(Id);

/// <summary>Linha de comando de um shell ou do cmd de um processo salvo.</summary>
internal sealed record TerminalLaunch(string ApplicationName, string CommandLine, IReadOnlyDictionary<string, string> Environment);

/// <summary>Resultado de iniciar um processo salvo num terminal.</summary>
internal sealed record SavedTerminalStart(TerminalInfo Terminal, StartedProcess Process);

/// <summary>
/// Terminais embutidos (ConPTY) do app: no máximo <see cref="MaxTerminals"/>, cada um com o anel de saída
/// (<see cref="TerminalOutputBuffer"/>). Só aceita ids criados aqui; o comando de um terminal vem do app (shell padrão
/// ou cmd do processo salvo), nunca da UI. A saída é acumulada em eventos que o host recolhe com <see cref="TakeEvents"/>.
/// </summary>
internal sealed class TerminalManager : ISavedTerminalHost, IDisposable
{
    public const int MaxTerminals = 16;
    public const int MinCols = 20;
    public const int MaxCols = 500;
    public const int MinRows = 5;
    public const int MaxRows = 200;
    public const int DefaultCols = 120;
    public const int DefaultRows = 30;
    public const int MaxInputBytes = 64 * 1024;

    /// <summary>Caracteres por mensagem <c>termOutput</c> (mesmo com escapes JSON fica abaixo de 256 KB).</summary>
    public const int MaxMessageChars = 32 * 1024;

    /// <summary>Saída ainda não recolhida pelo host; acima disso a mais antiga é descartada (a UI usa termAttach).</summary>
    public const long MaxPendingChars = 2 * 1024 * 1024;

    public const string NotFoundError = "Terminal não encontrado.";
    public const string NotRunningError = "O terminal não está rodando.";
    public const string UnsupportedError = "Terminais embutidos precisam do Windows 10 versão 1809 ou mais recente.";
    public const string AlreadyRunningError = "Este processo salvo já está em execução.";
    public const string SavedRestartError = "Este terminal é de um processo salvo; use Iniciar/Parar do processo salvo.";
    public const string SavedDeletedError = "O processo salvo foi excluído; feche este terminal.";
    public static readonly string LimitError = $"Limite de {MaxTerminals} terminais abertos. Feche um terminal para abrir outro.";
    public static readonly string InputTooLargeError = $"Entrada grande demais para o terminal (limite de {MaxInputBytes / 1024} KB).";

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(3);

    private readonly IPtyFactory _factory;
    private readonly Func<TerminalLaunch> _shell;
    private readonly TimeProvider _time;
    private readonly int _bufferCapacity;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Lock _eventGate = new();
    private readonly List<TerminalEvent> _events = [];
    private long _pendingChars;
    private bool _disposed;

    /// <param name="shell">Shell padrão do <c>termOpen</c> (pwsh ou powershell).</param>
    public TerminalManager(IPtyFactory factory, Func<TerminalLaunch> shell, TimeProvider? time = null, int bufferCapacity = TerminalOutputBuffer.DefaultCapacity)
    {
        _factory = factory;
        _shell = shell;
        _time = time ?? TimeProvider.System;
        _bufferCapacity = bufferCapacity;
    }

    public bool IsSupported => _factory.IsSupported;

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count(IsAlive);
            }
        }
    }

    /// <summary><c>termOpen</c>: shell padrão na pasta (já aceita pela regra de pastas).</summary>
    public OperationResult<TerminalInfo> Open(string folder, int? cols, int? rows)
    {
        if (!_factory.IsSupported)
        {
            return OperationResult<TerminalInfo>.Failure(UnsupportedError);
        }

        lock (_gate)
        {
            if (_entries.Count >= MaxTerminals)
            {
                return OperationResult<TerminalInfo>.Failure(LimitError);
            }

            var entry = new Entry(NewId(), GitWorkspaceRules.FolderName(folder), folder, null, _shell(), new TerminalOutputBuffer(_bufferCapacity))
            {
                Cols = ClampCols(cols),
                Rows = ClampRows(rows),
            };
            var error = StartLocked(entry);
            if (error is not null)
            {
                return OperationResult<TerminalInfo>.Failure(error);
            }

            _entries.Add(entry);
            return OperationResult<TerminalInfo>.Success(Info(entry, null));
        }
    }

    /// <summary>
    /// Inicia o processo salvo num terminal. Reaproveita (limpa e reinicia) o terminal parado do mesmo salvo em vez de
    /// criar outro.
    /// </summary>
    public OperationResult<SavedTerminalStart> StartSaved(SavedProcess saved, TerminalLaunch launch, string folder)
    {
        if (!_factory.IsSupported)
        {
            return OperationResult<SavedTerminalStart>.Failure(UnsupportedError);
        }

        lock (_gate)
        {
            var entry = _entries.Find(e => string.Equals(e.SavedId, saved.Id, StringComparison.Ordinal));
            if (entry is { Running: true })
            {
                return OperationResult<SavedTerminalStart>.Failure(AlreadyRunningError);
            }

            var isNew = entry is null;
            if (isNew && _entries.Count >= MaxTerminals)
            {
                return OperationResult<SavedTerminalStart>.Failure(LimitError);
            }

            entry ??= new Entry(NewId(), saved.Name, folder, saved.Id, launch, new TerminalOutputBuffer(_bufferCapacity))
            {
                Cols = DefaultCols,
                Rows = DefaultRows,
            };
            entry.Title = string.IsNullOrWhiteSpace(saved.Name) ? GitWorkspaceRules.FolderName(folder) : saved.Name.Trim();
            entry.Folder = folder;
            entry.Launch = launch;
            if (!isNew)
            {
                DisposeSession(entry);
                entry.Buffer.Clear();
            }

            var error = StartLocked(entry);
            if (error is not null)
            {
                return OperationResult<SavedTerminalStart>.Failure(error);
            }

            if (isNew)
            {
                _entries.Add(entry);
            }

            return OperationResult<SavedTerminalStart>.Success(
                new SavedTerminalStart(Info(entry, null), new StartedProcess(entry.Pid ?? 0, entry.CreateTime)));
        }
    }

    /// <summary>Terminal que roda um comando montado pelo app (ex.: processo salvo num worktree), sem vínculo com o salvo.</summary>
    public OperationResult<TerminalInfo> OpenCommand(string title, string folder, TerminalLaunch launch, int? cols, int? rows)
    {
        if (!_factory.IsSupported)
        {
            return OperationResult<TerminalInfo>.Failure(UnsupportedError);
        }

        lock (_gate)
        {
            if (_entries.Count >= MaxTerminals)
            {
                return OperationResult<TerminalInfo>.Failure(LimitError);
            }

            var entry = new Entry(NewId(), title, folder, null, launch, new TerminalOutputBuffer(_bufferCapacity))
            {
                Cols = ClampCols(cols),
                Rows = ClampRows(rows),
            };
            var error = StartLocked(entry);
            if (error is not null)
            {
                return OperationResult<TerminalInfo>.Failure(error);
            }

            _entries.Add(entry);
            return OperationResult<TerminalInfo>.Success(Info(entry, null));
        }
    }

    /// <summary>Processo salvo excluído: a aba fica, mas sem vínculo nem comando (não reinicia).</summary>
    public void Unlink(string savedId)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Where(e => string.Equals(e.SavedId, savedId, StringComparison.Ordinal)))
            {
                entry.SavedId = null;
                entry.Launch = null;
            }
        }
    }

    /// <summary><c>termInput</c>: só para terminal existente e rodando; até 64 KB (UTF-8).</summary>
    public OperationResult<bool> Input(string? id, string? data)
    {
        if (data is null || Encoding.UTF8.GetByteCount(data) > MaxInputBytes)
        {
            return OperationResult<bool>.Failure(data is null ? "Informe os dados para o terminal." : InputTooLargeError);
        }

        IPtySession? session;
        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null)
            {
                return OperationResult<bool>.Failure(NotFoundError);
            }

            if (!entry.Running || entry.Session is null)
            {
                return OperationResult<bool>.Failure(NotRunningError);
            }

            session = entry.Session;
        }

        try
        {
            session.Write(Encoding.UTF8.GetBytes(data));
            return OperationResult<bool>.Success(true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return OperationResult<bool>.Failure(NotRunningError); // o processo terminou entre a checagem e a escrita
        }
    }

    /// <summary><c>termResize</c>: tamanho limitado a 20..500 colunas e 5..200 linhas.</summary>
    public OperationResult<bool> Resize(string? id, int? cols, int? rows)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null)
            {
                return OperationResult<bool>.Failure(NotFoundError);
            }

            entry.Cols = ClampCols(cols ?? entry.Cols);
            entry.Rows = ClampRows(rows ?? entry.Rows);
            if (entry.Running && entry.Session is { } session)
            {
                try
                {
                    session.Resize(entry.Cols, entry.Rows);
                }
                catch (Win32Exception ex)
                {
                    Trace.TraceWarning($"Monitoramento: falha ao redimensionar terminal: {ex.NativeErrorCode}");
                }
            }

            return OperationResult<bool>.Success(true);
        }
    }

    /// <summary><c>termAttach</c>: conteúdo recente (anel) e o seq do último caractere incluído.</summary>
    public OperationResult<(string Data, long Seq)> Attach(string? id)
    {
        lock (_gate)
        {
            var entry = Find(id);
            return entry is null
                ? OperationResult<(string, long)>.Failure(NotFoundError)
                : OperationResult<(string, long)>.Success(entry.Buffer.Snapshot());
        }
    }

    /// <summary><c>termRestart</c> de um shell: encerra a árvore (se rodando), limpa o anel e roda o mesmo comando.</summary>
    public OperationResult<TerminalInfo> Restart(string? id)
    {
        lock (_gate)
        {
            var target = Find(id);
            if (target is { SavedId: not null })
            {
                return OperationResult<TerminalInfo>.Failure(SavedRestartError); // salvo: reinicia pelo Parar + Iniciar
            }

            if (target is { Launch: null })
            {
                return OperationResult<TerminalInfo>.Failure(SavedDeletedError);
            }
        }

        if (!StopAndWait(id, out var error))
        {
            return OperationResult<TerminalInfo>.Failure(error!);
        }

        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null)
            {
                return OperationResult<TerminalInfo>.Failure(NotFoundError);
            }

            if (entry.Running)
            {
                return OperationResult<TerminalInfo>.Failure("O terminal ainda está encerrando. Tente de novo em instantes.");
            }

            DisposeSession(entry);
            entry.Buffer.Clear();
            var startError = StartLocked(entry);
            return startError is null ? OperationResult<TerminalInfo>.Success(Info(entry, null)) : OperationResult<TerminalInfo>.Failure(startError);
        }
    }

    /// <summary>Encerra a árvore do terminal (se rodando) e espera o fim, até 3 s.</summary>
    public bool StopAndWait(string? id, out string? error)
    {
        IPtySession? session;
        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null)
            {
                error = NotFoundError;
                return false;
            }

            session = IsAlive(entry) ? entry.Session : null;
        }

        error = null;
        if (session is not null)
        {
            KillQuietly(session);
            session.WaitForExit(KillWait);
        }

        return true;
    }

    /// <summary><c>termClose</c>: encerra a árvore (se rodando) e remove o terminal.</summary>
    public OperationResult<bool> Close(string? id)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(id);
            if (entry is null)
            {
                return OperationResult<bool>.Failure(NotFoundError);
            }

            _entries.Remove(entry);
            entry.Generation++; // eventos tardios da sessão antiga são ignorados
        }

        if (entry.Session is { } session)
        {
            KillQuietly(session);
            session.WaitForExit(KillWait);
            session.Dispose();
        }

        return OperationResult<bool>.Success(true);
    }

    /// <summary>Terminal pelo id, ou null.</summary>
    public TerminalInfo? Get(string? id, Func<string, string?> projectFolderOf)
    {
        lock (_gate)
        {
            return Find(id) is { } entry ? Info(entry, projectFolderOf(entry.Folder)) : null;
        }
    }

    /// <summary>Terminais vivos (rodando ou com descendentes vivos): título e pasta.</summary>
    public IReadOnlyList<(string Title, string Folder)> AliveTerminals()
    {
        lock (_gate)
        {
            return _entries.Where(IsAlive).Select(e => (e.Title, e.Folder)).ToList();
        }
    }

    /// <summary>Terminal do processo salvo (rodando ou parado), ou null.</summary>
    public string? TerminalIdForSaved(string savedId)
    {
        lock (_gate)
        {
            return _entries.Find(e => string.Equals(e.SavedId, savedId, StringComparison.Ordinal))?.Id;
        }
    }

    /// <param name="projectFolderOf">Pasta do projeto salvo que contém a pasta do terminal, ou null.</param>
    public IReadOnlyList<TerminalInfo> List(Func<string, string?> projectFolderOf)
    {
        lock (_gate)
        {
            return _entries.Select(e => Info(e, projectFolderOf(e.Folder))).ToList();
        }
    }

    /// <summary>Recolhe os eventos pendentes, juntando saídas seguidas do mesmo terminal (até 32 Ki caracteres cada).</summary>
    public IReadOnlyList<TerminalEvent> TakeEvents()
    {
        List<TerminalEvent> taken;
        lock (_eventGate)
        {
            if (_events.Count == 0)
            {
                return [];
            }

            taken = [.. _events];
            _events.Clear();
            _pendingChars = 0;
        }

        var merged = new List<TerminalEvent>(taken.Count);
        StringBuilder? text = null;
        TerminalOutputEvent? head = null;
        foreach (var item in taken)
        {
            if (item is TerminalOutputEvent output && head is not null && text is not null
                && string.Equals(head.Id, output.Id, StringComparison.Ordinal) && text.Length + output.Data.Length <= MaxMessageChars)
            {
                text.Append(output.Data);
                head = output;
                continue;
            }

            if (head is not null)
            {
                merged.Add(new TerminalOutputEvent(head.Id, text!.ToString(), head.Seq));
                head = null;
                text = null;
            }

            if (item is TerminalOutputEvent next)
            {
                head = next;
                text = new StringBuilder(next.Data, MaxMessageChars);
            }
            else
            {
                merged.Add(item);
            }
        }

        if (head is not null)
        {
            merged.Add(new TerminalOutputEvent(head.Id, text!.ToString(), head.Seq));
        }

        return merged;
    }

    /// <summary>Encerra todos os terminais (saída do app): mata as árvores e fecha os pseudo-consoles.</summary>
    public void CloseAll()
    {
        List<Entry> entries;
        lock (_gate)
        {
            entries = [.. _entries];
            _entries.Clear();
            foreach (var entry in entries)
            {
                entry.Generation++;
            }
        }

        var sessions = entries.Select(e => e.Session).OfType<IPtySession>().ToList();
        foreach (var session in sessions)
        {
            KillQuietly(session);
        }

        // um prazo só para todos (não 3 s por terminal)
        var deadline = Stopwatch.GetTimestamp() + (long)(KillWait.TotalSeconds * Stopwatch.Frequency);
        foreach (var session in sessions)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            session.WaitForExit(remaining);
            session.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
    }

    public static int ClampCols(int? cols) => Math.Clamp(cols ?? DefaultCols, MinCols, MaxCols);

    public static int ClampRows(int? rows) => Math.Clamp(rows ?? DefaultRows, MinRows, MaxRows);

    /// <summary>Inicia a sessão do <paramref name="entry"/> (dentro do lock). Devolve o erro em português, ou null.</summary>
    private string? StartLocked(Entry entry)
    {
        if (entry.Launch is null)
        {
            return SavedDeletedError;
        }

        var generation = ++entry.Generation;
        var info = new PtyStartInfo(entry.Launch.ApplicationName, entry.Launch.CommandLine, entry.Folder, entry.Launch.Environment, entry.Cols, entry.Rows);
        try
        {
            var session = _factory.Start(info, bytes => OnOutput(entry, generation, bytes), code => OnExit(entry, generation, code));
            entry.Session = session;
            entry.Running = true;
            entry.ExitCode = null;
            entry.Pid = session.Pid;
            entry.CreateTime = session.CreateTime;
            entry.StartedAt = _time.GetUtcNow();
            return null;
        }
        catch (Win32Exception ex)
        {
            entry.Session = null;
            entry.Running = false;
            return $"Não foi possível iniciar o terminal (erro {ex.NativeErrorCode}).";
        }
    }

    private void OnOutput(Entry entry, int generation, ReadOnlyMemory<byte> bytes)
    {
        if (Volatile.Read(ref entry.Generation) != generation)
        {
            return;
        }

        var (text, seq) = entry.Buffer.Append(bytes.Span);
        Enqueue(entry.Id, text, seq);
    }

    private void OnExit(Entry entry, int generation, int exitCode)
    {
        lock (_gate)
        {
            if (entry.Generation != generation)
            {
                return;
            }

            entry.Running = false;
            entry.ExitCode = exitCode;
        }

        var (text, seq) = entry.Buffer.Flush();
        Enqueue(entry.Id, text, seq);
        lock (_eventGate)
        {
            _events.Add(new TerminalExitEvent(entry.Id, exitCode));
        }
    }

    private void Enqueue(string id, string text, long seq)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_eventGate)
        {
            _events.Add(new TerminalOutputEvent(id, text, seq));
            _pendingChars += text.Length;

            // ninguém recolhendo (host travado): descarta a saída mais antiga; a UI percebe o salto do seq e usa termAttach
            while (_pendingChars > MaxPendingChars)
            {
                var index = _events.FindIndex(e => e is TerminalOutputEvent);
                if (index < 0)
                {
                    break;
                }

                _pendingChars -= ((TerminalOutputEvent)_events[index]).Data.Length;
                _events.RemoveAt(index);
            }
        }
    }

    /// <summary>Rodando, ou o raiz terminou mas ainda há descendentes vivos na árvore.</summary>
    private static bool IsAlive(Entry entry)
    {
        if (entry.Running)
        {
            return true;
        }

        try
        {
            return entry.Session?.HasLiveProcesses == true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private Entry? Find(string? id) =>
        id is null ? null : _entries.Find(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    private static void DisposeSession(Entry entry)
    {
        entry.Session?.Dispose();
        entry.Session = null;
    }

    private static void KillQuietly(IPtySession session)
    {
        try
        {
            session.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            Trace.TraceWarning($"Monitoramento: falha ao encerrar terminal: {ex.GetType().Name}");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private TerminalInfo Info(Entry entry, string? projectFolder) => new(
        entry.Id,
        entry.Title,
        entry.Folder,
        projectFolder,
        entry.SavedId,
        entry.Pid,
        entry.Running,
        entry.ExitCode,
        entry.StartedAt.ToString("o"),
        entry.Cols,
        entry.Rows);

    private sealed class Entry(string id, string title, string folder, string? savedId, TerminalLaunch launch, TerminalOutputBuffer buffer)
    {
        public int Generation;

        public string Id { get; } = id;

        public string Title { get; set; } = title;

        public string Folder { get; set; } = folder;

        public string? SavedId { get; set; } = savedId;

        /// <summary>Null depois de o processo salvo ser excluído (a aba não reinicia mais).</summary>
        public TerminalLaunch? Launch { get; set; } = launch;

        public TerminalOutputBuffer Buffer { get; } = buffer;

        public IPtySession? Session { get; set; }

        public bool Running { get; set; }

        public int? ExitCode { get; set; }

        public int? Pid { get; set; }

        public long CreateTime { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public int Cols { get; set; }

        public int Rows { get; set; }
    }
}
