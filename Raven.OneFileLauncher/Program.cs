using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Raven.OneFileLauncher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var buildId = assembly.ManifestModule.ModuleVersionId.ToString("N");
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Raven",
                "OneFile",
                buildId
            );

            using var mutex = new Mutex(false, $"Local\\RavenOneFile_{buildId}");
            if (!mutex.WaitOne(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Timed out waiting for Raven OneFile extraction.");

            try
            {
                EnsurePayloadExtracted(assembly, baseDir);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }

            var ravenExe = Path.Combine(baseDir, "Raven.exe");
            if (!File.Exists(ravenExe))
                throw new FileNotFoundException("Raven.exe is missing from the embedded payload.", ravenExe);

            var psi = new ProcessStartInfo
            {
                FileName = ravenExe,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            psi.Environment["RAVEN_ONEFILE_ROOT"] = baseDir;

            if (Process.Start(psi) == null)
                throw new InvalidOperationException("Windows could not start Raven.exe.");

            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(
                0,
                ex.Message,
                "Raven Portable - Startup failed",
                0x00000010u
            );
            return 1;
        }
    }

    private static void EnsurePayloadExtracted(Assembly assembly, string destination)
    {
        var marker = Path.Combine(destination, ".complete");
        var ravenExe = Path.Combine(destination, "Raven.exe");
        if (File.Exists(marker) && File.Exists(ravenExe))
            return;

        if (Directory.Exists(destination))
        {
            try
            {
                Directory.Delete(destination, recursive: true);
            }
            catch
            {
                // A previous Raven process may still have files open. Extract into the
                // same version directory only when it can be safely recreated.
                if (File.Exists(marker) && File.Exists(ravenExe))
                    return;
                throw;
            }
        }

        Directory.CreateDirectory(destination);

        using var payload = assembly.GetManifestResourceStream("RavenPayload.zip")
            ?? throw new InvalidOperationException("Embedded Raven payload was not found.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);

        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var outputPath = Path.GetFullPath(Path.Combine(destination, relative));

            if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in embedded payload: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var input = entry.Open();
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        File.WriteAllText(marker, "Raven OneFile payload extracted successfully.");
    }
}
