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

            // ✅ Period: Start-End arası
            var periodRows = validRows
                .Where(r => r.PostingDate!.Value.Date >= start &&
                            r.PostingDate!.Value.Date <= end)
                .ToList();

            // ✅ Closing hesaplamak için: EndDate'e kadar (dahil)
            var allToEndRows = validRows
                .Where(r => r.PostingDate!.Value.Date <= end)
                .ToList();

            // ✅ Opening hesaplamak için: StartDate'ten önce
            var rowsBeforeStart = validRows
                .Where(r => r.PostingDate!.Value.Date < start)
                .ToList();

            // Kebir grupları (closing evreni üzerinden)
            var kebirGroups = allToEndRows
                .GroupBy(r => (r.Kebir ?? string.Empty).Trim())
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            var result = new List<MizanRow>();

            foreach (var kebirGroup in kebirGroups)
            {
                string kebirCode = kebirGroup.Key;

                // Period rows for this kebir
                var kebirPeriodRows = periodRows
                    .Where(r => string.Equals((r.Kebir ?? "").Trim(), kebirCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                decimal kebirDonemBorc = kebirPeriodRows.Sum(r => r.Borc);
                decimal kebirDonemAlacak = kebirPeriodRows.Sum(r => r.Alacak);
                bool kebirIsWorking = kebirDonemBorc != 0m || kebirDonemAlacak != 0m;

                // ✅ Closing net (EndDate'e kadar)
                decimal kebirClosingBorcToplam = kebirGroup.Sum(r => r.Borc);
                decimal kebirClosingAlacakToplam = kebirGroup.Sum(r => r.Alacak);
                decimal kebirClosingNet = kebirClosingBorcToplam - kebirClosingAlacakToplam;

                // ✅ Opening net (StartDate öncesi)
                var kebirBeforeRows = rowsBeforeStart
                    .Where(r => string.Equals((r.Kebir ?? "").Trim(), kebirCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                decimal kebirOpeningNet = kebirBeforeRows.Sum(r => r.Borc) - kebirBeforeRows.Sum(r => r.Alacak);

                // UI'da gösterdiğin bakiye: closing net'i borç/alacak bakiyeye böl
                decimal kebirBorcBakiye = 0m;
                decimal kebirAlacakBakiye = 0m;
                if (kebirClosingNet > 0) kebirBorcBakiye = kebirClosingNet;
                else if (kebirClosingNet < 0) kebirAlacakBakiye = -kebirClosingNet;

                if (hareketDurumu == HareketDurumu.SadeceIsleyen && !kebirIsWorking)
                    goto SkipKebirHeader;
                if (hareketDurumu == HareketDurumu.SadeceIslemeyen && kebirIsWorking)
                    goto SkipKebirHeader;

                // fallback muavinden gelen ad (eski davranış)
                string fallbackKebirAdi =
                    kebirGroup
                        .FirstOrDefault(r => string.Equals(
                            (r.HesapKodu ?? string.Empty).Trim(),
                            kebirCode,
                            StringComparison.OrdinalIgnoreCase))
                        ?.HesapAdi?.Trim()
                    ?? kebirGroup.First().HesapAdi?.Trim()
                    ?? string.Empty;

                // asıl HesapPlani.txt den kebir adı
                string kebirHesapAdi = AccountPlan.GetNameForHeader(kebirCode, fallbackKebirAdi);

                var kebirRow = new MizanRow
                {
                    Kebir = kebirCode,
                    HesapKodu = kebirCode,
                    HesapAdi = kebirHesapAdi,

                    // Period
                    Borc = kebirDonemBorc,
                    Alacak = kebirDonemAlacak,

                    // Closing (UI)
                    BorcBakiye = kebirBorcBakiye,
                    AlacakBakiye = kebirAlacakBakiye,

                    // ✅ Opening/Closing net (denetim için)
                    OpeningNetBakiye = kebirOpeningNet,
                    ClosingNetBakiye = kebirClosingNet,

                    // geriye dönük
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

                    // Period for account
                    var periodForAccount = kebirPeriodRows
                        .Where(r => string.Equals((r.HesapKodu ?? "").Trim(), hesapKodu, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    decimal donemBorc = periodForAccount.Sum(r => r.Borc);
                    decimal donemAlacak = periodForAccount.Sum(r => r.Alacak);
                    bool isWorking = donemBorc != 0m || donemAlacak != 0m;

                    if (hareketDurumu == HareketDurumu.SadeceIsleyen && !isWorking)
                        continue;
                    if (hareketDurumu == HareketDurumu.SadeceIslemeyen && isWorking)
                        continue;

                    // Closing net for account (EndDate'e kadar)
                    decimal closingBorcToplam = accountGroup.Sum(r => r.Borc);
                    decimal closingAlacakToplam = accountGroup.Sum(r => r.Alacak);
                    decimal closingNet = closingBorcToplam - closingAlacakToplam;

                    // Opening net for account (StartDate öncesi)
                    var beforeForAccount = rowsBeforeStart
                        .Where(r => string.Equals((r.Kebir ?? "").Trim(), kebirCode, StringComparison.OrdinalIgnoreCase))
                        .Where(r => string.Equals((r.HesapKodu ?? "").Trim(), hesapKodu, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    decimal openingNet = beforeForAccount.Sum(r => r.Borc) - beforeForAccount.Sum(r => r.Alacak);

                    // UI bakiye (closing)
                    decimal borcBakiye = 0m;
                    decimal alacakBakiye = 0m;
                    if (closingNet > 0) borcBakiye = closingNet;
                    else if (closingNet < 0) alacakBakiye = -closingNet;

                    var row = new MizanRow
                    {
                        Kebir = kebirCode,
                        HesapKodu = hesapKodu,
                        HesapAdi = hesapAdi,

                        // Period
                        Borc = donemBorc,
                        Alacak = donemAlacak,

                        // Closing UI
                        BorcBakiye = borcBakiye,
                        AlacakBakiye = alacakBakiye,

                        // ✅ Opening/Closing net
                        OpeningNetBakiye = openingNet,
                        ClosingNetBakiye = closingNet,

                        // geriye dönük
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
