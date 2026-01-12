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
        AcikDetayli,         // kebir + alt hesaplar indentli
        KapaliSadeceKebir    // sadece kebir satırları
    }
}
