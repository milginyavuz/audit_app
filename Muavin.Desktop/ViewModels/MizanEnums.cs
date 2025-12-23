// MizanEnums.cs
namespace Muavin.Desktop.Enums
{
    public enum MizanType
    {
        Genel,
        SadeceDuzeltmeler,
        IkiTarihArasi
    }

    public enum HareketDurumu
    {
        Hepsi,
        SadeceIsleyen,
        SadeceIslemeyen
    }

    public enum GorunumModu
    {
        AcikDetayli,         // Kebir + alt hesaplar (indentli)
        KapaliSadeceKebir    // Sadece Kebir satırları
    }
}
