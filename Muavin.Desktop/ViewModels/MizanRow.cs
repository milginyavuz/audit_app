// MizanRow.cs
namespace Muavin.Desktop.ViewModels
{
    /// <summary>
    /// Çalışma mizanındaki bir satırı temsil eder.
    /// </summary>
    public sealed class MizanRow
    {
        public string Kebir { get; set; } = string.Empty;
        public string HesapKodu { get; set; } = string.Empty;
        public string HesapAdi { get; set; } = string.Empty;

        public decimal Borc { get; set; }
        public decimal Alacak { get; set; }

        public decimal BorcBakiye { get; set; }
        public decimal AlacakBakiye { get; set; }

        // Geriye dönük uyumluluk için tutuluyor (UI/Excel'de kullanılmıyor)
        public decimal DuzenlenmisBorcBakiye { get; set; }
        public decimal DuzenlenmisAlacakBakiye { get; set; }

        // İşleyen / açık hesap flag’leri
        public bool IsIsleyen { get; set; }
        public bool IsAcik { get; set; }

        // UI için
        public bool IsKebirRow { get; set; }   // Kebir satırı mı?
        public int Level { get; set; }         // 0 = kebir, 1 = alt hesap
    }
}
