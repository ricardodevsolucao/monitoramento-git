using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Monitoramento.App.Hosting;
using Monitoramento.Core;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;

namespace Monitoramento.App;

internal sealed class MainForm : Form
{
    private const string Origin = "https://monitoramento.app/";
    private const string HostName = "monitoramento.app";
    private const int MaxMessageChars = 1024 * 1024;
    private const int ReleaseDelayMs = 30_000;
    private const int DefaultWidth = 1100;
    private const int DefaultHeight = 720;

    private readonly MonitorEngine _engine;
    private readonly BridgeRouter _router;
    private WebView2? _web;
    private CoreWebView2Environment? _environment;
    private Task? _creation;
    private readonly System.Windows.Forms.Timer _releaseTimer = new() { Interval = ReleaseDelayMs };
    private readonly TrayController _tray;
    private readonly SnapshotPump _pump;
    private readonly TerminalPump _terminalPump;
    private readonly Icon _formIcon;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _startMaximized;
    private bool _dark;
    private string _appliedTheme = string.Empty;
    private int _postPending;
    private volatile bool _active;

    public MainForm(MonitorEngine engine)
    {
        _engine = engine;
        _router = new BridgeRouter(engine);

        Text = "Monitoramento";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 560);
        _formIcon = AppIcon.Create(32);
        Icon = _formIcon;
        RestoreWindow();

        _releaseTimer.Tick += (_, _) => ReleaseWebViewIfHidden();
        ApplyTheme();

        _tray = new TrayController(engine, _router, ShowFromTray, ExitApplication, () => _pump?.WakeUp());
        _pump = new SnapshotPump(engine, _router, () => _active, PublishSnapshot, _tray.UpdateTooltip);
        _terminalPump = new TerminalPump(_router, PublishTerminalMessages);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void RestoreWindow()
    {
        var saved = WindowStateStore.Load();
        _trayHintShown = saved?.TrayHintShown ?? false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(DefaultWidth, DefaultHeight);
        if (saved is null || saved.Width < MinimumSize.Width || saved.Height < MinimumSize.Height)
        {
            return;
        }

        var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            _startMaximized = saved.Maximized;
        }
    }

    private void SaveWindow()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowStateStore.Save(new SavedWindow(
            bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized, _trayHintShown));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetDarkTitleBar(Handle, _dark);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        UpdateActive();
        try
        {
            await EnsureWebViewAsync();
            _pump.Start();
            _terminalPump.Start();
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex);
        }
    }

    /// <summary>Cria o WebView2 (se ainda não existe) e navega para a UI. Chamadas simultâneas compartilham a mesma tarefa.</summary>
    private Task EnsureWebViewAsync()
    {
        if (_web is not null)
        {
            return _creation ?? Task.CompletedTask;
        }

        _creation = CreateWebViewAsync();
        return _creation;
    }

    private async Task CreateWebViewAsync()
    {
        var web = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false,
            Visible = true,
            BackColor = ThemeColors.Background(_dark),
            DefaultBackgroundColor = ThemeColors.Background(_dark),
        };
        _web = web;
        Controls.Add(web);

        try
        {
            Directory.CreateDirectory(AppPaths.WebViewData);
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
            if (!ReferenceEquals(_web, web) || web.IsDisposed)
            {
                return;
            }

            _environment = environment;
            await web.EnsureCoreWebView2Async(environment);
            if (!ReferenceEquals(_web, web) || web.IsDisposed)
            {
                return;
            }
        }
        catch
        {
            if (ReferenceEquals(_web, web))
            {
                DiscardWebView();
            }

            throw;
        }

        var core = web.CoreWebView2;
        var settings = core.Settings;
#if DEBUG
        const bool Developer = true;
#else
        const bool Developer = false;
