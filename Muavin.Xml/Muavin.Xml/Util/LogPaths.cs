// LogPaths.cs
using System;
using System.Diagnostics;
using System.IO;

namespace Muavin.Xml.Util
{
    public static class LogPaths
    {
        public static string GetLogDirectory()
        {
#if DEBUG
            // Debug: output klasörüne Logs (yazılabilir ve taşınabilir)
            var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(dir);
            return dir;
#else
            // Publish/Release: LocalAppData (Program Files yazılamaz!)
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Muavin",
                "Logs"
            );
            Directory.CreateDirectory(dir);
            return dir;
#endif
        }

        public static string NewLogFilePath(string prefix)
        {
            var dir = GetLogDirectory();
            var file = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.MachineName}_{Environment.UserName}.log";
            return Path.Combine(dir, file);
        }

        public static string GetStableLogFilePath(string fileName)
        {
            var dir = GetLogDirectory();
            return Path.Combine(dir, fileName);
        }

        public static void OpenLogFolderInExplorer()
        {
            var dir = GetLogDirectory();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}