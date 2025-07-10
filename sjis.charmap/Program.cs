using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Text.Encodings.Web; // Adicione este using para JavaScriptEncoder
using VNTextPatch.Shared.Util;


class Program
{
    public static char[] jisChars = GetJisChars();

    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var s = Encoding.GetEncoding(932);
        byte[] bytes = s.GetBytes("ダンガンロンパ 希望の学園と絶望の高校生");
        Encoding encoding = new ShiftJISEncoding();
        string resultString = encoding.GetString(bytes);
        Console.Write("\n\n" + resultString + '\n');
        Console.Write("\n\n" + resultString.Length + '\n');
    }

    static void BuildCharMap()
    {
        var bytesToChar = new Dictionary<string, string>(); // hex Shift JIS → char
        var charToBytes = new Dictionary<string, string>(); // char → hex Shift JIS
        Encoding shiftJisEncoding = Encoding.GetEncoding("shift_jis");

        foreach (var ch in jisChars)
        {
            try
            {
                byte[] sjisBytes = shiftJisEncoding.GetBytes(ch.ToString());
                string sjisHex = BytesToHex(sjisBytes);
                string charAsString = ch.ToString();

                bytesToChar[sjisHex] = charAsString;
                charToBytes[charAsString] = sjisHex;
            }
            catch
            {
                continue;
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
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

    public static char[] GetJisChars()
    {
        string originalContent = File.ReadAllText("chars.txt");
        string cleanContent = new string(originalContent.Where(c => !Char.IsWhiteSpace(c)).ToArray());
        char[] splited = cleanContent.ToCharArray();
        return splited;
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