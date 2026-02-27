// DbMuavinRepository.cs
using Muavin.Xml.Parsing;
using Muavin.Xml.Util;
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

        // ===================== ✅ DB DEBUG LOG =====================
        private static readonly object _dbLogLock = new();

        // ✅ LogPaths varsa onu kullan; yoksa BaseDirectory'e düş
        private static string DbLogPath
        {
            get
            {
                try
                {
                    // LogPaths ile aynı yere:
                    return LogPaths.GetStableLogFilePath("debug_db.log");
                }
                catch
                {
                    // en son fallback:
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Muavin", "Logs", "debug_db.log"
                    );
                }
            }
        }

        private static void DbLog(string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}";
                lock (_dbLogLock)
                    File.AppendAllText(DbLogPath, line);
            }
            catch { }
        }

        private static void DbLogEx(string prefix, Exception ex)
        {
            DbLog($"{prefix} | {ex.GetType().Name} | {ex.Message}");
            DbLog(ex.StackTrace ?? "(no stack)");

            if (ex.InnerException != null)
            {
                DbLog($"[INNER] {ex.InnerException.GetType().Name} | {ex.InnerException.Message}");
                DbLog(ex.InnerException.StackTrace ?? "(no inner stack)");
            }

            if (ex is PostgresException pg)
            {
                DbLog($"[PG] SqlState={pg.SqlState} MessageText={pg.MessageText}");
                DbLog($"[PG] Detail={pg.Detail}");
                DbLog($"[PG] Where={pg.Where}");
                DbLog($"[PG] Position={pg.Position}");
                DbLog($"[PG] Routine={pg.Routine}");
                DbLog($"[PG] Schema={pg.SchemaName} Table={pg.TableName} Column={pg.ColumnName}");
                DbLog($"[PG] Constraint={pg.ConstraintName}");
            }
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

            // ✅ Tek instance şema kurulum kilidi (tüm session’lar için)
            const long LockKey = 987654321; // sabit bir sayı (proje için unique olması yeterli)

            await using var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k);", conn);
            lockCmd.Parameters.AddWithValue("@k", LockKey);
            lockCmd.CommandTimeout = 30;

            await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k);", conn);
            unlockCmd.Parameters.AddWithValue("@k", LockKey);
            unlockCmd.CommandTimeout = 30;

            try
            {
                await lockCmd.ExecuteNonQueryAsync(ct);

                // 1) Schema + tables + constraints (tek sefer)
                const string ddlCore = @"
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
    karsi_hesap      text NULL,

    created_at       timestamp without time zone NOT NULL DEFAULT now(),
    batch_id         bigint NULL
);

CREATE TABLE IF NOT EXISTS muavin.import_batch(
    batch_id     bigserial PRIMARY KEY,
    company_code text NOT NULL,
    period_year  int  NOT NULL,
    period_month int  NOT NULL,
    source_file  text NOT NULL,
    loaded_at    timestamptz NOT NULL DEFAULT now(),
    loaded_by    text NULL
);

CREATE TABLE IF NOT EXISTS muavin.import_batch_scope(
    batch_id     bigint NOT NULL REFERENCES muavin.import_batch(batch_id) ON DELETE CASCADE,
    period_year  int    NOT NULL,
    period_month int    NOT NULL,
    PRIMARY KEY(batch_id, period_year, period_month)
);

-- ✅ FK: import_batch.company_code -> company(company_code)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'import_batch_company_code_fkey'
    ) THEN
        ALTER TABLE muavin.import_batch
        ADD CONSTRAINT import_batch_company_code_fkey
        FOREIGN KEY (company_code)
        REFERENCES muavin.company(company_code)
        ON DELETE CASCADE;
    END IF;
