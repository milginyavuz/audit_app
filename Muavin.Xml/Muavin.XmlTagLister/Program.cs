using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Muavin.Xml.Tagging;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        var input = args[0];
        var output = args[1];

        // Kip seçimi: .csv => detaylı istatistik; .txt => tekilleştirilmiş yol listeleri
        if (output.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return RunCsvMode(input, output);
        }
        else if (output.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var outAll = output;
            var outEl = args.Length > 2 ? args[2] : null;
            var outAt = args.Length > 3 ? args[3] : null;
            var outTx = args.Length > 4 ? args[4] : null;
            return RunTxtMode(input, outAll, outEl, outAt, outTx);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Uyari: 2. argumanin uzantisi .csv ya da .txt olmali.");
            Console.ResetColor();
            PrintUsage();
            return 2;
        }
    }

    // ---------------- CSV kipi: TagExplorer ile ayrıntılı istatistik ----------------
    static int RunCsvMode(string input, string output)
    {
        var xmlFiles = ExpandInput(input);
        if (xmlFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Uyari: Islenecek XML bulunamadi.");
            Console.ResetColor();
            return 2;
        }

        var explorer = new TagExplorer();
        var merged = new Dictionary<string, PathStats>(StringComparer.Ordinal);

        foreach (var file in xmlFiles)
        {
            try
            {
                Console.WriteLine("[INFO] " + file);
                var stats = explorer.Analyze(file);
                Merge(merged, stats);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[HATA] {file}: {ex.Message}");
                Console.ResetColor();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var sw = new StreamWriter(output, false, new UTF8Encoding(true));
        sw.WriteLine("Kind,Path,Count,NamespaceUri,HasAttributes,AttributeList,SampleValues");

        foreach (var kv in merged.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var ps = kv.Value;
            var hasAttrs = ps.AttributeNames.Count > 0 ? "Yes" : "No";
            var attrList = ps.AttributeNames.Count > 0
                ? string.Join("|", ps.AttributeNames.OrderBy(x => x, StringComparer.Ordinal))
                : "";
            var samples = ps.SampleValues.Count > 0 ? string.Join(" ; ", ps.SampleValues) : "";

            static string Csv(string? v) => string.IsNullOrEmpty(v) ? "" :
                (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
                    ? "\"" + v.Replace("\"", "\"\"") + "\""
                    : v;

            sw.WriteLine(string.Join(",",
                Csv(ps.Kind),
                Csv(ps.Path),
                ps.Count.ToString(CultureInfo.InvariantCulture),
                Csv(ps.NamespaceUri ?? ""),
                Csv(hasAttrs),
                Csv(attrList),
                Csv(samples)
            ));
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"OK → {output}");
        Console.ResetColor();
        return 0;
    }

    // --------------- TXT kipi: PathLister ile tekilleştirilmiş yol listeleri ---------------
    static int RunTxtMode(string input, string outAll, string? outEl, string? outAt, string? outTx)
    {
        var lister = new PathLister();
        var result = lister.ListPaths(input);

        if (result.TotalPaths == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Uyari: Yol bulunamadi (gecerli XML yok).");
            Console.ResetColor();
            return 2;
        }

        lister.WriteToFiles(result, outAll, outEl, outAt, outTx);

        Console.WriteLine($"Toplam benzersiz yol: {result.TotalPaths}");
        foreach (var p in result.All.Take(20)) Console.WriteLine(p);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("OK");
        Console.ResetColor();
        return 0;
    }

    // ---------------- Yardımcılar ----------------
    static void PrintUsage()
    {
        Console.WriteLine("Kullanim:");
        Console.WriteLine("  CSV (ayrintili istatistik):");
        Console.WriteLine("    Muavin.XmlTagLister <girdi.xml|klasor|girdi.zip> <cikti.csv>");
        Console.WriteLine("  TXT (tekillestirilmis yol listeleri):");
        Console.WriteLine("    Muavin.XmlTagLister <girdi.xml|klasor|girdi.zip> <tum.txt> [elements.txt] [attributes.txt] [texts.txt]");
    }

    // input yolunu dosya listesine genişlet
    static List<string> ExpandInput(string input)
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
                var tempDir = Path.Combine(Path.GetTempPath(), "Muavin_TagLister_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                using var zip = ZipFile.OpenRead(input);
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var outPath = Path.Combine(tempDir, Path.GetFileName(e.FullName));
                    e.ExtractToFile(outPath, overwrite: true);
                    list.Add(outPath);
                }
            }
        }

        return list;
    
    }

    // İki sözlüğü birleştir (count toplar, attribute set birleşir, sample değerleri ilk 3 benzersiz)
    static void Merge(Dictionary<string, PathStats> target, Dictionary<string, PathStats> add)
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

            foreach (var a in ps.AttributeNames)
                dst.AttributeNames.Add(a);

            foreach (var s in ps.SampleValues)
            {
                if (!string.IsNullOrWhiteSpace(s) && dst.SampleValues.Count < 3 && !dst.SampleValues.Contains(s))
                    dst.SampleValues.Add(s);
            }
        }
    }
}
