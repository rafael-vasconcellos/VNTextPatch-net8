using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;


namespace VNTextPatch.Shared.Util
{
    public class ShiftJISEncoding : Encoding
    {
        public static Dictionary<string, char> bytesToChar = LoadEmbeddedJson<string, char>("bytesToChar.json");
        public static Dictionary<char, string> charToBytes = LoadEmbeddedJson<char, string>("charToBytes.json");

        public override string GetString(byte[] bytes)
        {
            return GetString(bytes, 0, bytes.Length);
        }

        public override byte[] GetBytes(string str)
        {
            var byteCount = GetByteCount(str.ToCharArray(), 0, str.Length);
            var bytes = new byte[byteCount];
            GetBytes(str, 0, str.Length, bytes, 0);
            return bytes;
        }

        public override byte[] GetPreamble()
        {
            return [];
        }

        public override int GetMaxByteCount(int charCount) => charCount * 2;
        public override int GetMaxCharCount(int byteCount) => byteCount;

        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            string s = GetString(bytes, index, count);
            return s.Length;
        }

        public override int GetByteCount(char[] chars, int index, int count)
        {
            var slice = new string(chars, index, count);
            return GetBytes(slice, 0, count, new byte[1024], 0);
        }

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            string s = GetString(bytes, byteIndex, byteIndex + byteCount);
            s.CopyTo(0, chars, charIndex, s.Length);
            return s.Length;
        }



        //Mapping
        public static Dictionary<T1, T2> GetDictFromFile<T1, T2>(string filename)
        {
            string jsonFileString = File.ReadAllText(filename);
            var hashmap = JsonSerializer.Deserialize<Dictionary<T1, T2>>(jsonFileString);
            return hashmap;
        }

        public static char GetCharFromByte(string targetByteString)
        {
            if (bytesToChar.ContainsKey(targetByteString)) { return bytesToChar[targetByteString]; }
            Console.Write("Byte not found: " + targetByteString + '\n');
            return '?';
        }

        public static string? GetByteFromChar(char target)
        {
            if (charToBytes.ContainsKey(target)) { return charToBytes[target]; }
            return "3F";
        }

        public static Dictionary<T1, T2> LoadEmbeddedJson<T1, T2>(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"VNTextPatch.Shared.Util.ShiftJISEncoding.{fileName}";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new FileNotFoundException($"Resource {resourceName} not found");

            var options = new JsonSerializerOptions{ TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<T1, T2>>(json, options);
        }

        /* public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            int i = byteIndex;
            int end = byteIndex + byteCount;
            int outputIndex = charIndex;

            while (i < end && outputIndex < chars.Length)
            {
                byte b1 = bytes[i];
                if ((b1 >= 0x81 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xFC))
                {
                    if (i + 1 >= end) break;
                    byte b2 = bytes[i + 1];
                    string hex = $"{b1:X2}{b2:X2}";
                    chars[outputIndex++] = GetCharFromByte(hex);
                    i += 2;
                }
                else
                {
                    string hex = $"{b1:X2}";
                    chars[outputIndex++] = GetCharFromByte(hex);
                    i += 1;
                }
            }

            return outputIndex - charIndex;
        } */


        public override string GetString(byte[] bytes, int startIndex, int stopIndex)
        {
            var sb = new StringBuilder();
            int i = startIndex;

            while (i < stopIndex)
            {
                byte b1 = bytes[i];

                // ASCII (inclui 0x00)
                if (b1 <= 0x7F)
                {
                    sb.Append((char)b1);
                    i++;
                }
                // Half-width katakana
                else if (b1 >= 0xA1 && b1 <= 0xDF)
                {
                    // Opcional: mapear para Unicode U+FF61–U+FF9F
                    sb.Append((char)(0xFF61 + (b1 - 0xA1)));
                    i++;
                }
                // Possível início de caractere de 2 bytes
                else if ((b1 >= 0x81 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xFC))
                {
                    if (i + 1 >= stopIndex)
                    {
                        Console.Write("Unexpected final Byte in byte pair.\n");
                        break;
                    }

                    byte b2 = bytes[i + 1];
                    if ((b2 >= 0x40 && b2 <= 0x7E) || (b2 >= 0x80 && b2 <= 0xFC))
                    {
                        string hex = $"{b1:X2}{b2:X2}";
                        sb.Append(GetCharFromByte(hex));
                        i += 2;
                    }
                    else
                    {
                        Console.Write($"Invalid second byte in Shift JIS pair: 0x{b2:X2}\n");
                        i += 2;
                    }
                }
                else
                {
                    Console.Write($"Invalid Shift JIS first byte: 0x{b1:X2}\n");
                    i++;
                }
            }

            return sb.ToString();
        }

        public override int GetBytes(char[] charArr, int charStartIndex, int charCount, byte[] bytes, int byteIndex)
        {
            string sliced = new string(charArr).Substring(charStartIndex, charCount);
            int currentIndex = byteIndex;

            foreach (char c in sliced)
            {
                string hex = GetByteFromChar(c); // ex: "8140"
                if (hex == null) continue;

                if (hex.Length == 2)
                {
                    bytes[currentIndex++] = System.Convert.ToByte(hex, 16);
                }
                else if (hex.Length == 4)
                {
                    bytes[currentIndex++] = System.Convert.ToByte(hex.Substring(0, 2), 16);
                    bytes[currentIndex++] = System.Convert.ToByte(hex.Substring(2, 2), 16);
                }
            }

            return currentIndex - byteIndex;
        }


    }
}