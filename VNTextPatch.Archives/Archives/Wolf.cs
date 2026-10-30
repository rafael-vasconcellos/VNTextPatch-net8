// Wolf.cs
// Leitor (somente extração) de arquivos DXArchive usados pelo Wolf RPG Editor (.wolf, .data, .pak ...).
// Port para C# da lógica de descriptografia do UberWolf (https://github.com/Sinflower/UberWolf):
//   - DXArchive v5 (Wolf 2.01/2.10), v6 (Wolf 2.20) e v8 (Wolf 2.225 em diante)
//   - Cifras: XOR de 12 bytes (v5/v6), XOR de 7 bytes derivado da chave (v8),
//             ChaCha20 (CC2 v1) e o esquema "novo" (3.31/3.50: AES-CTR + chaves de 768 bytes)
//   - Compressão: LZ + Huffman
// Não suportado: versões Pro (cryptVersion >= 1000 / 0xC8), que dependem do Game.dat / chave Pro.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using System.Text;

namespace VNTextPatch.Archives
{
    public sealed class Wolf : IArchive
    {
        internal sealed record Mode(string Name, ushort CryptVersion, int Format, byte[] Key);

        // Tabela copiada de UberWolfLib/WolfDec.cpp (DEFAULT_CRYPT_MODES).
        internal static readonly Mode[] Modes =
        {
            new Mode("Wolf RPG v2.01", 0x0, 5, new byte[] { 0x0F, 0x53, 0xE1, 0x3E, 0x04, 0x37, 0x12, 0x17, 0x60, 0x0F, 0x53, 0xE1 }),
            new Mode("Wolf RPG v2.10", 0x0, 5, new byte[] { 0x4C, 0xD9, 0x2A, 0xB7, 0x28, 0x9B, 0xAC, 0x07, 0x3E, 0x77, 0xEC, 0x4C }),
            new Mode("Wolf RPG v2.20", 0x0, 6, new byte[] { 0x38, 0x50, 0x40, 0x28, 0x72, 0x4F, 0x21, 0x70, 0x3B, 0x73, 0x35, 0x38 }),
            new Mode("Wolf RPG v2.225", 0x0, 8, new byte[] { 0x57, 0x4C, 0x46, 0x52, 0x50, 0x72, 0x4F, 0x21, 0x70, 0x28, 0x3B, 0x73, 0x35, 0x28, 0x28, 0x38, 0x50, 0x40, 0x28, 0x28, 0x55, 0x46, 0x57, 0x6C, 0x75, 0x24, 0x23, 0x35, 0x28, 0x3D, 0x00 }),
            new Mode("Wolf RPG v3.00", 0x12C, 8, new byte[] { 0x0F, 0x53, 0xE1, 0x3E, 0x8E, 0xB5, 0x41, 0x91, 0x52, 0x16, 0x55, 0xAE, 0x34, 0xC9, 0x8F, 0x79, 0x59, 0x2F, 0x59, 0x6B, 0x95, 0x19, 0x9B, 0x1B, 0x35, 0x9A, 0x2F, 0xDE, 0xC9, 0x7C, 0x12, 0x96, 0xC3, 0x14, 0xB5, 0x0F, 0x53, 0xE1, 0x3E, 0x8E, 0x00 }),
            new Mode("Wolf RPG v3.14", 0x13A, 8, new byte[] { 0x31, 0xF9, 0x01, 0x36, 0xA3, 0xE3, 0x8D, 0x3C, 0x7B, 0xC3, 0x7D, 0x25, 0xAD, 0x63, 0x28, 0x19, 0x1B, 0xF7, 0x8E, 0x6C, 0xC4, 0xE5, 0xE2, 0x76, 0x82, 0xEA, 0x4F, 0xED, 0x61, 0xDA, 0xE0, 0x44, 0x5B, 0xB6, 0x46, 0x3B, 0x06, 0xD5, 0xCE, 0xB6, 0x78, 0x58, 0xD0, 0x7C, 0x82, 0x00 }),
            new Mode("Wolf RPG v3.31", 0x14B, 8, new byte[] { 0xCA, 0x08, 0x4C, 0x5D, 0x17, 0x0D, 0xDA, 0xA1, 0xD7, 0x27, 0xC8, 0x41, 0x54, 0x38, 0x82, 0x32, 0x54, 0xB7, 0xF9, 0x46, 0x8E, 0x13, 0x6B, 0xCA, 0xD0, 0x5C, 0x95, 0x95, 0xE2, 0xDC, 0x03, 0x53, 0x60, 0x9B, 0x4A, 0x38, 0x17, 0xF3, 0x69, 0x59, 0xA4, 0xC7, 0x9A, 0x43, 0x63, 0xE6, 0x54, 0xAF, 0xDB, 0xBB, 0x43, 0x58, 0x00 }),
            new Mode("Wolf RPG v3.50", 0x15E, 8, new byte[] { 0xD2, 0x84, 0xCE, 0x28, 0xCE, 0x88, 0x82, 0xE4, 0x2A, 0x18, 0x2E, 0x4C, 0x06, 0xB4, 0xEA, 0x84, 0x06, 0xB8, 0xC6, 0x88, 0x5A, 0xA0, 0x9E, 0x7C, 0x56, 0x40, 0xBA, 0x34, 0x52, 0xCC, 0xC6, 0x7C, 0x2E, 0x14, 0x12, 0x68, 0xFE, 0x5C, 0x76, 0x94, 0x86, 0x78, 0x8E, 0x4C, 0xBE, 0x88, 0x66, 0x9C, 0x1E, 0xE0, 0x8E, 0x6C, 0x00 }),
            new Mode("Wolf RPG ChaCha2 v1", 0x64, 8, new byte[] { 0xC9, 0x82, 0xF8, 0xB4, 0x2C, 0x93, 0x9E, 0x83, 0x0E, 0xBC, 0xBC, 0x92, 0x68, 0x8D, 0x59, 0xA1, 0x4A, 0x9E, 0x7F, 0xB0, 0xAC, 0xAF, 0x1D, 0x8F, 0x8E, 0xB8, 0x3B, 0x9E, 0xE8, 0x89, 0xD9, 0xAD, 0xFF, 0xBC, 0x2D, 0xAB, 0x9D, 0x8B, 0x0F, 0xB4, 0xBB, 0x9A, 0x69, 0x85, 0x00 }),
            new Mode("One Way Heroics", 0x0, 8, new byte[] { 0x6E, 0x47, 0x75, 0x69, 0x39, 0x28, 0x27, 0x26, 0x31, 0x3D, 0x40, 0x33, 0x23, 0x61, 0x00 }),
            new Mode("One Way Heroics Plus", 0x0, 8, new byte[] { 0x50, 0x68, 0x3D, 0x58, 0x33, 0x5E, 0x5D, 0x6F, 0x32, 0x41, 0x28, 0x2C, 0x31, 0x3D, 0x40, 0x33, 0x23, 0x61, 0x00 }),
        };

        private const int HeadSize8 = 64;
        private const int KeyStringMax = 63;      // DXA_KEY_STRING_LENGTH
        private const int KeyBufferMax = 2048 - 8; // DXA_KEY_STRING_MAXLENGTH - 8
        private const long AttrDirectory = 0x10;  // FILE_ATTRIBUTE_DIRECTORY
        private const int ChunkSize = 0x1000000;

        // O Wolf Pro acrescenta esta mensagem no começo dos arquivos .dat protegidos contra extração.
        private static readonly byte[] AntiUnpackMarker =
            Encoding.ASCII.GetBytes("Extracting data from encrypted files violates the guidelines.\0");

        private static readonly string[] AntiUnpackFiles =
            { "game.dat", "cdatabase.dat", "database.dat", "commonevent.dat" };

        private readonly string _extension;
        private FileStream? _stream;
        private List<Entry>? _entries;
        private Context? _ctx;
        private bool _disposed;

        public Wolf() : this(".wolf") { }

        public Wolf(string extension)
        {
            _extension = extension;
        }

        public string? Extension => _extension;

        // ------------------------------------------------------------------ IArchive

