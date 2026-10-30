// YurisYPF.cs
// Implementação de VNTextPatch.Archives.IArchive para arquivos YPF (Yu-Ris).
// Baseada em https://github.com/Xorboth/YPF-Manager (versões 234 a 500: tabela de tamanho
// de nome, chave de nome, CRC32/Adler-32/MurmurHash2, offsets de 32/64 bits, zlib, .ycg).

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;


namespace VNTextPatch.Archives
{
    /// <summary>
    /// Leitor e empacotador de YPF (versões 234 a 500). Entradas sem compressão (método 0) ou
    /// com zlib (método 1). Pack grava entradas ordenadas pelo checksum do nome (como o formato
    /// espera), comprime com zlib quando isso reduz o tamanho e reaproveita dados idênticos.
    /// </summary>
    public sealed class YurisYPF : IArchive
    {
        private const int HeaderSize = 0x20;
        private const int MinVersion = 234;           // 0xEA
        private const int MaxVersion = 500;           // 0x1F4
        private const int DefaultPackVersion = 476;
        private const int FirstVersionWith64BitOffsets = 479;
        private const byte TypeYcg = 8;

        private static readonly byte[] Signature = { 0x59, 0x50, 0x46, 0x00 }; // "YPF\0"

        private static readonly Encoding NameEncoding = CreateNameEncoding();
        private static readonly uint[] Crc32Table = CreateCrc32Table();

        // Trocas que definem a tabela de tamanho de nome (índice lido -> tamanho real).
        private static readonly (int, int)[] LengthSwapsV500 =
        {
            (0x03, 0x0A), (0x06, 0x35), (0x09, 0x0B), (0x0C, 0x10), (0x0D, 0x13), (0x11, 0x18),
            (0x15, 0x1B), (0x1C, 0x1E), (0x20, 0x23), (0x26, 0x29), (0x2C, 0x2F), (0x2E, 0x14),
        };

        private static readonly (int, int)[] LengthSwapsOlder =
        {
            (0x03, 0x48), (0x06, 0x35), (0x09, 0x0B), (0x0C, 0x10), (0x0D, 0x13), (0x11, 0x19),
            (0x15, 0x1B), (0x1C, 0x1E), (0x20, 0x23), (0x26, 0x29), (0x2C, 0x2F), (0x2E, 0x32),
        };