END $$;
";

                await using (var cmd = new NpgsqlCommand(ddlCore, conn) { CommandTimeout = 180 })
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 2) Index’leri ayrı ayrı çalıştır (daha stabil; “hang” teşhisi de kolay)
                string[] indexSqls =
                {
            "CREATE INDEX IF NOT EXISTS ix_company_year_company ON muavin.company_year(company_code);",

            "CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year ON muavin.muavin_row(company_code, period_year);",
            "CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_month ON muavin.muavin_row(company_code, period_year, period_month);",
            "CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_source ON muavin.muavin_row(company_code, period_year, source_file);",
            "CREATE INDEX IF NOT EXISTS ix_muavin_row_batch ON muavin.muavin_row(batch_id);",
            "CREATE INDEX IF NOT EXISTS ix_muavin_row_batch_groupkey ON muavin.muavin_row(batch_id, group_key);",
            "CREATE INDEX IF NOT EXISTS ix_muavin_row_company_year_groupkey ON muavin.muavin_row(company_code, period_year, group_key);",

            "CREATE INDEX IF NOT EXISTS ix_import_batch_company_y_m ON muavin.import_batch(company_code, period_year, period_month);",
            "CREATE INDEX IF NOT EXISTS ix_import_batch_loaded_at ON muavin.import_batch(loaded_at);",
            "CREATE INDEX IF NOT EXISTS ix_import_batch_scope_y_m ON muavin.import_batch_scope(period_year, period_month);"
        };

                foreach (var sql in indexSqls)
                {
                    await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 180 };
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            finally
            {
                try { await unlockCmd.ExecuteNonQueryAsync(ct); } catch { /* ignore */ }
            }
        }

        // helper: index var mı? yoksa concurrently yarat
        private static async Task EnsureIndexConcurrentlyAsync(
            NpgsqlConnection conn,
            string schema,
            string table,
            string index,
            string createSql,
            CancellationToken ct)
        {
            const string existsSql = @"
SELECT 1
FROM pg_indexes
WHERE schemaname = @s
  AND tablename  = @t
  AND indexname  = @i;";

            await using (var cmd = new NpgsqlCommand(existsSql, conn))
            {
                cmd.Parameters.AddWithValue("@s", schema);
                cmd.Parameters.AddWithValue("@t", table);
                cmd.Parameters.AddWithValue("@i", index);

                var exists = await cmd.ExecuteScalarAsync(ct);
                if (exists != null) return;
            }

            // CREATE INDEX CONCURRENTLY bazı durumlarda "already exists" diye race yapabilir.
            // Bunu nazikçe yutalım.
            try
            {
                await using var create = new NpgsqlCommand(createSql, conn) { CommandTimeout = 300 };
                await create.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07") // duplicate_table / duplicate_relation
            {
                // başka instance aynı anda oluşturdu
            }
        }

        // ===================== COMPANIES =====================
        public async Task<List<CompanyItem>> GetCompaniesAsync(CancellationToken ct = default)
        {
            var list = new List<CompanyItem>();
            await using var conn = await OpenAsync(ct);

            const string sql = @"SELECT company_code, company_name
                                 FROM muavin.company
                                 ORDER BY company_name, company_code;";

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            while (await rdr.ReadAsync(ct))
                list.Add(new CompanyItem(rdr.GetString(0), rdr.GetString(1)));

            return list;
        }

        // 🔒 (Public) Single-call helpers (opens its own conn)
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@code", companyCode);
            cmd.Parameters.AddWithValue("@name", companyName);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ✅ TX içinde çalışacak overload (IMPORT için asıl gereken)
        private static async Task EnsureCompanyAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            string companyCode,
            string? companyName,
            CancellationToken ct = default)
        {
            companyCode = (companyCode ?? "").Trim();
            if (companyCode.Length == 0)
                throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));

            companyName = string.IsNullOrWhiteSpace(companyName) ? companyCode : companyName.Trim();

            const string sql = @"
