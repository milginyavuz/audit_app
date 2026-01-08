using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Muavin.Xml.Tagging;

public sealed class PathListResult
{
    public IReadOnlyList<string> All { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Elements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();
    public long TotalPaths => All.Count;
}

public sealed class PathLister
{
    private static readonly StringComparer Ord = StringComparer.Ordinal;

    /// <summary>
    /// tek bir xml bir klasör (tüm alt klasörler) veya bir ZIP ver
    /// tüm dosyalardaki yol istatistiklerini birleştirip benzersiz ve sıralı yol listelerini döndürür
    /// </summary>
    public PathListResult ListPaths(string input)
    {
        var files = ExpandInput(input);
        if (files.Count == 0)
            return new PathListResult();

        var explorer = new TagExplorer();
        var merged = new Dictionary<string, PathStats>(Ord);

        foreach (var f in files)
        {
            var stats = explorer.Analyze(f);
            Merge(merged, stats);
        }

        // benzersiz yolları türlerine göre ayır
        var elements = new HashSet<string>(Ord);
        var attributes = new HashSet<string>(Ord);
        var texts = new HashSet<string>(Ord);

        foreach (var kv in merged)
        {
            var kind = kv.Value.Kind;
            var path = kv.Value.Path;
            switch (kind)
            {
                case "Element": elements.Add(path); break;
                case "Attribute": attributes.Add(path); break;
                case "Text": texts.Add(path); break;
            }
        }

        // sıralama önce derinlik (kaç “/”) sonra sözlük (stabil)
        static IEnumerable<string> SortPaths(IEnumerable<string> src)
            => src.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, Ord);

        var all = SortPaths(elements.Concat(attributes).Concat(texts)).ToList();
        return new PathListResult
        {
            All = all,
            Elements = SortPaths(elements).ToList(),
            Attributes = SortPaths(attributes).ToList(),
            Texts = SortPaths(texts).ToList()
        };
    }

    /// <summary> listeyi .txt dosyalarına yazar (hepsi opsiyonel null geçebilir) </summary>
    public void WriteToFiles(PathListResult list,
                             string? allTxtPath = null,
                             string? elementsTxtPath = null,
                             string? attributesTxtPath = null,
                             string? textsTxtPath = null)
    {
        void Write(string? path, IEnumerable<string> lines)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllLines(full, lines);
        }

        Write(allTxtPath, list.All);
        Write(elementsTxtPath, list.Elements);
        Write(attributesTxtPath, list.Attributes);
        Write(textsTxtPath, list.Texts);
    }

    // ----------------- iç yardımcılar -----------------

    // inputu dosya listesine genişlet (.xml  klasör  .zip)
    private static List<string> ExpandInput(string input)
    {
        var list = new List<string>();
        if (Directory.Exists(input))
        {
            list.AddRange(Directory.EnumerateFiles(input, "*.xml", SearchOption.AllDirectories));
        }
        else if (File.Exists(input))
        {
            var ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext == ".xml")
            {
                list.Add(input);
            }
            else if (ext == ".zip")
            {
                var temp = Path.Combine(Path.GetTempPath(), "Muavin_PathLister_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                using var zip = ZipFile.OpenRead(input);
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var outPath = Path.Combine(temp, Path.GetFileName(e.FullName));
                    e.ExtractToFile(outPath, overwrite: true);
                    list.Add(outPath);
                }
            }
        }
        return list;
    }

    // PathStats sözlüklerini birleştir (ilk 3 sample korunur)
    private static void Merge(Dictionary<string, PathStats> target, Dictionary<string, PathStats> add)
    {
        foreach (var (path, ps) in add)
        {
            if (!target.TryGetValue(path, out var dst))
            {
                dst = new PathStats
                {
                    Kind = ps.Kind,
                    Path = ps.Path,
                    NamespaceUri = ps.NamespaceUri
                };
                target[path] = dst;
            }

            dst.Count += ps.Count;
            foreach (var a in ps.AttributeNames) dst.AttributeNames.Add(a);

            foreach (var s in ps.SampleValues)
            {
                if (!string.IsNullOrWhiteSpace(s) &&
                    dst.SampleValues.Count < 3 &&
                    !dst.SampleValues.Contains(s))
                {
                    dst.SampleValues.Add(s);
                }
            }
        }
    }
}
