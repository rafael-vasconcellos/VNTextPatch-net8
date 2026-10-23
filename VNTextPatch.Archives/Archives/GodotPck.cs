// GodotPck.cs
// Implementação de VNTextPatch.Archives.IArchive para arquivos PCK do Godot (formatos 0 a 4:
// Godot 2, 3, 4.0+, 4.5+ e 4.7+). O formato 4 tem o mesmo layout do 3; os recursos novos dele
// (pacote "sparse" e arquivos "delta") não são suportados e geram NotSupportedException.
// Baseada em https://github.com/DmitriySalnikov/GodotPCKExplorer
// (PCKReader / PCKPacker), mas sem a biblioteca nativa mbedTLS: o AES-256-CFB128 usado pelo Godot
// é montado em cima de Aes (modo ECB) do .NET, o que mantém o projeto compatível com Native AOT.
// GodotExe herda desta classe e só muda a extensão e o destino de Pack (PCK embutido no executável).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace VNTextPatch.Archives
{
    public class GodotPck : IArchive
    {
        private const int Magic = 0x43504447;              // "GDPC"
        private const int MaxFormat = 4;
        private const int FlagEncryptedIndex = 1 << 0;
        private const int FlagRelativeFileBase = 1 << 1;   // 4.3+
        private const int FlagSparseBundle = 1 << 2;       // formato 4: arquivos soltos ao lado do PCK
        private const int FileFlagEncrypted = 1 << 0;
        private const int FileFlagRemoval = 1 << 1;        // 4.4+
        private const int FileFlagDelta = 1 << 2;          // formato 4: a entrada é um patch binário
        private const int BlockSize = 16;
        private const int EncryptedHeaderSize = 16 + 8 + BlockSize; // MD5 + tamanho + IV
        private const int BufferSize = 1024 * 1024;        // múltiplo de BlockSize
        private const long Alignment = 16;

        private const string ResPrefix = "res://";
        private const string UserPrefix = "user://";
        private const string ExtractUserPrefix = "@@user@@/";
        private const string RemovalTag = ".@@removal@@";
        private const string NotPckMessage =
            "Cabeçalho PCK não encontrado (nem no início do arquivo, nem embutido no final dele).";

        private FileStream? _stream;
        private Header? _header;
        private List<Entry>? _entries;
        private byte[]? _key;
        private bool _disposed;

        private FileStream LoadedStream => _stream
            ?? throw new InvalidOperationException("O arquivo PCK ainda não foi carregado.");

        private Header LoadedHeader => _header
            ?? throw new InvalidOperationException("O arquivo PCK ainda não foi carregado.");

        private List<Entry> LoadedEntries => _entries
            ?? throw new InvalidOperationException("O arquivo PCK ainda não foi carregado.");

        public virtual string? Extension => ".pck";

        /// <summary>
        /// Formato do PCK criado por Pack (0 a 4). A interface IArchive.Pack não recebe versão, então
        /// ela é propriedade da instância. Load atualiza o valor; se o destino de Pack já existir e for
        /// um PCK válido, a versão dele tem prioridade. Padrão: 2 (Godot 4.0 a 4.4).
        /// </summary>
        public int FormatVersion { get; set; } = 2;

        /// <summary>
        /// Versão do Godot gravada no cabeçalho (major.minor.revision). O Godot recusa PCKs feitos por
        /// uma versão mais nova que a dele, então o padrão é o mais baixo possível (4.0.0).
        /// </summary>
        public Version EngineVersion { get; set; } = new Version(4, 0, 0);

        /// <summary>
        /// Abre o PCK (normal ou embutido no final de outro arquivo) e carrega o índice. encriptionKey
        /// é opcional: chave de 256 bits em 64 caracteres hexadecimais, necessária para PCK com índice
        /// ou arquivos criptografados (Godot 4).
        /// </summary>
        public void Load(string filePath, string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("O caminho do arquivo é obrigatório.", nameof(filePath));

            Close();
            byte[]? key = ParseKey(encriptionKey);

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Header header = ReadHeader(stream);
                List<Entry> entries = ReadEntries(stream, header, key);
                _stream = stream;
                _header = header;
                _entries = entries;
                _key = key;
                FormatVersion = header.Format;
                EngineVersion = header.Engine;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Lista os caminhos internos (sem "res://"; "user://" vira "@@user@@/"). Se recursive for false,
        /// retorna somente a raiz (pastas de primeiro nível com "/" no fim).
        /// </summary>
        public HashSet<string> ListFiles(bool recursive)
        {
            ThrowIfDisposed();

            var result = new HashSet<string>();
            foreach (Entry entry in LoadedEntries)
            {
                string name = entry.ExtractName;
                if (!recursive && name.Contains('/'))
                {
                    name = name.Split('/')[0] + '/';
                }

                result.Add(name);
            }

            return result;
        }

        /// <summary>
        /// Extrai todos os arquivos se pathToExtract for null/vazio. Se for um arquivo, extrai somente
        /// esse arquivo; se for um diretório, extrai o conteúdo dele e de seus subdiretórios.
        /// </summary>
        public void Extract(string outputPath, string? pathToExtract)
        {
            ThrowIfDisposed();

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
                    string.Equals(e.ExtractName, requested, StringComparison.OrdinalIgnoreCase));

                if (exact != null)
                {
                    selected = new List<Entry> { exact };
                }
                else
                {
                    string prefix = requested + "/";
                    selected = entries.Where(e =>
                        e.ExtractName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (selected.Count == 0)
                        throw new FileNotFoundException($"O caminho não existe no arquivo PCK: {pathToExtract}");
                }
            }

            Entry? deltaEntry = selected.FirstOrDefault(e => e.IsDelta && !e.IsRemoval);
            if (deltaEntry != null)
                throw new NotSupportedException(
                    $"Arquivo 'delta' (patch binário, formato 4) não é suportado: {deltaEntry.ExtractName}");

            if (_key == null && selected.Any(e => e.IsEncrypted))
                throw new ArgumentException(
                    "O PCK contém arquivos criptografados: informe a chave (64 caracteres hexadecimais) em encriptionKey.");

            // Ordenar por offset deixa a leitura do arquivo sequencial.
            foreach (Entry entry in selected.OrderBy(e => e.Offset))
            {
                string destination = SafeOutputPath(root, entry.ExtractName);
                Console.WriteLine(entry.ExtractName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ExtractEntry(entry, destination);
            }
        }

        /// <summary>
        /// Cria um PCK com todos os arquivos de pathToPack (recursivo). Com encriptionKey, o índice e os
        /// arquivos são criptografados (somente formatos 2 e 3). "@@user@@/x" vira "user://x" e o sufixo
        /// ".@@removal@@" vira a flag de remoção (4.4+), como o GodotPCKExplorer. O arquivo é escrito em
        /// um temporário e só substitui o destino se tudo der certo.
        /// </summary>
        public void Pack(string pathToPack, string outputPath, string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(pathToPack))
                throw new ArgumentException("A pasta de origem é obrigatória.", nameof(pathToPack));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("O caminho do arquivo de saída é obrigatório.", nameof(outputPath));

            byte[]? key = ParseKey(encriptionKey);

            string inputRoot = Path.GetFullPath(pathToPack);
            if (!Directory.Exists(inputRoot))
                throw new DirectoryNotFoundException($"A pasta de origem não existe: {inputRoot}");

            string outputFullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            (int format, Version engine) = ReadExistingVersion(outputFullPath) ?? (FormatVersion, EngineVersion);
            if (format < 0 || format > MaxFormat)
                throw new NotSupportedException($"Formato de PCK não suportado: {format} (suportados: 0 a {MaxFormat}).");
            if (key != null && format < 2)
                throw new NotSupportedException("PCK dos formatos 0 e 1 (Godot 2 e 3) não suporta criptografia.");

            string[] localFiles = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), outputFullPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (localFiles.Length == 0)
                throw new InvalidDataException($"Nenhum arquivo para empacotar em: {inputRoot}");

            var files = new List<PackEntry>(localFiles.Length);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in localFiles)
            {
                string relative = Path.GetRelativePath(inputRoot, file).Replace('\\', '/');
                ValidateArchivePath(relative);

                PackEntry entry = CreatePackEntry(file, relative, format, engine);
                if (!usedNames.Add(entry.Name))
                    throw new InvalidDataException($"Caminhos duplicados no PCK: {entry.Name}");

                files.Add(entry);
            }

            // MD5 antes de abrir a saída: se algum arquivo estiver faltando ou travado, falha antes.
            if (format >= 2)
                Parallel.ForEach(files, f => f.Md5 = f.Removal ? new byte[16] : ComputeMd5(f.LocalPath));

            string tempPath = outputFullPath + ".tmp";
            try
            {
                using (FileStream output = OpenOutput(tempPath, outputFullPath, out bool embedded))
                {
                    WritePck(output, files, key, embedded, format, engine);
                }

                File.Move(tempPath, outputFullPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Close();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Abre o arquivo temporário onde o PCK será escrito e devolve o stream posicionado no ponto onde
        /// o PCK deve começar. Aqui: arquivo novo, PCK solto. GodotExe sobrescreve para embutir.
        /// </summary>
        protected virtual FileStream OpenOutput(string tempPath, string outputFullPath, out bool embedded)
        {
            embedded = false;
            return new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }

        /// <summary>Início do PCK embutido no final de stream, ou null se não houver.</summary>
        protected static long? FindEmbeddedStart(FileStream stream)
        {
            try
            {
                Header header = ReadHeader(stream);
                return header.Embedded ? header.Start : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>Copia exatamente count bytes da posição atual de source (e opcionalmente alimenta um hash).</summary>
        protected static void CopyExactly(Stream source, Stream destination, long count, IncrementalHash? hash = null)
        {
            byte[] buffer = new byte[(int)Math.Min(BufferSize, Math.Max(count, 1))];
            long remaining = count;
            while (remaining > 0)
            {
                int n = (int)Math.Min(buffer.Length, remaining);
                source.ReadExactly(buffer, 0, n);
                destination.Write(buffer, 0, n);
                hash?.AppendData(buffer, 0, n);
                remaining -= n;
            }
        }

        /// <summary>Grava zeros até que a posição do stream seja múltipla de 8 (início/fim embutido do Godot).</summary>
        protected static void PadTo8(Stream stream) => WriteZeros(stream, (8 - stream.Position % 8) % 8);

        private void Close()
        {
            FileStream? stream = _stream;
            _stream = null;
            _header = null;
            _entries = null;
            _key = null;
            stream?.Dispose();
        }

        // ---------- Leitura ----------

        private static Header ReadHeader(FileStream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            long length = stream.Length;
            long start = 0;
            long end = length;
            bool embedded = false;

            stream.Position = 0;
            if (length < 4 || reader.ReadInt32() != Magic)
            {
                // PCK embutido: [PCK][tamanho: int64]["GDPC"] no final do arquivo.
                if (length < 12)
                    throw new InvalidDataException(NotPckMessage);

                stream.Position = length - 12;
                long size = reader.ReadInt64();
                if (reader.ReadInt32() != Magic || size <= 0 || size > length - 12)
                    throw new InvalidDataException(NotPckMessage);

                start = length - 12 - size;
                end = length - 12;
                embedded = true;

                stream.Position = start;
                if (reader.ReadInt32() != Magic)
                    throw new InvalidDataException(NotPckMessage);
            }

            int format = reader.ReadInt32();
            int major = reader.ReadInt32();
            int minor = reader.ReadInt32();
            int revision = reader.ReadInt32();
            if (format < 0 || format > MaxFormat)
                throw new NotSupportedException($"Formato de PCK não suportado: {format} (suportados: 0 a {MaxFormat}).");
            if (major < 0 || minor < 0 || revision < 0)
                throw new InvalidDataException("Versão do Godot inválida no cabeçalho do PCK.");

            int flags = 0;
            long fileBase = 0;
            long indexBase = 0;
            if (format >= 2)
            {
                flags = reader.ReadInt32();
                if ((flags & FlagSparseBundle) != 0)
                    throw new NotSupportedException(
                        "PCK 'sparse bundle' (formato 4) não é suportado: os arquivos ficam fora do PCK.");

                fileBase = reader.ReadInt64();
                if ((flags & FlagRelativeFileBase) != 0 || format >= 3)
                    fileBase += start;

                if (format >= 3)
                    indexBase = reader.ReadInt64() + start;
            }

            stream.Position += 16 * sizeof(int); // reservado
            if (format < 3)
                indexBase = stream.Position;

            if (indexBase < start || indexBase > end - 4 || (format >= 2 && (fileBase < start || fileBase > end)))
                throw new InvalidDataException("Cabeçalho PCK inválido (endereços fora do arquivo).");

            return new Header(format, new Version(major, minor, revision), flags, start, end, embedded, fileBase, indexBase);
        }

        private static List<Entry> ReadEntries(FileStream stream, Header header, byte[]? key)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            stream.Position = header.IndexBase;
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Quantidade de arquivos inválida no PCK.");

            MemoryStream? decrypted = null;
            try
            {
                BinaryReader index = reader;
                if (header.EncryptedIndex)
                {
                    if (key == null)
                        throw new ArgumentException(
                            "O índice deste PCK é criptografado: informe a chave (64 caracteres hexadecimais) em encriptionKey.");

                    var memory = decrypted = new MemoryStream();
                    byte[] expected = DecryptBlock(stream, stream.Position, key, (buf, n) => memory.Write(buf, 0, n));
                    byte[] actual = MD5.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length));
                    if (!actual.AsSpan().SequenceEqual(expected))
                        throw new CryptographicException("MD5 do índice incorreto: chave de criptografia errada ou PCK corrompido.");

                    memory.Position = 0;
                    index = new BinaryReader(memory);
                }

                // PCK repetido: a última ocorrência vence, como no Godot.
                var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    int pathSize = index.ReadInt32();
                    if (pathSize < 0 || pathSize > 1 << 16)
                        throw new InvalidDataException($"Tamanho de caminho inválido na entrada {i} do PCK.");

                    string path = Encoding.UTF8.GetString(index.ReadBytes(pathSize)).Replace("\0", "");
                    long offset = index.ReadInt64() + header.FileBase;
                    long size = index.ReadInt64();
                    byte[] md5 = index.ReadBytes(16);
                    int flags = header.Format >= 2 ? index.ReadInt32() : 0;
                    if (md5.Length != 16)
                        throw new EndOfStreamException("Índice PCK truncado.");

                    bool encrypted = (flags & FileFlagEncrypted) != 0;
                    bool removal = (flags & FileFlagRemoval) != 0 && AtLeast(header.Engine, 4, 4);
                    bool delta = (flags & FileFlagDelta) != 0 && header.Format >= 4;
                    if (!removal)
                    {
                        long stored = encrypted ? EncryptedHeaderSize + AlignUp(size, BlockSize) : size;
                        if (size < 0 || offset < header.Start || offset > header.End || stored > header.End - offset)
                            throw new InvalidDataException($"Entrada PCK fora dos limites do arquivo: {path}");
                    }

                    var entry = new Entry(path, offset, size, md5, encrypted, removal, delta);
                    ValidateArchivePath(entry.ExtractName);
                    entries[entry.ExtractName] = entry;
                }

                return entries.Values.ToList();
            }
            finally
            {
                decrypted?.Dispose();
            }
        }

        private void ExtractEntry(Entry entry, string destination)
        {
            FileStream stream = LoadedStream;
            bool verify = LoadedHeader.Format >= 2 && !entry.IsRemoval;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            bool done = false;

            var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                if (entry.Size > 0 && !entry.IsRemoval)
                {
                    if (entry.IsEncrypted)
                    {
                        DecryptBlock(stream, entry.Offset, _key!, (buf, n) =>
                        {
                            output.Write(buf, 0, n);
                            hash.AppendData(buf, 0, n);
                        });
                    }
                    else
                    {
                        stream.Position = entry.Offset;
                        CopyExactly(stream, output, entry.Size, hash);
                    }
                }

                output.Flush();
                if (verify && !hash.GetHashAndReset().AsSpan().SequenceEqual(entry.Md5))
                    throw new InvalidDataException(
                        $"MD5 incorreto para {entry.ExtractName}" + (entry.IsEncrypted ? " (chave de criptografia errada?)." : "."));

                done = true;
            }
            finally
            {
                output.Dispose();
                if (!done)
                {
                    try { File.Delete(destination); } catch (IOException) { }
                }
            }
        }

        /// <summary>
        /// Lê um bloco criptografado (MD5[16] + tamanho[8] + IV[16] + dados alinhados a 16) e entrega os
        /// bytes decifrados em pedaços a sink. Retorna o MD5 gravado no cabeçalho do bloco.
        /// </summary>
        private static byte[] DecryptBlock(FileStream stream, long offset, byte[] key, Action<byte[], int> sink)
        {
            stream.Position = offset;
            byte[] header = new byte[EncryptedHeaderSize];
            stream.ReadExactly(header);

            byte[] md5 = header[..16];
            long dataSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
            long encodedSize = AlignUp(dataSize, BlockSize);
            if (dataSize < 0 || encodedSize > stream.Length - offset - EncryptedHeaderSize)
                throw new InvalidDataException("Bloco criptografado fora dos limites do arquivo PCK.");

            using var cfb = new Cfb128(key, header[24..40]);
            byte[] buffer = new byte[(int)Math.Min(BufferSize, encodedSize)];
            long remainingEncoded = encodedSize;
            long remainingData = dataSize;
            while (remainingEncoded > 0)
            {
                int n = (int)Math.Min(buffer.Length, remainingEncoded);
                stream.ReadExactly(buffer, 0, n);
                cfb.Decrypt(buffer, n);

                int valid = (int)Math.Min(n, remainingData);
                sink(buffer, valid);
                remainingEncoded -= n;
                remainingData -= valid;
            }

            return md5;
        }

        // ---------- Escrita ----------

        private static PackEntry CreatePackEntry(string localPath, string relative, int format, Version engine)
        {
            string name;
            if (relative.StartsWith(ExtractUserPrefix, StringComparison.Ordinal))
                name = UserPrefix + relative.Substring(ExtractUserPrefix.Length);
            else
                name = AtLeast(engine, 4, 4) ? relative : ResPrefix + relative; // a partir da 4.4 não há "res://"

            bool removal = false;
            if (format >= 2 && name.EndsWith(RemovalTag, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - RemovalTag.Length);
                removal = true;
            }

            return new PackEntry
            {
                Name = name,
                LocalPath = localPath,
                Removal = removal,
                Size = removal ? 0 : new FileInfo(localPath).Length,
                Md5 = new byte[16] // formatos 0 e 1 não têm MD5; os demais preenchem em Pack
            };
        }

        private static void WritePck(FileStream output, List<PackEntry> files, byte[]? key, bool embedded, int format, Version engine)
        {
            bool encrypt = key != null;
            foreach (PackEntry f in files)
            {
                f.Encrypted = encrypt && !f.Removal;
                f.StoredSize = f.Removal ? 0 : f.Encrypted ? EncryptedHeaderSize + AlignUp(f.Size, BlockSize) : f.Size;
            }

            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            long pckStart = output.Position;

            int flags = 0;
            if (format >= 2)
            {
                if (encrypt) flags |= FlagEncryptedIndex;
                if ((embedded && AtLeast(engine, 4, 3)) || format >= 3) flags |= FlagRelativeFileBase;
            }

            writer.Write(Magic);
            writer.Write(format);
            writer.Write(engine.Major);
            writer.Write(engine.Minor);
            writer.Write(Math.Max(engine.Build, 0));

            long fileBasePos = 0;
            long indexBasePos = 0;
            if (format >= 2)
            {
                writer.Write(flags);
                fileBasePos = output.Position;
                writer.Write(0L); // file_base, corrigido no final
                if (format >= 3)
                {
                    indexBasePos = output.Position;
                    writer.Write(0L); // index_base, corrigido no final
                }
            }

            writer.Write(new byte[16 * sizeof(int)]); // reservado
            long afterHeader = output.Position;

            // O tamanho do índice não depende dos offsets, então o layout todo é conhecido antes de gravar.
            long indexLength = BuildIndex(files, format, 0).Length;
            long indexBlobSize = encrypt ? EncryptedHeaderSize + AlignUp(indexLength, BlockSize) : indexLength;
            long fileBase = format < 3
                ? AlignUp(afterHeader + 4 + indexBlobSize, Alignment) // índice no início
                : AlignUp(afterHeader, Alignment);                    // índice no final (4.5+)

            long cursor = fileBase;
            foreach (PackEntry f in files)
            {
                if (f.Removal) continue;
                f.Offset = cursor;
                cursor = AlignUp(cursor + f.StoredSize, Alignment);
            }

            byte[] index = BuildIndex(files, format, fileBase);

            if (format < 3)
            {
                writer.Write(files.Count);
                WriteIndexBlob(output, index, key);
            }

            foreach (PackEntry f in files)
            {
                if (f.Removal) continue;
                WriteZeros(output, f.Offset - output.Position);

                using var source = new FileStream(f.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (f.Encrypted)
                    WriteEncrypted(output, key!, source, f.Size, f.Md5);
                else
                    CopyExactly(source, output, f.Size);
            }

            long indexBase = 0;
            if (format >= 3)
            {
                WriteZeros(output, cursor - output.Position);
                indexBase = output.Position;
                writer.Write(files.Count);
                WriteIndexBlob(output, index, key);
            }

            if (embedded)
            {
                WriteZeros(output, (8 - (output.Position - pckStart + 12) % 8) % 8);
                writer.Write(output.Position - pckStart);
                writer.Write(Magic);
            }

            if (format >= 2)
            {
                long end = output.Position;
                output.Position = fileBasePos;
                writer.Write((flags & FlagRelativeFileBase) != 0 ? fileBase - pckStart : fileBase);
                if (format >= 3)
                {
                    output.Position = indexBasePos;
                    writer.Write(indexBase - pckStart);
                }

                output.Position = end;
            }
        }

        private static byte[] BuildIndex(List<PackEntry> files, int format, long fileBase)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

            foreach (PackEntry f in files)
            {
                byte[] name = Encoding.UTF8.GetBytes(f.Name);
                int nameLength = format >= 2 ? (int)AlignUp(name.Length, 4) : name.Length; // Godot 4 alinha em 4
                writer.Write(nameLength);
                writer.Write(name);
                writer.Write(new byte[nameLength - name.Length]);

                // Formatos 0 e 1: offset absoluto no arquivo; demais: relativo ao file_base.
                writer.Write(f.Removal ? 0L : format >= 2 ? f.Offset - fileBase : f.Offset);
                writer.Write(f.Size);
                writer.Write(f.Md5);

                if (format >= 2)
                {
                    int fileFlags = 0;
                    if (f.Encrypted) fileFlags |= FileFlagEncrypted;
                    if (f.Removal) fileFlags |= FileFlagRemoval;
                    writer.Write(fileFlags);
                }
            }

            writer.Flush();
            return memory.ToArray();
        }

        private static void WriteIndexBlob(Stream output, byte[] index, byte[]? key)
        {
            if (key == null)
            {
                output.Write(index);
                return;
            }

            using var source = new MemoryStream(index, writable: false);
            WriteEncrypted(output, key, source, index.Length, MD5.HashData(index));
        }

        private static void WriteEncrypted(Stream output, byte[] key, Stream source, long size, byte[] md5)
        {
            byte[] iv = RandomNumberGenerator.GetBytes(BlockSize);
            output.Write(md5);
            output.Write(BitConverter.GetBytes(size)); // little-endian nas plataformas suportadas pelo .NET
            output.Write(iv);

            using var cfb = new Cfb128(key, iv);
            byte[] buffer = new byte[(int)Math.Min(BufferSize, Math.Max(AlignUp(size, BlockSize), BlockSize))];
            long remaining = size;
            while (remaining > 0)
            {
                int read = (int)Math.Min(buffer.Length, remaining);
                source.ReadExactly(buffer, 0, read);

                int padded = (int)AlignUp(read, BlockSize);
                Array.Clear(buffer, read, padded - read);
                cfb.Encrypt(buffer, padded);
                output.Write(buffer, 0, padded);
                remaining -= read;
            }
        }

        private static void WriteZeros(Stream stream, long count)
        {
            if (count < 0)
                throw new InvalidOperationException("Layout do PCK inconsistente (posição além do esperado).");

            byte[] zeros = new byte[(int)Math.Min(count, 64 * 1024)];
            while (count > 0)
            {
                int n = (int)Math.Min(zeros.Length, count);
                stream.Write(zeros, 0, n);
                count -= n;
            }
        }

        private static (int Format, Version Engine)? ReadExistingVersion(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Header header = ReadHeader(stream);
                return (header.Format, header.Engine);
            }
            catch (InvalidDataException)
            {
                return null; // o destino existe, mas não é um PCK: será substituído.
            }
        }

        // ---------- Utilitários ----------

        private static byte[] ComputeMd5(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return MD5.HashData(stream);
        }

        private static bool AtLeast(Version v, int major, int minor) =>
            v.Major > major || (v.Major == major && v.Minor >= minor);

        private static long AlignUp(long value, long alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private static byte[]? ParseKey(string? encriptionKey)
        {
            if (string.IsNullOrWhiteSpace(encriptionKey))
                return null;

            string hex = encriptionKey.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);

            try
            {
                if (hex.Length == 64)
                    return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
            }

            throw new ArgumentException(
                "A chave do PCK deve ter 64 caracteres hexadecimais (256 bits).", nameof(encriptionKey));
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
                throw new InvalidDataException($"Caminho inseguro ou inválido dentro do PCK: {path}");
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        private sealed record Header(
            int Format, Version Engine, int Flags, long Start, long End, bool Embedded, long FileBase, long IndexBase)
        {
            public bool EncryptedIndex => (Flags & FlagEncryptedIndex) != 0;
        }

        private sealed class Entry
        {
            public Entry(string path, long offset, long size, byte[] md5, bool encrypted, bool removal, bool delta)
            {
                IsDelta = delta;
                Offset = offset;
                Size = size;
                Md5 = md5;
                IsEncrypted = encrypted;
                IsRemoval = removal;

                string name = path.StartsWith(ResPrefix, StringComparison.Ordinal) ? path.Substring(ResPrefix.Length) : path;
                if (name.StartsWith(UserPrefix, StringComparison.Ordinal))
                    name = ExtractUserPrefix + name.Substring(UserPrefix.Length);
                if (removal)
                    name += RemovalTag;

                ExtractName = NormalizeArchivePath(name);
            }

            /// <summary>Nome usado ao listar/extrair.</summary>
            public string ExtractName { get; }
            public long Offset { get; }
            public long Size { get; }
            public byte[] Md5 { get; }
            public bool IsEncrypted { get; }
            public bool IsRemoval { get; }
            public bool IsDelta { get; }
        }

        private sealed class PackEntry
        {
            public string Name { get; set; } = "";
            public string LocalPath { get; set; } = "";
            public long Size { get; set; }
            public byte[] Md5 { get; set; } = new byte[16];
            public bool Removal { get; set; }
            public bool Encrypted { get; set; }
            public long StoredSize { get; set; }
            public long Offset { get; set; }
        }

        /// <summary>
        /// AES-256 em modo CFB de 128 bits (o que o mbedTLS faz no Godot), montado sobre ECB para não
        /// depender do suporte a CFB da plataforma. O IV avança entre chamadas, então os dados podem
        /// ser processados em pedaços (múltiplos de 16 bytes).
        /// </summary>
        private sealed class Cfb128 : IDisposable
        {
            private readonly Aes _aes = Aes.Create();
            private readonly byte[] _iv;
            private byte[] _input = Array.Empty<byte>();
            private byte[] _keystream = Array.Empty<byte>();

            public Cfb128(byte[] key, byte[] iv)
            {
                _aes.Key = key;
                _iv = (byte[])iv.Clone();
            }

            // Decifrar: P[i] = C[i] xor AES(C[i-1]), com C[-1] = IV. Todas as entradas são conhecidas
            // de antemão (IV + blocos cifrados), então o fluxo de chaves sai de uma única chamada ECB.
            public void Decrypt(byte[] data, int count)
            {
                if (count == 0) return;
                if (_input.Length < count)
                {
                    _input = new byte[count];
                    _keystream = new byte[count];
                }

                byte[] nextIv = data.AsSpan(count - BlockSize, BlockSize).ToArray();
                _iv.CopyTo(_input, 0);
                Buffer.BlockCopy(data, 0, _input, BlockSize, count - BlockSize);
                _aes.EncryptEcb(_input.AsSpan(0, count), _keystream.AsSpan(0, count), PaddingMode.None);

                for (int i = 0; i < count; i++)
                    data[i] ^= _keystream[i];

                nextIv.CopyTo(_iv, 0);
            }

            // Cifrar: C[i] = P[i] xor AES(C[i-1]); depende do bloco anterior, então é bloco a bloco.
            public void Encrypt(byte[] data, int count)
            {
                Span<byte> keystream = stackalloc byte[BlockSize];
                for (int offset = 0; offset < count; offset += BlockSize)
                {
                    _aes.EncryptEcb(_iv, keystream, PaddingMode.None);
                    for (int j = 0; j < BlockSize; j++)
                        data[offset + j] ^= keystream[j];

                    Buffer.BlockCopy(data, offset, _iv, 0, BlockSize);
                }
            }

            public void Dispose() => _aes.Dispose();
        }
    }
}
