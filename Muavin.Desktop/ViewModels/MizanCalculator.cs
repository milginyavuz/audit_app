// MizanCalculator.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Muavin.Desktop.Enums;
using Muavin.Xml.Parsing;
using Muavin.Desktop.Util;

namespace Muavin.Desktop.ViewModels
{
    public static class MizanCalculator
    {
        public static IEnumerable<MizanRow> Calculate(
            IEnumerable<MuavinRow> source,
            DateTime startDate,
            DateTime endDate,
            MizanType mizanTipi,
            HareketDurumu hareketDurumu,
            GorunumModu gorunum)
        {
            if (source == null)
                return Enumerable.Empty<MizanRow>();

            var start = startDate.Date;
            var end = endDate.Date;

            var validRows = source
                .Where(r => r.PostingDate.HasValue)
                .ToList();

            if (!validRows.Any())
                return Enumerable.Empty<MizanRow>();

            var periodRows = validRows
                .Where(r => r.PostingDate!.Value.Date >= start &&
                            r.PostingDate!.Value.Date <= end)
                .ToList();

            var allToEndRows = validRows
                .Where(r => r.PostingDate!.Value.Date <= end)
                .ToList();

            var kebirGroups = allToEndRows
                .GroupBy(r => (r.Kebir ?? string.Empty).Trim())
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            var result = new List<MizanRow>();

            foreach (var kebirGroup in kebirGroups)
            {
                string kebirCode = kebirGroup.Key;

                var kebirPeriodRows = periodRows
                    .Where(r => string.Equals(
                        (r.Kebir ?? string.Empty).Trim(),
                        kebirCode,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                decimal kebirDonemBorc = kebirPeriodRows.Sum(r => r.Borc);
                decimal kebirDonemAlacak = kebirPeriodRows.Sum(r => r.Alacak);
                bool kebirIsWorking = kebirDonemBorc != 0m || kebirDonemAlacak != 0m;

                decimal kebirBorcToplam = kebirGroup.Sum(r => r.Borc);
                decimal kebirAlacakToplam = kebirGroup.Sum(r => r.Alacak);
                decimal kebirNet = kebirBorcToplam - kebirAlacakToplam;

                decimal kebirBorcBakiye = 0m;
                decimal kebirAlacakBakiye = 0m;
                if (kebirNet > 0)
                    kebirBorcBakiye = kebirNet;
                else if (kebirNet < 0)
                    kebirAlacakBakiye = -kebirNet;

                if (hareketDurumu == HareketDurumu.SadeceIsleyen && !kebirIsWorking)
                    goto SkipKebirHeader;
                if (hareketDurumu == HareketDurumu.SadeceIslemeyen && kebirIsWorking)
                    goto SkipKebirHeader;

                // fallback: muavinden gelen ad (eski davranış)
                string fallbackKebirAdi =
                    kebirGroup
                        .FirstOrDefault(r => string.Equals(
                            (r.HesapKodu ?? string.Empty).Trim(),
                            kebirCode,
                            StringComparison.OrdinalIgnoreCase))
                        ?.HesapAdi?.Trim()
                    ?? kebirGroup.First().HesapAdi?.Trim()
                    ?? string.Empty;

                // ✅ asıl: HesapPlani.txt’den kebir adı
                // 8 ve 9 için otomatik özel başlık da burada çözülüyor.
                string kebirHesapAdi = AccountPlan.GetNameForHeader(kebirCode, fallbackKebirAdi);

                var kebirRow = new MizanRow
                {
                    Kebir = kebirCode,
                    HesapKodu = kebirCode,
                    HesapAdi = kebirHesapAdi,
                    Borc = kebirDonemBorc,
                    Alacak = kebirDonemAlacak,
                    BorcBakiye = kebirBorcBakiye,
                    AlacakBakiye = kebirAlacakBakiye,

                    DuzenlenmisBorcBakiye = kebirBorcBakiye,
                    DuzenlenmisAlacakBakiye = kebirAlacakBakiye,

                    IsIsleyen = kebirIsWorking,
                    IsKebirRow = true,
                    Level = 0
                };

                result.Add(kebirRow);

            SkipKebirHeader:

                if (gorunum == GorunumModu.KapaliSadeceKebir)
                    continue;

                var accountGroups = kebirGroup
                    .GroupBy(r => new
                    {
                        HesapKodu = (r.HesapKodu ?? string.Empty).Trim(),
                        HesapAdi = (r.HesapAdi ?? string.Empty).Trim()
                    })
                    .OrderBy(g => g.Key.HesapKodu);

                foreach (var accountGroup in accountGroups)
                {
                    string hesapKodu = accountGroup.Key.HesapKodu;
                    string hesapAdi = accountGroup.Key.HesapAdi;

                    if (string.Equals(hesapKodu, kebirCode, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var periodForAccount = kebirPeriodRows
                        .Where(r => string.Equals(
                            (r.HesapKodu ?? string.Empty).Trim(),
                            hesapKodu,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    decimal donemBorc = periodForAccount.Sum(r => r.Borc);
                    decimal donemAlacak = periodForAccount.Sum(r => r.Alacak);
                    bool isWorking = donemBorc != 0m || donemAlacak != 0m;

                    if (hareketDurumu == HareketDurumu.SadeceIsleyen && !isWorking)
                        continue;
                    if (hareketDurumu == HareketDurumu.SadeceIslemeyen && isWorking)
                        continue;

                    decimal borcToplam = accountGroup.Sum(r => r.Borc);
                    decimal alacakToplam = accountGroup.Sum(r => r.Alacak);
                    decimal net = borcToplam - alacakToplam;

                    decimal borcBakiye = 0m;
                    decimal alacakBakiye = 0m;
                    if (net > 0)
                        borcBakiye = net;
                    else if (net < 0)
                        alacakBakiye = -net;

                    var row = new MizanRow
                    {
                        Kebir = kebirCode,
                        HesapKodu = hesapKodu,
                        HesapAdi = hesapAdi, // alt hesap adları aynen kalsın demiştin
                        Borc = donemBorc,
                        Alacak = donemAlacak,
                        BorcBakiye = borcBakiye,
                        AlacakBakiye = alacakBakiye,

                        DuzenlenmisBorcBakiye = borcBakiye,
                        DuzenlenmisAlacakBakiye = alacakBakiye,

                        IsIsleyen = isWorking,
                        IsKebirRow = false,
                        Level = 1
                    };

                    result.Add(row);
                }
            }

            return result;
        }
    }
}
