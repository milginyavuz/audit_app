using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Muavin.Xml.Parsing;
using Npgsql;
using NpgsqlTypes;

namespace Muavin.Xml.Data
{
    /// <summary>
    /// muavin_row tablosundan satır okur ve RunningBalance (bakiye) hesaplar.
    /// Tutarlar DB’de kuruş bazında olduğu için 100’e bölünerek ekrana yansıtılır.
    /// </summary>
    public sealed class DbMuavinRepository
    {
        private readonly string _connectionString;

        public DbMuavinRepository(string? connectionString = null)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _connectionString = connectionString!;
                return;
            }

            var csElement = ConfigurationManager.ConnectionStrings["MuavinDb"];
            if (csElement != null && !string.IsNullOrWhiteSpace(csElement.ConnectionString))
            {
                _connectionString = csElement.ConnectionString;
            }
            else
            {
                _connectionString =
                    "Host=localhost;Port=5432;Database=muavin;Username=postgres;Password=anilymm";
            }
        }

        // Small DTOs for selector UI
        public sealed record CompanyItem(string CompanyCode, string CompanyName);
        public sealed record PeriodItem(int Year, int Month);

        // ========================== COMPANIES (TEK KAYNAK: muavin.company) ==========================

        /// <summary>
        /// Şirket listesi (muavin.company) - TEK KAYNAK.
        /// </summary>
        public async Task<List<CompanyItem>> GetCompaniesAsync()
        {
            const string sql = @"
SELECT company_code, company_name
FROM muavin.company
ORDER BY company_name;";

            var list = new List<CompanyItem>();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
            {
                var code = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var name = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                if (!string.IsNullOrWhiteSpace(code))
                    list.Add(new CompanyItem(code, name));
            }

            return list;
        }

        /// <summary>
        /// Tek bir şirketi getir (yoksa null).
        /// </summary>
        public async Task<CompanyItem?> GetCompanyAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return null;

            const string sql = @"
SELECT company_code, company_name
FROM muavin.company
WHERE company_code = @c;";

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("c", companyCode);

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                var code = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var name = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                return new CompanyItem(code, name);
            }

            return null;
        }

        /// <summary>
        /// Şirket kaydını garanti eder (muavin.company) - TEK KAYNAK.
        /// companyName null ise companyCode kullanılır.
        /// </summary>
        public async Task EnsureCompanyAsync(string companyCode, string? companyName = null)
        {
            if (string.IsNullOrWhiteSpace(companyCode)) return;

            // Not: updated_at trigger varsa otomatik günceller.
            // Trigger yoksa bile bu SQL sorunsuz çalışır.
            const string sql = @"
INSERT INTO muavin.company (company_code, company_name)
VALUES (@c, COALESCE(@n, @c))
ON CONFLICT (company_code) DO UPDATE
SET company_name = EXCLUDED.company_name;";

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("c", companyCode);
            cmd.Parameters.AddWithValue("n", (object?)companyName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // ========================== PERIODS ==========================

        /// <summary>
        /// Seçili şirkete ait yıl/ay listesi (muavin.import_batch üzerinden).
        /// </summary>
        public async Task<List<PeriodItem>> GetPeriodsAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<PeriodItem>();

            const string sql = @"
SELECT period_year, period_month
FROM muavin.import_batch
WHERE company_code = @code
GROUP BY period_year, period_month
ORDER BY period_year, period_month;";

            var list = new List<PeriodItem>();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("code", companyCode);

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                int y = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                int m = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                if (y > 0 && m is >= 1 and <= 12)
                    list.Add(new PeriodItem(y, m));
            }

            return list;
        }

        public async Task<List<int>> GetYearsAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<int>();

            const string sql = @"
SELECT period_year
FROM muavin.import_batch
WHERE company_code = @code
GROUP BY period_year
ORDER BY period_year;";

            var list = new List<int>();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("code", companyCode);

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!rdr.IsDBNull(0))
                    list.Add(rdr.GetInt32(0));
            }

            return list;
        }

        // ========================== ROWS (READ) ==========================

        /// <summary>
        /// Geriye dönük uyumluluk: Tüm satırları çeker (muavin.muavin_row).
        /// </summary>
        public async Task<List<MuavinRow>> GetRowsAsync()
        {
            const string sql = @"
SELECT
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
    karsi_hesap
FROM muavin.muavin_row
ORDER BY posting_date, kebir, hesap_kodu, entry_number, entry_counter;";

            var rows = await ReadRowsAsync(sql, _ => { });
            ComputeRunningBalances(rows);
            return rows;
        }

        /// <summary>
        /// Seçilen şirket + yıl’a göre satır çeker.
        /// </summary>
        public async Task<List<MuavinRow>> GetRowsAsync(string companyCode, int year)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return new List<MuavinRow>();

            const string sql = @"
SELECT
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
    karsi_hesap
FROM muavin.muavin_row
WHERE company_code = @code
  AND period_year  = @y
