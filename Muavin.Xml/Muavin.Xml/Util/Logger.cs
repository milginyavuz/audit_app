// Logger.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Muavin.Xml.Util
{
    /// <summary>
    /// thread-safe debug log yazar
    /// etiketli satırlar: RUN, PARSE, ROWS, HIT/HDR, HIT/DET, MISS, INFO, ERROR
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static StreamWriter? _sw;
        private static bool _initialized;
        private static string? _currentPath;

        // gürültüyü azaltmak için isteğe bağlı önbellekler:
        private static readonly HashSet<string> _missCache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _hitHdrCache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _hitDetCache = new(StringComparer.Ordinal);

        /// <summary>
        /// örn: Init(@"C:\out\debug.txt", overwrite:true)
        /// Aynı process içinde farklı dosyaya geçmek için forceReinit=true kullan.
        /// </summary>
        public static void Init(string path, bool overwrite = true, bool forceReinit = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Logger.Init path boş olamaz.", nameof(path));

            lock (_lock)
            {
                var full = Path.GetFullPath(path);

                // aynı dosya + init edilmişse -> çık
                if (!forceReinit && _initialized && _sw != null && string.Equals(_currentPath, full, StringComparison.OrdinalIgnoreCase))
                    return;

                // farklı path’e geçilecekse mevcut dosyayı kapat
                if (_sw != null)
                {
                    try
                    {
                        WriteLineRaw($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} END =====");
                        _sw.Dispose();
                    }
                    catch { /* ignore */ }
                    _sw = null;
                    _initialized = false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);

                _sw = new StreamWriter(full, append: !overwrite,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true };

                _currentPath = full;
                _initialized = true;

                // yeni dosyada cache sıfırlayalım (gürültü kontrolü için daha doğru)
                _missCache.Clear();
                _hitHdrCache.Clear();
                _hitDetCache.Clear();

                WriteLineRaw($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} START =====");
            }
        }

        public static void WriteLine(string message) => WriteLineRaw(message);

        public static void Info(string message) => WriteTagged("INFO", message);
        public static void Error(string message) => WriteTagged("ERROR", message);

        public static void Run(string message) => WriteTagged("RUN", message);
        public static void Parse(string message) => WriteTagged("PARSE", message);
        public static void Rows(string message) => WriteTagged("ROWS", message);

        /// <summary> header tarafında eşleşen path </summary>
        public static void HitHeader(string path)
        {
            if (!_hitHdrCache.Add(path)) return;
            WriteTagged("HIT/HDR", path);
        }

        /// <summary> detail tarafında eşleşen path </summary>
        public static void HitDetail(string path)
        {
            if (!_hitDetCache.Add(path)) return;
            WriteTagged("HIT/DET", path);
        }

        /// <summary> eşleşmeyen path </summary>
        public static void Miss(string path)
        {
            // ✅ ilk kez görüyorsak yaz; daha önce gördüysek sus
            if (!_missCache.Add(path)) return;
            WriteTagged("MISS", path);
        }

        public static void Close()
        {
            lock (_lock)
            {
                if (_sw != null)
                {
                    try { WriteLineRaw($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} END ====="); } catch { }
                    try { _sw.Dispose(); } catch { }
                    _sw = null;
                }

                _initialized = false;
                _currentPath = null;
            }
        }

        private static void WriteTagged(string tag, string message)
            => WriteLineRaw($"[{tag}] {message}");

        private static void WriteLineRaw(string line)
        {
            lock (_lock)
            {
                if (_sw == null) return;
                _sw.WriteLine(line);
            }
        }
    }
}