        public void Load(string filePath, string? encriptionKey)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("O caminho do arquivo é obrigatório.", nameof(filePath));

            ReleaseCurrent();

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                (Context ctx, List<Entry> entries) = Open(stream, encriptionKey);
                _stream = stream;
                _ctx = ctx;
                _entries = entries;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public HashSet<string> ListFiles(bool recursive)
        {
            ThrowIfDisposed();
            var result = new HashSet<string>();
            foreach (Entry entry in LoadedEntries)
            {
                string name = entry.Path;
                if (!recursive && name.Contains('/'))
                    name = name.Split('/')[0] + '/';

                result.Add(name);
            }

            return result;
        }

        public void Extract(string outputPath, string? pathToExtract)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A pasta de saída é obrigatória.", nameof(outputPath));

            List<Entry> entries = LoadedEntries;
            string root = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(root);
            Console.WriteLine("Unpacking...");

            List<Entry> selected;
            if (string.IsNullOrWhiteSpace(pathToExtract))
            {
                selected = entries;
            }
            else
            {
                string requested = pathToExtract.Trim().Replace('\\', '/').Trim('/');
                Entry? exact = entries.FirstOrDefault(e =>
                    string.Equals(e.Path, requested, StringComparison.OrdinalIgnoreCase));

                if (exact != null)
                {
                    selected = new List<Entry> { exact };
                }
                else
                {
                    string prefix = requested + "/";
                    selected = entries
                        .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (selected.Count == 0)
                        throw new FileNotFoundException($"O caminho não existe no arquivo Wolf: {pathToExtract}");
                }
            }

            foreach (Entry entry in selected)
            {
                string destination = SafeOutputPath(root, entry.Path);
                Console.WriteLine(entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ExtractEntry(entry, destination);
            }
        }

        public void Pack(string pathToPack, string outputPath, string? encriptionKey)
        {
            throw new NotSupportedException(
                "Empacotar arquivos Wolf RPG ainda não foi implementado (o Wolf.cs só lê/extrai).");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseCurrent();
        }

        // ------------------------------------------------------------------ Estado

        private sealed class Entry
        {
            public string Path = "";
            public long Offset;          // posição absoluta dos dados no arquivo
            public long Size;            // tamanho original (descomprimido)
            public long PressSize = -1;  // tamanho após LZ (-1 = sem LZ)
            public long HuffSize = -1;   // tamanho após Huffman (-1 = sem Huffman)
            public byte[]? Key;          // chave XOR por arquivo (v8 sem NewCrypt/ChaCha)
        }

        internal sealed class Context
        {
            public FileStream Stream = null!;
            public long FileLength;
            public int Format;                 // 5, 6 ou 8
            public ushort CryptVersion;
            public bool NoKey;
            public bool NewCrypt;
            public bool ChaCha;
            public byte[] ArchKey = Array.Empty<byte>(); // 7 bytes (v8) ou 12 bytes (v5/v6)
            public byte[] KeyString = Array.Empty<byte>();
            public byte[]? SpecialKey;         // 768 bytes (NewCrypt, chave "por arquivo")
            public byte[]? SpecialMod;         // 512 bytes (somente v3.50)
            public byte[]? OtherKey;           // 768 bytes (NewCrypt, camada sobre o arquivo todo)
            public byte[]? OtherMod;
            public byte[] ChaChaKey = Array.Empty<byte>();
            public byte[] ChaChaNonce = Array.Empty<byte>();
            public List<(long Start, byte[] Stream)> Overlays = new();
            public long DataStart;
            public byte HuffmanKB;
            public uint CharCode = 932;
        }

        private List<Entry> LoadedEntries =>
            _entries ?? throw new InvalidOperationException("Nenhum arquivo Wolf foi carregado. Chame Load primeiro.");

