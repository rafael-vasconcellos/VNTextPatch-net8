// KirikiriXp3.cs
// Implementação básica de VNTextPatch.Archives.IArchive para arquivos XP3 padrão.
// Requer .NET 7+ (a interface usa static abstract).

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Buffers.Binary;

namespace VNTextPatch.Archives
{
    public sealed class Xp3FileInfo
    {
        public string Path { get; init; } = "";
        public long OriginalSize { get; init; }
        public long ArchiveSize { get; init; }

        public override string ToString() => $"{Path} ({OriginalSize} bytes)";
    }

    /// <summary>
    /// Leitor e empacotador de XP3 padrão. índice XP3 normal e segmentos sem compressão
    /// ou comprimidos com zlib.
    /// </summary>
    public sealed class KirikiriXp3 : IArchive
    {
        private static readonly byte[] Magic =
            { 0x58, 0x50, 0x33, 0x0D, 0x0A, 0x20, 0x0A, 0x1A, 0x8B, 0x67, 0x01 };

        private FileStream? _stream;
        private List<Entry>? _entries;
        private bool _disposed;

        private FileStream LoadedStream => _stream
            ?? throw new InvalidOperationException("O arquivo XP3 ainda não foi carregado.");

        private List<Entry> LoadedEntries => _entries
            ?? throw new InvalidOperationException("O arquivo XP3 ainda não foi carregado.");

        public string? Extension => ".xp3";

        /// <summary>
        /// Abre o arquivo XP3 e carrega seu índice. A chave é ignorada:
        /// esta implementação não suporta XP3 criptografado.
        /// </summary>
        public void Load(string filePath, string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("O caminho do arquivo é obrigatório.", nameof(filePath));

            // O parâmetro encriptionKey é mantido para cumprir a interface.
            // O script Python original não implementava descriptografia.
            if (!string.IsNullOrEmpty(encriptionKey))
                throw new NotSupportedException(
                    "Esta implementação não suporta arquivos XP3 criptografados.");

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                byte[] index = ReadIndex(stream);
                List<Entry> entries = ParseIndex(index);
                _stream = stream;
                _entries = entries;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Lista os caminhos internos. Se recursive for false, retorna somente
        /// arquivos diretamente na raiz do arquivo XP3.
        /// </summary>
        public HashSet<string> ListFiles(bool recursive)
        {
            ThrowIfDisposed();
            List<Entry> entries = LoadedEntries;

            var result = new HashSet<string>();
            foreach (Entry entry in entries)
            {
                string name = NormalizeArchivePath(entry.Name);
                if (!recursive && name.Contains('/'))
                {
                    name = name.Split('/')[0] + '/';
                }

                result.Add(name);
            }

            return result;
        }

        /// <summary>
        /// Extrai todos os arquivos se pathToExtract for null/vazio.
        /// Se for um arquivo, extrai somente esse arquivo; se for um diretório,
        /// extrai os arquivos contidos nesse diretório e em seus subdiretórios.
        /// </summary>
        public void Extract(string outputPath, string? pathToExtract)
        {
            ThrowIfDisposed();
            Console.WriteLine("Unpacking...");

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A pasta de saída é obrigatória.", nameof(outputPath));

            List<Entry> entries = LoadedEntries;
            string root = Path.GetFullPath(outputPath);
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            List<Entry> selected;
            if (string.IsNullOrWhiteSpace(pathToExtract))
            {
                selected = entries;
            }
            else
            {
                string requested = NormalizeArchivePath(pathToExtract.Trim().TrimEnd('/', '\\'));
                Entry? exact = entries.FirstOrDefault(e =>
                    string.Equals(
                        NormalizeArchivePath(e.Name), requested, StringComparison.OrdinalIgnoreCase
                    )
                );

                if (exact != null)
                {
                    selected = new List<Entry> { exact };
                }
                else
                {
                    string prefix = requested + "/";
                    selected = entries.Where(e =>
                        NormalizeArchivePath(e.Name).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    if (selected.Count == 0)
                        throw new FileNotFoundException($"O caminho não existe no arquivo XP3: {pathToExtract}");
                }
            }

            foreach (Entry entry in selected)
            {
                string relative = NormalizeArchivePath(entry.Name);
                string destination = SafeOutputPath(root, relative);
                Console.WriteLine(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ExtractEntry(entry, destination);
                DescrambleTextFile(destination);
            }
        }

        /// <summary>
        /// Cria um XP3 com segmentos sem compressão a partir de todos os arquivos
        /// encontrados recursivamente em pathToPack. O índice é comprimido com zlib.
        /// </summary>
        public void Pack(string pathToPack, string outputPath, string? encriptionKey = null)
        {
            if (string.IsNullOrWhiteSpace(pathToPack))
                throw new ArgumentException("A pasta de origem é obrigatória.", nameof(pathToPack));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("O caminho do XP3 de saída é obrigatório.", nameof(outputPath));

            string inputRoot = Path.GetFullPath(pathToPack);
            if (!Directory.Exists(inputRoot))
                throw new DirectoryNotFoundException($"A pasta de origem não existe: {inputRoot}");

            string outputFullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(outputFullPath);
            Console.WriteLine("Packing...");
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            string[] files = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), outputFullPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            using var output = new FileStream(outputFullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

            writer.Write(Magic);
            writer.Write((ulong)0); // Ponteiro do índice será atualizado no final.

            var packedEntries = new List<PackedEntry>();

            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(inputRoot, file).Replace('\\', '/');
                ValidateArchivePath(relative);
                Console.WriteLine(relative);

                ulong offset = checked((ulong)output.Position);
                uint checksum = 1;
                ulong total = 0;

                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        checksum = Adler32(checksum, buffer.AsSpan(0, read));
                        total = checked(total + (ulong)read);
                    }
                }

                packedEntries.Add(new PackedEntry
                {
                    Name = relative,
                    Offset = offset,
                    OriginalSize = total,
                    ArchiveSize = total,
                    Checksum = checksum
                });
            }

            byte[] indexData = BuildIndex(packedEntries);
            ulong indexOffset = checked((ulong)output.Position);

            using (var compressedStream = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
                    zlib.Write(indexData, 0, indexData.Length);

                byte[] compressedIndex = compressedStream.ToArray();
                writer.Write((byte)1); // Índice comprimido com zlib.
                writer.Write((ulong)compressedIndex.Length);
                writer.Write((ulong)indexData.Length);
                writer.Write(compressedIndex);
            }

            output.Position = Magic.Length;
            writer.Write(indexOffset);
            writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            FileStream? stream = _stream;
            _stream = null;
            _entries = null;
            stream?.Dispose();
        }

