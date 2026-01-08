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

        public decimal Borc { get; set; }
        public decimal Alacak { get; set; }

        public decimal BorcBakiye { get; set; }
        public decimal AlacakBakiye { get; set; }

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