INSERT INTO muavin.company(company_code, company_name, updated_at)
VALUES (@code, @name, now())
ON CONFLICT (company_code)
DO UPDATE SET company_name = EXCLUDED.company_name,
              updated_at   = now();";

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 60 };
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@by", (object?)createdBy ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ✅ TX overload (IMPORT)
        private static async Task EnsureCompanyYearAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            string companyCode,
            int year,
            string? createdBy,
            CancellationToken ct = default)
        {
            companyCode = (companyCode ?? "").Trim();
            if (companyCode.Length == 0) throw new ArgumentException("companyCode boş olamaz", nameof(companyCode));
            if (year <= 0) throw new ArgumentException("year geçersiz", nameof(year));

            createdBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();

            const string sql = @"
INSERT INTO muavin.company_year(company_code, year, created_by)
VALUES (@c, @y, @by)
ON CONFLICT (company_code, year) DO NOTHING;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 60 };
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
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

            await using var cmd = new NpgsqlCommand(sql, conn)
            {
                CommandTimeout = 180
            };

            cmd.Parameters.Add("@c", NpgsqlTypes.NpgsqlDbType.Text).Value = companyCode;
            cmd.Parameters.Add("@y", NpgsqlTypes.NpgsqlDbType.Integer).Value = year;

            // İstersen:
            // await cmd.PrepareAsync(ct);

            var list = new List<MuavinRow>(16_384);

            await using var rdr = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess,
                ct);

            while (await rdr.ReadAsync(ct))
            {
                list.Add(new MuavinRow
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
                });
            }

            return list;
        }


        private static async Task LogDbIdentityAsync(
    NpgsqlConnection conn,
    NpgsqlTransaction tx,
    Action<string> log,
    CancellationToken ct)
        {
            const string sql = @"
SELECT
  current_database()::text,
  current_user::text,
  inet_server_addr()::text,
  inet_server_port()::int,
  current_setting('data_directory')::text;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (await rd.ReadAsync(ct))
            {
                log($"[DB] db={rd.GetString(0)} " +
                    $"user={rd.GetString(1)} " +
                    $"ip={rd.GetString(2)} " +
                    $"port={rd.GetInt32(3)} " +
                    $"data_dir={rd.GetString(4)}");
            }
        }

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

            DbLog("====================================================");
            DbLog($"[IMPORT] START company={companyCode} source={src} replaceMode={replaceMode} replaceExistingForSameSource={replaceExistingForSameSource}");
            DbLog($"[IMPORT] DbLogPath={DbLogPath}");

            // ---------- robust schema probes ----------
            static async Task<bool> TableExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string schema, string table, CancellationToken ct)
            {
                const string sql = "SELECT to_regclass(@fq) IS NOT NULL;";
                await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@fq", $"{schema}.{table}");
                return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
            }

            static async Task<bool> ColumnExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string schema, string table, string column, CancellationToken ct)
            {
                const string sql = @"
SELECT EXISTS(
  SELECT 1
  FROM information_schema.columns
  WHERE table_schema=@s AND table_name=@t AND column_name=@c
);";
                await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@s", schema);
                cmd.Parameters.AddWithValue("@t", table);
                cmd.Parameters.AddWithValue("@c", column);
                return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
            }

            // ---------- FK guarantee helpers ----------
            static async Task EnsureCompanyForFkAsync(
                NpgsqlConnection conn,
                NpgsqlTransaction tx,
                string code,
                string? name,
                CancellationToken ct)
            {
                code = (code ?? "").Trim();
                if (code.Length == 0) throw new ArgumentException("companyCode boş olamaz", nameof(code));
                var nm = string.IsNullOrWhiteSpace(name) ? code : name.Trim();

                async Task UpsertCompanyInSchemaAsync(string schema)
                {
                    if (!await TableExistsAsync(conn, tx, schema, "company", ct))
                        return;

                    var hasUpdatedAt = await ColumnExistsAsync(conn, tx, schema, "company", "updated_at", ct);

                    var sql = hasUpdatedAt
                        ? $@"
INSERT INTO {schema}.company(company_code, company_name, updated_at)
VALUES (@c, @n, now())
ON CONFLICT (company_code)
DO UPDATE SET company_name = EXCLUDED.company_name,
              updated_at   = now();"
                        : $@"
INSERT INTO {schema}.company(company_code, company_name)
VALUES (@c, @n)
ON CONFLICT (company_code)
DO UPDATE SET company_name = EXCLUDED.company_name;";

                    await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 60 };
                    cmd.Parameters.AddWithValue("@c", code);
                    cmd.Parameters.AddWithValue("@n", nm);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await UpsertCompanyInSchemaAsync("muavin");
                await UpsertCompanyInSchemaAsync("public");
            }

            static async Task EnsureCompanyYearForFkAsync(
                NpgsqlConnection conn,
                NpgsqlTransaction tx,
                string code,
                int year,
                string? createdBy,
                CancellationToken ct)
            {
                if (year <= 0) throw new ArgumentException("year geçersiz", nameof(year));
                code = (code ?? "").Trim();
                if (code.Length == 0) throw new ArgumentException("companyCode boş olamaz", nameof(code));

                var by = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();

                async Task InsertCompanyYearInSchemaAsync(string schema)
                {
                    if (!await TableExistsAsync(conn, tx, schema, "company_year", ct))
                        return;

                    var hasCreatedBy = await ColumnExistsAsync(conn, tx, schema, "company_year", "created_by", ct);

                    var sql = hasCreatedBy
                        ? $@"
INSERT INTO {schema}.company_year(company_code, year, created_by)
VALUES (@c, @y, @by)
ON CONFLICT (company_code, year) DO NOTHING;"
                        : $@"
INSERT INTO {schema}.company_year(company_code, year)
VALUES (@c, @y)
ON CONFLICT (company_code, year) DO NOTHING;";

                    await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 60 };
                    cmd.Parameters.AddWithValue("@c", code);
                    cmd.Parameters.AddWithValue("@y", year);
                    if (hasCreatedBy) cmd.Parameters.AddWithValue("@by", (object?)by ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await InsertCompanyYearInSchemaAsync("muavin");
                await InsertCompanyYearInSchemaAsync("public");
            }

            try
            {
                var incoming = rows as IList<MuavinRow> ?? rows.ToList();
                var list = incoming.Where(r => r.PostingDate.HasValue).ToList();

                DbLog($"[IMPORT] rows_total_incoming={incoming.Count:n0} rows_with_postingDate={list.Count:n0}");
                if (list.Count == 0)
                    throw new InvalidOperationException("BulkInsert: PostingDate dolu satır yok. Parser tarih üretmiyor.");

                var yearSet = list.Select(r => r.PostingDate!.Value.Year).Distinct().ToArray();
                DbLog($"[IMPORT] yearSet={string.Join(",", yearSet.OrderBy(x => x))}");

                int gkFilled = 0;

                // SAKIN "src" deme -> CS0136 çakışıyor
                var normalizedSource = GroupKeyUtil.NormalizeSourceFile(sourceFile);

                foreach (var r in list)
                {
                    if (string.IsNullOrWhiteSpace(r.GroupKey))
                    {
                        r.GroupKey = GroupKeyUtil.Build(r.EntryNumber, r.PostingDate, r.DocumentNumber, r.FisTuru, normalizedSource);
                        gkFilled++;
                    }
                }

                DbLog($"[IMPORT] group_key_filled={gkFilled:n0}");

                var ymList = list
                    .Select(r => (y: r.PostingDate!.Value.Year, m: r.PostingDate!.Value.Month))
                    .Distinct()
                    .OrderBy(x => x.y).ThenBy(x => x.m)
                    .ToList();

                DbLog($"[IMPORT] ym_count={ymList.Count} ym={string.Join(", ", ymList.Select(x => $"{x.y}-{x.m:00}"))}");
                if (ymList.Count == 0)
                {
                    DbLog("[IMPORT] ymList empty -> RETURN");
                    return;
                }

                long batchId;

                // ============================================================
                // TX1: FK + DELETE + BATCH + SCOPE + COPY + COMMIT-1
                // ============================================================
                await using (var conn1 = await OpenAsync(ct))
                await using (var tx1 = await conn1.BeginTransactionAsync(ct))
                {
                    DbLog("[CHK] TX1 STARTED");
                    await LogDbIdentityAsync(conn1, tx1, DbLog, ct);

                    // takılmaları “hissedilir” yapalım
                    await using (var tcmd = new NpgsqlCommand(
                        "SET LOCAL statement_timeout = '10min'; SET LOCAL lock_timeout = '10s';",
                        conn1, tx1))
                    {
                        await tcmd.ExecuteNonQueryAsync(ct);
                    }

                    try
                    {
                        DbLog("[IMPORT] EnsureCompany/Year (TX1) starting...");
                        await EnsureCompanyForFkAsync(conn1, tx1, companyCode, companyName, ct);
                        var createdBy = Environment.UserName;
                        foreach (var y in yearSet)
                            await EnsureCompanyYearForFkAsync(conn1, tx1, companyCode, y, createdBy, ct);
                        DbLog("[IMPORT] EnsureCompany/Year (TX1) OK");

                        // ---- DELETE STRATEGY ----
                        if (replaceMode == ImportReplaceMode.SameSource)
                        {
                            if (replaceExistingForSameSource)
                            {
                                const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = ANY(@years)
  AND source_file = @src;";
                                await using var del = new NpgsqlCommand(delSql, conn1, tx1) { CommandTimeout = 300 };
                                del.Parameters.AddWithValue("@c", companyCode);
                                del.Parameters.AddWithValue("@years", yearSet);
                                del.Parameters.AddWithValue("@src", src);
                                var delCount = await del.ExecuteNonQueryAsync(ct);
                                DbLog($"[IMPORT] delete SameSource deletedRows={delCount:n0}");
                            }
                        }
                        else if (replaceMode == ImportReplaceMode.YearsInPayload)
                        {
                            const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = ANY(@years);";
                            await using var del = new NpgsqlCommand(delSql, conn1, tx1) { CommandTimeout = 300 };
                            del.Parameters.AddWithValue("@c", companyCode);
                            del.Parameters.AddWithValue("@years", yearSet);
                            var delCount = await del.ExecuteNonQueryAsync(ct);
                            DbLog($"[IMPORT] delete YearsInPayload deletedRows={delCount:n0}");
                        }
                        else // MonthsInPayload
                        {
                            const string delSql = @"
DELETE FROM muavin.muavin_row
WHERE company_code = @c
  AND period_year = @y
  AND period_month = ANY(@months);";

                            int totalDeleted = 0;
                            foreach (var g in ymList.GroupBy(x => x.y))
                            {
                                var months = g.Select(x => x.m).Distinct().ToArray();

                                await using var del = new NpgsqlCommand(delSql, conn1, tx1) { CommandTimeout = 300 };
                                del.Parameters.AddWithValue("@c", companyCode);
                                del.Parameters.AddWithValue("@y", g.Key);
                                del.Parameters.AddWithValue("@months", months);

                                var delCount = await del.ExecuteNonQueryAsync(ct);
                                totalDeleted += delCount;
                                DbLog($"[IMPORT] delete MonthsInPayload year={g.Key} months=[{string.Join(",", months)}] deletedRows={delCount:n0}");
                            }
                            DbLog($"[IMPORT] delete MonthsInPayload totalDeleted={totalDeleted:n0}");
                        }

                        // ---- BATCH + SCOPE ----
                        var loadedBy = Environment.UserName;
                        var first = ymList[0];

                        DbLog("[CHK] before InsertImportBatchAsync");
                        batchId = await InsertImportBatchAsync(conn1, tx1, companyCode, first.y, first.m, src, loadedBy, ct);
                        DbLog($"[IMPORT] batch_id_created={batchId} firstYM={first.y}-{first.m:00}");

                        foreach (var (y, m) in ymList)
                            await InsertImportBatchScopeAsync(conn1, tx1, batchId, y, m, ct);

                        DbLog($"[IMPORT] batch_scope_inserted count={ymList.Count}");

                        // ---- COPY ----
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

                        int written = 0;

                        DbLog("[CHK] before BeginBinaryImportAsync");
                        await using (var importer = await conn1.BeginBinaryImportAsync(copySql, ct))
                        {
                            DbLog("[CHK] after BeginBinaryImportAsync");

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
                                importer.Write(DBNull.Value, NpgsqlDbType.Text); // karsi_hesap importta boş
                                importer.Write(batchId, NpgsqlDbType.Bigint);

                                written++;
                                if (written % 50000 == 0)
                                    DbLog($"[IMPORT] COPY progress written={written:n0}");
                            }

                            DbLog("[CHK] before importer.CompleteAsync");
                            await importer.CompleteAsync(ct);
                            DbLog("[CHK] after importer.CompleteAsync");
                        }

                        DbLog($"[IMPORT] COPY complete written={written:n0}");

                        DbLog("[IMPORT] COMMIT-1 (after COPY) starting...");
                        await tx1.CommitAsync(CancellationToken.None);
                        DbLog($"[IMPORT] COMMIT-1 OK batch_id={batchId}");

                        // commit sonrası “gerçekten yazıldı mı” kanıt logu
                        await using (var verify = new NpgsqlCommand(
                            "SELECT count(*) FROM muavin.muavin_row WHERE batch_id=@bid;", conn1))
                        {
                            verify.Parameters.AddWithValue("@bid", batchId);
                            var cnt = Convert.ToInt64(await verify.ExecuteScalarAsync(ct));
                            DbLog($"[IMPORT] VERIFY after COMMIT-1 => muavin_row count for batch={batchId} is {cnt:n0}");
                        }
                    }
                    catch (Exception exTx1)
                    {
                        DbLogEx("[IMPORT] TX1 ERROR", exTx1);
                        try { await tx1.RollbackAsync(ct); DbLog("[IMPORT] TX1 ROLLBACK OK"); }
                        catch { DbLog("[IMPORT] TX1 ROLLBACK FAILED"); }
                        throw;
                    }
                }

                // ============================================================
                // TX2: RECOMPUTE (ayrı connection) + COMMIT-2
                // ============================================================
                await using (var conn2 = await OpenAsync(ct))
                await using (var tx2 = await conn2.BeginTransactionAsync(ct))
                {
                    DbLog("[CHK] TX2 STARTED");

                    try
                    {
                        await using (var cmd = new NpgsqlCommand(
                            "SET LOCAL statement_timeout = '10min'; SET LOCAL lock_timeout = '10s';",
                            conn2, tx2))
                        {
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        DbLog($"[IMPORT] recompute karsi_hesap for batch={batchId} START (TX2)");
                        await RecomputeKarsiHesapForBatchAsync(conn2, tx2, batchId, ct);
                        DbLog($"[IMPORT] recompute karsi_hesap for batch={batchId} DONE (TX2)");

                        DbLog("[IMPORT] COMMIT-2 (after recompute) starting...");
                        await tx2.CommitAsync(CancellationToken.None);
                        DbLog($"[IMPORT] COMMIT-2 OK batch_id={batchId}");
                    }
                    catch (Exception exTx2)
                    {
                        DbLogEx("[IMPORT] TX2 ERROR", exTx2);
                        try { await tx2.RollbackAsync(ct); DbLog("[IMPORT] TX2 ROLLBACK OK"); }
                        catch { DbLog("[IMPORT] TX2 ROLLBACK FAILED"); }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                DbLogEx("[IMPORT] ERROR (outer)", ex);
                throw;
            }
            finally
            {
                DbLog("[IMPORT] END");
                DbLog("====================================================");
            }
        }

        // ===================== import_batch helpers (FIXED) =====================
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

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 180 };
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@m", month);
            cmd.Parameters.AddWithValue("@src", sourceFile);
            cmd.Parameters.AddWithValue("@by", (object?)loadedBy ?? DBNull.Value);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(obj);
        }

        private static async Task InsertImportBatchScopeAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            long batchId,
            int year,
            int month,
            CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO muavin.import_batch_scope(batch_id, period_year, period_month)
