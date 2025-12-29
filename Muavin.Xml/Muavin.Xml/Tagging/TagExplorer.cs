using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Muavin.Xml.Util;

namespace Muavin.Xml.Tagging
{
    /// <summary>
    /// XML'i hızlıca tarayıp her "yol" için istatistik toplar:
    /// - Element yolu
    /// - @attribute yolu
    /// - #text yolu
    /// Ayrıca namespace URI, attribute adları ve örnek text değerlerinden ilk 3'ünü kaydeder.
    /// </summary>
    public sealed class TagExplorer
    {
        /// <summary>
        /// Verilen XML dosyasını tarar ve yol istatistiklerini döndürür.
        /// </summary>
        public Dictionary<string, PathStats> Analyze(string xmlPath)
        {
            var stats = new Dictionary<string, PathStats>(StringComparer.Ordinal);
            var nsb = new NamespacePathBuilder();

            var stack = new Stack<(string local, string ns)>(); // sadece namespace bilgisini tutmak için

            using var fs = File.OpenRead(xmlPath);
            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore
            };

            using var reader = XmlReader.Create(fs, settings);

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        {
                            // Yeni element: yığına bas ve mevcut yol oluşsun
                            stack.Push((reader.LocalName, reader.NamespaceURI));
                            nsb.Push(reader.LocalName);

                            // Element yolu
                            var elemPath = nsb.Path;
                            Touch(stats, elemPath, "Element", reader.NamespaceURI);

                            // Attribute'lar
                            if (reader.HasAttributes)
                            {
                                while (reader.MoveToNextAttribute())
                                {
                                    var attrPath = nsb.BuildPath("@" + reader.LocalName);
                                    Touch(stats, attrPath, "Attribute", reader.NamespaceURI, Sample(reader.Value));

                                    // Elementin attribute seti bilgisini PathStats'ta tutmak istiyorsak:
                                    if (stats.TryGetValue(elemPath, out var ps))
                                        ps.AttributeNames.Add(reader.LocalName);
                                }
                                reader.MoveToElement();
                            }

                            // Kendi kendine kapanan <tag/> ise hemen pop
                            if (reader.IsEmptyElement)
                            {
                                if (stack.Count > 0) stack.Pop();
                                nsb.Pop();
                            }
                            break;
                        }

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        {
                            // Metin değerleri (#text)
                            if (stack.Count > 0)
                            {
                                var textPath = nsb.BuildPath("#text");
                                var ns = stack.Peek().ns;
                                Touch(stats, textPath, "Text", ns, Sample(reader.Value));
                            }
                            break;
                        }

                    case XmlNodeType.EndElement:
                        {
                            if (stack.Count > 0) stack.Pop();
                            nsb.Pop();
                            break;
                        }
                }
            }

            return stats;
        }

        // ---------------- helpers ----------------

        private static void Touch(
            Dictionary<string, PathStats> bag,
            string path,
            string kind,
            string? ns,
            string? sample = null)
        {
            if (!bag.TryGetValue(path, out var ps))
            {
                ps = new PathStats
                {
                    Kind = kind,
                    Path = path,
                    NamespaceUri = ns
                };
                bag[path] = ps;
            }

            ps.Count++;

            if (!string.IsNullOrWhiteSpace(sample) &&
                ps.SampleValues.Count < 3 &&
                !ps.SampleValues.Contains(sample))
            {
                ps.SampleValues.Add(sample);
            }
        }

        private static string? Sample(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();
            if (s.Length > 200) s = s.Substring(0, 200);
            return s;
        }
    }
}
