using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;


namespace VNTextPatch.Shared.Util
{
    public class ShiftJISEncoding : Encoding
    {
        public static Dictionary<string, char> bytesToChar = GetDictFromFile<string, char>("bytesToChar.json");
        public static Dictionary<char, string> charToBytes = GetDictFromFile<char, string>("charToBytes.json");

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

        public override string GetString(byte[] bytes, int startIndex, int endIndex)
        {
            var chars = new List<char>();
            int i = startIndex;
            while (i < endIndex)
            {
                byte b1 = bytes[i];

                // Verifica se é início de um caractere de 2 bytes
                if ((b1 >= 0x81 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xFC))
                {
                    if (i + 1 >= endIndex) break;
                    byte b2 = bytes[i + 1];
                    string hex = $"{b1:X2}{b2:X2}";
                    chars.Add(GetCharFromByte(hex));
                    i += 2;
                }
                else
                {
                    string hex = $"{b1:X2}";
                    chars.Add(GetCharFromByte(hex));
                    i += 1;
                }
            }

            return new string(chars.ToArray());
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
            return ' ';
        }

        public static string? GetByteFromChar(char target)
        {
            if (charToBytes.ContainsKey(target)) { return charToBytes[target]; }
            return null;
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

    }
}