using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Muavin.Xml.Util
{
    /// <summary>
    /// XmlReader ile  bulunduğu düğümün namespacesiz yolunu üretir
    /// yolu duyarsız tek biçime indirger
    /// </summary>
    public sealed class NamespacePathBuilder
    {
        private readonly Stack<string> _stack = new();

        /// <summary>normalize edilmiş geçerli yol boşken string.Empty doluyken "/" başlar</summary>
        public string Path { get; private set; } = string.Empty;

        public void Push(string localName)
        {
            if (string.IsNullOrWhiteSpace(localName)) return;
            _stack.Push(localName.ToLowerInvariant());
            Rebuild();
        }

        public void Pop()
        {
            if (_stack.Count == 0) return;
            _stack.Pop();
            Rebuild();
        }

        /// <summary>
        /// mevcut pathin sonuna verilen parçayı ekleyip normalize eder
        /// </summary>
        public string BuildPath(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return Path;

            var t = tail.ToLowerInvariant();
            var basePath = Path;
            var raw = string.IsNullOrEmpty(basePath) ? "/" + t : basePath + "/" + t;
            return Normalize(raw);
        }

        private void Rebuild()
        {
            var raw = _stack.Count == 0 ? string.Empty : "/" + string.Join("/", _stack.Reverse());
            Path = Normalize(raw);
        }

        // normalize: tüm yolları aynı forma indir
        private static readonly Regex MultiSlash = new Regex("/{2,}", RegexOptions.Compiled);

        public static string Normalize(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return string.Empty;

            // lowercase
            p = p.Trim().ToLowerInvariant();

            // sondaki /#text varsa at
            if (p.EndsWith("/#text"))
                p = p[..^6];

            // çift slash -> tek slash
            p = MultiSlash.Replace(p, "/");

            // segmentlere ayır
            var parts = p.Split('/', System.StringSplitOptions.RemoveEmptyEntries).ToList();

            // "schemaref" segmentlerini tamamen çıkar
            parts = parts.Where(seg => seg != "schemaref").ToList();

            // mümkünse kökten /defter ya da /xbrl ile başlat (kök prefixlerini kırp)
            int idxDefter = parts.FindIndex(s => s == "defter");
            int idxXbrl = parts.FindIndex(s => s == "xbrl");
            int start = -1;
            if (idxDefter >= 0) start = idxDefter;
            else if (idxXbrl >= 0) start = idxXbrl;
            if (start > 0) parts = parts.Skip(start).ToList();

            // art arda tekrar eden segmentleri tekilleştir
            var compact = new List<string>(parts.Count);
            foreach (var seg in parts)
            {
                if (compact.Count > 0 && compact[^1] == seg) continue;
                compact.Add(seg);
            }

            // yeniden birleştir başa tek / koy sonda / bırakma
            var norm = "/" + string.Join("/", compact);
            if (norm.Length > 1 && norm[^1] == '/') norm = norm[..^1];

            return norm;
        }
    }
}