#endif
        settings.AreDevToolsEnabled = Developer;
        settings.AreDefaultContextMenusEnabled = Developer;
        settings.AreBrowserAcceleratorKeysEnabled = Developer;
        settings.AreHostObjectsAllowed = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(HostName, AppPaths.WwwRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnWebViewProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;

        core.Navigate(Origin + "index.html");
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) =>
        args.State = CoreWebView2PermissionState.Deny;

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    /// <summary>Descarta o controle e o ambiente: o msedgewebview2 encerra quando nenhuma referência resta.</summary>
    private void DiscardWebView()
    {
        var web = _web;
        _web = null;
        _environment = null;
        _creation = null;
        if (web is null)
        {
            return;
        }

        if (web.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.ProcessFailed -= OnWebViewProcessFailed;
            core.WebMessageReceived -= OnWebMessageReceived;
        }

        Controls.Remove(web);
        web.Dispose();
    }

    private void ReleaseWebViewIfHidden()
    {
        _releaseTimer.Stop();
        if (Visible || _exiting || _web is null)
        {
            return;
        }

        DiscardWebView();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (!args.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var json = ReadMessage(args);
            if (json is null || json.Length > MaxMessageChars)
            {
                return;
            }

            var reply = await _router.HandleMessageAsync(json);
            _web?.CoreWebView2?.PostWebMessageAsString(reply);
            AfterMessage();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Falha ao tratar mensagem da UI", ex);
        }
    }

    private static string? ReadMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() is { } text)
            {
                return text;
            }
        }
        catch (ArgumentException)
        {
            // a UI envia objeto, não string: lê como JSON
        }

        return args.WebMessageAsJson;
    }

    /// <summary>Config pode ter mudado (setSettings): reaplica tema e acorda o laço para respeitar o novo intervalo.</summary>
    private void AfterMessage()
    {
        if (!string.Equals(_appliedTheme, _engine.Config.Settings.Theme, StringComparison.Ordinal))
        {
            ApplyTheme();
        }

        _pump.WakeUp();
    }

    private void PublishSnapshot(string message)
    {
        if (IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref _postPending, 1) == 1)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    _web?.CoreWebView2?.PostWebMessageAsString(message);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // webview encerrando
                }
                finally
                {
                    Volatile.Write(ref _postPending, 0);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Volatile.Write(ref _postPending, 0);
        }
    }

    /// <summary>
    /// Saída dos terminais: posta tudo, em ordem (sem descartar como o snapshot). Sem WebView (bandeja/liberada) não
    /// empurra nada; a UI recupera o recente com termAttach ao voltar.
    /// </summary>
    private Task PublishTerminalMessages(IReadOnlyList<string> messages)
    {
        if (IsDisposed || !IsHandleCreated || _web is null)
        {
            return Task.CompletedTask;
        }

        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (_web?.CoreWebView2 is { } core)
                    {
                        foreach (var message in messages)
                        {
                            core.PostWebMessageAsString(message);
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // webview encerrando
                }
                finally
                {
                    posted.TrySetResult();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            posted.TrySetResult(); // janela encerrando
        }

        return posted.Task;
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        CrashLog.Write($"WebView2 falhou: {args.ProcessFailedKind} ({args.Reason})", null);
        if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            BeginInvoke(() => _web?.CoreWebView2?.Reload());
        }
    }

    // ---------- tema ----------

    private void ApplyTheme()
    {
        var theme = _engine.Config.Settings.Theme;
        _appliedTheme = theme;
        _dark = ThemeColors.IsDark(theme);
        var background = ThemeColors.Background(_dark);
        BackColor = background;
        if (_web is not null)
        {
            _web.BackColor = background;
            _web.DefaultBackgroundColor = background;
        }

        if (IsHandleCreated)
        {
            Native.SetDarkTitleBar(Handle, _dark);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && !IsDisposed && IsHandleCreated
            && string.Equals(_engine.Config.Settings.Theme, "system", StringComparison.Ordinal))
        {
            BeginInvoke(ApplyTheme);
        }
    }

    // ---------- visibilidade, bandeja e saída ----------

    private void UpdateActive()
    {
        var active = Visible && WindowState != FormWindowState.Minimized;
        if (active == _active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            _pump?.WakeUp();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _releaseTimer.Stop();
        }

        UpdateActive();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateActive();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // O WinForms rotula um WM_CLOSE cru (Process.CloseMainWindow, taskkill sem /f, "Finalizar tarefa") como
        // TaskManagerClosing, e o da barra/Alt+F4 como UserClosing: todos vão para a bandeja. Só o desligamento
        // do Windows e Application.Exit encerram (o resto: menu Sair ou encerramento forçado do processo).
        if (!_exiting && IsTrayCloseReason(e.CloseReason) && _engine.Config.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            SaveWindow();
            Hide();
            _releaseTimer.Stop();
            _releaseTimer.Start();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                SaveWindow();
                _tray.ShowBalloon("Monitoramento continua ativo", "O aplicativo foi para a bandeja. Use o ícone para abrir ou sair.");
            }

            return;
        }

        // sem bandeja, fechar pela janela também encerra os terminais: confirma como no "Sair"
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && !ConfirmExitWithTerminals())
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>
    /// Com terminais rodando, pergunta antes de sair (eles serão encerrados). Desligamento do Windows não passa aqui.
    /// </summary>
    private bool ConfirmExitWithTerminals()
    {
        var running = _engine.RunningTerminals;
        if (running == 0)
        {
            return true;
        }

        var text = running == 1
            ? "1 terminal rodando será encerrado. Sair mesmo assim?"
            : $"{running} terminais rodando serão encerrados. Sair mesmo assim?";
        var answer = MessageBox.Show(text, "Monitoramento", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    private static bool IsTrayCloseReason(CloseReason reason) =>
        reason is not (CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SaveWindow();
        _active = false;
        _terminalPump.Dispose();
        _engine.CloseTerminals(); // Sair confirmado, desligamento ou Application.Exit: mata as árvores e fecha os pseudo-consoles
        _pump.Dispose();
        _tray.Dispose();
        _releaseTimer.Dispose();
        DiscardWebView();
        _formIcon.Dispose();
        base.OnFormClosed(e);
    }

    public void ShowFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ShowFromTray);
            return;
        }

        _releaseTimer.Stop();
        _ = RecreateWebViewAsync();
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = _startMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        Activate();
        BringToFront();
        Native.BringToForeground(Handle);
    }

    private async Task RecreateWebViewAsync()
    {
        if (_web is not null || _exiting || IsDisposed)
        {
            return;
        }

        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Falha ao recriar o WebView2", ex);
        }
    }

    private void ExitApplication()
    {
        if (!ConfirmExitWithTerminals())
        {
            return;
        }

        _exiting = true;
        Close();
    }

    // ---------- falhas de inicialização ----------

    private void HandleStartupFailure(Exception ex)
    {
        CrashLog.Write("Falha ao iniciar o WebView2", ex);
        _exiting = true;
        if (ex is WebView2RuntimeNotFoundException)
        {
            Program.ShowMissingRuntime();
        }
        else
        {
            MessageBox.Show(
                this,
                "Não foi possível iniciar o componente de exibição (WebView2). Detalhes foram gravados em:\n" + AppPaths.Logs,
                "Monitoramento",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        Close();
    }

    internal static void OpenDownloadPage()
    {
        using var process = Process.Start(new ProcessStartInfo(Program.WebViewDownloadUrl) { UseShellExecute = true });
    }
}