        private void ExtractEntry(Entry entry, string destination)
        {
            FileStream stream = LoadedStream;
            uint checksum = 1;
            ulong total = 0;

            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            foreach (Segment segment in entry.Segments)
            {
                if (segment.ArchiveSize > int.MaxValue)
                    throw new InvalidDataException($"Segmento grande demais para esta implementação: {entry.Name}");

                ulong totalBeforeSegment = total;
                stream.Position = checked((long)segment.Offset);
                int storedSize = checked((int)segment.ArchiveSize);

                if (segment.Flags == 0)
                {
                    if (segment.ArchiveSize != segment.OriginalSize)
                        throw new InvalidDataException($"Tamanho inválido no segmento: {entry.Name}");

                    CopyExactlyAndChecksum(stream, output, storedSize, ref checksum, ref total);
                }
                else if (segment.Flags == 1)
                {
                    byte[] compressed = ReadExactly(stream, storedSize);
                    using var inputMemory = new MemoryStream(compressed, writable: false);
                    using var zlib = new ZLibStream(inputMemory, CompressionMode.Decompress);
                    CopyToAndChecksum(zlib, output, ref checksum, ref total);
                }
                else
                {
                    throw new NotSupportedException(
                        $"Flags de segmento XP3 não suportadas ({segment.Flags}) em {entry.Name}.");
                }

                ulong segmentOutputSize = total - totalBeforeSegment;
                if (segmentOutputSize != segment.OriginalSize)
                    throw new InvalidDataException(
                        $"Tamanho descompactado incorreto no segmento de {entry.Name}: " +
                        $"{segmentOutputSize}, esperado {segment.OriginalSize}.");
            }

            output.Flush();

            if (total != entry.OriginalSize)
                throw new InvalidDataException($"Tamanho final incorreto para {entry.Name}: {total}, esperado {entry.OriginalSize}");

            if (entry.Checksum.HasValue && checksum != entry.Checksum.Value)
                throw new InvalidDataException($"Checksum Adler-32 incorreto para {entry.Name}");
        }

