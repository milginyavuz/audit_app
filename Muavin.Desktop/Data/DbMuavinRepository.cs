// DbMuavinRepository.cs
using ControlzEx.Standard;
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
        public sealed record CompanyYearItem(string CompanyCode, int Year, DateTimeOffset CreatedAt, string? CreatedBy);

        public sealed record FisTypeOverrideItem(
            string CompanyCode,
            int Year,
            string GroupKey,
            string FisTuru,
            DateTimeOffset UpdatedAt,
            string? UpdatedBy
        );
        public sealed record ImportBatchSummary(
                long BatchId,
                string CompanyCode,
                int Year,
                DateTimeOffset LoadedAt,
                string SourceFile,
                IReadOnlyList<int> Months,
                int RowCount
        );
        // ===================== ENUMS =====================
        public enum ImportReplaceMode
        {
            SameSource = 0,
            YearsInPayload = 1,
            MonthsInPayload = 2
        }

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

CREATE TABLE IF NOT EXISTS muavin.company_year(
    company_code text NOT NULL REFERENCES muavin.company(company_code) ON DELETE CASCADE,
    year         int  NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    created_by   text NULL,
    PRIMARY KEY(company_code, year)
);
CREATE INDEX IF NOT EXISTS ix_company_year_company ON muavin.company_year(company_code);

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

-- idempotent kolonlar
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
ALTER TABLE muavin.muavin_row ADD COLUMN IF NOT EXISTS batch_id         bigint NULL;

CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year ON muavin.muavin_row(company_code, period_year);
CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_month ON muavin.muavin_row(company_code, period_year, period_month);
CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_source ON muavin.muavin_row(company_code, period_year, source_file);
CREATE INDEX IF NOT EXISTS ix_muavin_row_batch ON muavin.muavin_row(batch_id);

-- override tablo
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

-- ✅ import_batch
CREATE TABLE IF NOT EXISTS muavin.import_batch(
    batch_id     bigserial PRIMARY KEY,
    company_code text NOT NULL,
    period_year  int  NOT NULL,
    period_month int  NOT NULL,
    source_file  text NOT NULL,
    loaded_at    timestamptz NOT NULL DEFAULT now()
);

-- loaded_by sonradan eklendi (idempotent)
ALTER TABLE muavin.import_batch ADD COLUMN IF NOT EXISTS loaded_by text NULL;

CREATE INDEX IF NOT EXISTS ix_import_batch_company_y_m
ON muavin.import_batch(company_code, period_year, period_month);

CREATE INDEX IF NOT EXISTS ix_import_batch_loaded_at
ON muavin.import_batch(loaded_at);
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

        // ===================== YEARS (Company-Year) =====================
        public async Task EnsureCompanyYearAsync(
            string companyCode,
            int year,
            string? createdBy = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (year <= 0)
                throw new ArgumentException("year geçersiz", nameof(year));

            companyCode = companyCode.Trim();
            createdBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
INSERT INTO muavin.company_year(company_code, year, created_by)
VALUES (@c, @y, @by)
ON CONFLICT (company_code, year) DO NOTHING;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@by", (object?)createdBy ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<List<int>> GetCompanyYearsAsync(string companyCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<int>();

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT year
FROM muavin.company_year
WHERE company_code = @c
ORDER BY year;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);

            var years = new List<int>();
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                years.Add(rdr.GetInt32(0));

            return years;
        }

        public async Task<List<int>> GetYearsAsync(string companyCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                return new List<int>();

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT DISTINCT y FROM (
    SELECT year AS y
    FROM muavin.company_year
    WHERE company_code = @c

    UNION

    SELECT DISTINCT period_year AS y
    FROM muavin.muavin_row
    WHERE company_code = @c
) t
ORDER BY y;";

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

        // ===================== ✅ import_batch helpers (TEK BATCH / IMPORT) =====================

        /// <summary>
        /// ✅ Bu import operasyonu için tek bir batch açar ve batch_id döner.
        /// </summary>
        private static async Task<long> InsertImportBatchAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            string companyCode,
            int year,
            int month,
            string sourceFile,
            string? loadedBy,
            CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO muavin.import_batch(company_code, period_year, period_month, source_file, loaded_by)
VALUES (@c, @y, @m, @src, @by)
RETURNING batch_id;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@m", month);
            cmd.Parameters.AddWithValue("@src", sourceFile);
            cmd.Parameters.AddWithValue("@by", (object?)loadedBy ?? DBNull.Value);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(obj);
        }

        /// <summary>
        /// ✅  Aynı batch_id altında diğer (year,month) satırlarını import_batch’e yazar.
        /// </summary>
        private static async Task InsertImportBatchMonthLinkAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            long batchId,
            string companyCode,
            int year,
            int month,
            string sourceFile,
            string? loadedBy,
            CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO muavin.import_batch(batch_id, company_code, period_year, period_month, source_file, loaded_by)
