using System;
using System.IO;
using System.IO.Compression;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Kirikiri
{
    internal class KirikiriDescrambler
    {
        public static ArraySegment<byte> Descramble(ArraySegment<byte> data)
        {
            if (data.Count < 5)
                return data;

            // Magic
            if (data.Get(0) != 0xFE || data.Get(1) != 0xFE)
                return data;

            // BOM
            if (data.Get(3) != 0xFF || data.Get(4) != 0xFE)
                throw new InvalidDataException("Scrambled Kirikiri file is missing BOM.");

            byte mode = data.Get(2);

            switch (mode)
            {
                case 0:
                    return DescrambleMode0(data);

                case 1:
                    return DescrambleMode1(data);

                case 2:
                    return Decompress(data);

                default:
                    throw new NotSupportedException($"File uses unsupported Kirikiri scrambling mode {mode}.");
            }
        }

        private static byte[] RequireArray(ArraySegment<byte> data) =>
            data.Array ?? throw new InvalidDataException("Scrambled Kirikiri data has no underlying array.");

        private static ArraySegment<byte> DescrambleMode0(ArraySegment<byte> data)
        {
            byte[] array = RequireArray(data);

            for (int i = data.Offset + 5; i < data.Offset + data.Count; i += 2)
            {
                if (array[i + 1] == 0 && array[i] < 0x20)
                    continue;

                array[i + 1] ^= (byte)(array[i] & 0xFE);
                array[i] ^= 1;
            }
            return new ArraySegment<byte>(array, data.Offset + 3, data.Count - 3);
        }

        private static ArraySegment<byte> DescrambleMode1(ArraySegment<byte> data)
        {
            byte[] array = RequireArray(data);

            for (int i = data.Offset + 5; i < data.Offset + data.Count; i += 2)
            {
                char c = (char)(array[i] | (array[i + 1] << 8));
                c = (char)(((c & 0xAAAA) >> 1) | ((c & 0x5555) << 1));
                array[i] = (byte)c;
                array[i + 1] = (byte)(c >> 8);
            }
            return new ArraySegment<byte>(array, data.Offset + 3, data.Count - 3);
        }

        private static ArraySegment<byte> Decompress(ArraySegment<byte> data)
        {
            byte[] array = RequireArray(data);

            MemoryStream compressedStream = new MemoryStream(array, data.Offset + 5, data.Count - 5);
            BinaryReader compressedReader = new BinaryReader(compressedStream);

            int compressedLength = (int)compressedReader.ReadInt64();
            int uncompressedLength = (int)compressedReader.ReadInt64();
            short zlibHeader = compressedReader.ReadInt16();

            byte[] uncompressedData = new byte[2 + uncompressedLength];
            uncompressedData[0] = 0xFF;
            uncompressedData[1] = 0xFE;
            using (DeflateStream uncompressedStream = new DeflateStream(compressedStream, CompressionMode.Decompress, true))
            {
                uncompressedStream.Read(uncompressedData, 2, uncompressedLength);
            }
            return new ArraySegment<byte>(uncompressedData);
        }
    }
}