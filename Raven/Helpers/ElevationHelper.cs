using System.Diagnostics;

namespace Raven.Helpers;

public static class ElevationHelper
{
    public static bool TryRelaunchAsAdministrator(string[]? args = null)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            var argsList = args != null ? new List<string>(args) : new List<string>();
            argsList.Add("--wait-for-pid");
            argsList.Add(Environment.ProcessId.ToString());

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', argsList.Select(QuoteArg)),
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";

        return arg;
    }
}
