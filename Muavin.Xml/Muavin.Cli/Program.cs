using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;

class Program
{
    static int Main(string[] args)
    {
        // 1) Argüman kontrolü
        if (args.Length < 2)
        {
            Console.WriteLine("Kullanım:");
            Console.WriteLine(@"  Muavin.Cli <girdi.xml | klasör | zip> <cikti.xlsx>");
            return 1;
        }

        var input = args[0];
        var output = args[1];

        // 2) Logger'ı başlat (debug.txt çıktıyla aynı klasörde)
        var outDir = Path.GetDirectoryName(Path.GetFullPath(output)) ?? AppContext.BaseDirectory;
        var dbgTxt = Path.Combine(outDir, "debug.txt");
        Logger.Init(dbgTxt, overwrite: true);
        Logger.Run($"Input={input}");

        try
        {
            // 3) İşlenecek dosyaları topla
            var files = ExpandInput(input);
            if (files.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Uyarı: İşlenecek XML dosyası bulunamadı.");
                Console.ResetColor();
                Logger.Info("No XML files found.");
                return 2;
            }

            var parser = new EdefterParser();
            var rows = new List<MuavinRow>();

            // 4) Her XML'i sırayla işle
            foreach (var f in files)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[İŞLE] {f}");
                    Console.ResetColor();

                    Logger.Info($"Parsing: {f}");
                    var parsed = parser.Parse(f);
                    rows.AddRange(parsed);
                    Logger.Info($"Parsed rows (file): {parsed.Count()}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[HATA] {f}: {ex.Message}");
                    Console.ResetColor();

                    Logger.Error($"{f}: {ex}");
                }
            }

            // 5) Karşı hesap ve bakiye işlemleri
            PostProcessors.FillContraAccounts(rows);
            // Hesap-bazlı bakiye (global istersen ComputeRunningBalance kullan)
            PostProcessors.ComputeRunningBalancePerAccount(rows);

            // 6) Excel dışa aktarım
            PostProcessors.ExportExcel(rows, output, perAccountBalance: true);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"OK → {output}");
            Console.ResetColor();

            Logger.Rows(rows.Count.ToString());
            Logger.Info($"Finished. Output: {output}");
            return 0;
        }
        catch (Exception fatal)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL] {fatal.Message}");
            Console.ResetColor();

            Logger.Error($"FATAL: {fatal}");
            return 99;
        }
        finally
        {
            Logger.Close();
        }
    }

    // Klasör / XML / ZIP içinden dosyaları çıkar
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
                var tempDir = Path.Combine(Path.GetTempPath(), "Muavin_Unzip_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(input, tempDir);
                list.AddRange(Directory.EnumerateFiles(tempDir, "*.xml", SearchOption.AllDirectories));
            }
        }

        return list;
    }
}
