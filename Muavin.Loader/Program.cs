// Muavin.Loader/Program.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;
using Npgsql;
using NpgsqlTypes;

namespace Muavin.Loader
{
    internal sealed record LoadedRow(
        string CompanyCode,
        int PeriodYear,
        int PeriodMonth,
        string SourceFile,
        MuavinRow Row
    );

    internal static class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                // ---------------- PostgreSQL bağlantı bilgisi ----------------
                var connectionString =
                    "Host=localhost;Port=5432;Database=muavin;Username=postgres;Password=anilymm";

                // ---------------- Log + fieldmap ----------------
                var logPath = Path.Combine(AppContext.BaseDirectory, "loader-debug.txt");
                Logger.Init(logPath, overwrite: true);

                var configDir = Path.Combine(AppContext.BaseDirectory, "config");
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
                FieldMap.Load(Path.Combine("config", "fieldmap.json"));

                // ---------------- XML dosyalarını bul ----------------
                var testsDir = Path.Combine(AppContext.BaseDirectory, "tests");
                if (!Directory.Exists(testsDir))
                {
                    Console.WriteLine($"tests klasörü bulunamadı: {testsDir}");
                    return;
                }

                var xmlFiles = Directory.EnumerateFiles(testsDir, "*.xml", SearchOption.TopDirectoryOnly)
                                        .OrderBy(x => x)
                                        .ToList();

                if (xmlFiles.Count == 0)
                {
                    Console.WriteLine("tests klasöründe XML yok.");
                    return;
                }

                Console.WriteLine($"Bulunan XML sayısı: {xmlFiles.Count}");

                // ---------------- Parse et ----------------
                var parser = new EdefterParser();
                var allRows = new List<LoadedRow>();

                foreach (var file in xmlFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file); // 6120062612-202401-Y-000000
                    var parts = fileName.Split('-', StringSplitOptions.RemoveEmptyEntries);

                    var companyCode = parts.Length > 0 ? parts[0] : "UNKNOWN";

                    int periodYear = 0, periodMonth = 0;
                    if (parts.Length > 1 && parts[1].Length >= 6 &&
                        int.TryParse(parts[1][..4], out var y) &&
                        int.TryParse(parts[1].Substring(4, 2), out var m))
                    {
                        periodYear = y;
                        periodMonth = m;
                    }

                    var sourceFile = Path.GetFileName(file);

                    var parsed = parser.Parse(file);
                    foreach (var row in parsed)
                    {
                        allRows.Add(new LoadedRow(companyCode, periodYear, periodMonth, sourceFile, row));
                    }
                }

                Console.WriteLine($"Toplam parse edilen satır: {allRows.Count:N0}");

                // ---------------- Karşı hesaplar ----------------
                Console.WriteLine("Karşı hesaplar hesaplanıyor...");
                PostProcessors.FillContraAccounts(allRows.Select(x => x.Row).ToList(), alsoAccountCodes: true);

                // ---------------- PostgreSQL'e yaz ----------------
                await BulkCopyToPostgresAsync(connectionString, allRows);

                Console.WriteLine("İşlem bitti. Bir tuşa basın...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL HATA:");
                Console.WriteLine(ex);
            }
            finally
            {
                Logger.Close();
            }
        }

        private static async Task BulkCopyToPostgresAsync(
            string connectionString,
            IReadOnlyCollection<LoadedRow> allRows)
        {
            Console.WriteLine("PostgreSQL'e COPY BINARY ile yazılıyor...");

            using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            var sql = @"
COPY public.muavin_row
(
    company_code,
    period_year,
    period_month,
    source_file,
    entry_number,
    entry_counter,
    posting_date,
    document_number,
    fis_turu,
    fis_tipi,
    aciklama,
    kebir,
    hesap_kodu,
    hesap_adi,
    borc,
    alacak,
    tutar,
    group_key,
    side,
    contra_kebir_csv,
    contra_hesap_csv,
    karsi_hesap,
    created_at
)
FROM STDIN (FORMAT BINARY)";

            using var writer = await conn.BeginBinaryImportAsync(sql);

            var now = DateTime.UtcNow;

            foreach (var item in allRows)
            {
                var row = item.Row;

                await writer.StartRowAsync();

                // company_code, period_year, period_month, source_file
                await writer.WriteAsync(item.CompanyCode, NpgsqlDbType.Varchar);
                await writer.WriteAsync(item.PeriodYear, NpgsqlDbType.Integer);
                await writer.WriteAsync(item.PeriodMonth, NpgsqlDbType.Integer);
                await writer.WriteAsync(item.SourceFile, NpgsqlDbType.Varchar);

                // entry / header
                await writer.WriteAsync(row.EntryNumber, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.EntryCounter, NpgsqlDbType.Integer);
                await writer.WriteAsync(row.PostingDate, NpgsqlDbType.Date);
                await writer.WriteAsync(row.DocumentNumber, NpgsqlDbType.Varchar);

                // fis
                await writer.WriteAsync(row.FisTuru, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.FisTipi, NpgsqlDbType.Varchar);

                // açıklama
                await writer.WriteAsync(row.Aciklama, NpgsqlDbType.Varchar);

                // hesap bilgileri
                await writer.WriteAsync(row.Kebir, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.HesapKodu, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.HesapAdi, NpgsqlDbType.Varchar);

                // tutarlar
                await writer.WriteAsync(row.Borc, NpgsqlDbType.Numeric);
                await writer.WriteAsync(row.Alacak, NpgsqlDbType.Numeric);
                await writer.WriteAsync(row.Tutar, NpgsqlDbType.Numeric);

                // group / contra
                await writer.WriteAsync(row.GroupKey, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.Side, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.ContraKebirCsv, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.ContraHesapCsv, NpgsqlDbType.Varchar);
                await writer.WriteAsync(row.KarsiHesap, NpgsqlDbType.Varchar);

                // created_at
                await writer.WriteAsync(now, NpgsqlDbType.TimestampTz);
            }

            await writer.CompleteAsync();
            Console.WriteLine("COPY tamamlandı.");
        }
    }
}
