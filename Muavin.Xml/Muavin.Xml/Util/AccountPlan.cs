// AccountPlan.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Muavin.Desktop.Util
{
    public static class AccountPlan
    {
        private static readonly Dictionary<string, string> _kebirNames =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool IsLoaded => _kebirNames.Count > 0;

        public static void Load(string filePath)
        {
            _kebirNames.Clear();

            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (!File.Exists(filePath)) return;

            foreach (var raw in File.ReadAllLines(filePath))
            {
                var line = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("#")) continue;

                var pair = TryParseLineToPair(line);
                if (pair == null) continue;

                var (code, name) = pair.Value;
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                    continue;

                // 1 hane (8,9) veya 3 hane (100..999)
                if (code.Length == 1 || code.Length == 3)
                    _kebirNames[code] = name;
            }

            // GARANTİ: 8 ve 9 başlıkları her durumda olsun
            if (!_kebirNames.ContainsKey("8")) _kebirNames["8"] = "Serbest Hesaplar";
            if (!_kebirNames.ContainsKey("9")) _kebirNames["9"] = "Nazım Hesaplar";
        }

        public static bool TryGetKebirName(string code, out string name)
        {
            name = "";
            if (string.IsNullOrWhiteSpace(code)) return false;

            code = code.Trim();
            return _kebirNames.TryGetValue(code, out name!);
        }

        /// <summary>
        /// Mizan kebir başlık satırında gösterilecek adı döndürür.
        /// Öncelik: HesapPlani.txt (kebirCode) -> "8/9" başlıkları -> fallbackName -> ""
        /// </summary>
        public static string GetNameForHeader(string kebirCode, string? fallbackName = null)
        {
            kebirCode = (kebirCode ?? "").Trim();

            // 8 ve 9 için özel başlık
            if (kebirCode.StartsWith("8", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetKebirName("8", out var n8) ? n8 : "Serbest Hesaplar";
            }
            if (kebirCode.StartsWith("9", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetKebirName("9", out var n9) ? n9 : "Nazım Hesaplar";
            }

            // Normal kebir (100..999)
            if (kebirCode.Length >= 3)
            {
                var k = kebirCode.Substring(0, 3);
                if (TryGetKebirName(k, out var name))
                    return name;
            }

            return (fallbackName ?? "").Trim();
        }

        private static (string code, string name)? TryParseLineToPair(string line)
        {
            // format 1:  "8=SERBEST HESAPLAR"
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var left = line.Substring(0, eq).Trim();
                var right = line[(eq + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                    return null;

                var digits = new string(left.Where(char.IsDigit).ToArray());
                if (digits.Length == 0) return null;

                var code = digits.Length >= 3 ? digits[..3] : digits[..1];
                return (code, NormalizeName(right));
            }

            // format 2: "733. Genel Üretim..." veya "8. Serbest Hesaplar" veya "100 Kasa"
            var digits2 = new string(line.TakeWhile(char.IsDigit).ToArray());
            if (digits2.Length == 0) return null;

            // txt’de hem 1 hane (8,9) hem 2 hane (10,11,...) hem 3 hane (100..999) var.
            // Biz kebir için 1 veya 3 kullanacağız.
            string code2 =
                digits2.Length >= 3 ? digits2[..3] :
                digits2.Length == 1 ? digits2 :
                digits2; // 2 hane geldi (10,11,12...) — istersen sonra kullanırız

            var rest = line[digits2.Length..].TrimStart();
            if (rest.StartsWith(".")) rest = rest[1..].TrimStart();

            var name2 = NormalizeName(rest);
            if (string.IsNullOrWhiteSpace(name2)) return null;

            // Eğer 2 hane geldiyse (10,11,12...) şu an kebir sözlüğüne yazmayacağız ama parse bozulmasın:
            // Load() zaten sadece 1 veya 3 hane ise ekliyor.
            return (code2, name2);
        }

        private static string NormalizeName(string s)
        {
            s = (s ?? "").Trim();
            while (s.Contains("  "))
                s = s.Replace("  ", " ");
            return s;
        }
    }
}