VALUES (@bid, @c, @y, @m, @src, @by);";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@bid", batchId);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@m", month);
            cmd.Parameters.AddWithValue("@src", sourceFile);
            cmd.Parameters.AddWithValue("@by", (object?)loadedBy ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ===================== BULK INSERT (✅ tek batch_id yazan sürüm) =====================
        public async Task BulkInsertRowsAsync(
            string companyCode,
            IEnumerable<MuavinRow> rows,
            string sourceFile,
            bool replaceExistingForSameSource = true,
            string? companyName = null,
            CancellationToken ct = default,
            ImportReplaceMode replaceMode = ImportReplaceMode.MonthsInPayload
        )
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

            // year set
            var yearSet = list.Select(r => r.PostingDate!.Value.Year).Distinct().ToArray();

            foreach (var y in yearSet)
                await EnsureCompanyYearAsync(companyCode, y, Environment.UserName, ct);

            foreach (var r in list)
                if (string.IsNullOrWhiteSpace(r.GroupKey))
                    r.GroupKey = BuildGroupKey(r);

            // payload YM set
            var ymList = list
                .Select(r => (y: r.PostingDate!.Value.Year, m: r.PostingDate!.Value.Month))
                .Distinct()
                .OrderBy(x => x.y).ThenBy(x => x.m)
                .ToList();

            if (ymList.Count == 0) return;

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // ===================== DELETE STRATEGY =====================
            if (replaceMode == ImportReplaceMode.SameSource)
            {
                if (replaceExistingForSameSource)
                {
                    const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = ANY(@years)
  AND source_file = @src;";
                    await using var del = new NpgsqlCommand(delSql, conn, tx);
                    del.Parameters.AddWithValue("@c", companyCode);
                    del.Parameters.AddWithValue("@years", yearSet);
                    del.Parameters.AddWithValue("@src", src);
                    await del.ExecuteNonQueryAsync(ct);
                }
            }
            else if (replaceMode == ImportReplaceMode.YearsInPayload)
            {
                const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = ANY(@years);";
                await using var del = new NpgsqlCommand(delSql, conn, tx);
                del.Parameters.AddWithValue("@c", companyCode);
                del.Parameters.AddWithValue("@years", yearSet);
                await del.ExecuteNonQueryAsync(ct);
            }
            else // MonthsInPayload
            {
                const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = @y
  AND period_month = ANY(@months);";

                foreach (var g in ymList.GroupBy(x => x.y))
                {
                    var months = g.Select(x => x.m).Distinct().ToArray();

                    await using var del = new NpgsqlCommand(delSql, conn, tx);
                    del.Parameters.AddWithValue("@c", companyCode);
                    del.Parameters.AddWithValue("@y", g.Key);
                    del.Parameters.AddWithValue("@months", months);
                    await del.ExecuteNonQueryAsync(ct);
                }
            }
            // ===========================================================

            // ✅ 1) Tek batch oluştur (ilk YM ile) + aynı batch altında diğer YM satırlarını import_batch’e bağla
            var loadedBy = Environment.UserName;

            var first = ymList[0];
            var batchId = await InsertImportBatchAsync(conn, tx, companyCode, first.y, first.m, src, loadedBy, ct);

            for (int i = 1; i < ymList.Count; i++)
            {
                var (y, m) = ymList[i];
                await InsertImportBatchMonthLinkAsync(conn, tx, batchId, companyCode, y, m, src, loadedBy, ct);
            }

            // ✅ 2) COPY: batch_id alanını da yaz (TÜM satırlar aynı batchId)
            const string copySql = @"
COPY muavin.muavin_row(
    company_code, period_year, period_month, source_file,
    entry_number, entry_counter, posting_date,
    document_number,
    fis_turu, fis_tipi, aciklama,
    kebir, hesap_kodu, hesap_adi,
    borc, alacak, tutar,
    group_key,
    karsi_hesap,
    batch_id
)
FROM STDIN (FORMAT BINARY);";

            await using (var importer = await conn.BeginBinaryImportAsync(copySql, ct))
            {
                foreach (var r in list)
                {
                    var dt = r.PostingDate!.Value.Date;
                    var py = dt.Year;
                    var pm = dt.Month;

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

                    importer.Write(batchId, NpgsqlDbType.Bigint);
                }

                await importer.CompleteAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        // ===================== OVERRIDES =====================
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

        public async Task DeleteFisTypeOverrideAsync(
            string companyCode,
            int year,
            string groupKey,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (year <= 0)
                throw new ArgumentException("year geçersiz", nameof(year));
            if (string.IsNullOrWhiteSpace(groupKey))
                return;

            companyCode = companyCode.Trim();
            groupKey = groupKey.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
DELETE FROM muavin.fis_type_override
WHERE company_code = @c AND year = @y AND group_key = @gk;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@gk", groupKey);

            await cmd.ExecuteNonQueryAsync(ct);
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

        /// <summary> bu şirket + yıl için en son import edilen batch_id'yi döner
        /// ui'da son importu al kısmı için kullanılacak 
        /// </summary>
        public async Task<long?> GetLastBatchIdAsync(
            string companyCode,
            int year,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return null;

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT batch_id
FROM muavin.import_batch
WHERE company_code = @c
  AND period_year  = @y
ORDER BY loaded_at DESC, batch_id DESC
LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj == null || obj == DBNull.Value ? null : Convert.ToInt64(obj);
        }

        /// <summary>
        /// verilen batch_id için kaç muavin_row var
        /// </summary>
        public async Task<int> GetRowCountByBatchIdAsync(
            long batchId,
            CancellationToken ct = default)
        {
            if (batchId <= 0)
                return 0;

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT COUNT(*)
FROM muavin.muavin_row
WHERE batch_id = @bid;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@bid", batchId);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(obj);
        }

        /// <summary>
        /// ✅ Bu şirket + yıl için EN SON import edilen batch'i geri alır.
        /// - muavin.muavin_row içinden batch_id ile siler
        /// - muavin.import_batch içinden ilgili kayıtları siler
        /// Transaction + güvenli geri dönüş.
        /// 
        /// Returns:
        /// (success, batchId, deletedRows, message)
        /// </summary>
        public async Task<(bool ok, long? batchId, int deletedRows, string message)> UndoLastImportBatchAsync(
            string companyCode,
            int year,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return (false, null, 0, "Şirket kodu veya yıl geçersiz.");

            companyCode = companyCode.Trim();

            // 1) Son batch_id'yi bul
            var lastBatchId = await GetLastBatchIdAsync(companyCode, year, ct);
            if (!lastBatchId.HasValue || lastBatchId.Value <= 0)
                return (false, null, 0, "Geri alınacak import bulunamadı (batch yok).");

            // 2) Kaç satır silinecek (bilgi amaçlı)
            var rowCount = await GetRowCountByBatchIdAsync(lastBatchId.Value, ct);

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                // 3) muavin_row sil
                const string delRowsSql = @"
DELETE FROM muavin.muavin_row
WHERE batch_id = @bid;";

                await using (var cmd = new NpgsqlCommand(delRowsSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@bid", lastBatchId.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 4) import_batch sil (o batch_id'ye ait tüm satırlar)
                const string delBatchSql = @"
DELETE FROM muavin.import_batch
WHERE batch_id = @bid;";

                int deletedBatchRows;
                await using (var cmd = new NpgsqlCommand(delBatchSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@bid", lastBatchId.Value);
                    deletedBatchRows = await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);

                var msg =
                    $"Geri alındı. BatchId={lastBatchId.Value}. " +
                    $"Silinen muavin_row ≈ {rowCount}. " +
                    $"Silinen import_batch kaydı = {deletedBatchRows}.";

                return (true, lastBatchId.Value, rowCount, msg);
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
                return (false, lastBatchId.Value, 0, "Undo hatası: " + ex.Message);
            }
        }

        public async Task<ImportBatchSummary?> GetLastBatchSummaryAsync(
    string companyCode,
    int year,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return null;

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);

            // 1️⃣ Son batch_id + metadata
            const string batchSql = @"
SELECT batch_id, source_file, loaded_at
FROM muavin.import_batch
WHERE company_code = @c
  AND period_year  = @y
ORDER BY loaded_at DESC, batch_id DESC
LIMIT 1;
";

            long batchId;
            string sourceFile;
            DateTimeOffset loadedAt;

            await using (var cmd = new NpgsqlCommand(batchSql, conn))
            {
                cmd.Parameters.AddWithValue("@c", companyCode);
                cmd.Parameters.AddWithValue("@y", year);

                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                if (!await rdr.ReadAsync(ct))
                    return null;

                batchId = rdr.GetInt64(0);
                sourceFile = rdr.GetString(1);
                loadedAt = rdr.GetFieldValue<DateTimeOffset>(2);
            }

            // 2️⃣ Bu batch’e ait aylar
            const string monthsSql = @"
SELECT DISTINCT period_month
FROM muavin.import_batch
WHERE batch_id = @bid
ORDER BY period_month;
";

            var months = new List<int>();
            await using (var cmd = new NpgsqlCommand(monthsSql, conn)) 
            {
                cmd.Parameters.AddWithValue("@bid", batchId);
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    months.Add(rdr.GetInt32(0));
            }

            // 3️⃣ Bu batch’teki satır sayısı
            const string countSql = @"
SELECT COUNT(*)
FROM muavin.muavin_row
WHERE batch_id = @bid;
";

            int rowCount;
            await using (var cmd = new NpgsqlCommand(countSql, conn))
            {
                cmd.Parameters.AddWithValue("@bid", batchId);
                rowCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            return new ImportBatchSummary(
                batchId,
                companyCode,
                year,
                loadedAt,
                sourceFile,
                months,
                rowCount
            );
        }


    }
}