        /// <summary>
        /// Scripts de texto do Kirikiri (.ks, .tjs...) costumam ser gravados "scrambled":
        /// FE FE <modo> FF FE + UTF-16LE embaralhado. Depois que o Adler-32 do XP3 foi
        /// verificado, converte o arquivo extraído para UTF-16LE com BOM (FF FE).
        /// Mesmo algoritmo de VNTextPatch.Shared.Scripts.Kirikiri.KirikiriDescrambler.
        /// </summary>
        private static void DescrambleTextFile(string path)
        {
            byte[] header = new byte[5];
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (probe.Read(header, 0, header.Length) < header.Length)
                    return;
            }

            if (header[0] != 0xFE || header[1] != 0xFE || header[3] != 0xFF || header[4] != 0xFE)
                return;

            byte mode = header[2];
            if (mode > 2)
                return; // modo desconhecido: mantém o arquivo como veio do XP3.

            byte[] data = File.ReadAllBytes(path);
            byte[] result;

            if (mode == 2)
            {
                using var compressed = new MemoryStream(data, 5, data.Length - 5, writable: false);
                using var reader = new BinaryReader(compressed);
                reader.ReadInt64(); // tamanho comprimido
                int uncompressedLength = checked((int)reader.ReadInt64());
                reader.ReadInt16(); // cabeçalho zlib (2 bytes) antes do deflate puro

                result = new byte[2 + uncompressedLength];
                result[0] = 0xFF;
                result[1] = 0xFE;
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                deflate.ReadExactly(result, 2, uncompressedLength);
            }
            else
            {
                for (int i = 5; i + 1 < data.Length; i += 2)
                {
                    if (mode == 0)
                    {
                        if (data[i + 1] == 0 && data[i] < 0x20)
                            continue;

                        data[i + 1] ^= (byte)(data[i] & 0xFE);
                        data[i] ^= 1;
                    }
                    else
                    {
                        int c = data[i] | (data[i + 1] << 8);
                        c = ((c & 0xAAAA) >> 1) | ((c & 0x5555) << 1);
                        data[i] = (byte)c;
                        data[i + 1] = (byte)(c >> 8);
                    }
                }

                result = new byte[data.Length - 3];
                Array.Copy(data, 3, result, 0, result.Length);
            }

            File.WriteAllBytes(path, result);
        }