        private void ReleaseCurrent()
        {
            FileStream? stream = _stream;
            _stream = null;
            _entries = null;
            _ctx = null;
            stream?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Wolf));
        }

        // ------------------------------------------------------------------ Abertura

        private static (Context, List<Entry>) Open(FileStream stream, string? userKey)
        {
            long length = stream.Length;
            if (length < 28)
                throw new InvalidDataException("Arquivo pequeno demais para ser um arquivo Wolf RPG.");

            byte[] first = new byte[(int)Math.Min(length, HeadSize8)];
            ReadExactAt(stream, 0, first, 0, first.Length);

            byte[]? custom = string.IsNullOrEmpty(userKey) ? null : TerminateWithNul(Encoding.UTF8.GetBytes(userKey));
            var errors = new List<string>();

            bool isV8 = first.Length >= HeadSize8 && first[0] == (byte)'D' && first[1] == (byte)'X'
                        && BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(2)) == 8;

            if (isV8)
            {
                ushort cv = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(46));
                if (cv == 0xC8 || cv >= 1000)
                    throw new NotSupportedException(
                        $"Versão de criptografia Wolf RPG Pro não suportada ({cv}). Ela depende do Game.dat / chave Pro.");

                List<Mode> candidates;
                if (custom != null)
                {
                    // Mantém o fluxo do UberWolf: a versão vem do cabeçalho, a chave é a informada.
                    Mode? known = Modes.FirstOrDefault(m => m.Format == 8 && m.CryptVersion == cv);
                    byte[] key = custom;
                    if (cv == 0x64 && known != null && custom.Length < 44)
                        key = known.Key;
                    candidates = new List<Mode> { new Mode("Chave informada", cv, 8, key) };
                }
                else
                {
                    candidates = Modes.Where(m => m.Format == 8 && m.CryptVersion == cv).ToList();
                    if (candidates.Count == 0)
                        throw new NotSupportedException(
                            $"Versão de criptografia Wolf RPG desconhecida ({cv}). Informe a chave manualmente.");
                }

                foreach (Mode mode in candidates)
                {
                    try
                    {
                        return OpenV8(stream, first, mode);
                    }
                    catch (Exception ex) when (IsFormatError(ex))
                    {
                        errors.Add($"{mode.Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                IEnumerable<Mode> candidates = Modes.Where(m => m.Format is 5 or 6);
                if (custom != null)
                    candidates = new[]
                    {
                        new Mode("Chave informada (v5)", 0, 5, custom),
                        new Mode("Chave informada (v6)", 0, 6, custom),
                    };

                foreach (Mode mode in candidates)
                {
                    try
                    {
                        return OpenLegacy(stream, mode);
                    }
                    catch (Exception ex) when (IsFormatError(ex))
                    {
                        errors.Add($"{mode.Name}: {ex.Message}");
                    }
                }
            }

            throw new InvalidDataException(
                "Não foi possível abrir o arquivo como Wolf RPG (formato ou chave desconhecidos). " +
                string.Join(" | ", errors));
        }

        private static bool IsFormatError(Exception ex) =>
            ex is InvalidDataException or ArgumentException or IndexOutOfRangeException
                or OverflowException or EndOfStreamException or DivideByZeroException;

        private static (Context, List<Entry>) OpenV8(FileStream stream, byte[] first, Mode mode)
        {
            long fileLen = stream.Length;
            byte[] head = (byte[])first.Clone();
            ushort cv = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(46));

            var ctx = new Context
            {
                Stream = stream,
                FileLength = fileLen,
                Format = 8,
                CryptVersion = cv,
                NewCrypt = (cv >= 331 && cv < 1000) || cv >= 1010,
                ChaCha = cv == 0x64 || cv == 0xC8,
            };

            byte[] pwd = head.AsSpan(49, 15).ToArray();
            if (ctx.NewCrypt)
                WolfCrypto.CryptAddresses(head, pwd, cv);

            uint headSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            long dataStart = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(8)));
            long fnt = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16)));
            long fileTable = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(24)));
            long dirTable = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(32)));
            uint charCode = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(40));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));
            byte huffKb = head[48];

            if (fnt < HeadSize8 || fnt > fileLen || headSize == 0 || headSize > 0x40000000u
                || dataStart < 0 || dataStart > fileLen)
                throw new InvalidDataException("Cabeçalho DXA inválido.");

            ctx.NoKey = (flags & 1) != 0;
            ctx.HuffmanKB = huffKb;
            ctx.DataStart = dataStart;
            ctx.CharCode = charCode;

            byte[] keyStr = TruncateAtNul(mode.Key);
            ctx.KeyString = keyStr.Length > KeyStringMax ? keyStr[..KeyStringMax] : keyStr;
            ctx.ArchKey = KeyCreate(ctx.KeyString, ctx.KeyString.Length);

            if (ctx.ChaCha)
            {
                if (mode.Key.Length < 44)
                    throw new InvalidDataException("Chave ChaCha20 inválida (esperado 32 bytes de chave + 12 de nonce).");
                ctx.ChaChaKey = mode.Key[..32];
                ctx.ChaChaNonce = mode.Key[32..44];
            }

            if (ctx.NewCrypt)
            {
                bool v35 = WolfCrypto.IsV35(cv);

                ctx.OtherKey = WolfCrypto.InitWolfCrypt(cv, pwd, true, keyStr);
                ctx.OtherMod = v35 ? WolfCrypto.BuildModdedKey(ctx.OtherKey) : null;

                ctx.SpecialKey = WolfCrypto.InitWolfCrypt(cv, pwd, false, null);
                ctx.SpecialMod = v35 ? WolfCrypto.BuildModdedKey(ctx.SpecialKey) : null;

                // Camada AES-CTR: só afeta o começo dos dados e a região do cabeçalho (do FNT até o fim).
                if (fileLen - 64 >= 0x400)
                {
                    byte[] roundKey = WolfCrypto.InitAes128(pwd, cv);
                    uint bodySize = 0x400;

                    if (v35)
                    {
                        uint seed = (uint)(pwd[2] * pwd[4] + pwd[12]);
                        if (seed == 0) seed = 1;

                        var xs = new WolfCrypto.XorShift32(seed);
                        if (fileLen >= (long)(xs.Next() % 500 + 800))
                            xs.Next();

                        long body = fileLen - 64;
                        if (body >= (long)(xs.Next() % 500 + 800))
                            body = xs.Next() % 500 + 800;
                        bodySize = (uint)body;
                    }

                    byte[] ks1 = new byte[bodySize];
                    WolfCrypto.AesCtrXCrypt(ks1, roundKey);
                    byte[] ks2 = new byte[fileLen - fnt];
                    WolfCrypto.AesCtrXCrypt(ks2, roundKey);

                    ctx.Overlays.Add((64, ks1));
                    ctx.Overlays.Add((fnt, ks2));
                }
            }

            // Tabelas (nomes + arquivos + diretórios), comprimidas com Huffman + LZ.
            byte[] headBuf;
            if ((flags & 2) != 0)
            {
                headBuf = ReadDecrypted(ctx, fnt, (int)headSize, 0, null);
            }
            else
            {
                byte[] huff = ReadDecrypted(ctx, fnt, checked((int)(fileLen - fnt)), 0, null);
                byte[] lz = WolfCompression.HuffmanDecode(huff);
                headBuf = WolfCompression.LzDecode(lz);
            }

            if (headBuf.Length != headSize)
                throw new InvalidDataException("Tabelas do arquivo inválidas (chave incorreta?).");

            var layout = new Layout(Word: 8, FileHeadSize: 72, DirSize: 32, OffAttr: 8, OffDataAddr: 40,
                OffDataSize: 48, OffPress: 56, OffHuff: 64);

            var entries = BuildEntries(ctx, headBuf, fileTable, dirTable, layout);
            return (ctx, entries);
        }

        private static (Context, List<Entry>) OpenLegacy(FileStream stream, Mode mode)
        {
            long fileLen = stream.Length;
            bool v6 = mode.Format == 6;
            int headLen = v6 ? 48 : 28;
            int word = v6 ? 8 : 4;

            var ctx = new Context
            {
                Stream = stream,
                FileLength = fileLen,
                Format = mode.Format,
                KeyString = mode.Key,
                ArchKey = LegacyKeyCreate(mode.Key),
            };

            byte[] h = new byte[headLen];
            ReadExactAt(stream, 0, h, 0, headLen);
            XorKey(h, 0, h.Length, 0, ctx.ArchKey);

            if (h[0] != (byte)'D' || h[1] != (byte)'X')
                throw new InvalidDataException("Assinatura DX não encontrada (chave incorreta?).");

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(2));
            if (v6 ? version != 6 : version != 5)
                throw new InvalidDataException($"Versão DXA inesperada ({version}).");

            uint headSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            long dataStart, fnt, fileTable, dirTable;
            if (v6)
            {
                dataStart = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8)));
                fnt = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(16)));
                fileTable = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24)));
                dirTable = ToOffset(BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32)));
            }
            else
            {
                dataStart = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
                fnt = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
                fileTable = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16));
                dirTable = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20));
            }

            if (headSize == 0 || fnt < headLen || fnt + headSize > fileLen || dataStart > fileLen)
                throw new InvalidDataException("Cabeçalho DXA inválido.");

            ctx.DataStart = dataStart;
            byte[] headBuf = ReadDecrypted(ctx, fnt, (int)headSize, 0, null);

            Layout layout = v6
                ? new Layout(Word: 8, FileHeadSize: 64, DirSize: 32, OffAttr: 8, OffDataAddr: 40,
                    OffDataSize: 48, OffPress: 56, OffHuff: -1)
                : new Layout(Word: 4, FileHeadSize: 44, DirSize: 16, OffAttr: 4, OffDataAddr: 32,
                    OffDataSize: 36, OffPress: 40, OffHuff: -1);

            var entries = BuildEntries(ctx, headBuf, fileTable, dirTable, layout);
            return (ctx, entries);
        }

        // ------------------------------------------------------------------ Tabelas

        private readonly record struct Layout(int Word, int FileHeadSize, int DirSize, int OffAttr,
            int OffDataAddr, int OffDataSize, int OffPress, int OffHuff);

        private sealed class Walk
        {
            public Context Ctx = null!;
            public byte[] Buf = null!;
            public long FileP;
            public long DirP;
            public Layout L;
            public Encoding Enc = null!;
            public List<Entry> Result = new();
        }

        private static List<Entry> BuildEntries(Context ctx, byte[] buf, long fileTable, long dirTable, Layout layout)
        {
            if (fileTable < 0 || fileTable >= buf.Length || dirTable < 0 || dirTable >= buf.Length)
                throw new InvalidDataException("Tabelas de arquivos/diretórios fora do intervalo.");

            var walk = new Walk
            {
                Ctx = ctx,
                Buf = buf,
                FileP = fileTable,
                DirP = dirTable,
                L = layout,
                Enc = GetNameEncoding(ctx.CharCode),
            };

            WalkDirectory(walk, 0, new List<string>(), new List<byte[]>(), 0);
            return walk.Result;
        }

        private static void WalkDirectory(Walk w, long dirOffset, List<string> pathParts, List<byte[]> keyParts, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("Hierarquia de diretórios profunda demais.");

            Layout l = w.L;
            byte[] b = w.Buf;
            long d = w.DirP + dirOffset;

            long num = ToIndex(ReadWord(b, d + 2L * l.Word, l.Word));
            long headAddr = ToIndex(ReadWord(b, d + 3L * l.Word, l.Word));
            if (num < 0 || num > b.Length)
                throw new InvalidDataException("Quantidade de arquivos inválida.");

            bool computeKey = w.Ctx.Format == 8 && !w.Ctx.NewCrypt && !w.Ctx.ChaCha && !w.Ctx.NoKey;

            for (long i = 0; i < num; i++)
            {
                long f = w.FileP + headAddr + i * l.FileHeadSize;
                long nameAddr = ToIndex(ReadWord(b, f, l.Word));
                long attr = (long)ReadWord(b, f + l.OffAttr, l.Word);
                byte[] upper = ReadNameBytes(b, nameAddr, upper: true);
                string name = w.Enc.GetString(ReadNameBytes(b, nameAddr, upper: false));

                if ((attr & AttrDirectory) != 0)
                {
                    long child = ToIndex(ReadWord(b, f + l.OffDataAddr, l.Word));
                    var p = new List<string>(pathParts) { name };
                    var k = new List<byte[]>(keyParts) { upper };
                    WalkDirectory(w, child, p, k, depth + 1);
                    continue;
                }

                var entry = new Entry
                {
                    Path = string.Join("/", pathParts.Append(name)),
                    Offset = checked(w.Ctx.DataStart + ToIndex(ReadWord(b, f + l.OffDataAddr, l.Word))),
                    Size = ToIndex(ReadWord(b, f + l.OffDataSize, l.Word)),
                };

                ulong press = ReadWord(b, f + l.OffPress, l.Word);
                entry.PressSize = press == ulong.MaxValue ? -1 : ToIndex(press);
                if (l.OffHuff >= 0)
                {
                    ulong huff = ReadWord(b, f + l.OffHuff, l.Word);
                    entry.HuffSize = huff == ulong.MaxValue ? -1 : ToIndex(huff);
                }

                if (computeKey)
                {
                    // chave = chave-mestra + NOME + diretórios (do mais interno ao mais externo, sem a raiz)
                    var s = new List<byte>(w.Ctx.KeyString);
                    s.AddRange(upper);
                    for (int n = keyParts.Count - 1; n >= 0; n--)
                        s.AddRange(keyParts[n]);
                    if (s.Count > KeyBufferMax)
                        s.RemoveRange(KeyBufferMax, s.Count - KeyBufferMax);
                    entry.Key = KeyCreate(s.ToArray(), s.Count);
                }

                if (entry.Size < 0)
                    throw new InvalidDataException("Tamanho de arquivo inválido.");

                if (entry.Offset < 0 || entry.Offset > w.Ctx.FileLength)
                    throw new InvalidDataException("Endereço de dados fora do arquivo.");

                w.Result.Add(entry);
            }
        }

        // Formato da entrada na tabela de nomes: u16 nº de "packs" de 4 bytes, u16 paridade,
        // nome em MAIÚSCULAS (packs*4 bytes) e depois o nome original (packs*4 bytes).
        private static byte[] ReadNameBytes(byte[] buf, long nameAddr, bool upper)
        {
            if (nameAddr < 0 || nameAddr + 4 > buf.Length)
                throw new InvalidDataException("Endereço de nome inválido.");

            long packs = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan((int)nameAddr));
            long start = nameAddr + 4 + (upper ? 0 : packs * 4);
            long limit = Math.Min(buf.Length, start + packs * 4);
            if (start > limit)
                throw new InvalidDataException("Nome fora da tabela.");

            long end = start;
            while (end < limit && buf[end] != 0)
                end++;

            return buf.AsSpan((int)start, (int)(end - start)).ToArray();
        }

        private static Encoding GetNameEncoding(uint charCode)
        {
            if (charCode == 65001)
                return new UTF8Encoding(false);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }

        private static ulong ReadWord(byte[] b, long offset, int word)
        {
            if (offset < 0 || offset + word > b.Length)
                throw new InvalidDataException("Leitura fora das tabelas do arquivo.");

            if (word == 8)
                return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)offset));

            uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)offset));
            return v == 0xFFFFFFFFu ? ulong.MaxValue : v;
        }

        private static long ToIndex(ulong v)
        {
            if (v == ulong.MaxValue)
                return -1;
            if (v > long.MaxValue)
                throw new InvalidDataException("Valor fora do intervalo.");
            return (long)v;
        }

        private static long ToOffset(ulong v)
        {
            if (v > long.MaxValue)
                throw new InvalidDataException("Endereço inválido.");
            return (long)v;
        }

        // ------------------------------------------------------------------ Leitura / decifragem

        private static void ReadExactAt(FileStream stream, long offset, byte[] buf, int index, int count)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buf, index + total, count - total);
                if (n <= 0)
                    throw new EndOfStreamException("Fim inesperado do arquivo Wolf.");
                total += n;
            }
        }

        // Lê bytes do arquivo aplicando as camadas que valem para o arquivo inteiro (NewCrypt).
        private static void ReadRaw(Context ctx, long offset, byte[] buf, int index, int count)
        {
            ReadExactAt(ctx.Stream, offset, buf, index, count);
            if (!ctx.NewCrypt)
                return;

            // Camada 1: XOR com a chave "other" nos bytes [64, tamanho - 64).
            long lo = Math.Max(offset, 64);
            long hi = Math.Min(offset + count, ctx.FileLength - 64);
            if (lo < hi)
                WolfCrypto.WolfCryptXor(ctx.OtherKey!, ctx.OtherMod, buf, index + (int)(lo - offset),
                    (int)(hi - lo), lo, ctx.CryptVersion);

            // Camada 2: AES-CTR (início dos dados e região do cabeçalho).
            foreach ((long start, byte[] ks) in ctx.Overlays)
            {
                long a = Math.Max(offset, start);
                long z = Math.Min(offset + count, start + ks.Length);
                for (long p = a; p < z; p++)
                    buf[index + (int)(p - offset)] ^= ks[p - start];
            }
        }

        // Cifra por posição aplicada depois (KeyConv do DXArchive).
        private static void Decrypt(Context ctx, byte[] buf, int index, int count, long position, byte[]? fileKey)
        {
            if (ctx.NoKey || count <= 0)
                return;

            if (ctx.NewCrypt)
            {
                WolfCrypto.WolfCryptXor(ctx.SpecialKey!, ctx.SpecialMod, buf, index, count, position, ctx.CryptVersion);
                return;
            }

            if (ctx.ChaCha)
            {
                WolfCrypto.ChaCha20Xor(ctx.ChaChaKey, ctx.ChaChaNonce, buf, index, count, (uint)position);
                return;
            }

            XorKey(buf, index, count, position, fileKey ?? ctx.ArchKey);
        }

        private static void XorKey(byte[] buf, int index, int count, long position, byte[] key)
        {
            int j = (int)(position % key.Length);
            for (int i = 0; i < count; i++)
            {
                buf[index + i] ^= key[j];
                if (++j == key.Length) j = 0;
            }
        }

        private static byte[] ReadDecrypted(Context ctx, long offset, int count, long keyPosition, byte[]? fileKey)
        {
            if (count < 0 || offset < 0 || offset + count > ctx.FileLength)
                throw new InvalidDataException("Região de dados fora do arquivo.");

            byte[] buf = new byte[count];
            ReadRaw(ctx, offset, buf, 0, count);
            Decrypt(ctx, buf, 0, count, keyPosition, fileKey);
            return buf;
        }

        // ------------------------------------------------------------------ Extração

        private static bool IsProtectedName(string path)
        {
            string lower = path.ToLowerInvariant();
            return AntiUnpackFiles.Any(n => lower.EndsWith(n, StringComparison.Ordinal));
        }

        private void ExtractEntry(Entry entry, string destination)
        {
            Context ctx = _ctx!;
            bool raw = entry.PressSize < 0 && entry.HuffSize < 0;

            if (raw && !IsProtectedName(entry.Path))
            {
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                if (entry.Size == 0)
                    return;

                long written = 0;
                byte[] chunk = new byte[(int)Math.Min(entry.Size, ChunkSize)];
                while (written < entry.Size)
                {
                    int move = (int)Math.Min(entry.Size - written, ChunkSize);
                    ReadRaw(ctx, entry.Offset + written, chunk, 0, move);
                    Decrypt(ctx, chunk, 0, move, entry.Size + written, entry.Key);
                    output.Write(chunk, 0, move);
                    written += move;
                }

                return;
            }

            byte[] data = ReadEntryData(ctx, entry);

            if (IsProtectedName(entry.Path) && data.Length >= AntiUnpackMarker.Length
                && data.AsSpan(0, AntiUnpackMarker.Length).SequenceEqual(AntiUnpackMarker))
            {
                data = data.AsSpan(AntiUnpackMarker.Length).ToArray();
            }

            File.WriteAllBytes(destination, data);
        }

        private static byte[] ReadEntryData(Context ctx, Entry e)
        {
            if (e.Size == 0)
                return Array.Empty<byte>();

            // Tamanho dos dados "internos" que passam (ou não) pelo Huffman: o resultado do LZ,
            // se houver LZ; senão os próprios dados.
            long inner = e.PressSize >= 0 ? e.PressSize : e.Size;
            byte[] data;

            if (e.HuffSize >= 0)
            {
                byte[] huffBlob = ReadDecrypted(ctx, e.Offset, checked((int)e.HuffSize), e.Size, e.Key);
                byte[] h = WolfCompression.HuffmanDecode(huffBlob);

                long edge = (long)ctx.HuffmanKB * 1024;
                if (ctx.HuffmanKB != 0xFF && inner > edge * 2)
                {
                    // Huffman cobre só os primeiros e os últimos "edge" bytes; o meio está sem Huffman.
                    if (h.Length < edge * 2)
                        throw new InvalidDataException("Bloco Huffman menor que o esperado.");

                    data = new byte[inner];
                    Buffer.BlockCopy(h, 0, data, 0, (int)edge);
                    Buffer.BlockCopy(h, (int)edge, data, (int)(inner - edge), (int)edge);

                    int middle = checked((int)(inner - edge * 2));
                    byte[] mid = ReadDecrypted(ctx, e.Offset + e.HuffSize, middle, e.Size + e.HuffSize, e.Key);
                    Buffer.BlockCopy(mid, 0, data, (int)edge, middle);
                }
                else
                {
                    data = h;
                }
            }
            else
            {
                data = ReadDecrypted(ctx, e.Offset, checked((int)inner), e.Size, e.Key);
            }

            if (e.PressSize >= 0)
                data = WolfCompression.LzDecode(data);

            return data;
        }

        private static string SafeOutputPath(string root, string relative)
        {
            string normalized = relative.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("/", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                || normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
            {
                throw new InvalidDataException($"Caminho inseguro ou inválido dentro do arquivo Wolf: {relative}");
            }

            string fullRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O caminho de saída escapa da pasta de destino: {relative}");

            return fullPath;
        }

        // ------------------------------------------------------------------ Chaves

        internal static byte[] TruncateAtNul(byte[] bytes)
        {
            int n = Array.IndexOf(bytes, (byte)0);
            return n < 0 ? bytes : bytes[..n];
        }

        private static byte[] TerminateWithNul(byte[] bytes) => bytes.Concat(new byte[] { 0 }).ToArray();

        private static readonly byte[] DefaultKeyString = Encoding.ASCII.GetBytes("DXLIBARC");

        // DXArchive::KeyCreate (v8): 7 bytes a partir de dois CRC32 (bytes pares e ímpares).
        internal static byte[] KeyCreate(byte[] source, int sourceBytes)
        {
            byte[] src = source;
            if (sourceBytes < 4)
            {
                src = source.AsSpan(0, sourceBytes).ToArray().Concat(DefaultKeyString).ToArray();
                sourceBytes = src.Length;
            }

            var even = new List<byte>();
            var odd = new List<byte>();
            for (int i = 0; i < sourceBytes; i += 2) even.Add(src[i]);
            for (int i = 1; i < sourceBytes; i += 2) odd.Add(src[i]);

            uint c0 = Crc32(even.ToArray());
            uint c1 = Crc32(odd.ToArray());
            return new[]
            {
                (byte)(c0 >> 0), (byte)(c0 >> 8), (byte)(c0 >> 16), (byte)(c0 >> 24),
                (byte)(c1 >> 0), (byte)(c1 >> 8), (byte)(c1 >> 16),
            };
        }

        // DXArchive_VER5/VER6::KeyCreate: 12 bytes (a chave do modo já tem 12 bytes) + transformações fixas.
        internal static byte[] LegacyKeyCreate(byte[] source)
        {
            byte[] k = new byte[12];
            byte[] s = TruncateAtNul(source);
            int len = s.Length;
            if (len == 0)
            {
                // Chaves dos modos 2.x não têm NUL no fim e usam exatamente 12 bytes.
                s = source;
                len = s.Length;
            }

            if (len >= 12)
            {
                Array.Copy(s, k, 12);
            }
            else
            {
                int i;
                for (i = 0; i + len <= 12; i += len)
                    Array.Copy(s, 0, k, i, len);
                if (i < 12)
                    Array.Copy(s, 0, k, i, 12 - i);
            }

            static byte Swap4(byte v) => (byte)((v >> 4) | (v << 4));

            k[0] = (byte)~k[0];
            k[1] = Swap4(k[1]);
            k[2] = (byte)(k[2] ^ 0x8A);
            k[3] = (byte)~Swap4(k[3]);
            k[4] = (byte)~k[4];
            k[5] = (byte)(k[5] ^ 0xAC);
            k[6] = (byte)~k[6];
            k[7] = (byte)~(byte)((k[7] >> 3) | (k[7] << 5));
            k[8] = (byte)((k[8] >> 5) | (k[8] << 3));
            k[9] = (byte)(k[9] ^ 0x7F);
            k[10] = (byte)(Swap4(k[10]) ^ 0xD6);
            k[11] = (byte)(k[11] ^ 0xCC);
            return k;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                t[i] = c;
            }

            return t;
        }

        internal static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    // ---------------------------------------------------------------------- Compressão

    internal static class WolfCompression
    {
        // Huffman_Decode (DxLib): cabeçalho com tabela de pesos + fluxo de bits (LSB primeiro).
        internal static byte[] HuffmanDecode(byte[] src)
        {
            int bytePos = 0;
            int bitPos = 0;

            ulong ReadBits(int n)
            {
                ulong result = 0;
                for (int i = 0; i < n; i++)
                {
                    if (bytePos >= src.Length)
                        throw new InvalidDataException("Fluxo Huffman truncado.");
                    result |= (ulong)((src[bytePos] >> (7 - bitPos)) & 1) << (n - 1 - i);
                    if (++bitPos == 8)
                    {
                        bytePos++;
                        bitPos = 0;
                    }
                }

                return result;
            }

            int sizeBits = (int)ReadBits(6) + 1;
            ulong originalSize = ReadBits(sizeBits);
            int pressBits = (int)ReadBits(6) + 1;
            ReadBits(pressBits); // tamanho comprimido (não usado na decodificação)

            var weight = new ushort[256];
            {
                int bitNum = ((int)ReadBits(3) + 1) * 2;
                ReadBits(1);
                weight[0] = (ushort)ReadBits(bitNum);
                for (int i = 1; i < 256; i++)
                {
                    bitNum = ((int)ReadBits(3) + 1) * 2;
                    ulong minus = ReadBits(1);
                    ushort save = (ushort)ReadBits(bitNum);
                    weight[i] = minus == 1 ? (ushort)(weight[i - 1] - save) : (ushort)(weight[i - 1] + save);
                }
            }

            int headBytes = bytePos + (bitPos != 0 ? 1 : 0);
            if (originalSize > 0x7FFFFFC7UL)
                throw new InvalidDataException("Tamanho Huffman inválido.");

            // Monta a árvore exatamente como o compressor (desempate por ordem de índice).
            const int Total = 256 + 255;
            var nodeWeight = new ulong[Total];
            var parent = new int[Total];
            var child0 = new int[Total];
            var child1 = new int[Total];
            for (int i = 0; i < Total; i++)
            {
                nodeWeight[i] = i < 256 ? (ulong)weight[i] : 0UL;
                parent[i] = -1;
                child0[i] = -1;
                child1[i] = -1;
            }

            int dataNum = 256;
            int nodeNum = 256;
            while (dataNum > 1)
            {
                int min1 = -1, min2 = -1;
                int nodeIndex = 0;
                for (int i = 0; i < dataNum; nodeIndex++)
                {
                    if (parent[nodeIndex] != -1) continue;
                    i++;

                    if (min1 == -1 || nodeWeight[min1] > nodeWeight[nodeIndex])
                    {
                        min2 = min1;
                        min1 = nodeIndex;
                    }
                    else if (min2 == -1 || nodeWeight[min2] > nodeWeight[nodeIndex])
                    {
                        min2 = nodeIndex;
                    }
                }

                nodeWeight[nodeNum] = nodeWeight[min1] + nodeWeight[min2];
                child0[nodeNum] = min1;
                child1[nodeNum] = min2;
                parent[min1] = nodeNum;
                parent[min2] = nodeNum;
                nodeNum++;
                dataNum--;
            }

            byte[] dest = new byte[(int)originalSize];
            int pos = headBytes;
            int bit = 0;
            for (int o = 0; o < dest.Length; o++)
            {
                int node = 510;
                while (node > 255)
                {
                    if (pos >= src.Length)
                        throw new InvalidDataException("Fluxo Huffman truncado.");
                    int b = (src[pos] >> bit) & 1;
                    if (++bit == 8)
                    {
                        pos++;
                        bit = 0;
                    }

                    node = b == 0 ? child0[node] : child1[node];
                }

                dest[o] = (byte)node;
            }

            return dest;
        }

        // DXArchive::Decode (LZ): u32 tamanho final, u32 tamanho comprimido (com cabeçalho), u8 keycode.
        internal static byte[] LzDecode(byte[] src)
        {
            if (src.Length < 9)
                throw new InvalidDataException("Dados LZ truncados.");

            uint destSize = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(0));
            long srcSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(4)) - 9;
            byte keycode = src[8];
            if (destSize > 0x7FFFFFC7u || srcSize < 0 || 9 + srcSize > src.Length)
                throw new InvalidDataException("Cabeçalho LZ inválido.");

            byte[] dest = new byte[destSize];
            int sp = 9;
            int dp = 0;
            long remain = srcSize;

            while (remain > 0)
            {
                byte c = src[sp];
                if (c != keycode)
                {
                    if (dp >= dest.Length) throw new InvalidDataException("Saída LZ excedida.");
                    dest[dp++] = c;
                    sp++;
                    remain--;
                    continue;
                }

                if (src[sp + 1] == keycode)
                {
                    if (dp >= dest.Length) throw new InvalidDataException("Saída LZ excedida.");
                    dest[dp++] = keycode;
                    sp += 2;
                    remain -= 2;
                    continue;
                }

                uint code = src[sp + 1];
                if (code > keycode) code--;
                sp += 2;
                remain -= 2;

                uint conbo = code >> 3;
                if ((code & (1u << 2)) != 0)
                {
                    conbo |= (uint)src[sp] << 5;
                    sp++;
                    remain--;
                }

                conbo += 4; // MIN_COMPRESS

                uint index;
                switch (code & 0x3)
                {
                    case 0:
                        index = src[sp];
                        sp++;
                        remain--;
                        break;
                    case 1:
                        index = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(sp));
                        sp += 2;
                        remain -= 2;
                        break;
                    case 2:
                        index = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(sp)) | (src[sp + 2] << 16));
                        sp += 3;
                        remain -= 3;
                        break;
                    default:
                        throw new InvalidDataException("Código LZ inválido.");
                }

                index++;
                if (index > dp || dp + conbo > dest.Length)
                    throw new InvalidDataException("Referência LZ inválida.");

                // Cópia byte a byte: equivalente ao memcpy em blocos do original (sobreposição permitida).
                int from = dp - (int)index;
                for (uint n = 0; n < conbo; n++)
                    dest[dp++] = dest[from++];
            }

            return dest;
        }
    }

    // ---------------------------------------------------------------------- Criptografia

    internal static class WolfCrypto
    {
        internal static bool IsV35(ushort cv) => (cv >= 0x15E && cv < 0x3E8) || cv >= 0x3FC;

        // rand()/srand() do MSVC
        internal sealed class MsvcRand
        {
            private uint _state = 1;
            public void Seed(uint seed) => _state = seed;

            public int Next()
            {
                _state = unchecked(_state * 214013u + 2531011u);
                return (int)((_state >> 16) & 0x7FFF);
            }
        }

        internal sealed class XorShift32
        {
            private uint _state;

            // xorshift32(seed) do original: define o estado e já avança um passo.
            public XorShift32(uint seed)
            {
                _state = seed;
                Next();
            }

            public uint Next()
            {
                _state ^= _state << 0xB;
                _state ^= _state >> 0x13;
                _state ^= _state << 0x7;
                return _state;
            }
        }

        private static byte RotR8(byte v, int n) => (byte)((v >> n) | (v << (8 - n)));

        internal static byte[] BuildModdedKey(byte[] key)
        {
            var m = new byte[512];
            for (int i = 0; i < 512; i++)
                m[i] = (byte)(key[i % 256] ^ (7 * i));
            return m;
        }

        // wolfCrypt(): XOR com fluxo derivado da posição absoluta no arquivo.
        internal static void WolfCryptXor(byte[] key, byte[]? modded, byte[] buf, int index, int count,
            long start, ushort cv)
        {
            if (IsV35(cv))
            {
                byte[] m = modded ?? BuildModdedKey(key);
                for (int i = 0; i < count; i++)
                {
                    long p = start + i;
                    buf[index + i] ^= (byte)(m[(int)(p & 0xFF)] ^ m[(int)((p >> 8) & 0xFF) + 256]);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    long p = start + i;
                    buf[index + i] ^= (byte)(key[(int)(p & 0xFF)] ^ key[(int)((p >> 8) & 0xFF) + 256]
                                             ^ key[(int)((p >> 16) & 0xFF) + 512]);
                }
            }
        }

        private static byte[] CalcSalt(byte[] str)
        {
            var salt = new byte[128];
            int len = str.Length;
            if (len == 0)
                throw new InvalidDataException("Chave vazia.");

            for (int i = 0; i < 128; i++)
                salt[i] = (byte)((i / len) + (sbyte)str[i % len]);
            return salt;
        }

        // initWolfCrypt(): gera a chave de 768 bytes. other=true aplica a "salgadura" extra.
        internal static byte[] InitWolfCrypt(ushort cv, byte[] pw, bool other, byte[]? keyString)
        {
            var key = new byte[768];
            var fac = new byte[3];

            byte s0 = pw[2];
            byte s1 = pw[5];
            byte s2 = pw[12];
            byte s3 = 0;

            if (!other)
            {
                byte len = (byte)(pw[11] / 3);
                for (byte i = 0; i < len; i++)
                    s3 = (byte)(i ^ RotR8((byte)(s3 ^ pw[i % 15]), 3));
            }
            else
            {
                byte len = (byte)(pw[8] / 4);
                for (byte i = 0; i < len; i++)
                    s3 = (byte)(i ^ RotR8((byte)(s3 ^ pw[i % 15]), 2));
            }

            var rng = new MsvcRand();
            rng.Seed((uint)(s0 * s1 + s2 + s3));

            fac[s3 % 3] = (byte)(rng.Next() % 256);

            if (!other && IsV35(cv))
                fac[1] = (byte)(rng.Next() % 0xFB);

            for (int i = 0; i < 256; i++)
            {
                short rn = (short)(rng.Next() & 0xFFFF);
                key[i] = (byte)(fac[0] ^ (rng.Next() & 0xFF));
                key[i + 256] = (byte)(fac[1] ^ (rn >> 8));
                key[i + 512] = (byte)(fac[2] ^ rn);
            }

            if (!other)
                return key;

            byte[] salt = cv == 0x15E ? CalcSalt(Encoding.ASCII.GetBytes("958")) : CalcSalt(keyString!);
            int modFactor = 7;

            if (IsV35(cv))
            {
                s3 += 0x22;
                modFactor = 16;
            }

            for (int i = 0; i < 3; i++)
            {
                int t = s3;

                for (int j = 0; j < 256; j++)
                {
                    bool skip = false;

                    byte curS = salt[j & 0x7F];
                    byte curS2 = salt[(j + i) % 0x80];
                    byte curK = key[i * 256 + j];
                    byte sXk = (byte)(curS ^ curK);

                    byte round = (byte)((curS2 | (curS << 8)) % modFactor);
                    byte newK = sXk;

                    switch (round)
                    {
                        case 1:
                            if (curS2 % 0xB == 0)
                                newK = curK;
                            break;
                        case 2:
                            if (curS % 0x1D == 0)
                                newK = (byte)~sXk;
                            break;
                        case 3:
                            if ((round + j) % 0x25 == 0)
                                newK = (byte)(curS2 ^ sXk);
                            break;
                        case 4:
                            if ((curS + curS2) % 97 == 0)
                                newK = (byte)(curS + sXk);
                            break;
                        case 5:
                            if ((j * round) % 0x7B == 0)
                                newK = (byte)(sXk ^ t);
                            break;
                        case 6:
                            if (curS == 0xFF && curS2 == 0)
                            {
                                newK = 0;
                                skip = true;
                            }

                            break;
                        case 7:
                            if (cv < 0x154 || (cv > 0x3E8 && cv < 0x3FC))
                                break;
                            if (((round + j) % 0x33) == 0 || cv >= 0x3FC)
                                newK ^= curS;
                            break;
                        case 8:
                            if (cv < 0x154 || (cv > 0x3E8 && cv < 0x3FC))
                                break;
                            if ((curS % 0x1D) == 0 || cv >= 0x3FC)
                                newK ^= curS;
                            break;
                    }

                    if (((j + i) % (curS % 5 + 1)) == 0)
                        newK = (byte)(newK ^ t);
                    else if (skip)
                        newK = (byte)~sXk;

                    key[i * 256 + j] = newK;
                    t += i;
                }
            }

            return key;
        }

        // cryptAddresses(): decifra in-place os endereços do cabeçalho de 64 bytes.
        internal static void CryptAddresses(byte[] head, byte[] pwd, ushort cv)
        {
            var rng = new MsvcRand();

            void Xor16(int byteOffset, int idx)
            {
                int o = byteOffset + idx * 2;
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(o));
                v ^= (ushort)(rng.Next() & 0xFFFF);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(o), v);
            }

            if (IsV35(cv))
            {
                rng.Seed((uint)(0xC + pwd[9] * pwd[10] + pwd[3]));

                int off = 8;
                for (int i = 0; i < 2; i++)
                {
                    for (int j = 3; j >= 0; j--)
                        Xor16(off, j);
                    off += 8;
                }

                ulong r0 = (ulong)rng.Next() << 17;
                ulong r1 = (ulong)rng.Next() << 31;
                uint v0 = (uint)(r0 & 0xFFFFFFFF) | (uint)(r1 & 0xFFFFFFFF) | (uint)rng.Next();
                uint v1 = (uint)(r0 >> 32) | (uint)(r1 >> 32);

                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(off),
                    BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(off)) ^ v0);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(off + 4),
                    BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(off + 4)) ^ v1);
                off += 8;

                for (int i = 3; i >= 0; i--)
                    Xor16(off, i);
            }
            else
            {
                rng.Seed((uint)(pwd[0] + pwd[7] * pwd[12]));

                int off = 8;
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 3; j >= 0; j--)
                        Xor16(off, j);
                    off += 8;
                }
            }
        }

        // ------------------------------------------------------------ ChaCha20 (variante do Wolf)

        internal static void ChaCha20Xor(byte[] key, byte[] nonce, byte[] buf, int index, int count, uint startPos)
        {
            var state = new uint[16];
            var ks = new uint[16];
            var ks8 = new byte[64];

            byte[] magic = Encoding.ASCII.GetBytes("expand 32-byte k");
            for (int i = 0; i < 4; i++) state[i] = BinaryPrimitives.ReadUInt32LittleEndian(magic.AsSpan(i * 4));
            for (int i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
            state[12] = 1;
            for (int i = 0; i < 3; i++) state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(i * 4));

            state[12] += startPos / 64;
            int offset = (int)(startPos % 64);
            int position = 0;

            while (position < count)
            {
                int steps = Math.Min(64 - offset, count - position);
                ChaChaBlock(state, ks);
                for (int i = 0; i < 16; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(ks8.AsSpan(i * 4), ks[i]);

                for (int i = 0; i < steps; i++)
                    buf[index + position + i] ^= ks8[offset + i];

                position += steps;
                offset = 0;
            }
        }

        private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

        private static void Quarter(uint[] x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
        }

        private static void ChaChaBlock(uint[] state, uint[] ks)
        {
            Array.Copy(state, ks, 16);
            for (int i = 0; i < 10; i++)
            {
                Quarter(ks, 0, 4, 8, 12);
                Quarter(ks, 1, 5, 9, 13);
                Quarter(ks, 2, 6, 10, 14);
                Quarter(ks, 3, 7, 11, 15);
                Quarter(ks, 0, 5, 10, 15);
                Quarter(ks, 1, 6, 11, 12);
                Quarter(ks, 2, 7, 8, 13);
                Quarter(ks, 3, 4, 9, 14);
            }

            for (int i = 0; i < 16; i++)
                ks[i] += state[i];

            state[12]++;
            if (state[12] == 0)
                state[13]++;
        }

        // ------------------------------------------------------------ AES-128 CTR (variante do Wolf)

        private static readonly byte[] SBox =
        {
            0x63, 0x7C, 0x77, 0x7B, 0xF2, 0x6B, 0x6F, 0xC5, 0x30, 0x01, 0x67, 0x2B, 0xFE, 0xD7, 0xAB, 0x76,
            0xCA, 0x82, 0xC9, 0x7D, 0xFA, 0x59, 0x47, 0xF0, 0xAD, 0xD4, 0xA2, 0xAF, 0x9C, 0xA4, 0x72, 0xC0,
            0xB7, 0xFD, 0x93, 0x26, 0x36, 0x3F, 0xF7, 0xCC, 0x34, 0xA5, 0xE5, 0xF1, 0x71, 0xD8, 0x31, 0x15,
            0x04, 0xC7, 0x23, 0xC3, 0x18, 0x96, 0x05, 0x9A, 0x07, 0x12, 0x80, 0xE2, 0xEB, 0x27, 0xB2, 0x75,
            0x09, 0x83, 0x2C, 0x1A, 0x1B, 0x6E, 0x5A, 0xA0, 0x52, 0x3B, 0xD6, 0xB3, 0x29, 0xE3, 0x2F, 0x84,
            0x53, 0xD1, 0x00, 0xED, 0x20, 0xFC, 0xB1, 0x5B, 0x6A, 0xCB, 0xBE, 0x39, 0x4A, 0x4C, 0x58, 0xCF,
            0xD0, 0xEF, 0xAA, 0xFB, 0x43, 0x4D, 0x33, 0x85, 0x45, 0xF9, 0x02, 0x7F, 0x50, 0x3C, 0x9F, 0xA8,
            0x51, 0xA3, 0x40, 0x8F, 0x92, 0x9D, 0x38, 0xF5, 0xBC, 0xB6, 0xDA, 0x21, 0x10, 0xFF, 0xF3, 0xD2,
            0xCD, 0x0C, 0x13, 0xEC, 0x5F, 0x97, 0x44, 0x17, 0xC4, 0xA7, 0x7E, 0x3D, 0x64, 0x5D, 0x19, 0x73,
            0x60, 0x81, 0x4F, 0xDC, 0x22, 0x2A, 0x90, 0x88, 0x46, 0xEE, 0xB8, 0x14, 0xDE, 0x5E, 0x0B, 0xDB,
            0xE0, 0x32, 0x3A, 0x0A, 0x49, 0x06, 0x24, 0x5C, 0xC2, 0xD3, 0xAC, 0x62, 0x91, 0x95, 0xE4, 0x79,
            0xE7, 0xC8, 0x37, 0x6D, 0x8D, 0xD5, 0x4E, 0xA9, 0x6C, 0x56, 0xF4, 0xEA, 0x65, 0x7A, 0xAE, 0x08,
            0xBA, 0x78, 0x25, 0x2E, 0x1C, 0xA6, 0xB4, 0xC6, 0xE8, 0xDD, 0x74, 0x1F, 0x4B, 0xBD, 0x8B, 0x8A,
            0x70, 0x3E, 0xB5, 0x66, 0x48, 0x03, 0xF6, 0x0E, 0x61, 0x35, 0x57, 0xB9, 0x86, 0xC1, 0x1D, 0x9E,
            0xE1, 0xF8, 0x98, 0x11, 0x69, 0xD9, 0x8E, 0x94, 0x9B, 0x1E, 0x87, 0xE9, 0xCE, 0x55, 0x28, 0xDF,
            0x8C, 0xA1, 0x89, 0x0D, 0xBF, 0xE6, 0x42, 0x68, 0x41, 0x99, 0x2D, 0x0F, 0xB0, 0x54, 0xBB, 0x16,
        };

        private static readonly byte[] Rcon = { 0x8D, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36 };

        private const int KeyExpSize = 176;
        private const int BlockLen = 16;
        private const int PwSize = 15;

        internal static byte[] InitAes128(byte[] pwd, ushort cv)
        {
            byte[] pro = new byte[4]; // sem chave Pro nas versões suportadas
            byte[] key = new byte[16];
            byte[] iv = new byte[16];

            if (IsV35(cv))
            {
                for (uint i = 0; i < PwSize; i++)
                {
                    byte proElem = pro[i % 4];
                    byte idxKey = (byte)(((i * (uint)(proElem % 5 + 7)) ^ (uint)(3 * pwd[i])) % PwSize);
                    byte idxIv = (byte)(((i * (uint)((pro[(i + 1) % 4] % 7) + 0xB)) ^ (uint)(5 * pwd[(i + 3) % 15])) % PwSize);

                    key[i] ^= (byte)(((i ^ proElem) + ((uint)pwd[idxKey] << (int)(i % 3))) % 0xFB);
                    iv[i] ^= (byte)(((uint)(pwd[idxIv] >> (int)(i % 2)) + ((i * i) ^ pro[(i + 2) % 4])) % 0xF6);

                    key[PwSize] ^= (byte)((7 * (pwd[i] + ((i + 1) ^ proElem))) % 0xFD);
                    ushort t = (ushort)(pwd[i] - ((i * 2) ^ pro[(i + 2) % 4]));
                    iv[PwSize] ^= (byte)((11 * t) % 0x100);
                }
            }
            else if (cv == 0x3F2)
            {
                for (uint i = 0; i < PwSize; i++)
                {
                    key[i] ^= (byte)((pwd[(i * 7) % 0xF] + pro[i & 3]) * i * i);
                    iv[i] ^= (byte)((pwd[(i * 11) % 0xF] + pro[(i + 2) % 4]) - i * i);

                    key[PwSize] ^= (byte)((i * 3) + pwd[i] + pro[i & 3]);
                    iv[PwSize] ^= (byte)((i * 5) + pwd[i] + pro[(i + 2) % 4]);
                }
            }
            else
            {
                for (uint i = 0; i < PwSize; i++)
                {
                    key[i] ^= (byte)(pwd[(i * 7) % 0xF] + i * i);
                    iv[i] ^= (byte)(pwd[(i * 11) % 0xF] - i * i);

                    key[PwSize] ^= (byte)(pwd[i] + (i * 3));
                    iv[PwSize] ^= (byte)(pwd[i] + (i * 5));
                }
            }

            key[0] ^= pro[0];
            iv[10] ^= pro[0];
            key[4] ^= pro[1];
            iv[1] ^= pro[1];
            key[8] ^= pro[2];
            iv[4] ^= pro[2];
            key[12] ^= pro[3];
            iv[7] ^= pro[3];

            byte[] roundKey = new byte[KeyExpSize + 16];
            KeyExpansion(roundKey, key);
            Array.Copy(iv, 0, roundKey, KeyExpSize, 16);
            return roundKey;
        }

        private static void KeyExpansion(byte[] rk, byte[] key)
        {
            const int Nk = 4, Nb = 4, Nr = 10;
            byte[] tempa = new byte[4];

            for (int i = 0; i < Nk; i++)
            {
                rk[i * 4 + 0] = key[i * 4 + 0];
                rk[i * 4 + 1] = key[i * 4 + 1];
                rk[i * 4 + 2] = key[i * 4 + 2];
                rk[i * 4 + 3] = key[i * 4 + 3];
            }

            for (int i = Nk; i < Nb * (Nr + 1); i++)
            {
                int k = (i - 1) * 4;
                tempa[0] = rk[k + 0];
                tempa[1] = rk[k + 1];
                tempa[2] = rk[k + 2];
                tempa[3] = rk[k + 3];

                if (i % Nk == 0)
                {
                    byte u = tempa[0];
                    tempa[0] = tempa[1];
                    tempa[1] = tempa[2];
                    tempa[2] = tempa[3];
                    tempa[3] = u;

                    // Diferente do AES padrão (versão do Wolf RPG)
                    tempa[0] = (byte)(SBox[tempa[0]] ^ Rcon[i / Nk]);
                    tempa[1] = (byte)(SBox[tempa[1]] >> 4);
                    tempa[2] = (byte)~SBox[tempa[2]];
                    tempa[3] = RotR8(SBox[tempa[3]], 7);
                }

                int j = i * 4;
                k = (i - Nk) * 4;
                rk[j + 0] = (byte)(rk[k + 0] ^ tempa[0]);
                rk[j + 1] = (byte)(rk[k + 1] ^ tempa[1]);
                rk[j + 2] = (byte)(rk[k + 2] ^ tempa[2]);
                rk[j + 3] = (byte)(rk[k + 3] ^ tempa[3]);
            }
        }

        private static byte XTime(byte x) => (byte)((x << 1) ^ (((x >> 7) & 1) * 0x1B));

        private static void AddRoundKey(byte[] s, int round, byte[] rk)
        {
            for (int i = 0; i < 16; i++)
                s[i] ^= rk[round * 16 + i];
        }

        private static void Cipher(byte[] s, byte[] rk)
        {
            AddRoundKey(s, 0, rk);

            for (int round = 1; round < 10; round++)
            {
                SubBytes(s);
                ShiftRows(s);
                MixColumns(s);
                AddRoundKey(s, round, rk);
            }

            SubBytes(s);
            ShiftRows(s);
            AddRoundKey(s, 10, rk);
        }

        private static void SubBytes(byte[] s)
        {
            for (int i = 0; i < 16; i++)
                s[i] = SBox[s[i]];
        }

        private static void ShiftRows(byte[] s)
        {
            byte t = s[1];
            s[1] = s[5]; s[5] = s[9]; s[9] = s[13]; s[13] = t;

            t = s[2]; s[2] = s[10]; s[10] = t;
            t = s[6]; s[6] = s[14]; s[14] = t;

            t = s[3];
            s[3] = s[15]; s[15] = s[11]; s[11] = s[7]; s[7] = t;
        }

        private static void MixColumns(byte[] s)
        {
            for (int i = 0; i < 4; i++)
            {
                int o = i * 4;
                byte t = s[o];
                byte tmp = (byte)(s[o + 1] ^ s[o] ^ s[o + 2] ^ s[o + 3]);

                s[o + 0] ^= (byte)(tmp ^ XTime((byte)(s[o + 1] ^ s[o])));
                s[o + 1] ^= (byte)(tmp ^ XTime((byte)(s[o + 2] ^ s[o + 1])));
                s[o + 2] ^= (byte)(tmp ^ XTime((byte)(s[o + 2] ^ s[o + 3])));
                s[o + 3] ^= (byte)(tmp ^ XTime((byte)(s[o + 3] ^ t)));
            }
        }

        // aesCtrXCrypt(): o IV (últimos 16 bytes de roundKey) é atualizado a cada bloco gerado,
        // então chamadas sucessivas continuam o mesmo fluxo (a sobra do último bloco é descartada).
        internal static void AesCtrXCrypt(byte[] data, byte[] roundKey)
        {
            byte[] state = new byte[BlockLen];
            int bi = BlockLen;

            for (int i = 0; i < data.Length; i++, bi++)
            {
                if (bi == BlockLen)
                {
                    Array.Copy(roundKey, KeyExpSize, state, 0, BlockLen);
                    Cipher(state, roundKey);

                    for (bi = BlockLen - 1; bi >= 0; bi--)
                    {
                        if (roundKey[KeyExpSize + bi] == 0xFF)
                        {
                            roundKey[KeyExpSize + bi] = 0;
                            continue;
                        }

                        roundKey[KeyExpSize + bi]++;
                        break;
                    }

                    bi = 0;
                }

                data[i] ^= state[bi];
            }
        }
    }
}
