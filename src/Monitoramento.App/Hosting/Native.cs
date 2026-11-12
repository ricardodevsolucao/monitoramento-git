using System.Runtime.InteropServices;

namespace Monitoramento.App.Hosting;

internal static class Native
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private const int AsfwAny = -1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    public static void SetDarkTitleBar(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    public static void BringToForeground(nint hwnd) => _ = SetForegroundWindow(hwnd);

    public static void AllowAnyForeground() => _ = AllowSetForegroundWindow(AsfwAny);

    public static void Destroy(nint iconHandle) => _ = DestroyIcon(iconHandle);
}
