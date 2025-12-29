using Muavin.Xml.Parsing;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Muavin.Xml.Data
{
    public sealed class DbMuavinRepository
    {
        private readonly string _connectionString;

        public DbMuavinRepository() : this("") { }

        public DbMuavinRepository(string connectionString)
        {
            _connectionString = connectionString ?? "";
        }

        private void EnsureConnString()
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException(
                    "DbMuavinRepository connectionString boş. App startup’ta DbMuavinRepository(string) ile oluşturmalısın.");
        }

        private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
        {
            EnsureConnString();
            var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            return conn;
        }

        // ===================== MODELS =====================
        public sealed record CompanyItem(string CompanyCode, string CompanyName);

        // (Opsiyonel) Fiş türü override modeli
        public sealed record FisTypeOverrideItem(
            string CompanyCode,
            int Year,
            string GroupKey,
            string FisTuru,
            DateTimeOffset UpdatedAt,
            string? UpdatedBy
        );

        // ===================== SCHEMA =====================
        public async Task EnsureSchemaAsync(CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);

            const string sql = @"
CREATE SCHEMA IF NOT EXISTS muavin;

CREATE TABLE IF NOT EXISTS muavin.company(
    company_code text PRIMARY KEY,
    company_name text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS muavin.muavin_row(
    id               bigserial PRIMARY KEY,
    company_code     text NOT NULL,
    period_year      int  NOT NULL,
    period_month     int  NOT NULL,
    source_file      text NOT NULL,

    entry_number     text NULL,
    entry_counter    int  NULL,
    posting_date     date NULL,

    document_number  text NULL,

    fis_turu         text NULL,
    fis_tipi         text NULL,
    aciklama         text NULL,

    kebir            text NULL,
    hesap_kodu       text NULL,
    hesap_adi        text NULL,

    borc             numeric(18,2) NOT NULL DEFAULT 0,
    alacak           numeric(18,2) NOT NULL DEFAULT 0,
    tutar            numeric(18,2) NOT NULL DEFAULT 0,

    group_key        text NULL,
    side             text NULL,

    contra_kebir_csv text NULL,
    contra_hesap_csv text NULL,

    karsi_hesap      text NULL,

    created_at       timestamp without time zone NOT NULL DEFAULT now(),
    batch_id         bigint NULL
);

-- ✅ KÖKTEN ÇÖZÜM: kolonlar eksikse ekle (idempotent)
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS document_number  text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS fis_turu         text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS fis_tipi         text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS aciklama         text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS kebir            text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS hesap_kodu       text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS hesap_adi        text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS borc             numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS alacak           numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS tutar            numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS group_key        text NULL;
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS karsi_hesap      text NULL;

CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year ON muavin.muavin_row(company_code, period_year);
CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_source ON muavin.muavin_row(company_code, period_year, source_file);

-- override tablo (istersen)
CREATE TABLE IF NOT EXISTS muavin.fis_type_override (
    company_code    text        NOT NULL,
    year            int         NOT NULL,
    group_key       text        NOT NULL,
    fis_turu        text        NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      text        NULL,
    PRIMARY KEY (company_code, year, group_key)
);
CREATE INDEX IF NOT EXISTS ix_fis_type_override_company_year
ON muavin.fis_type_override(company_code, year);
";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ===================== COMPANIES =====================
        public async Task<List<CompanyItem>> GetCompaniesAsync(CancellationToken ct = default)
        {
            var list = new List<CompanyItem>();
            await using var conn = await OpenAsync(ct);

            const string sql = @"SELECT company_code, company_name
                                 FROM muavin.company
                                 ORDER BY company_name, company_code;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            while (await rdr.ReadAsync(ct))
                list.Add(new CompanyItem(rdr.GetString(0), rdr.GetString(1)));

            return list;
        }

        public async Task EnsureCompanyAsync(string companyCode, string? companyName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));

            companyCode = companyCode.Trim();
            companyName = string.IsNullOrWhiteSpace(companyName) ? companyCode : companyName.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
INSERT INTO muavin.company(company_code, company_name, updated_at)
VALUES (@code, @name, now())
ON CONFLICT (company_code)
DO UPDATE SET company_name = EXCLUDED.company_name,
              updated_at   = now();";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@code", companyCode);
            cmd.Parameters.AddWithValue("@name", companyName);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ===================== YEARS =====================
        public async Task<List<int>> GetYearsAsync(string companyCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<int>();

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT DISTINCT period_year
FROM muavin.muavin_row
WHERE company_code = @c
ORDER BY period_year;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);

            var years = new List<int>();
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                years.Add(rdr.GetInt32(0));

            return years;
        }

        // ===================== GET ROWS =====================
        public async Task<List<MuavinRow>> GetRowsAsync(string companyCode, int year, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return new List<MuavinRow>();

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT
    posting_date,
    entry_number,
    entry_counter,
    fis_turu,
    fis_tipi,
    aciklama,
    hesap_kodu,
    hesap_adi,
    kebir,
    borc,
    alacak,
    tutar,
    document_number,
    group_key,
    karsi_hesap
FROM muavin.muavin_row
WHERE company_code = @c AND period_year = @y
ORDER BY posting_date, kebir, hesap_kodu, entry_number, entry_counter;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);

            var list = new List<MuavinRow>(16_384);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var r = new MuavinRow
                {
                    PostingDate = rdr.IsDBNull(0) ? null : rdr.GetDateTime(0),
                    EntryNumber = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    EntryCounter = rdr.IsDBNull(2) ? (int?)null : rdr.GetInt32(2),

                    FisTuru = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    FisTipi = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    Aciklama = rdr.IsDBNull(5) ? null : rdr.GetString(5),

                    HesapKodu = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    HesapAdi = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    Kebir = rdr.IsDBNull(8) ? null : rdr.GetString(8),

                    Borc = rdr.GetDecimal(9),
                    Alacak = rdr.GetDecimal(10),
                    Tutar = rdr.GetDecimal(11),

                    DocumentNumber = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                    GroupKey = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                    KarsiHesap = rdr.IsDBNull(14) ? null : rdr.GetString(14),
                };

                list.Add(r);
            }

            return list;
        }

        // ===================== BULK INSERT =====================
        public async Task BulkInsertRowsAsync(
            string companyCode,
            IEnumerable<MuavinRow> rows,
            string sourceFile,
            bool replaceExistingForSameSource = true,
            string? companyName = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (rows is null)
                throw new ArgumentNullException(nameof(rows));

            companyCode = companyCode.Trim();
            var src = NormalizeSourceFile(sourceFile);

            await EnsureCompanyAsync(companyCode, companyName, ct);

            var list = rows.Where(r => r.PostingDate.HasValue).ToList();
            if (list.Count == 0) return;

            var years = list.Select(r => r.PostingDate!.Value.Year).Distinct().ToArray();

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (replaceExistingForSameSource)
            {
                const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = ANY(@years)
  AND source_file = @src;";

                await using var del = new NpgsqlCommand(delSql, conn, tx);
                del.Parameters.AddWithValue("@c", companyCode);
                del.Parameters.AddWithValue("@years", years);
                del.Parameters.AddWithValue("@src", src);
                await del.ExecuteNonQueryAsync(ct);
            }

            const string copySql = @"
COPY muavin.muavin_row(
    company_code, period_year, period_month, source_file,
    entry_number, entry_counter, posting_date,
    document_number,
    fis_turu, fis_tipi, aciklama,
    kebir, hesap_kodu, hesap_adi,
    borc, alacak, tutar,
    group_key,
    karsi_hesap
)
FROM STDIN (FORMAT BINARY);";

            await using (var importer = await conn.BeginBinaryImportAsync(copySql, ct))
            {
                foreach (var r in list)
                {
                    var dt = r.PostingDate!.Value.Date;
                    var py = dt.Year;
                    var pm = dt.Month;

                    if (string.IsNullOrWhiteSpace(r.GroupKey))
                        r.GroupKey = BuildGroupKey(r);

                    await importer.StartRowAsync(ct);

                    importer.Write(companyCode, NpgsqlDbType.Text);
                    importer.Write(py, NpgsqlDbType.Integer);
                    importer.Write(pm, NpgsqlDbType.Integer);
                    importer.Write(src, NpgsqlDbType.Text);

                    importer.Write((object?)r.EntryNumber ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.EntryCounter ?? DBNull.Value, NpgsqlDbType.Integer);
                    importer.Write(dt, NpgsqlDbType.Date);

                    importer.Write((object?)r.DocumentNumber ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write((object?)r.FisTuru ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.FisTipi ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.Aciklama ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write((object?)r.Kebir ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.HesapKodu ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.HesapAdi ?? DBNull.Value, NpgsqlDbType.Text);

                    importer.Write(r.Borc, NpgsqlDbType.Numeric);
                    importer.Write(r.Alacak, NpgsqlDbType.Numeric);
                    importer.Write(r.Tutar, NpgsqlDbType.Numeric);

                    importer.Write((object?)r.GroupKey ?? DBNull.Value, NpgsqlDbType.Text);
                    importer.Write((object?)r.KarsiHesap ?? DBNull.Value, NpgsqlDbType.Text);
                }

                await importer.CompleteAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        // ===================== OVERRIDES (opsiyonel) =====================
        public async Task<Dictionary<string, string>> GetFisTypeOverridesAsync(
            string companyCode, int year, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            companyCode = companyCode.Trim();

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT group_key, fis_turu
FROM muavin.fis_type_override
WHERE company_code = @c AND year = @y;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var gk = rdr.GetString(0);
                var ft = rdr.GetString(1);
                if (!string.IsNullOrWhiteSpace(gk) && !string.IsNullOrWhiteSpace(ft))
                    dict[gk] = ft;
            }

            return dict;
        }

        public async Task UpsertFisTypeOverridesAsync(
            string companyCode,
            int year,
            IEnumerable<(string groupKey, string fisTuru)> items,
            string? updatedBy = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (year <= 0)
                throw new ArgumentException("year geçersiz", nameof(year));
            if (items is null)
                throw new ArgumentNullException(nameof(items));

            companyCode = companyCode.Trim();
            updatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();

            var list = items
                .Select(x => (groupKey: (x.groupKey ?? "").Trim(), fisTuru: (x.fisTuru ?? "").Trim()))
                .Where(x => x.groupKey.Length > 0 && x.fisTuru.Length > 0)
                .ToList();

            if (list.Count == 0) return;

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string sql = @"
INSERT INTO muavin.fis_type_override(company_code, year, group_key, fis_turu, updated_by)
VALUES (@c, @y, @gk, @ft, @ub)
ON CONFLICT (company_code, year, group_key)
DO UPDATE SET fis_turu = EXCLUDED.fis_turu,
              updated_at = now(),
              updated_by = EXCLUDED.updated_by;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            var pC = cmd.Parameters.Add("@c", NpgsqlDbType.Text);
            var pY = cmd.Parameters.Add("@y", NpgsqlDbType.Integer);
            var pG = cmd.Parameters.Add("@gk", NpgsqlDbType.Text);
            var pF = cmd.Parameters.Add("@ft", NpgsqlDbType.Text);
            var pU = cmd.Parameters.Add("@ub", NpgsqlDbType.Text);

            foreach (var (groupKey, fisTuru) in list)
            {
                pC.Value = companyCode;
                pY.Value = year;
                pG.Value = groupKey;
                pF.Value = fisTuru;
                pU.Value = (object?)updatedBy ?? DBNull.Value;

                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        // ===================== HELPERS =====================
        private static string NormalizeSourceFile(string? sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile)) return "_unknown_";
            try { return Path.GetFileName(sourceFile.Trim()); }
            catch { return sourceFile.Trim(); }
        }

        private static string BuildGroupKey(MuavinRow r)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";
            if (r.FisTuru is "Açılış" or "Kapanış") return $"{no}|{d}";
            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }
    }
}
