using System.Text;

namespace Monitoramento.App.Hosting;

/// <summary>Log simples em arquivo diário. Nunca lança: falhar ao registrar não pode derrubar o app.</summary>
internal static class CrashLog
{
    private const int RetentionDays = 14;
    private static readonly object Gate = new();

    public static void Write(string context, Exception? exception)
    {
        try
        {
            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(context).AppendLine("]");
            if (exception is not null)
            {
                text.AppendLine(exception.ToString());
            }

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyy-MM-dd}.log"), text.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // sem alternativa: o log é o último recurso
        }
    }

    public static void PruneOld()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Logs))
            {
                return;
            }

            var limit = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(AppPaths.Logs, "*.log").Where(f => File.GetLastWriteTime(f) < limit))
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
            // limpeza é opcional
        }
    }
}