VALUES (@bid, @y, @m)
ON CONFLICT (batch_id, period_year, period_month) DO NOTHING;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 180 };
            cmd.Parameters.AddWithValue("@bid", batchId);
            cmd.Parameters.AddWithValue("@y", year);
            cmd.Parameters.AddWithValue("@m", month);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ===================== ✅ DB-side KarsiHesap (FAST + CORRECT) =====================
        private static async Task RecomputeKarsiHesapForBatchAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            long batchId,
            CancellationToken ct = default)
        {
            const string sql = @"
WITH r0 AS (
    SELECT
        id,
        group_key,
        NULLIF(btrim(kebir), '') AS kebir,
        (borc > 0) AS is_deb,
        (alacak > 0) AS is_crd
    FROM muavin.muavin_row
    WHERE batch_id = @bid
      AND group_key IS NOT NULL
      AND group_key <> ''
),
debs AS (
    SELECT DISTINCT group_key, kebir
    FROM r0
    WHERE is_deb AND kebir IS NOT NULL
),
crds AS (
    SELECT DISTINCT group_key, kebir
    FROM r0
    WHERE is_crd AND kebir IS NOT NULL
),
map_deb AS (
    SELECT
        d.group_key,
        d.kebir AS my_kebir,
        COALESCE(string_agg(c.kebir, ' | ' ORDER BY c.kebir), '') AS karsi
    FROM debs d
    LEFT JOIN crds c
      ON c.group_key = d.group_key
     AND c.kebir <> d.kebir
    GROUP BY d.group_key, d.kebir
),
map_crd AS (
    SELECT
        c.group_key,
        c.kebir AS my_kebir,
        COALESCE(string_agg(d.kebir, ' | ' ORDER BY d.kebir), '') AS karsi
    FROM crds c
    LEFT JOIN debs d
      ON d.group_key = c.group_key
     AND d.kebir <> c.kebir
    GROUP BY c.group_key, c.kebir
),
upd AS (
    SELECT
        r.id,
        CASE
            WHEN r.is_deb AND r.kebir IS NOT NULL THEN COALESCE(md.karsi, '')
            WHEN r.is_crd AND r.kebir IS NOT NULL THEN COALESCE(mc.karsi, '')
            ELSE ''
        END AS karsi
    FROM r0 r
    LEFT JOIN map_deb md
      ON md.group_key = r.group_key
     AND md.my_kebir  = r.kebir
    LEFT JOIN map_crd mc
      ON mc.group_key = r.group_key
     AND mc.my_kebir  = r.kebir
)
UPDATE muavin.muavin_row t
SET karsi_hesap = NULLIF(u.karsi, '')
FROM upd u
WHERE t.id = u.id;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 600 };
            cmd.Parameters.AddWithValue("@bid", batchId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task RecomputeKarsiHesapForCompanyYearAsync(string companyCode, int year, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return;

            companyCode = companyCode.Trim();

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Kilit beklemeyi sınırlayalım
            await using (var set = new Npgsql.NpgsqlCommand(
                "SET LOCAL statement_timeout = '10min'; SET LOCAL lock_timeout = '10s';",
                conn, tx))
            {
                await set.ExecuteNonQueryAsync(ct);
            }

            const string sql = @"
WITH r0 AS (
    SELECT
        id,
        group_key,
        NULLIF(btrim(kebir), '') AS kebir,
        (borc > 0) AS is_deb,
        (alacak > 0) AS is_crd
    FROM muavin.muavin_row
    WHERE company_code = @c
      AND period_year  = @y
      AND group_key IS NOT NULL
      AND group_key <> ''
),
debs AS (
    SELECT DISTINCT group_key, kebir
    FROM r0
    WHERE is_deb AND kebir IS NOT NULL
),
crds AS (
    SELECT DISTINCT group_key, kebir
    FROM r0
    WHERE is_crd AND kebir IS NOT NULL
),
map_deb AS (
    SELECT
        d.group_key,
        d.kebir AS my_kebir,
        COALESCE(string_agg(c.kebir, ' | ' ORDER BY c.kebir), '') AS karsi
    FROM debs d
    LEFT JOIN crds c
      ON c.group_key = d.group_key
     AND c.kebir <> d.kebir
    GROUP BY d.group_key, d.kebir
),
map_crd AS (
    SELECT
        c.group_key,
        c.kebir AS my_kebir,
        COALESCE(string_agg(d.kebir, ' | ' ORDER BY d.kebir), '') AS karsi
    FROM crds c
    LEFT JOIN debs d
      ON d.group_key = c.group_key
     AND d.kebir <> c.kebir
    GROUP BY c.group_key, c.kebir
),
upd AS (
    SELECT
        r.id,
        CASE
            WHEN r.is_deb AND r.kebir IS NOT NULL THEN COALESCE(md.karsi, '')
            WHEN r.is_crd AND r.kebir IS NOT NULL THEN COALESCE(mc.karsi, '')
            ELSE ''
        END AS karsi
    FROM r0 r
    LEFT JOIN map_deb md
      ON md.group_key = r.group_key
     AND md.my_kebir  = r.kebir
    LEFT JOIN map_crd mc
      ON mc.group_key = r.group_key
     AND mc.my_kebir  = r.kebir
)
UPDATE muavin.muavin_row t
SET karsi_hesap = NULLIF(u.karsi, '')
FROM upd u
WHERE t.id = u.id;";

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 600 };
            cmd.Parameters.Add("@c", NpgsqlTypes.NpgsqlDbType.Text).Value = companyCode;
            cmd.Parameters.Add("@y", NpgsqlTypes.NpgsqlDbType.Integer).Value = year;

            var affected = await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);

            // DbLog gibi bir logger varsa burada yaz:
             DbLog($"[RECOMPUTE] affected={affected:n0} company={companyCode} year={year}");
        }

        // ===================== OVERRIDES =====================
        public async Task<Dictionary<string, string>> GetFisTypeOverridesAsync(string companyCode, int year, CancellationToken ct = default)
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.Add("@c", NpgsqlTypes.NpgsqlDbType.Text).Value = companyCode;
            cmd.Parameters.Add("@y", NpgsqlTypes.NpgsqlDbType.Integer).Value = year;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var gk = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                var ft = rdr.IsDBNull(1) ? null : rdr.GetString(1);

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

            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 120 };
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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
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

        private static bool IsTxtLikeSource(string? normalizedSourceFile)
        {
            var s = (normalizedSourceFile ?? "").Trim();
            if (s.Length == 0) return false;

            var ext = Path.GetExtension(s);
            return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSpecialClosingType(string? fisTuru)
        {
            var ft = (fisTuru ?? "").Trim();
            return ft.Equals("Açılış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Gelir Tablosu Kapanış", StringComparison.OrdinalIgnoreCase)
                || ft.Equals("Yansıtma Kapama", StringComparison.OrdinalIgnoreCase);
        }

        // ✅ TXT/CSV için DOC ASLA eklenmez (override kalıcılığı)
        private static string BuildGroupKey(MuavinRow r, string normalizedSourceFile)
        {
            var d = r.PostingDate?.ToString("yyyy-MM-dd") ?? "";
            var no = r.EntryNumber ?? "";

            if (IsSpecialClosingType(r.FisTuru))
                return $"{no}|{d}";

            if (IsTxtLikeSource(normalizedSourceFile))
                return $"{no}|{d}";

            var doc = r.DocumentNumber ?? "";
            return string.IsNullOrWhiteSpace(doc) ? $"{no}|{d}" : $"{no}|{d}|DOC:{doc}";
        }

        /// <summary> bu şirket + yıl için en son import edilen batch_id'yi döner </summary>
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
SELECT b.batch_id
FROM muavin.import_batch b
JOIN muavin.import_batch_scope s ON s.batch_id = b.batch_id
WHERE b.company_code = @c
  AND s.period_year  = @y
ORDER BY b.loaded_at DESC, b.batch_id DESC
LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@c", companyCode);
            cmd.Parameters.AddWithValue("@y", year);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj == null || obj == DBNull.Value ? null : Convert.ToInt64(obj);
        }

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

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@bid", batchId);

            var obj = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(obj);
        }

        public async Task<(bool ok, long? batchId, int deletedRows, string message)> UndoLastImportBatchAsync(
            string companyCode,
            int year,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode) || year <= 0)
                return (false, null, 0, "Şirket kodu veya yıl geçersiz.");

            companyCode = companyCode.Trim();

            var lastBatchId = await GetLastBatchIdAsync(companyCode, year, ct);
            if (!lastBatchId.HasValue || lastBatchId.Value <= 0)
                return (false, null, 0, "Geri alınacak import bulunamadı (batch yok).");

            var rowCount = await GetRowCountByBatchIdAsync(lastBatchId.Value, ct);

            await using var conn = await OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                const string delRowsSql = @"