        private static readonly Dictionary<string, byte> EntryTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".bmp", 1 },
            { ".png", 2 },
            { ".jpg", 3 },
            { ".gif", 4 },
            { ".wav", 5 },
            { ".ogg", 6 },
            { ".psd", 7 },
            { ".ycg", TypeYcg },
            { ".psb", 9 },
        };

        private FileStream? _stream;
        private List<Entry>? _entries;
        private VersionInfo? _info;
        private bool _disposed;

        private FileStream LoadedStream => _stream
            ?? throw new InvalidOperationException("O arquivo YPF ainda não foi carregado.");

        private List<Entry> LoadedEntries => _entries
            ?? throw new InvalidOperationException("O arquivo YPF ainda não foi carregado.");

        private VersionInfo LoadedInfo => _info
            ?? throw new InvalidOperationException("O arquivo YPF ainda não foi carregado.");

        public string? Extension => ".ypf";

        /// <summary>
        /// Versão do YPF criada por Pack (234 a 500). A interface IArchive.Pack não recebe a
        /// versão, então ela é uma propriedade da instância; o padrão é 500.
        /// </summary>
        public int PackVersion { get; set; } = DefaultPackVersion;

        /// <summary>
        /// Abre o arquivo YPF e carrega seu índice. encriptionKey é opcional: se informada, é
        /// a chave de XOR dos nomes de arquivo, em hexadecimal de 1 byte (ex.: "36" ou "0x36").
        /// Sem ela a chave é deduzida da versão (0x40 na 290, 0x36 a partir da 500, 0x00 nas demais),
        /// o que pode não valer para todos os jogos.
        /// </summary>
        public void Load(string filePath, string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("O caminho do arquivo é obrigatório.", nameof(filePath));

            byte? nameKey = ParseNameKey(encriptionKey);

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                (VersionInfo info, List<Entry> entries) = ReadEntries(stream, nameKey);
                _stream = stream;
                _info = info;
                _entries = entries;
                PackVersion = info.Version;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Lista os caminhos internos. Se recursive for false, retorna somente
        /// arquivos diretamente na raiz do arquivo YPF (e pastas de primeiro nível com "/" no fim).
        /// Entradas do tipo YCG aparecem com o sufixo ".ycg", como são extraídas.
        /// </summary>
        public HashSet<string> ListFiles(bool recursive)
        {
            ThrowIfDisposed();
            Console.WriteLine($"Version: {PackVersion}");
            List<Entry> entries = LoadedEntries;

            var result = new HashSet<string>();
            foreach (Entry entry in entries)
            {
                string name = NormalizeArchivePath(entry.ExtractName);
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
            Console.WriteLine($"Version: {PackVersion}");

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
                        NormalizeArchivePath(e.ExtractName), requested, StringComparison.OrdinalIgnoreCase
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
                        NormalizeArchivePath(e.ExtractName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    if (selected.Count == 0)
                        throw new FileNotFoundException($"O caminho não existe no arquivo YPF: {pathToExtract}");
                }
            }

            // Ordenar por offset deixa a leitura do arquivo sequencial.
            foreach (Entry entry in selected.OrderBy(e => e.Offset))
            {
                string relative = NormalizeArchivePath(entry.ExtractName);
                string destination = SafeOutputPath(root, relative);
                Console.WriteLine(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ExtractEntry(entry, destination);
            }
        }

        /// <summary>
        /// Cria um YPF da versão PackVersion a partir de todos os arquivos encontrados
        /// recursivamente em pathToPack. Os nomes são gravados com "\" como separador.
        /// Arquivos com extensão ".ycg" perdem o sufixo no nome e recebem o tipo YCG.
        /// Arquivos vazios não são suportados.
        /// </summary>
        public void Pack(string pathToPack, string outputPath, string? version, string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(pathToPack))
                throw new ArgumentException("A pasta de origem é obrigatória.", nameof(pathToPack));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("O caminho do YPF de saída é obrigatório.", nameof(outputPath));
            if (!string.IsNullOrEmpty(version))
                PackVersion = int.Parse(version);

            VersionInfo info = VersionInfo.Create(PackVersion, null);
            string inputRoot = Path.GetFullPath(pathToPack);
            Console.WriteLine("Packing...");

            if (!Directory.Exists(inputRoot))
                throw new DirectoryNotFoundException($"A pasta de origem não existe: {inputRoot}");

            string outputFullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            string[] files = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), outputFullPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
                throw new InvalidDataException($"Nenhum arquivo para empacotar em: {inputRoot}");

            // O tamanho do cabeçalho (índice) só depende dos nomes, então o offset inicial dos
            // dados já é conhecido antes de gravar qualquer arquivo.
            var packedEntries = new List<PackedEntry>(files.Length);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            long headerSize = HeaderSize;

            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(inputRoot, file).Replace('/', '\\');
                string extension = Path.GetExtension(relative);

                EntryTypes.TryGetValue(extension, out byte type);

                // O sufixo .ycg não faz parte do nome gravado no arquivo.
                string name = type == TypeYcg ? relative.Substring(0, relative.Length - extension.Length) : relative;
                if (name.Length == 0)
                    throw new InvalidDataException($"Nome de arquivo vazio depois de remover o sufixo .ycg: {relative}");

                ValidateArchivePath(name);

                if (!usedNames.Add(name))
                    throw new InvalidDataException($"Nomes de arquivo duplicados no YPF (ex.: .png e .png.ycg): {name}");

                byte[] nameBytes;
                try
                {
                    nameBytes = NameEncoding.GetBytes(name);
                }
                catch (EncoderFallbackException ex)
                {
                    throw new InvalidDataException($"O nome contém caractere que não existe em Shift-JIS: {name}", ex);
                }

                if (nameBytes.Length > 255)
                    throw new InvalidDataException($"Caminho longo demais para YPF (máx. 255 bytes em Shift-JIS): {name}");

                packedEntries.Add(new PackedEntry
                {
                    LocalPath = file,
                    Name = name,
                    NameBytes = nameBytes,
                    NameChecksum = info.NameHash(nameBytes),
                    Type = type
                });
                headerSize += info.FixedEntrySize + nameBytes.Length;
            }

            if (headerSize > int.MaxValue)
                throw new InvalidDataException("Índice YPF grande demais.");

            // O formato espera as entradas ordenadas pelo checksum do nome (OrderBy é estável).
            packedEntries = packedEntries.OrderBy(e => e.NameChecksum).ToList();

            using var output = new FileStream(outputFullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

            output.Position = headerSize; // o cabeçalho é gravado no final, quando offsets e checksums são conhecidos

            var writtenData = new Dictionary<(string Digest, int RawSize, bool Compressed), long>();

            foreach (PackedEntry entry in packedEntries)
            {
                Console.WriteLine(entry.Name);

                byte[] raw = File.ReadAllBytes(entry.LocalPath);
                if (raw.Length == 0)
                    throw new InvalidDataException($"Arquivos vazios (0 byte) não são suportados: {entry.Name}");

                byte[] compressed = CompressZlib(raw);
                bool useCompressed = compressed.Length < raw.Length;
                byte[] stored = useCompressed ? compressed : raw;

                // Dados idênticos (mesmo conteúdo armazenado e mesmo tamanho original) são gravados uma vez.
                var key = (Convert.ToHexString(SHA256.HashData(stored)), raw.Length, useCompressed);
                if (!writtenData.TryGetValue(key, out long offset))
                {
                    offset = output.Position;
                    writer.Write(stored);
                    writtenData[key] = offset;
                }

                if (info.Legacy && output.Position > int.MaxValue)
                    throw new InvalidDataException("Offset máximo atingido para esta versão de YPF.");

                entry.Offset = offset;
                entry.RawSize = raw.Length;
                entry.StoredSize = stored.Length;
                entry.Compressed = useCompressed;
                entry.DataChecksum = info.DataHash(stored);
            }

            output.Position = 0;
            writer.Write(Signature);
            writer.Write(info.Version);
            writer.Write(packedEntries.Count);
            writer.Write((int)headerSize);
            writer.Write(new byte[16]);

            foreach (PackedEntry entry in packedEntries)
            {
                byte[] encodedName = (byte[])entry.NameBytes.Clone();
                for (int i = 0; i < encodedName.Length; i++)
                    encodedName[i] = (byte)~(encodedName[i] ^ info.NameKey);

                writer.Write(entry.NameChecksum);
                writer.Write((byte)~info.LengthEncodeTable[entry.NameBytes.Length]);
                writer.Write(encodedName);
                writer.Write(entry.Type);
                writer.Write((byte)(entry.Compressed ? 1 : 0));
                writer.Write(entry.RawSize);
                writer.Write(entry.StoredSize);

                if (info.Legacy)
                    writer.Write((int)entry.Offset);
                else
                    writer.Write(entry.Offset);

                writer.Write(entry.DataChecksum);
            }

            writer.Flush();

            if (output.Position != headerSize)
                throw new InvalidDataException("Tamanho do cabeçalho YPF inesperado.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            FileStream? stream = _stream;
            _stream = null;
            _entries = null;
            _info = null;
            stream?.Dispose();
        }

        private void ExtractEntry(Entry entry, string destination)
        {
            FileStream stream = LoadedStream;
            VersionInfo info = LoadedInfo;

            stream.Position = entry.Offset;
            byte[] stored = ReadExactly(stream, entry.StoredSize);

            // O checksum cobre os bytes armazenados (comprimidos, se for o caso).
            if (info.DataHash(stored) != entry.DataChecksum)
                throw new InvalidDataException($"Checksum dos dados incorreto (arquivo corrompido?): {entry.Name}");

            byte[] result;
            if (entry.Method == 0)
            {
                if (entry.StoredSize != entry.RawSize)
                    throw new InvalidDataException($"Tamanho inválido na entrada sem compressão: {entry.Name}");

                result = stored;
            }
            else if (entry.Method == 1)
            {
                using var inputMemory = new MemoryStream(stored, writable: false);
                using var zlib = new ZLibStream(inputMemory, CompressionMode.Decompress);
                using var decompressed = new MemoryStream(entry.RawSize);

                try
                {
                    zlib.CopyTo(decompressed);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException($"Os dados de {entry.Name} não são zlib válido.", ex);
                }

                if (decompressed.Length != entry.RawSize)
                    throw new InvalidDataException(
                        $"Tamanho descompactado incorreto para {entry.Name}: {decompressed.Length}, esperado {entry.RawSize}.");

                result = decompressed.ToArray();
            }
            else
            {
                throw new NotSupportedException(
                    $"Método de compressão YPF não suportado ({entry.Method}) em {entry.Name}.");
            }

            File.WriteAllBytes(destination, result);
        }

        private static (VersionInfo, List<Entry>) ReadEntries(FileStream stream, byte? nameKey)
        {
            if (stream.Length < HeaderSize)
                throw new InvalidDataException("Arquivo YPF menor que o cabeçalho.");

            stream.Position = 0;
            byte[] header = ReadExactly(stream, HeaderSize);

            if (!header.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                throw new InvalidDataException("Cabeçalho YPF inválido (assinatura \"YPF\" não encontrada).");

            VersionInfo info = VersionInfo.Create(ReadInt32(header, 4), nameKey);

            int entryCount = ReadInt32(header, 8);
            int indexEnd = ReadInt32(header, 12); // tamanho do cabeçalho + índice = offset do primeiro dado
            if (entryCount <= 0)
                throw new InvalidDataException("Cabeçalho YPF inválido (nenhuma entrada).");
            if (indexEnd < HeaderSize || indexEnd > stream.Length)
                throw new InvalidDataException("Cabeçalho YPF inválido (tamanho do índice).");

            byte[] index = ReadExactly(stream, indexEnd - HeaderSize);
            return (info, ParseIndex(index, entryCount, info, stream.Length));
        }

        private static List<Entry> ParseIndex(byte[] index, int entryCount, VersionInfo info, long fileLength)
        {
            var entries = new List<Entry>(Math.Min(entryCount, index.Length / info.FixedEntrySize));
            int pos = 0;

            for (int i = 0; i < entryCount; i++)
            {
                EnsureRange(index, pos, 5);
                uint nameChecksum = ReadUInt32(index, pos);
                int nameLength = info.LengthDecodeTable[(byte)~index[pos + 4]];
                pos += 5;

                EnsureRange(index, pos, nameLength);
                byte[] nameBytes = new byte[nameLength];
                for (int n = 0; n < nameLength; n++)
                    nameBytes[n] = (byte)(~index[pos + n] ^ info.NameKey);
                pos += nameLength;

                if (info.NameHash(nameBytes) != nameChecksum)
                    throw new InvalidDataException(
                        $"Checksum do nome incorreto na entrada {i} (versão {info.Version}, chave de nome 0x{info.NameKey:X2}). " +
                        "A chave de nome desta versão pode ser outra: informe-a em encriptionKey (hex).");

                int afterName = info.FixedEntrySize - 5; // bytes fixos depois do nome
                EnsureRange(index, pos, afterName);

                long offset = info.Legacy
                    ? ReadUInt32(index, pos + 10)
                    : checked((long)ReadUInt64(index, pos + 10));

                var entry = new Entry
                {
                    Name = NameEncoding.GetString(nameBytes),
                    Type = index[pos],
                    Method = index[pos + 1],
                    RawSize = ReadInt32(index, pos + 2),
                    StoredSize = ReadInt32(index, pos + 6),
                    Offset = offset,
                    DataChecksum = ReadUInt32(index, pos + 10 + (info.Legacy ? 4 : 8))
                };
                pos += afterName;

                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"Entrada YPF {i} sem nome.");

                if (entry.RawSize < 0 || entry.StoredSize < 0 ||
                    entry.Offset > fileLength || entry.StoredSize > fileLength - entry.Offset)
                    throw new InvalidDataException($"Entrada YPF fora dos limites do arquivo: {entry.Name}");

                entries.Add(entry);
            }

            return entries;
        }

        private static byte? ParseNameKey(string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(encriptionKey))
                return null;

            string text = encriptionKey.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            if (!byte.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte key))
                throw new ArgumentException(
                    "A chave do YPF deve ser um byte em hexadecimal (ex.: \"36\" ou \"0x36\").", nameof(encriptionKey));

            return key;
        }

        private static byte[] CompressZlib(byte[] data)
        {
            using var result = new MemoryStream();
            using (var zlib = new ZLibStream(result, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(data, 0, data.Length);

            return result.ToArray();
        }

        private static Encoding CreateNameEncoding()
        {
            // Necessário no .NET Core/5+ para obter o Shift-JIS (página de código 932).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }

        private static uint[] CreateCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte value in data)
                crc = (crc >> 8) ^ Crc32Table[(byte)((crc ^ value) & 0xFF)];

            return ~crc;
        }

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            const uint Mod = 65521;
            const int MaxChunk = 5552; // maior bloco sem estourar uint antes do módulo

            uint a = 1;
            uint b = 0;
            int index = 0;

            while (index < data.Length)
            {
                int count = Math.Min(MaxChunk, data.Length - index);
                for (int i = 0; i < count; i++)
                {
                    a += data[index + i];
                    b += a;
                }

                a %= Mod;
                b %= Mod;
                index += count;
            }

            return (b << 16) | a;
        }

        private static uint MurmurHash2(ReadOnlySpan<byte> data)
        {
            const uint m = 0x5bd1e995;
            const int r = 24;

            uint h = (uint)data.Length;
            int p = 0;
            int len = data.Length;

            while (len >= 4)
            {
                uint k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4));

                k *= m;
                k ^= k >> r;
                k *= m;

                h *= m;
                h ^= k;

                p += 4;
                len -= 4;
            }

            switch (len)
            {
                case 3:
                    h ^= (uint)data[p + 2] << 16;
                    goto case 2;
                case 2:
                    h ^= (uint)data[p + 1] << 8;
                    goto case 1;
                case 1:
                    h ^= data[p];
                    h *= m;
                    break;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;

            return h;
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
                throw new InvalidDataException($"Caminho inseguro ou inválido dentro do YPF: {path}");
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

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                    throw new EndOfStreamException("Fim inesperado do arquivo YPF.");
                offset += read;
            }
            return buffer;
        }

        private static int ReadInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        private static ulong ReadUInt64(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

        private static void EnsureRange(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new InvalidDataException("Estrutura YPF truncada ou inválida.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(YurisYPF));
        }

        /// <summary>
        /// Parâmetros que dependem da versão do YPF.
        /// </summary>
        private sealed class VersionInfo
        {
            public int Version { get; private init; }

            /// <summary>Antes da versão 479: CRC32 (nomes), Adler-32 (dados) e offsets de 32 bits.</summary>
            public bool Legacy { get; private init; }

            public byte NameKey { get; private init; }

            /// <summary>Índice lido (já com complemento de 1 desfeito) -> tamanho real do nome.</summary>
            public byte[] LengthDecodeTable { get; private init; } = Array.Empty<byte>();

            /// <summary>Tamanho real do nome -> índice a gravar (antes do complemento de 1).</summary>
            public byte[] LengthEncodeTable { get; private init; } = Array.Empty<byte>();

            /// <summary>Bytes de uma entrada sem contar o nome (23, ou 27 com offset de 64 bits).</summary>
            public int FixedEntrySize => Legacy ? 23 : 27;

            public static VersionInfo Create(int version, byte? nameKeyOverride)
            {
                if (version < MinVersion || version > MaxVersion)
                    throw new NotSupportedException(
                        $"Versão de YPF não suportada: {version} (suportadas: {MinVersion} a {MaxVersion}).");

                byte[] decode = new byte[256];
                for (int i = 0; i < decode.Length; i++)
                    decode[i] = (byte)i;

                foreach ((int a, int b) in version >= 500 ? LengthSwapsV500 : LengthSwapsOlder)
                    (decode[a], decode[b]) = (decode[b], decode[a]);

                byte[] encode = new byte[256];
                for (int i = 0; i < decode.Length; i++)
                    encode[decode[i]] = (byte)i;

                byte assumedKey = version == 290 ? (byte)0x40 : version >= 500 ? (byte)0x36 : (byte)0x00;

                return new VersionInfo
                {
                    Version = version,
                    Legacy = version < FirstVersionWith64BitOffsets,
                    NameKey = nameKeyOverride ?? assumedKey,
                    LengthDecodeTable = decode,
                    LengthEncodeTable = encode
                };
            }

            public uint NameHash(ReadOnlySpan<byte> data) => Legacy ? Crc32(data) : MurmurHash2(data);

            public uint DataHash(ReadOnlySpan<byte> data) => Legacy ? Adler32(data) : MurmurHash2(data);
        }

        private sealed class Entry
        {
            public string Name { get; set; } = "";
            public byte Type { get; set; }
            public byte Method { get; set; }
            public int RawSize { get; set; }
            public int StoredSize { get; set; }
            public long Offset { get; set; }
            public uint DataChecksum { get; set; }

            /// <summary>Nome usado ao listar/extrair: entradas YCG voltam a ter o sufixo ".ycg".</summary>
            public string ExtractName => Type == TypeYcg ? Name + ".ycg" : Name;
        }

        private sealed class PackedEntry
        {
            public string LocalPath { get; set; } = "";
            public string Name { get; set; } = "";
            public byte[] NameBytes { get; set; } = Array.Empty<byte>();
            public uint NameChecksum { get; set; }
            public byte Type { get; set; }
            public long Offset { get; set; }
            public int RawSize { get; set; }
            public int StoredSize { get; set; }
            public bool Compressed { get; set; }
            public uint DataChecksum { get; set; }
        }
    }
}
