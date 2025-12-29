using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;

namespace Muavin.Xml.Parsing
{
    /// <summary>
    /// MuavinRow nesnelerini PostgreSQL'deki muavin_row tablosuna yazmak için repository.
    /// </summary>
    public static class MuavinRowRepository
    {
        /// <summary>
        /// Bir XML dosyasından gelen satırları toplu olarak muavin_row tablosuna ekler.
        /// </summary>
        public static async Task InsertManyAsync(
            string connectionString,
            IEnumerable<MuavinRow> rows,
            string companyCode,
            int periodYear,
            int periodMonth,
            string sourceFile)
        {
            if (rows == null) return;

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

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
    karsi_hesap,
    created_at
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
    @karsi_hesap,
    @created_at
);";

            await using var cmd = new NpgsqlCommand(sql, conn);

            cmd.Parameters.Add(new NpgsqlParameter("company_code", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("period_year", DbType.Int32));
            cmd.Parameters.Add(new NpgsqlParameter("period_month", DbType.Int32));
            cmd.Parameters.Add(new NpgsqlParameter("source_file", DbType.String));

            cmd.Parameters.Add(new NpgsqlParameter("entry_number", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("entry_counter", DbType.Int32));
            cmd.Parameters.Add(new NpgsqlParameter("posting_date", DbType.Date));
            cmd.Parameters.Add(new NpgsqlParameter("document_number", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("fis_turu", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("fis_tipi", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("aciklama", DbType.String));

            cmd.Parameters.Add(new NpgsqlParameter("kebir", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("hesap_kodu", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("hesap_adi", DbType.String));

            cmd.Parameters.Add(new NpgsqlParameter("borc", DbType.Decimal));
            cmd.Parameters.Add(new NpgsqlParameter("alacak", DbType.Decimal));
            cmd.Parameters.Add(new NpgsqlParameter("tutar", DbType.Decimal));

            cmd.Parameters.Add(new NpgsqlParameter("group_key", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("side", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("contra_kebir_csv", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("contra_hesap_csv", DbType.String));
            cmd.Parameters.Add(new NpgsqlParameter("karsi_hesap", DbType.String));

            cmd.Parameters.Add(new NpgsqlParameter("created_at", DbType.DateTime));

            foreach (var r in rows)
            {

                cmd.Parameters["company_code"].Value = companyCode;
                cmd.Parameters["period_year"].Value = periodYear;
                cmd.Parameters["period_month"].Value = periodMonth;
                cmd.Parameters["source_file"].Value = sourceFile;

                cmd.Parameters["entry_number"].Value = (object?)r.EntryNumber ?? DBNull.Value;
                cmd.Parameters["entry_counter"].Value = (object?)r.EntryCounter ?? DBNull.Value;
                cmd.Parameters["posting_date"].Value = (object?)r.PostingDate?.Date ?? DBNull.Value;
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

                cmd.Parameters["created_at"].Value = DateTime.UtcNow;

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
