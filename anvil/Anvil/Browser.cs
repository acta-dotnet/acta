using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anvil;

/// <summary>
/// Opens the default browser at the dashboard url so the demo is one command. Best-effort: a launch
/// failure (headless box, no browser) is swallowed and the url is left for the operator to open.
/// </summary>
public static class Browser
{
    public static void TryOpen(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // No browser to launch; the caller already printed the url.
        }
    }
}
