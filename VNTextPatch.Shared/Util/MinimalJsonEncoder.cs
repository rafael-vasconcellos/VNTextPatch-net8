using System.Text;
using System.Text.Encodings.Web;


namespace VNTextPatch.Shared.Util
{
    internal sealed class MinimalJsonEncoder : JavaScriptEncoder
    {
        public static readonly MinimalJsonEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6; // pior caso: \uXXXX

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (int i = 0; i < textLength; i++)
            {
                if (NeedsEscaping(text[i]))
                    return i;
            }
            return -1;
        }

        public override bool WillEncode(int unicodeScalar) => NeedsEscaping(unicodeScalar);

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            if (!NeedsEscaping(unicodeScalar))
            {
                return new Rune(unicodeScalar).TryEncodeToUtf16(new Span<char>(buffer, bufferLength), out numberOfCharactersWritten);
            }

            char? shorthand = unicodeScalar switch
            {
                '"' => '"',
                '\\' => '\\',
                '\b' => 'b',
                '\f' => 'f',
                '\n' => 'n',
                '\r' => 'r',
                '\t' => 't',
                _ => null
            };

            if (shorthand is char c)
            {
                if (bufferLength < 2)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }
                buffer[0] = '\\';
                buffer[1] = c;
                numberOfCharactersWritten = 2;
                return true;
            }

            // Demais caracteres de controle (sem forma curta definida): \uXXXX
            if (bufferLength < 6)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            const string hex = "0123456789abcdef";
            buffer[0] = '\\';
            buffer[1] = 'u';
            buffer[2] = hex[(unicodeScalar >> 12) & 0xF];
            buffer[3] = hex[(unicodeScalar >> 8) & 0xF];
            buffer[4] = hex[(unicodeScalar >> 4) & 0xF];
            buffer[5] = hex[unicodeScalar & 0xF];
            numberOfCharactersWritten = 6;
            return true;
        }

        private static bool NeedsEscaping(int unicodeScalar) =>
            unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar < 0x20;
    }
}
