using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Reflection.Metadata.Ecma335;


namespace Muavin.Xml.Util
{
    /// <summary> 
    /// Tek dosyaya thread-safe debug log yazar.
    /// Etiketli satırlar: RUN, PARSE, ROWS, HIT/HDR, HIT/DET, MISS, INFO, ERROR
    /// </summary>

    public static class Logger
    {
        private static readonly object _lock = new();
        private static StreamWriter? _sw;
        private static bool _initialized;

        ///Tek dosyada gürültüyü azaltmak için isteğe bağlu "aynı satırı bir kere yaz" önbellekleri:
        private static readonly HashSet<string> _missCache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _hitHdrCache = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _hitDetCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Örn: Init(@"C:\out\debug.txt", overwrite:true)
        /// </summary>
        public static void Init(string path, bool overwrite = true)
        {
            lock (_lock)
            {
                if (_initialized && _sw != null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

                _sw = new StreamWriter(path, append: !overwrite,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true };

                _initialized = true;

                WriteLineRaw($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} START =====");

            }
        }

        /// <summary>
        ///Serbest metin satırı yazar (etiket yok) 
        /// </summary>
        
        public static void WriteLine(string message) => WriteLineRaw(message);

        /// <summary>
        /// Basit bilgi satırı
        /// </summary>
        public static void Info(string message) => WriteTagged("INFO", message);

        /// <summary>
        /// Hata satırı
        /// </summary>
        public static void Error(string message) => WriteTagged("ERROR" ,message);

        /// <summary>
        /// Koşu/çalıştırma başlıkları
        /// </summary>
        public static void Run(string message) => WriteTagged("RUN" ,message);
        public static void Parse(string message) => WriteTagged("PARSE", message);
        public static void Rows(string message) => WriteTagged("ROWS", message);

        /// <summary>
        /// Header tarafında eşleşen path
        /// </summary>
        public static void HitHeader(string path)
        {
            if (!_hitHdrCache.Add(path)) return;
            WriteTagged("HIT/HDR", path);
        }

        /// <summary>
        /// Detail tarafında eşleşen path
        /// </summary>
        
        public static void HitDetail(string path)
        {
            if (!_hitDetCache.Add(path)) return;
            WriteTagged("HIT/DET", path);
        }

        /// <summary>
        /// Eşleşmeyen path
        /// </summary>
        public static void Miss(string path)
        {
            if (_missCache.Add(path)) return;
            WriteTagged("MISS", path );
        }

        public static void Close()
        {
            lock (_lock)
            {
                if (_sw != null)
                {
                    WriteLineRaw($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} END =====");
                    _sw.Dispose();
                    _sw = null;
                    _initialized = false;
                }
                
            }
        }

        // helpers

        private static void WriteTagged(string tag, string message)
        {
            WriteLineRaw($"[{tag}] {message}");
        }

        private static void WriteLineRaw(string line)
        {
            lock ( _lock)
            {
                if (_sw == null) return;
                _sw.WriteLine(line);
            }
        }

    }
}