        private static byte[] ReadIndex(FileStream stream)
        {
            stream.Position = 0;
            byte[] magic = ReadExactly(stream, Magic.Length);
            if (!magic.SequenceEqual(Magic))
                throw new InvalidDataException("Cabeçalho XP3 inválido (XP3 magic não encontrado).");

            ulong indexOffset = ReadUInt64(stream);
            stream.Position = checked((long)indexOffset);
            int flag = stream.ReadByte();
            if (flag < 0)
                throw new EndOfStreamException("Índice XP3 ausente.");

            // Marcador de continuação encontrado no arquivo tratado pelo script Python.
            if ((flag & 0x80) != 0)
            {
                ulong firstOffset = ReadUInt64(stream);
                ulong nextOffset = ReadUInt64(stream);
                indexOffset = nextOffset != 0 ? nextOffset : firstOffset;
                if (indexOffset == 0)
                    throw new InvalidDataException("Marcador de continuação XP3 sem destino.");
                stream.Position = checked((long)indexOffset);
                flag = stream.ReadByte();
                if (flag < 0)
                    throw new EndOfStreamException("Índice XP3 ausente após o marcador de continuação.");
            }

            int method = flag & 0x7F;
            if (method == 0)
            {
                ulong size = ReadUInt64(stream);
                if (size > int.MaxValue)
                    throw new InvalidDataException("Índice XP3 grande demais.");
                return ReadExactly(stream, (int)size);
            }

            if (method == 1)
            {
                ulong compressedSize = ReadUInt64(stream);
                ulong originalSize = ReadUInt64(stream);
                if (compressedSize > int.MaxValue || originalSize > int.MaxValue)
                    throw new InvalidDataException("Índice XP3 grande demais.");

                byte[] compressed = ReadExactly(stream, (int)compressedSize);
                using var input = new MemoryStream(compressed, writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var result = new MemoryStream();
                zlib.CopyTo(result);
                byte[] data = result.ToArray();

                if ((ulong)data.Length != originalSize)
                    throw new InvalidDataException(
                        $"Tamanho do índice XP3 incorreto: {data.Length}, esperado {originalSize}.");
                return data;
            }

            throw new NotSupportedException($"Método de índice XP3 não suportado: {method}");
        }

        private static List<Entry> ParseIndex(byte[] index)
        {
            var entries = new List<Entry>();
            int pos = 0;

            while (pos < index.Length)
            {
                EnsureRange(index, pos, 12);
                string tag = Encoding.ASCII.GetString(index, pos, 4);
                ulong bodySizeU = ReadUInt64(index, pos + 4);
                int bodySize = CheckedInt(bodySizeU, "registro XP3");
                int bodyStart = checked(pos + 12);
                EnsureRange(index, bodyStart, bodySize);

                if (tag != "File")
                    throw new InvalidDataException($"Tag XP3 inesperada: {tag} na posição 0x{pos:x}.");

                int bodyEnd = bodyStart + bodySize;
                var entry = new Entry();
                int subPos = bodyStart;

                while (subPos < bodyEnd)
                {
                    EnsureRange(index, subPos, 12);
                    string subTag = Encoding.ASCII.GetString(index, subPos, 4);
                    int subSize = CheckedInt(ReadUInt64(index, subPos + 4), "subregistro XP3");
                    int subStart = checked(subPos + 12);
                    EnsureRange(index, subStart, subSize);
                    if (subStart + subSize > bodyEnd)
                        throw new InvalidDataException("Subregistro XP3 ultrapassa o limite do registro.");

                    if (subTag == "info")
                    {
                        if (subSize < 22)
                            throw new InvalidDataException("Subregistro info XP3 truncado.");

                        entry.Flags = ReadUInt32(index, subStart);
                        entry.OriginalSize = ReadUInt64(index, subStart + 4);
                        entry.ArchiveSize = ReadUInt64(index, subStart + 12);
                        ushort nameLength = ReadUInt16(index, subStart + 20);
                        int nameBytes = checked(nameLength * 2);
                        if (22 + nameBytes > subSize)
                            throw new InvalidDataException("Nome XP3 truncado.");

                        entry.Name = Encoding.Unicode.GetString(index, subStart + 22, nameBytes);
                    }
                    else if (subTag == "segm")
                    {
                        if (subSize % 28 != 0)
                            throw new InvalidDataException($"Tabela de segmentos XP3 inválida: {entry.Name}");

                        for (int p = 0; p < subSize; p += 28)
                        {
                            entry.Segments.Add(new Segment
                            {
                                Flags = ReadUInt32(index, subStart + p),
                                Offset = ReadUInt64(index, subStart + p + 4),
                                OriginalSize = ReadUInt64(index, subStart + p + 12),
                                ArchiveSize = ReadUInt64(index, subStart + p + 20)
                            });
                        }
                    }
                    else if (subTag == "adlr")
                    {
                        if (subSize >= 4)
                            entry.Checksum = ReadUInt32(index, subStart);
                    }

                    subPos = checked(subStart + subSize);
                }

                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"Registro XP3 na posição 0x{pos:x} sem nome.");

                ValidateArchivePath(entry.Name);
                entries.Add(entry);
                pos = checked(bodyEnd);
            }

            return entries;
        }

        private static byte[] BuildIndex(List<PackedEntry> entries)
        {
            using var indexStream = new MemoryStream();
            using var indexWriter = new BinaryWriter(indexStream, Encoding.UTF8, leaveOpen: true);

            foreach (PackedEntry entry in entries)
            {
                byte[] nameBytes = Encoding.Unicode.GetBytes(entry.Name);
                int nameChars = nameBytes.Length / 2;
                if (nameChars > ushort.MaxValue)
                    throw new InvalidDataException($"Nome de arquivo longo demais para XP3: {entry.Name}");

                using var bodyStream = new MemoryStream();
                using var bodyWriter = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true);

                using (var infoStream = new MemoryStream())
                using (var infoWriter = new BinaryWriter(infoStream, Encoding.UTF8, leaveOpen: true))
                {
                    infoWriter.Write((uint)0); // Flags de info.
                    infoWriter.Write(entry.OriginalSize);
                    infoWriter.Write(entry.ArchiveSize);
                    infoWriter.Write((ushort)nameChars);
                    infoWriter.Write(nameBytes);
                    WriteChunk(bodyWriter, "info", infoStream.ToArray());
                }

                using (var segmentStream = new MemoryStream())
                using (var segmentWriter = new BinaryWriter(segmentStream, Encoding.UTF8, leaveOpen: true))
                {
                    segmentWriter.Write((uint)0); // Segmento sem compressão.
                    segmentWriter.Write(entry.Offset);
                    segmentWriter.Write(entry.OriginalSize);
                    segmentWriter.Write(entry.ArchiveSize);
                    WriteChunk(bodyWriter, "segm", segmentStream.ToArray());
                }

                using (var checksumStream = new MemoryStream())
                using (var checksumWriter = new BinaryWriter(checksumStream, Encoding.UTF8, leaveOpen: true))
                {
                    checksumWriter.Write(entry.Checksum);
                    WriteChunk(bodyWriter, "adlr", checksumStream.ToArray());
                }

                WriteChunk(indexWriter, "File", bodyStream.ToArray());
            }

