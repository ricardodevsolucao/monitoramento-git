using System.Drawing;
using System.Text.Json;
using Monitoramento.Core;
using Monitoramento.Core.Bridge;
using Monitoramento.Core.Models;

namespace Monitoramento.App.Hosting;

/// <summary>Ícone da bandeja: tooltip com memória/CPU, menu Abrir / Processos salvos / Sair.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly MonitorEngine _engine;
    private readonly BridgeRouter _router;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly Action _afterAction;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _savedMenu = new("Processos salvos");
    private readonly Icon _appIcon;

    public TrayController(MonitorEngine engine, BridgeRouter router, Action open, Action exit, Action afterAction)
    {
        _engine = engine;
        _router = router;
        _open = open;
        _exit = exit;
        _afterAction = afterAction;

        var openItem = new ToolStripMenuItem("Abrir", null, (_, _) => _open()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_savedMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Sair", null, (_, _) => _exit()));
        _menu.Opening += (_, _) => RebuildSavedMenu();

        _appIcon = AppIcon.Create(SystemInformation.SmallIconSize.Width);
        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Monitoramento",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _open();
            }
        };
    }

    public void UpdateTooltip(Snapshot snapshot)
    {
        var machine = snapshot.Machine;
        var text = string.Create(System.Globalization.CultureInfo.CurrentCulture,
            $"Monitoramento\nMemória {machine.MemoryPercent:0}% · CPU {machine.CpuPercent:0}%");
        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    public void ShowBalloon(string title, string text, ToolTipIcon kind = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(4000, title, text, kind);

    private void RebuildSavedMenu()
    {
        _savedMenu.DropDownItems.Clear();
        var saved = _engine.LastSnapshot?.Saved ?? [];
        if (saved.Count == 0)
        {
            _savedMenu.DropDownItems.Add(new ToolStripMenuItem("Nenhum processo salvo") { Enabled = false });
            return;
        }

        foreach (var item in saved)
        {
            var id = item.Id;
            var type = item.Running ? "stopSaved" : "startSaved";
            var label = (item.Running ? "■ Parar — " : "▶ Iniciar — ") + item.Name.Replace("&", "&&", StringComparison.Ordinal);
            _savedMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, async (_, _) => await RunAsync(type, id)));
        }
    }

    private async Task RunAsync(string type, string id)
    {
        try
        {
            var request = JsonSerializer.Serialize(new { id = "tray", type, payload = new { id } });
            var reply = await _router.HandleMessageAsync(request);
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                ShowBalloon("Monitoramento", error ?? "A operação falhou.", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Falha ao executar ação da bandeja", ex);
            ShowBalloon("Monitoramento", "Não foi possível executar a ação.", ToolTipIcon.Warning);
        }
        finally
        {
            _afterAction();
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _appIcon.Dispose();
    }
}
