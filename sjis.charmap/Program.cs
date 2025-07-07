using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.IO;


class Program
{
    public static string[] jisChars =
        [
            "あ", "い", "う", "え", "お",
            "日", "本", "語", "漢", "字"
        ];

    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytesToChar = new Dictionary<string, string>(); // hex Shift JIS → char
        var charToBytes = new Dictionary<string, string>(); // char → hex Shift JIS
        Encoding shiftJisEncoding = Encoding.GetEncoding("shift_jis");

        foreach (var ch in jisChars)
        {
            try
            {
                byte[] sjisBytes = shiftJisEncoding.GetBytes(ch);
                string sjisHex = BytesToHex(sjisBytes);

                bytesToChar[sjisHex] = ch;
                charToBytes[ch] = sjisHex;
            }
            catch
            {
                continue;
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText("bytesToChar.json", JsonSerializer.Serialize(bytesToChar, options));
        File.WriteAllText("charToBytes.json", JsonSerializer.Serialize(charToBytes, options));
    }

    static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
            sb.Append(b.ToString("X2")); // formato hexadecimal com 2 dígitos
        return sb.ToString();
    }

    static string DictionaryToString<TKey, TValue>(Dictionary<TKey, TValue> dict)
    {
        var sb = new StringBuilder();
        sb.Append("{ ");
        foreach (var kvp in dict)
        {
            sb.Append($"[\"{kvp.Key}\"]: {kvp.Value}, ");
        }
        if (dict.Count > 0)
            sb.Length -= 2;  // Remove a última vírgula e espaço
        sb.Append(" }");
        return sb.ToString();
    }
}


/*
        string jsonFormatado = JsonSerializer.Serialize(charToBytes, options);
*/