            indexWriter.Flush();
            return indexStream.ToArray();
        }

        private static void WriteChunk(BinaryWriter writer, string tag, byte[] payload)
        {
            writer.Write(Encoding.ASCII.GetBytes(tag));
            writer.Write((ulong)payload.Length);
            writer.Write(payload);
        }

        private static string NormalizeArchivePath(string path) =>
            path.Replace('\\', '/').TrimStart('/');

        private static void ValidateArchivePath(string path)
        {
            string normalized = path.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(path) ||
                normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
            {
                throw new InvalidDataException($"Caminho inseguro ou inválido dentro do XP3: {path}");
            }
        }

        private static string SafeOutputPath(string root, string relative)
        {
            ValidateArchivePath(relative);
            string fullRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O caminho de saída escapa da pasta de destino: {relative}");

            return fullPath;
        }

        private static void CopyExactlyAndChecksum(
            Stream input, Stream output, int count, ref uint checksum, ref ulong total)
        {
            byte[] buffer = new byte[1024 * 1024];
            int remaining = count;

            while (remaining > 0)
            {
                int read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException("Fim inesperado ao ler um segmento XP3.");

                output.Write(buffer, 0, read);
                checksum = Adler32(checksum, buffer.AsSpan(0, read));
                total = checked(total + (ulong)read);
                remaining -= read;
            }
        }

        private static void CopyToAndChecksum(
            Stream input, Stream output, ref uint checksum, ref ulong total)
        {
            byte[] buffer = new byte[1024 * 1024];
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                checksum = Adler32(checksum, buffer.AsSpan(0, read));
                total = checked(total + (ulong)read);
            }
        }

        private static uint Adler32(uint adler, ReadOnlySpan<byte> data)
        {
            const uint Mod = 65521;
            uint a = adler & 0xFFFF;
            uint b = (adler >> 16) & 0xFFFF;

            foreach (byte value in data)
            {
                a = (a + value) % Mod;
                b = (b + a) % Mod;
            }

            return (b << 16) | a;
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                    throw new EndOfStreamException("Fim inesperado do arquivo XP3.");
                offset += read;
            }
            return buffer;
        }

        private static ulong ReadUInt64(Stream stream)
        {
            byte[] data = ReadExactly(stream, 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(data);
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        private static ulong ReadUInt64(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

        private static int CheckedInt(ulong value, string what)
        {
            if (value > int.MaxValue)
                throw new InvalidDataException($"{what} grande demais.");
            return (int)value;
        }

        private static void EnsureRange(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new InvalidDataException("Estrutura XP3 truncada ou inválida.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KirikiriXp3));
        }

        private sealed class Entry
        {
            public string Name { get; set; } = "";
            public uint Flags { get; set; }
            public ulong OriginalSize { get; set; }
            public ulong ArchiveSize { get; set; }
            public List<Segment> Segments { get; } = new();
            public uint? Checksum { get; set; }
        }

        private sealed class Segment
        {
            public uint Flags { get; set; }
            public ulong Offset { get; set; }
            public ulong OriginalSize { get; set; }
            public ulong ArchiveSize { get; set; }
        }

        private sealed class PackedEntry
        {
            public string Name { get; set; } = "";
            public ulong Offset { get; set; }
            public ulong OriginalSize { get; set; }
            public ulong ArchiveSize { get; set; }
            public uint Checksum { get; set; }
        }
    }
}
