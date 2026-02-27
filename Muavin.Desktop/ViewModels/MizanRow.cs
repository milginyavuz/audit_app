// MizanRow.cs
namespace Muavin.Desktop.ViewModels
{
    /// <summary>
    /// çalışma mizanındaki bir satırı temsil eder
    /// </summary>
    public sealed class MizanRow
    {
        public string Kebir { get; set; } = string.Empty;
        public string HesapKodu { get; set; } = string.Empty;
        public string HesapAdi { get; set; } = string.Empty;

        // Period hareketleri (Start-End arası)
        public decimal Borc { get; set; }
        public decimal Alacak { get; set; }

        // Closing bakiyeyi şu an UI’da gösterdiğin alanlar (borç/alacak bakiyesi)
        public decimal BorcBakiye { get; set; }
        public decimal AlacakBakiye { get; set; }

        // ✅ Yeni: Opening / Closing net bakiyeler (drill-down ve denetim için kritik)
        public decimal OpeningNetBakiye { get; set; }   // StartDate öncesi net (Borc-Alacak)
        public decimal ClosingNetBakiye { get; set; }   // EndDate dahil net (Borc-Alacak)

        // geriye dönük uyumluluk için tutuluyor
        public decimal DuzenlenmisBorcBakiye { get; set; }
        public decimal DuzenlenmisAlacakBakiye { get; set; }

        // işleyen / açık hesap flagleri
        public bool IsIsleyen { get; set; }
        public bool IsAcik { get; set; }

        // ui için
        public bool IsKebirRow { get; set; }   // kebir satırı mı
        public int Level { get; set; }         // 0 = kebir, 1 = alt hesap
    }
}