ORDER BY posting_date, kebir, hesap_kodu, entry_number, entry_counter;";

            var rows = await ReadRowsAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("code", companyCode);
                cmd.Parameters.AddWithValue("y", year);
            });

            ComputeRunningBalances(rows);
            return rows;
        }

        /// <summary>
        /// Seçilen şirket + yıl/ay’a göre satır çeker.
        /// </summary>
        public async Task<List<MuavinRow>> GetRowsAsync(string companyCode, int year, int month)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<MuavinRow>();
            if (year <= 0)
                return new List<MuavinRow>();
            if (month is < 1 or > 12)
                return new List<MuavinRow>();

            const string sql = @"
SELECT
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
    karsi_hesap
FROM muavin.muavin_row
WHERE company_code = @code
  AND period_year  = @y
  AND period_month = @m
ORDER BY posting_date, kebir, hesap_kodu, entry_number, entry_counter;";

            var rows = await ReadRowsAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("code", companyCode);
                cmd.Parameters.AddWithValue("y", year);
                cmd.Parameters.AddWithValue("m", month);
            });

            ComputeRunningBalances(rows);
            return rows;
        }

        // ------------------------- internals (READ) -------------------------

        private async Task<List<MuavinRow>> ReadRowsAsync(string sql, Action<NpgsqlCommand> configure)
        {
            var result = new List<MuavinRow>();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, conn);
            configure(cmd);

            await using var rdr = await cmd.ExecuteReaderAsync();

            int ordEntryCounter = rdr.GetOrdinal("entry_counter");
            int ordPostingDate = rdr.GetOrdinal("posting_date");
            int ordBorc = rdr.GetOrdinal("borc");
            int ordAlacak = rdr.GetOrdinal("alacak");
            int ordTutar = rdr.GetOrdinal("tutar");

            while (await rdr.ReadAsync())
            {
                decimal borcRaw = rdr.IsDBNull(ordBorc) ? 0m : rdr.GetDecimal(ordBorc);
                decimal alacakRaw = rdr.IsDBNull(ordAlacak) ? 0m : rdr.GetDecimal(ordAlacak);
                decimal tutarRaw = rdr.IsDBNull(ordTutar) ? 0m : rdr.GetDecimal(ordTutar);

                var row = new MuavinRow
                {
                    EntryNumber = rdr["entry_number"] as string,
                    EntryCounter = rdr.IsDBNull(ordEntryCounter) ? null : rdr.GetInt32(ordEntryCounter),
                    PostingDate = rdr.IsDBNull(ordPostingDate) ? (DateTime?)null : rdr.GetDateTime(ordPostingDate),
                    DocumentNumber = rdr["document_number"] as string,

                    Kebir = rdr["kebir"] as string,
                    HesapKodu = rdr["hesap_kodu"] as string,
                    HesapAdi = rdr["hesap_adi"] as string,

                    Borc = borcRaw / 100m,
                    Alacak = alacakRaw / 100m,
                    Tutar = tutarRaw / 100m,

                    Aciklama = rdr["aciklama"] as string,
                    FisTuru = rdr["fis_turu"] as string,
                    FisTipi = rdr["fis_tipi"] as string,

                    GroupKey = rdr["group_key"] as string,
                    Side = rdr["side"] as string ?? "",
                    ContraKebirCsv = rdr["contra_kebir_csv"] as string,
                    ContraHesapCsv = rdr["contra_hesap_csv"] as string,
                    KarsiHesap = rdr["karsi_hesap"] as string
                };

                result.Add(row);
            }

            return result;
        }

        private static void ComputeRunningBalances(List<MuavinRow> rows)
        {
            var groups = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.HesapKodu) ? (r.Kebir ?? "") : r.HesapKodu!)
                .ToList();

            foreach (var g in groups)
            {
                decimal balance = 0m;

                var ordered = g.OrderBy(r => r.PostingDate)
                               .ThenBy(r => r.Kebir)
                               .ThenBy(r => r.HesapKodu)
                               .ThenBy(r => r.EntryNumber)
                               .ThenBy(r => r.EntryCounter ?? 0);

                foreach (var r in ordered)
                {
                    balance += r.Borc - r.Alacak;
                    r.RunningBalance = balance;
                }
            }
        }

        // ========================== WRITE / BULK INSERT ==========================

        private static decimal ToKurus(decimal tl)
            => decimal.Round(tl * 100m, 0, MidpointRounding.AwayFromZero);

        private static string SourceFileName(string pathOrName)
            => string.IsNullOrWhiteSpace(pathOrName) ? "" : Path.GetFileName(pathOrName);

        private static async Task EnsureImportBatchAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx,
            string companyCode, int year, int month, string sourceFile)
        {
            const string sql = @"
INSERT INTO muavin.import_batch (company_code, period_year, period_month, source_file)
VALUES (@c, @y, @m, @f)
ON CONFLICT (company_code, period_year, period_month, source_file) DO NOTHING;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("c", companyCode);
            cmd.Parameters.AddWithValue("y", year);
            cmd.Parameters.AddWithValue("m", month);
            cmd.Parameters.AddWithValue("f", sourceFile);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task DeleteExistingAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx,
            string companyCode, int year, int month, string sourceFile)
        {
            const string sql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c AND period_year = @y AND period_month = @m AND source_file = @f;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("c", companyCode);
            cmd.Parameters.AddWithValue("y", year);
            cmd.Parameters.AddWithValue("m", month);
            cmd.Parameters.AddWithValue("f", sourceFile);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Parse edilen satırları DB’ye bulk yazar (COPY BINARY).
        /// Satırlar PostingDate’e göre yıl/ay’a gruplanır, her grup için import_batch ensure edilir.
        /// Tutarlar DB’ye KURUŞ olarak yazılır.
        /// </summary>
        public async Task BulkInsertRowsAsync(
            string companyCode,
            IEnumerable<MuavinRow> rows,
            string sourceFile,
            bool replaceExistingForSameSource = true,
            string? companyName = null)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));

            var list = rows.Where(r => r.PostingDate.HasValue).ToList();
            if (list.Count == 0) return;

            sourceFile = SourceFileName(sourceFile);

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // company kaydı garanti (aynı transaction içinde)
            {
                const string sqlCompany = @"
INSERT INTO muavin.company (company_code, company_name)
VALUES (@c, COALESCE(@n, @c))
ON CONFLICT (company_code) DO UPDATE
SET company_name = EXCLUDED.company_name;";
                await using var cmdCompany = new NpgsqlCommand(sqlCompany, conn, tx);
                cmdCompany.Parameters.AddWithValue("c", companyCode);
                cmdCompany.Parameters.AddWithValue("n", (object?)companyName ?? DBNull.Value);
                await cmdCompany.ExecuteNonQueryAsync();
            }

            foreach (var g in list.GroupBy(r => (y: r.PostingDate!.Value.Year, m: r.PostingDate!.Value.Month)))
            {
                int y = g.Key.y;
                int m = g.Key.m;

                await EnsureImportBatchAsync(conn, tx, companyCode, y, m, sourceFile);

                if (replaceExistingForSameSource)
                    await DeleteExistingAsync(conn, tx, companyCode, y, m, sourceFile);

                var copySql = @"
COPY muavin.muavin_row (
    company_code, period_year, period_month, source_file,
    entry_number, entry_counter, posting_date, document_number,
    fis_turu, fis_tipi, aciklama,
    kebir, hesap_kodu, hesap_adi,
    borc, alacak, tutar,
    group_key, side,
    contra_kebir_csv, contra_hesap_csv, karsi_hesap
)
FROM STDIN (FORMAT BINARY);";

                await using var importer = conn.BeginBinaryImport(copySql);

                foreach (var r in g)
                {
                    importer.StartRow();

                    importer.Write(companyCode, NpgsqlDbType.Text);
                    importer.Write(y, NpgsqlDbType.Integer);
                    importer.Write(m, NpgsqlDbType.Integer);
                    importer.Write(sourceFile, NpgsqlDbType.Text);

                    importer.Write((object?)r.EntryNumber ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.EntryCounter ?? DBNull.Value, NpgsqlDbType.Integer);
                    importer.Write(r.PostingDate!.Value, NpgsqlDbType.Date);
                    importer.Write((object?)r.DocumentNumber ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write((object?)r.FisTuru ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.FisTipi ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.Aciklama ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write((object?)r.Kebir ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.HesapKodu ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.HesapAdi ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write(ToKurus(r.Borc), NpgsqlDbType.Numeric);
                    importer.Write(ToKurus(r.Alacak), NpgsqlDbType.Numeric);
                    importer.Write(ToKurus(r.Tutar), NpgsqlDbType.Numeric);

                    importer.Write((object?)r.GroupKey ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.Side ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write((object?)r.ContraKebirCsv ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.ContraHesapCsv ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.KarsiHesap ?? DBNull.Value, NpgsqlDbType.Text);
                }

                await importer.CompleteAsync();
            }

            await tx.CommitAsync();
        }

        // ========================== MAINTENANCE / CLEANUP ==========================

        /// <summary>
        /// Yanlış yüklenen şirket verilerini temizlemek için:
        /// 1) muavin_row -> 2) import_batch -> (istersen company de silebilirsin)
        /// Not: company kaydını burada silmiyorum, sadece veriyi temizliyorum.
        /// </summary>
        public async Task DeleteCompanyDataAsync(string companyCode)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // önce satırlar
            await using (var cmd = new NpgsqlCommand(@"
DELETE FROM muavin.muavin_row WHERE company_code = @c;", conn, tx))
            {
                cmd.Parameters.AddWithValue("c", companyCode);
                await cmd.ExecuteNonQueryAsync();
            }

            // sonra batch
            await using (var cmd = new NpgsqlCommand(@"
DELETE FROM muavin.import_batch WHERE company_code = @c;", conn, tx))
            {
                cmd.Parameters.AddWithValue("c", companyCode);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
    }
}
