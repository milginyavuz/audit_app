using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.HighPerformance;              // (opsiyonel) - ileride StringPool vb. kullanmak isterseniz
using CommunityToolkit.HighPerformance.Buffers;     // (opsiyonel)
using Muavin.Xml.Util;

namespace Muavin.Xml.Util
{
    /// <summary>
    /// fieldmap.json içeriğini okur ve anahtar -> XPath listesi eşlemesini sunar.
    /// Aşağıdaki yer tutucuları otomatik genişletir:
    ///  - (ROOT)           => defter  ve edefter  varyasyonları üretir
    ///  - (defter|edefter) => defter  ve edefter  varyasyonları üretir
    /// 
    /// Yükleme sırasında:
    ///  - Tüm yollar PathNormalizer.Normalize ile normalize edilir (küçük harf vb.)
    ///  - Element yollarına güvenli olması için “/#text” varyasyonu da eklenir
    ///  - Tekrarlı yollar Ordinal olarak tekilleştirilir
    /// </summary>
    public sealed class FieldMap
    {
        private readonly Dictionary<string, string[]> _map;

        private FieldMap(Dictionary<string, string[]> map) => _map = map;

        // Tekil erişim isteyenler için küçük bir cache.
        private static FieldMap? _current;
        private static readonly object _lock = new();

        /// <summary>
        /// Varsayılan konumdan (./config/fieldmap.json) veya verilen path’ten yükler.
        /// </summary>
        public static FieldMap Load(string? jsonPath = null)
        {
            var baseDir = AppContext.BaseDirectory;
            var defaultPath = Path.Combine(baseDir, "config", "fieldmap.json");
            var path = string.IsNullOrWhiteSpace(jsonPath) ? defaultPath : jsonPath;

            if (!File.Exists(path))
                throw new FileNotFoundException($"fieldmap.json bulunamadı: {path}");

            var json = File.ReadAllText(path);

            // Anahtarları büyük/küçük harf duyarsız sözlükte tutalım
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                json,
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip }
            ) ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            var ord = StringComparer.OrdinalIgnoreCase;
            var expanded = new Dictionary<string, string[]>(ord);

            foreach (var kv in raw)
            {
                var inputList = kv.Value ?? Array.Empty<string>();

                // 1) Yer tutucuları genişlet
                var withTokens = ExpandTokens(inputList);

                // 2) Normalize et ve güvenli (#text) varyasyonlarını ekle
                var normalized = NormalizeAndAugment(withTokens);

                // 3) Tekilleştir (Ordinal)
                var uniq = normalized.Distinct(StringComparer.Ordinal).ToArray();

                expanded[kv.Key] = uniq;
            }

            return new FieldMap(expanded);
        }

        /// <summary>
        /// İlk kez ihtiyaç olduğunda (./config/fieldmap.json) yükleyip cache’ler.
        /// </summary>
        public static FieldMap Current
        {
            get
            {
                if (_current != null) return _current;
                lock (_lock)
                {
                    _current ??= Load();
                    return _current;
                }
            }
        }

        /// <summary>
        /// Anahtar için tüm XPath adaylarını döndürür; yoksa boş dizi döner.
        /// </summary>
        public IReadOnlyList<string> Get(string key) =>
            _map.TryGetValue(key, out var arr) ? arr : Array.Empty<string>();

        // ---------------- internals ----------------

        /// <summary>
        /// fieldmap.json’daki yer tutucuları genişletir:
        ///  - (ROOT)           => “defter” ve “edefter”
        ///  - (defter|edefter) => “defter” ve “edefter”
        /// </summary>
        private static IEnumerable<string> ExpandTokens(IEnumerable<string> inputs)
        {
            foreach (var p in inputs)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                // (defter|edefter)
                if (p.Contains("(defter|edefter)", StringComparison.Ordinal))
                {
                    yield return p.Replace("(defter|edefter)", "defter", StringComparison.Ordinal);
                    yield return p.Replace("(defter|edefter)", "edefter", StringComparison.Ordinal);
                    continue;
                }

                // (ROOT)
                if (p.Contains("(ROOT)", StringComparison.Ordinal))
                {
                    yield return p.Replace("(ROOT)", "defter", StringComparison.Ordinal);
                    yield return p.Replace("(ROOT)", "edefter", StringComparison.Ordinal);
                    continue;
                }

                // Yer tutucu yoksa olduğu gibi
                yield return p;
            }
        }

        /// <summary>
        /// - Yol parçalarını normalize eder (PathNormalizer.Normalize).
        /// - Element yollarına “/#text” varyasyonu ekler (atribüt yoluna dokunmaz).
        /// - Boş/Geçersiz yolları atar.
        /// </summary>
        private static IEnumerable<string> NormalizeAndAugment(IEnumerable<string> paths)
        {
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var norm = PathNormalizer.Normalize(raw);
                if (string.IsNullOrEmpty(norm)) continue;

                // Her zaman normalize edilmiş yol
                yield return norm;

                // Atribüt içeriyorsa (#text eklemiyoruz)
                var hasAttribute = norm.Contains("/@", StringComparison.Ordinal);

                // Zaten #text ile bitiyorsa gerek yok
                var endsWithText = norm.EndsWith("/#text", StringComparison.Ordinal);

                // Element yolu ise güvenli olması için #text varyasyonu da ekleyelim
                if (!hasAttribute && !endsWithText)
                {
                    yield return norm + "/#text";
                }
            }
        }
    }
}