DELETE FROM muavin.muavin_row
WHERE batch_id = @bid;";

                await using (var cmd = new NpgsqlCommand(delRowsSql, conn, tx) { CommandTimeout = 300 })
                {
                    cmd.Parameters.AddWithValue("@bid", lastBatchId.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                const string delScopeSql = @"
DELETE FROM muavin.import_batch_scope
WHERE batch_id = @bid;";

                await using (var cmd = new NpgsqlCommand(delScopeSql, conn, tx) { CommandTimeout = 300 })
                {
                    cmd.Parameters.AddWithValue("@bid", lastBatchId.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                const string delBatchSql = @"
DELETE FROM muavin.import_batch
WHERE batch_id = @bid;";

                int deletedBatchRows;
                await using (var cmd = new NpgsqlCommand(delBatchSql, conn, tx) { CommandTimeout = 300 })
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
                try { await tx.RollbackAsync(ct); } catch { }
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

            const string batchSql = @"
SELECT b.batch_id, b.source_file, b.loaded_at
FROM muavin.import_batch b
JOIN muavin.import_batch_scope s ON s.batch_id = b.batch_id
WHERE b.company_code = @c
  AND s.period_year  = @y
ORDER BY b.loaded_at DESC, b.batch_id DESC
LIMIT 1;
";

            long batchId;
            string sourceFile;
            DateTimeOffset loadedAt;

            await using (var cmd = new NpgsqlCommand(batchSql, conn) { CommandTimeout = 120 })
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

            const string monthsSql = @"
SELECT DISTINCT period_month
FROM muavin.import_batch_scope
WHERE batch_id = @bid AND period_year = @y
ORDER BY period_month;
";

            var months = new List<int>();
            await using (var cmd = new NpgsqlCommand(monthsSql, conn) { CommandTimeout = 120 })
            {
                cmd.Parameters.AddWithValue("@bid", batchId);
                cmd.Parameters.AddWithValue("@y", year);
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    months.Add(rdr.GetInt32(0));
            }

            const string countSql = @"
SELECT COUNT(*)
FROM muavin.muavin_row
WHERE batch_id = @bid;
";

            int rowCount;
            await using (var cmd = new NpgsqlCommand(countSql, conn) { CommandTimeout = 120 })
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

        public async Task<List<CompanyItem>> FindCompaniesByNameAsync(
            string name,
            int limit = 10,
            CancellationToken ct = default)
        {
            var list = new List<CompanyItem>();
            if (string.IsNullOrWhiteSpace(name)) return list;

            await using var conn = await OpenAsync(ct);

            const string sql = @"
SELECT company_code, company_name
FROM muavin.company
WHERE company_name ILIKE '%' || @q || '%'
ORDER BY
  CASE WHEN company_name ILIKE @q || '%' THEN 0 ELSE 1 END,
  length(company_name),
  company_name
LIMIT @lim;";

            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@q", name.Trim());
            cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 50));

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                list.Add(new CompanyItem(rdr.GetString(0), rdr.GetString(1)));

            return list;
        }
    }
}