using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Muavin.Xml.Parsing;   // MuavinRow için

namespace Muavin.Xml.Data
{
    /// <summary>
    /// PostgreSQL'e bağlanıp muavin_row tablosuna toplu kayıt atan basit helper.
    /// </summary>
    public static class MuavinDb
    {
        // TODO: ŞİFRENİ BURAYA YAZ
        private const string ConnectionString =
            "Host=localhost;Port=5432;Username=postgres;Password=anilymm;Database=muavin";

        private static async Task<NpgsqlConnection> OpenAsync()
        {
            var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            return conn;
        }

        /// <summary>
        /// Tek dosyadan gelen satırları muavin_row tablosuna yazar.
        /// companyCode: VKN veya şirket kodu
        /// year/month: yevmiye dönemi
        /// sourceFile: xml yolu (loglamak için)
        /// </summary>
        public static async Task BulkInsertAsync(
            IEnumerable<MuavinRow> rows,
            string companyCode,
            int year,
            int month,
            string sourceFile)
        {
            await using var conn = await OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            const string sql = @"
INSERT INTO muavin_row
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
    karsi_hesap
)
VALUES
(
    @company_code,
    @period_year,
    @period_month,
    @source_file,
    @entry_number,
    @entry_counter,
    @posting_date,
    @document_number,
    @fis_turu,
    @fis_tipi,
    @aciklama,
    @kebir,
    @hesap_kodu,
    @hesap_adi,
    @borc,
    @alacak,
    @tutar,
    @group_key,
    @side,
    @contra_kebir_csv,
    @contra_hesap_csv,
    @karsi_hesap
);";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);

            // Parametreleri bir kez tanımlayıp her satırda sadece değerini değiştiriyoruz
            cmd.Parameters.Add(new NpgsqlParameter("company_code", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("period_year", NpgsqlTypes.NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("period_month", NpgsqlTypes.NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("source_file", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("entry_number", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("entry_counter", NpgsqlTypes.NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("posting_date", NpgsqlTypes.NpgsqlDbType.Date));
            cmd.Parameters.Add(new NpgsqlParameter("document_number", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("fis_turu", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("fis_tipi", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("aciklama", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("kebir", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("hesap_kodu", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("hesap_adi", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("borc", NpgsqlTypes.NpgsqlDbType.Numeric));
            cmd.Parameters.Add(new NpgsqlParameter("alacak", NpgsqlTypes.NpgsqlDbType.Numeric));
            cmd.Parameters.Add(new NpgsqlParameter("tutar", NpgsqlTypes.NpgsqlDbType.Numeric));
            cmd.Parameters.Add(new NpgsqlParameter("group_key", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("side", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("contra_kebir_csv", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("contra_hesap_csv", NpgsqlTypes.NpgsqlDbType.Varchar));
            cmd.Parameters.Add(new NpgsqlParameter("karsi_hesap", NpgsqlTypes.NpgsqlDbType.Varchar));

            foreach (var r in rows)
            {
                cmd.Parameters["company_code"].Value = companyCode;
                cmd.Parameters["period_year"].Value = year;
                cmd.Parameters["period_month"].Value = month;
                cmd.Parameters["source_file"].Value = sourceFile;

                cmd.Parameters["entry_number"].Value = (object?)r.EntryNumber ?? DBNull.Value;
                cmd.Parameters["entry_counter"].Value = (object?)r.EntryCounter ?? DBNull.Value;
                cmd.Parameters["posting_date"].Value = (object?)r.PostingDate ?? DBNull.Value;
                cmd.Parameters["document_number"].Value = (object?)r.DocumentNumber ?? DBNull.Value;
                cmd.Parameters["fis_turu"].Value = (object?)r.FisTuru ?? DBNull.Value;
                cmd.Parameters["fis_tipi"].Value = (object?)r.FisTipi ?? DBNull.Value;
                cmd.Parameters["aciklama"].Value = (object?)r.Aciklama ?? DBNull.Value;
                cmd.Parameters["kebir"].Value = (object?)r.Kebir ?? DBNull.Value;
                cmd.Parameters["hesap_kodu"].Value = (object?)r.HesapKodu ?? DBNull.Value;
                cmd.Parameters["hesap_adi"].Value = (object?)r.HesapAdi ?? DBNull.Value;
                cmd.Parameters["borc"].Value = r.Borc;
                cmd.Parameters["alacak"].Value = r.Alacak;
                cmd.Parameters["tutar"].Value = r.Tutar;
                cmd.Parameters["group_key"].Value = (object?)r.GroupKey ?? DBNull.Value;
                cmd.Parameters["side"].Value = (object?)r.Side ?? DBNull.Value;
                cmd.Parameters["contra_kebir_csv"].Value = (object?)r.ContraKebirCsv ?? DBNull.Value;
                cmd.Parameters["contra_hesap_csv"].Value = (object?)r.ContraHesapCsv ?? DBNull.Value;
                cmd.Parameters["karsi_hesap"].Value = (object?)r.KarsiHesap ?? DBNull.Value;

                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
    }
}
