// Program.cs  — Muavin.PathMap
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Muavin.Xml.Tagging; // PathLister, TagExplorer, PathStats

namespace Muavin.PathMap
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            // ---- ARG / DEFAULT ----
            var root = args.Length >= 1 ? args[0] : @"C:\Users\user\Desktop\path map";
            var outRoot = args.Length >= 2 ? args[1] : @"C:\Users\user\Desktop\path-map-out";

            Console.WriteLine($"[INFO] ROOT   = {root}");
            Console.WriteLine($"[INFO] OUTPUT = {outRoot}");
            Directory.CreateDirectory(outRoot);

            if (!Directory.Exists(root))
            {
                Console.WriteLine("[HATA] Kök klasör bulunamadı: " + root);
                return 2;
            }

            // ---- firma klasörleri ya da kökün kendisi ----
            var companies = Directory.EnumerateDirectories(root).ToList();
            if (companies.Count == 0)
            {
                Console.WriteLine("[WARN] Alt firma klasörü bulunamadı; kökün kendisini firma olarak işleyeceğim.");
                companies.Add(root);
            }

            var lister = new PathLister();
            var explorer = new TagExplorer();

            // global birikim
            var globalDict = new Dictionary<string, (string Kind, long Count, HashSet<string> Firms)>(StringComparer.Ordinal);

            foreach (var companyDir in companies)
            {
                var company = companyDir == root ? (Path.GetFileName(root) ?? "ROOT") : Path.GetFileName(companyDir);
                Console.WriteLine($"\n[INFO] Firma: {company}");

                var files = ExpandAllXml(companyDir).ToList();
                Console.WriteLine($"[INFO] XML sayısı: {files.Count}");
                if (files.Count == 0) { Console.WriteLine("[WARN] XML bulunamadı, atlanıyor."); continue; }

                // 1) Firma bazında benzersiz path listeleri (.txt)
                var list = lister.ListPaths(companyDir);
                var compOut = Path.Combine(outRoot, company);
                Directory.CreateDirectory(compOut);

                lister.WriteToFiles(list,
                    allTxtPath: Path.Combine(compOut, "paths_all.txt"),
                    elementsTxtPath: Path.Combine(compOut, "paths_elements.txt"),
                    attributesTxtPath: Path.Combine(compOut, "paths_attributes.txt"),
                    textsTxtPath: Path.Combine(compOut, "paths_texts.txt"));

                // 2) Detaylı CSV (Count/Namespace/Attributes/SampleValues)
                var merged = new Dictionary<string, PathStats>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    try
                    {
                        var stats = explorer.Analyze(f);
                        Merge(merged, stats);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HATA] {f}: {ex.Message}");
                    }
                }
                var detailedCsv = Path.Combine(compOut, "paths_detailed.csv");
                WriteDetailedCsv(merged, detailedCsv);
                Console.WriteLine($"[INFO] Yazıldı: {detailedCsv} (toplam path: {merged.Count})");

                // 3) Global biriktir
                foreach (var kv in merged)
                {
                    var path = kv.Key;
                    var kind = kv.Value.Kind ?? "";
                    var cnt = kv.Value.Count;

                    if (!globalDict.TryGetValue(path, out var g))
                        g = (kind, 0, new HashSet<string>(StringComparer.Ordinal));

                    g.Count += cnt;
                    g.Firms.Add(company);
                    globalDict[path] = g;
                }
            }

            // 4) Global çıktılar
            var globalCsv = Path.Combine(outRoot, "global_paths.csv");
            WriteGlobalCsv(globalDict, globalCsv);
            Console.WriteLine($"[INFO] Yazıldı: {globalCsv} (toplam path: {globalDict.Count})");

            var ord = StringComparer.Ordinal;
            static IEnumerable<string> SortPaths(IEnumerable<string> s)
                => s.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal);

            var globalElements = SortPaths(globalDict.Where(kv => kv.Value.Kind == "Element").Select(kv => kv.Key).Distinct(ord));
            var globalAttributes = SortPaths(globalDict.Where(kv => kv.Value.Kind == "Attribute").Select(kv => kv.Key).Distinct(ord));
            var globalTexts = SortPaths(globalDict.Where(kv => kv.Value.Kind == "Text").Select(kv => kv.Key).Distinct(ord));

            File.WriteAllLines(Path.Combine(outRoot, "global_elements.txt"), globalElements);
            File.WriteAllLines(Path.Combine(outRoot, "global_attributes.txt"), globalAttributes);
            File.WriteAllLines(Path.Combine(outRoot, "global_texts.txt"), globalTexts);

            Console.WriteLine("\nOK");
            return 0;
        }

        // ---- Helpers ----
        static IEnumerable<string> ExpandAllXml(string folder)
        {
            // Düz .xml dosyaları
            foreach (var f in Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories))
                yield return f;

            // ZIP içindeki XML'ler
            foreach (var z in Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories))
            {
                string temp = Path.Combine(Path.GetTempPath(), "Muavin_PathMap_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);

                using var zip = ZipFile.OpenRead(z);
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var outPath = Path.Combine(temp, Path.GetFileName(e.FullName));

                    bool ok = true;
                    try
                    {
                        e.ExtractToFile(outPath, overwrite: true);
                    }
                    catch
                    {
                        ok = false; // bozuk giriş / izin vb. — atla
                    }

                    if (ok)
                        yield return outPath;
                }
            }
        }

        // PathStats sözlüklerini birleştir (Count toplar, attribute adları birleşir, ilk 3 sample)
        static void Merge(Dictionary<string, PathStats> target, Dictionary<string, PathStats> add)
        {
            foreach (var (path, ps) in add)
            {
                if (!target.TryGetValue(path, out var dst))
                {
                    dst = new PathStats { Kind = ps.Kind, Path = ps.Path, NamespaceUri = ps.NamespaceUri };
                    target[path] = dst;
                }

                dst.Count += ps.Count;
                foreach (var a in ps.AttributeNames) dst.AttributeNames.Add(a);

                foreach (var s in ps.SampleValues)
                {
                    if (!string.IsNullOrWhiteSpace(s) && dst.SampleValues.Count < 3 && !dst.SampleValues.Contains(s))
                        dst.SampleValues.Add(s);
                }
            }
        }

        static void WriteDetailedCsv(Dictionary<string, PathStats> dict, string csvPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            using var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true));
            sw.WriteLine("Kind,Path,Count,NamespaceUri,AttributeList,SampleValues");

            foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var ps = kv.Value;
                string Csv(string? v) => string.IsNullOrEmpty(v) ? "" :
                    (v.Contains(',') || v.Contains('"') || v.Contains('\n')) ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

                var attrs = ps.AttributeNames.Count > 0
                    ? string.Join("|", ps.AttributeNames.OrderBy(x => x, StringComparer.Ordinal))
                    : "";

                var samples = ps.SampleValues.Count > 0 ? string.Join(" ; ", ps.SampleValues) : "";

                sw.WriteLine(string.Join(",",
                    Csv(ps.Kind),
                    Csv(ps.Path),
                    ps.Count.ToString(CultureInfo.InvariantCulture),
                    Csv(ps.NamespaceUri ?? ""),
                    Csv(attrs),
                    Csv(samples)
                ));
            }
        }

        static void WriteGlobalCsv(Dictionary<string, (string Kind, long Count, HashSet<string> Firms)> dict, string csvPath)
        {
            using var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true));
            sw.WriteLine("Path,Kind,TotalCount,Firms");

            foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var firms = string.Join("|", kv.Value.Firms.OrderBy(x => x, StringComparer.Ordinal));
                string Csv(string? v) => string.IsNullOrEmpty(v) ? "" :
                    (v.Contains(',') || v.Contains('"') || v.Contains('\n')) ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

                sw.WriteLine(string.Join(",",
                    Csv(kv.Key),
                    Csv(kv.Value.Kind),
                    kv.Value.Count.ToString(CultureInfo.InvariantCulture),
                    Csv(firms)
                ));
            }
        }
    }
}
