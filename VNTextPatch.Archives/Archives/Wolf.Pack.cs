// Wolf.Pack.cs
// Empacotamento (DXArchive v8) para o Wolf RPG. Port do EncodeArchive/DirectoryEncode do UberWolf/DXLib,
// seguindo o mesmo fluxo de EncodeArchiveOneDirectoryWolf (Press = true, Huffman sempre, 0xC KB nas pontas).
// Suporta as versões em formato v8 (2.225 em diante). Não suporta v5/v6 (2.01/2.10/2.20) nem Wolf Pro.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VNTextPatch.Archives
{
    public sealed partial class Wolf
    {
        private const byte PackHuffmanKB = 0xC;
        private const long PackLzMaxFileSize = 10L * 1024 * 1024; // acima disso o original nem tenta LZ
        private const long PackMaxFileSize = 0x7FFFFFC7;

        public void Pack(string pathToPack, string outputPath, string? version, string? encriptionKey)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(pathToPack))
                throw new ArgumentException("A pasta de origem é obrigatória.", nameof(pathToPack));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("O caminho do arquivo Wolf de saída é obrigatório.", nameof(outputPath));

            Mode mode = ResolvePackMode(version);
            ushort cv = mode.CryptVersion;

            string inputRoot = Path.GetFullPath(pathToPack);
            if (!Directory.Exists(inputRoot))
                throw new DirectoryNotFoundException($"A pasta de origem não existe: {inputRoot}");

            string outputFullPath = Path.GetFullPath(outputPath);
            string? outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            // A chave do usuário substitui a embutida; a versão de criptografia continua a do modo escolhido.
            byte[] fullKey = string.IsNullOrEmpty(encriptionKey)
                ? mode.Key
                : TerminateWithNul(Encoding.UTF8.GetBytes(encriptionKey));
            byte[] keyStr = TruncateAtNul(fullKey);
            if (cv == 0x64 && fullKey.Length < 44)
                throw new ArgumentException("A chave ChaCha20 precisa ter 32 bytes de chave + 12 de nonce.", nameof(encriptionKey));

            PackNode root = BuildPackTree(inputRoot, outputFullPath);
            if (root.Children.Count == 0)
                throw new InvalidDataException($"Nenhum arquivo para empacotar em: {inputRoot}");

            Console.WriteLine($"Packing (versão {VersionLabel(mode)})...");

            try
            {
                using var output = new FileStream(outputFullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                WritePackedArchive(output, root, cv, fullKey, keyStr);
            }
            catch
            {
                try { File.Delete(outputFullPath); } catch { /* melhor esforço */ }
                throw;
            }
        }

        // ------------------------------------------------------------------ Versão

        private static Mode ResolvePackMode(string? version)
        {
            List<Mode> packable = Modes.Where(m => m.Format == 8).ToList();
            string available = string.Join(", ", packable.Select(m => $"\"{VersionLabel(m)}\""));

            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException(
                    $"Informe a versão do Wolf RPG para empacotar (version). Valores válidos: {available}. " +
                    "A versão detectada é mostrada ao extrair o arquivo original.", nameof(version));

            string wanted = NormalizeVersion(version);
            Mode? found = packable.FirstOrDefault(m => NormalizeVersion(VersionLabel(m)) == wanted)
                          ?? packable.FirstOrDefault(m => NormalizeVersion(m.Name) == wanted);
            if (found != null)
                return found;

            Mode? legacy = Modes.FirstOrDefault(m => m.Format != 8
                && (NormalizeVersion(VersionLabel(m)) == wanted || NormalizeVersion(m.Name) == wanted));
            if (legacy != null)
                throw new NotSupportedException(
                    $"O Pack não suporta a versão \"{VersionLabel(legacy)}\" (formato DXA v{legacy.Format}). " +
                    $"Valores válidos: {available}.");

            throw new ArgumentException($"Versão desconhecida: \"{version}\". Valores válidos: {available}.", nameof(version));
        }

        // ------------------------------------------------------------------ Árvore de arquivos

        private sealed class PackNode
        {
            public string Name = "";
            public bool IsDir;
            public string FullPath = "";
            public List<PackNode> Children = new();
            public long Create, Access, Write;
        }

        private static PackNode BuildPackTree(string dir, string outputFullPath)
        {
            var node = new PackNode { IsDir = true, FullPath = dir, Name = Path.GetFileName(dir) };
            var infos = new List<FileSystemInfo>();
            infos.AddRange(new DirectoryInfo(dir).GetDirectories());
            infos.AddRange(new DirectoryInfo(dir).GetFiles());

            // Mesma ordem que o NTFS devolve no FindFirstFile (sem diferenciar maiúsculas), com desempate estável.
            foreach (FileSystemInfo info in infos
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(i => i.Name, StringComparer.Ordinal))
            {
                if (info is DirectoryInfo sub)
                {
                    PackNode child = BuildPackTree(sub.FullName, outputFullPath);
                    child.Name = sub.Name;
                    child.Create = sub.CreationTimeUtc.ToFileTimeUtc();
                    child.Access = sub.LastAccessTimeUtc.ToFileTimeUtc();
                    child.Write = sub.LastWriteTimeUtc.ToFileTimeUtc();
                    node.Children.Add(child);
                }
                else
                {
                    if (string.Equals(info.FullName, outputFullPath, StringComparison.OrdinalIgnoreCase))
                        continue; // não empacota o próprio arquivo de saída

                    node.Children.Add(new PackNode
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        Create = info.CreationTimeUtc.ToFileTimeUtc(),
                        Access = info.LastAccessTimeUtc.ToFileTimeUtc(),
                        Write = info.LastWriteTimeUtc.ToFileTimeUtc(),
                    });
                }
            }

            return node;
        }

        // ------------------------------------------------------------------ Tabelas

        private sealed class ByteTable
        {
            public byte[] Buf = new byte[4096];
            public int Length;

            public void Grow(int newLength)
            {
                if (newLength > Buf.Length)
                    Array.Resize(ref Buf, Math.Max(newLength, Buf.Length * 2));
                Length = Math.Max(Length, newLength);
            }

            public void PutU64(long offset, ulong value)
            {
                Grow((int)offset + 8);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(Buf.AsSpan((int)offset), value);
            }

            public ulong GetU64(long offset) =>
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(Buf.AsSpan((int)offset));

            public byte[] ToArray() => Buf.AsSpan(0, Length).ToArray();
        }

        private struct DirInfo
        {
            public ulong DirectoryAddress;
            public ulong ParentDirectoryAddress;
            public ulong FileHeadNum;
            public ulong FileHeadAddress;
        }

        private struct FileRec
        {
            public ulong NameAddress, Attributes, Create, Access, Write;
            public ulong DataAddress, DataSize, PressDataSize, HuffPressDataSize;
        }

        private sealed class PackState
        {
            public Context Ctx = null!;
            public Stream Out = null!;
            public ByteTable Names = new();
            public ByteTable Files = new();
            public ByteTable Dirs = new();
            public long DataSize;          // bytes de dados já gravados (depois do cabeçalho de 64 bytes)
            public byte[] KeyString = Array.Empty<byte>();
            public Encoding Sjis = null!;
        }

        private const ulong None = ulong.MaxValue;

        private static void PutFileRec(ByteTable files, long offset, in FileRec f)
        {
            files.PutU64(offset + 0, f.NameAddress);
            files.PutU64(offset + 8, f.Attributes);
            files.PutU64(offset + 16, f.Create);
            files.PutU64(offset + 24, f.Access);
            files.PutU64(offset + 32, f.Write);
            files.PutU64(offset + 40, f.DataAddress);
            files.PutU64(offset + 48, f.DataSize);
            files.PutU64(offset + 56, f.PressDataSize);
            files.PutU64(offset + 64, f.HuffPressDataSize);
        }

        private static void PutDirRec(ByteTable dirs, long offset, in DirInfo d)
        {
            dirs.PutU64(offset + 0, d.DirectoryAddress);
            dirs.PutU64(offset + 8, d.ParentDirectoryAddress);
            dirs.PutU64(offset + 16, d.FileHeadNum);
            dirs.PutU64(offset + 24, d.FileHeadAddress);
        }

        // AddFileNameData: u16 packs, u16 paridade, nome em MAIÚSCULAS e nome original. Devolve o nome em maiúsculas
        // (usado na chave por arquivo).
        private static byte[] AddName(PackState s, string name)
        {
            byte[] sjis;
            try
            {
                sjis = s.Sjis.GetBytes(name);
            }
            catch (EncoderFallbackException)
            {
                throw new InvalidDataException(
                    $"O nome \"{name}\" tem caracteres que não existem em Shift-JIS, e o Wolf RPG exige esse encoding.");
            }

            ByteTable t = s.Names;
            int off = t.Length;

            if (sjis.Length == 0)
            {
                t.Grow(off + 4);
                return Array.Empty<byte>();
            }

            int packNum = (sjis.Length + 1 + 3) / 4;
            if (packNum > ushort.MaxValue)
                throw new InvalidDataException($"Nome de arquivo longo demais: {name}");

            t.Grow(off + 4 + packNum * 8);
            t.Buf[off] = (byte)packNum;
            t.Buf[off + 1] = (byte)(packNum >> 8);
            Array.Copy(sjis, 0, t.Buf, off + 4 + packNum * 4, sjis.Length);

            byte[] upper = new byte[sjis.Length];
            uint parity = 0;
            for (int i = 0; i < sjis.Length;)
            {
                byte b = sjis[i];
                bool lead = (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC);
                if (!lead || i + 1 >= sjis.Length)
                {
                    byte u = (b >= (byte)'a' && b <= (byte)'z') ? (byte)(b - 'a' + 'A') : b;
                    upper[i] = u;
                    parity += u;
                    i++;
                }
                else
                {
                    upper[i] = b;
                    upper[i + 1] = sjis[i + 1];
                    parity += b;
                    parity += sjis[i + 1];
                    i += 2;
                }
            }

            Array.Copy(upper, 0, t.Buf, off + 4, upper.Length);
            t.Buf[off + 2] = (byte)parity;
            t.Buf[off + 3] = (byte)(parity >> 8);
            return upper;
        }

        private static byte[] PackFileKey(PackState s, byte[] upperName, List<byte[]> dirChain)
        {
            var key = new List<byte>(s.KeyString);
            key.AddRange(upperName);
            for (int i = dirChain.Count - 1; i >= 0; i--)
                key.AddRange(dirChain[i]);
            if (key.Count > KeyBufferMax)
                key.RemoveRange(KeyBufferMax, key.Count - KeyBufferMax);
            return KeyCreate(key.ToArray(), key.Count);
        }

        // ------------------------------------------------------------------ Gravação

        private static void WritePackedArchive(FileStream output, PackNode root, ushort cv, byte[] fullKey, byte[] keyStr)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var ctx = new Context
            {
                Format = 8,
                CryptVersion = cv,
                NewCrypt = (cv >= 331 && cv < 1000) || cv >= 1010,
                ChaCha = cv == 0x64,
                HuffmanKB = PackHuffmanKB,
                CharCode = 932,
            };

            byte[] pwd = new byte[15]; // Head.Reserve: o encoder do UberWolf deixa zerado
            ctx.KeyString = keyStr.Length > KeyStringMax ? keyStr[..KeyStringMax] : keyStr;
            ctx.ArchKey = KeyCreate(ctx.KeyString, ctx.KeyString.Length);

            if (ctx.ChaCha)
            {
                ctx.ChaChaKey = fullKey[..32];
                ctx.ChaChaNonce = fullKey[32..44];
            }

            if (ctx.NewCrypt)
            {
                ctx.SpecialKey = WolfCrypto.InitWolfCrypt(cv, pwd, false, null);
                ctx.SpecialMod = WolfCrypto.IsV35(cv) ? WolfCrypto.BuildModdedKey(ctx.SpecialKey) : null;
            }

            var s = new PackState
            {
                Ctx = ctx,
                Out = output,
                KeyString = ctx.KeyString,
                Sjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            };

            output.Write(new byte[HeadSize8], 0, HeadSize8); // cabeçalho provisório

            // Entrada da raiz (nome vazio) + registro do diretório raiz.
            var rootRec = new FileRec
            {
                NameAddress = (ulong)s.Names.Length,
                Attributes = (ulong)AttrDirectory,
                DataAddress = 0,
                DataSize = 0,
                PressDataSize = None,
                HuffPressDataSize = 0,
            };
            AddName(s, "");
            PutFileRec(s.Files, 0, rootRec);

            var rootDir = new DirInfo
            {
                DirectoryAddress = 0,
                ParentDirectoryAddress = None,
                FileHeadNum = (ulong)root.Children.Count,
                FileHeadAddress = (ulong)s.Files.Length,
            };
            PutDirRec(s.Dirs, 0, rootDir);
            s.Files.Grow(s.Files.Length + 72 * root.Children.Count);

            var chain = new List<byte[]>();
            for (int i = 0; i < root.Children.Count; i++)
                EncodeNode(s, root.Children[i], rootDir, i, chain);

            // Tabelas -> LZ -> Huffman -> chave (posição 0).
            byte[] tables = s.Names.ToArray().Concat(s.Files.ToArray()).Concat(s.Dirs.ToArray()).ToArray();
            byte[] lz = WolfCompression.LzEncode(tables, tables.Length);
            byte[] huff = WolfCompression.HuffmanEncode(lz, lz.Length);
            Decrypt(ctx, huff, 0, huff.Length, 0, null);
            output.Write(huff, 0, huff.Length);

            long fnt = HeadSize8 + s.DataSize;
            long fileLen = output.Length;

            byte[] head = new byte[HeadSize8];
            head[0] = (byte)'D';
            head[1] = (byte)'X';
            head[2] = 8;
            WriteLe(head, 4, (ulong)tables.Length, 4);
            WriteLe(head, 8, HeadSize8, 8);
            WriteLe(head, 16, (ulong)fnt, 8);
            WriteLe(head, 24, (ulong)s.Names.Length, 8);
            WriteLe(head, 32, (ulong)(s.Names.Length + s.Files.Length), 8);
            WriteLe(head, 40, 932, 4);
            WriteLe(head, 44, (ulong)cv << 16, 4);
            head[48] = PackHuffmanKB;
            Array.Copy(pwd, 0, head, 49, 15);

            if (ctx.NewCrypt)
            {
                ctx.FileLength = fileLen;
                SetupNewCrypt(ctx, pwd, keyStr, fileLen, fnt);

                // Camadas sobre o arquivo todo, em blocos (o mesmo XOR da leitura).
                byte[] chunk = new byte[(int)Math.Min(fileLen, ChunkSize)];
                for (long pos = HeadSize8; pos < fileLen; pos += chunk.Length)
                {
                    int n = (int)Math.Min(chunk.Length, fileLen - pos);
                    output.Seek(pos, SeekOrigin.Begin);
                    ReadExactAt(output, pos, chunk, 0, n);
                    ApplyNewCryptLayers(ctx, pos, chunk, 0, n);
                    output.Seek(pos, SeekOrigin.Begin);
                    output.Write(chunk, 0, n);
                }

                WolfCrypto.CryptAddresses(head, pwd, cv);
            }

            output.Seek(0, SeekOrigin.Begin);
            output.Write(head, 0, HeadSize8);
            output.Flush();
        }

        private static void WriteLe(byte[] buf, int offset, ulong value, int bytes)
        {
            for (int i = 0; i < bytes; i++)
                buf[offset + i] = (byte)(value >> (8 * i));
        }

        private static void EncodeNode(PackState s, PackNode node, DirInfo parent, int index, List<byte[]> chain)
        {
            long entryOffset = (long)parent.FileHeadAddress + (long)index * 72;

            if (node.IsDir)
            {
                var rec = new FileRec
                {
                    NameAddress = (ulong)s.Names.Length,
                    Attributes = (ulong)AttrDirectory,
                    Create = (ulong)node.Create,
                    Access = (ulong)node.Access,
                    Write = (ulong)node.Write,
                    DataAddress = (ulong)s.Dirs.Length,
                    DataSize = 0,
                    PressDataSize = None,
                    HuffPressDataSize = None,
                };
                byte[] upper = AddName(s, node.Name);
                PutFileRec(s.Files, entryOffset, rec);

                var dir = new DirInfo
                {
                    DirectoryAddress = (ulong)entryOffset,
                    FileHeadAddress = (ulong)s.Files.Length,
                    ParentDirectoryAddress = parent.DirectoryAddress != None && parent.DirectoryAddress != 0
                        ? s.Files.GetU64((long)parent.DirectoryAddress + 40)
                        : 0,
                    FileHeadNum = (ulong)node.Children.Count,
                };
                PutDirRec(s.Dirs, s.Dirs.Length, dir);
                s.Files.Grow(s.Files.Length + 72 * node.Children.Count);

                if (node.Children.Count == 0)
                    return;

                chain.Add(upper);
                for (int i = 0; i < node.Children.Count; i++)
                    EncodeNode(s, node.Children[i], dir, i, chain);
                chain.RemoveAt(chain.Count - 1);
                return;
            }

            FileInfo fi = new FileInfo(node.FullPath);
            if (fi.Length > PackMaxFileSize)
                throw new NotSupportedException($"Arquivo grande demais para empacotar (limite ~2 GB): {node.FullPath}");

            var file = new FileRec
            {
                NameAddress = (ulong)s.Names.Length,
                Attributes = 0x20, // FILE_ATTRIBUTE_ARCHIVE
                Create = (ulong)node.Create,
                Access = (ulong)node.Access,
                Write = (ulong)node.Write,
                DataAddress = (ulong)s.DataSize,
                DataSize = (ulong)fi.Length,
                PressDataSize = None,
                HuffPressDataSize = None,
            };

            byte[] fileUpper = AddName(s, node.Name);
            byte[]? lKey = (s.Ctx.NewCrypt || s.Ctx.ChaCha) ? null : PackFileKey(s, fileUpper, chain);

            if (fi.Length != 0)
            {
                byte[] data = File.ReadAllBytes(node.FullPath);
                long written = EncodeFileData(s, node.Name, data, ref file, lKey);
                s.DataSize += written;
            }

            PutFileRec(s.Files, entryOffset, file);
        }

        private static bool HasExtension(string name, params string[] exts)
        {
            if (name.Length <= 4) return false;
            string tail = name.Substring(name.Length - 3);
            return exts.Any(e => e.Length == 3 && string.Equals(tail, e, StringComparison.OrdinalIgnoreCase));
        }

        private static long Pad4(long n) => (n + 3) / 4 * 4;

        private static long EncodeFileData(PackState s, string name, byte[] data, ref FileRec file, byte[]? lKey)
        {
            Context ctx = s.Ctx;
            long size = data.Length;
            long K = PackHuffmanKB * 1024L;

            bool alwaysPress = HasExtension(name, "wav", "tga", "bmp");
            bool skipLz = !alwaysPress && name.Length > 4 &&
                          (HasExtension(name, "wav", "jpg", "png", "mpg", "mp3", "mp4", "ogg", "ogv", "ops", "wmv")
                           || string.Equals(name.Substring(name.Length - 4), "jpeg", StringComparison.OrdinalIgnoreCase));

            byte[] inner = data;
            if ((alwaysPress || size < PackLzMaxFileSize) && !skipLz)
            {
                byte[] lz = WolfCompression.LzEncode(data, data.Length);
                // Quase não comprimiu: grava sem LZ.
                if (alwaysPress || (double)lz.Length / (double)size <= 0.90)
                {
                    inner = lz;
                    file.PressDataSize = (ulong)lz.Length;
                }
            }

            long writeSize;
            if (inner.Length <= K * 2)
            {
                // Huffman no conteúdo todo
                byte[] huff = WolfCompression.HuffmanEncode(inner, inner.Length);
                file.HuffPressDataSize = (ulong)huff.Length;
                writeSize = Pad4(huff.Length);
                byte[] buf = new byte[writeSize];
                Array.Copy(huff, buf, huff.Length);
                Decrypt(ctx, buf, 0, buf.Length, (long)file.DataSize, lKey);
                s.Out.Write(buf, 0, buf.Length);
                return writeSize;
            }

            // Huffman só nas pontas (K bytes no começo e K no fim); o meio vai sem Huffman.
            byte[] edges = new byte[K * 2];
            Array.Copy(inner, 0, edges, 0, K);
            Array.Copy(inner, inner.Length - K, edges, K, K);
            byte[] hb = WolfCompression.HuffmanEncode(edges, edges.Length);
            file.HuffPressDataSize = (ulong)hb.Length;

            Decrypt(ctx, hb, 0, hb.Length, (long)file.DataSize, lKey);
            s.Out.Write(hb, 0, hb.Length);

            writeSize = Pad4(hb.Length + inner.Length - K * 2);
            byte[] second = new byte[writeSize - hb.Length];
            // O padding sai dos bytes seguintes do buffer (como no original), não de zeros.
            Array.Copy(inner, K, second, 0, Math.Min(second.Length, inner.Length - K));
            Decrypt(ctx, second, 0, second.Length, (long)file.DataSize + hb.Length, lKey);
            s.Out.Write(second, 0, second.Length);
            return writeSize;
        }
    }

    internal static partial class WolfCompression
    {
        // ------------------------------------------------------------ Huffman_Encode

        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new() { 0 };
            private int _bits;

            public void Write(int bitNum, ulong data)
            {
                for (int i = 0; i < bitNum; i++)
                {
                    _bytes[^1] |= (byte)(((data >> (bitNum - 1 - i)) & 1) << (7 - _bits));
                    if (++_bits == 8)
                    {
                        _bits = 0;
                        _bytes.Add(0);
                    }
                }
            }

            public byte[] ToArray() => _bytes.Take(_bytes.Count - (_bits == 0 ? 1 : 0)).ToArray();
        }

        private static int BitCountOf(ulong data)
        {
            for (int i = 1; i < 64; i++)
                if (data < (1UL << i))
                    return i;
            return 64;
        }

        internal static byte[] HuffmanEncode(byte[] src, int srcSize)
        {
            if (srcSize <= 0)
                throw new ArgumentException("Huffman: dados vazios.", nameof(srcSize));

            const int Total = 256 + 255;
            var weight = new ulong[Total];
            var parent = new int[Total];
            var child0 = new int[Total];
            var child1 = new int[Total];
            var index = new int[Total];
            for (int i = 0; i < Total; i++)
            {
                parent[i] = -1;
                child0[i] = -1;
                child1[i] = -1;
            }

            for (int i = 0; i < srcSize; i++)
                weight[src[i]]++;
            for (int i = 0; i < 256; i++)
                weight[i] = weight[i] * 0xffff / (ulong)srcSize;

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

                    if (min1 == -1 || weight[min1] > weight[nodeIndex])
                    {
                        min2 = min1;
                        min1 = nodeIndex;
                    }
                    else if (min2 == -1 || weight[min2] > weight[nodeIndex])
                    {
                        min2 = nodeIndex;
                    }
                }

                weight[nodeNum] = weight[min1] + weight[min2];
                child0[nodeNum] = min1;
                child1[nodeNum] = min2;
                index[min1] = 0;
                index[min2] = 1;
                parent[min1] = nodeNum;
                parent[min2] = nodeNum;
                nodeNum++;
                dataNum--;
            }

            // Código de cada símbolo, da raiz para a folha.
            var codes = new bool[256][];
            for (int i = 0; i < 256; i++)
            {
                var bits = new List<bool>();
                for (int n = i; parent[n] != -1; n = parent[n])
                    bits.Add(index[n] == 1);
                bits.Reverse();
                codes[i] = bits.ToArray();
            }

            // Fluxo de bits (LSB primeiro em cada byte).
            var press = new List<byte> { 0 };
            int pressBit = 0;
            for (int i = 0; i < srcSize; i++)
            {
                foreach (bool bit in codes[src[i]])
                {
                    if (pressBit == 8)
                    {
                        press.Add(0);
                        pressBit = 0;
                    }

                    if (bit)
                        press[^1] |= (byte)(1 << pressBit);
                    pressBit++;
                }
            }

            ulong pressSize = (ulong)press.Count;

            // Cabeçalho: tamanhos + tabela de pesos (diferenças com sinal).
            var hw = new BitWriter();
            int bitNum = BitCountOf((ulong)srcSize);
            if (bitNum > 0) bitNum--;
            hw.Write(6, (ulong)bitNum);
            hw.Write(bitNum + 1, (ulong)srcSize);
            bitNum = BitCountOf(pressSize);
            hw.Write(6, (ulong)bitNum);
            hw.Write(bitNum + 1, pressSize);

            var saved = new int[256];
            saved[0] = (int)weight[0];
            for (int i = 1; i < 256; i++)
                saved[i] = (int)weight[i] - (int)weight[i - 1];

            for (int i = 0; i < 256; i++)
            {
                bool minus = saved[i] < 0;
                ulong output = (ulong)(minus ? -saved[i] : saved[i]);
                bitNum = (BitCountOf(output) + 1) / 2;
                if (bitNum > 0) bitNum--;
                hw.Write(3, (ulong)bitNum);
                hw.Write(1, minus ? 1UL : 0UL);
                hw.Write((bitNum + 1) * 2, output);
            }

            byte[] header = hw.ToArray();
            byte[] result = new byte[header.Length + press.Count];
            Array.Copy(header, result, header.Length);
            press.CopyTo(result, header.Length);
            return result;
        }

        // ------------------------------------------------------------ DXArchive::Encode (LZ)

        internal static byte[] LzEncode(byte[] src, int srcSizeSigned)
        {
            const int MinCompress = 4;
            const int MaxSearchListNum = 64;
            const int MaxSubListNum = 65536;
            const int MaxCopySize = 0x1fff + MinCompress;
            const int MaxAddressListNum = 1024 * 1024;
            const int MaxPosition = 1 << 24;
            const uint NoAddress = 0xFFFFFFFF;

            uint srcSize = (uint)srcSizeSigned;

            int sublistMax;
            if (srcSize < 100 * 1024) sublistMax = 1;
            else if (srcSize < 3 * 1024 * 1024) sublistMax = MaxSubListNum / 3;
            else sublistMax = MaxSubListNum;

            uint maxListNum = MaxAddressListNum;
            if (maxListNum > srcSize)
            {
                while ((maxListNum >> 1) > 0x100 && (maxListNum >> 1) > srcSize)
                    maxListNum >>= 1;
            }

            uint mask = maxListNum - 1;

            // "Slots" onde fica o ponteiro next: tabela de 16 bits, sublistas (por 3º byte) e nós.
            int nodeBase = 65536 + 256 * sublistMax;
            var firstNext = new int[65536];
            Array.Fill(firstNext, -1);
            var subOf = new int[65536];
            Array.Fill(subOf, -1);
            var subNext = new int[sublistMax][];
            var nodeAddr = new uint[maxListNum];
            Array.Fill(nodeAddr, NoAddress);
            var nodeNext = new int[maxListNum];
            var nodePrev = new int[maxListNum];
            int sublistNum = 0;
            uint listAddp = 0;

            int GetNext(int slot)
            {
                if (slot < 65536) return firstNext[slot];
                if (slot < nodeBase)
                {
                    int s = slot - 65536;
                    return subNext[s >> 8][s & 255];
                }

                return nodeNext[slot - nodeBase];
            }

            void SetNext(int slot, int value)
            {
                if (slot < 65536) firstNext[slot] = value;
                else if (slot < nodeBase)
                {
                    int s = slot - 65536;
                    subNext[s >> 8][s & 255] = value;
                }
                else nodeNext[slot - nodeBase] = value;
            }

            int SelectList(int p)
            {
                uint code = (uint)(src[p] | (src[p + 1] << 8));
                int third = src[p + 2];
                if (subOf[code] >= 0)
                    return 65536 + subOf[code] * 256 + third;

                if (sublistNum < sublistMax)
                {
                    subOf[code] = sublistNum;
                    subNext[sublistNum] = new int[256];
                    Array.Fill(subNext[sublistNum], -1);
                    sublistNum++;
                    return 65536 + subOf[code] * 256 + third;
                }

                return (int)code;
            }

            void InsertNode(int list, uint address)
            {
                int n = (int)listAddp;
                if (nodeAddr[n] != NoAddress)
                {
                    int pv = nodePrev[n];
                    int nx = nodeNext[n];
                    if (pv != -1) SetNext(pv, nx);
                    if (nx != -1) nodePrev[nx - nodeBase] = pv;
                    nodeAddr[n] = NoAddress;
                }

                nodeAddr[n] = address;
                nodePrev[n] = list;
                int ln = GetNext(list);
                nodeNext[n] = ln;
                if (ln != -1) nodePrev[ln - nodeBase] = nodeBase + n;
                SetNext(list, nodeBase + n);
                listAddp = (listAddp + 1) & mask;
            }

            // Byte menos frequente (o último em caso de empate) serve de código de escape.
            var table = new uint[256];
            for (int i = 0; i < srcSize; i++)
                table[src[i]]++;
            byte keycode = 0;
            uint minCode = table[0];
            for (int i = 1; i < 256; i++)
            {
                if (minCode < table[i]) continue;
                minCode = table[i];
                keycode = (byte)i;
            }

            byte[] dest = new byte[(long)srcSize * 2 + 64];
            dest[0] = (byte)srcSize;
            dest[1] = (byte)(srcSize >> 8);
            dest[2] = (byte)(srcSize >> 16);
            dest[3] = (byte)(srcSize >> 24);
            dest[8] = keycode;
            int dp = 9;
            int sp = 0;
            uint srcAddress = 0;
            int dstSize = 0;

            while (srcAddress < srcSize)
            {
                int maxConbo = -1, maxAddress = -1, maxBonus = -1, maxAddressSize = 0, maxConboSize = 0;

                if (srcAddress + MinCompress < srcSize)
                {
                    int list = SelectList(sp);

                    int listTemp = GetNext(list);
                    for (uint m = 0; m < MaxSearchListNum && listTemp != -1; listTemp = nodeNext[listTemp - nodeBase], m++)
                    {
                        int n = listTemp - nodeBase;
                        int address = (int)(srcAddress - nodeAddr[n]);
                        if (address >= MaxPosition)
                        {
                            int pv = nodePrev[n];
                            int nx = nodeNext[n];
                            if (pv != -1) SetNext(pv, nx);
                            if (nx != -1) nodePrev[nx - nodeBase] = pv;
                            nodeAddr[n] = NoAddress;
                            continue;
                        }

                        long limit = srcAddress + MaxCopySize < srcSize
                            ? MaxCopySize
                            : Math.Min(MaxCopySize, (long)srcSize - srcAddress);
                        int conbo = 0;
                        while (conbo < limit && src[sp - address + conbo] == src[sp + conbo])
                            conbo++;

                        if (conbo >= 4)
                        {
                            int conboSize = (conbo - MinCompress) < 0x20 ? 0 : 1;
                            int addressSize = address < 0x100 ? 0 : (address < 0x10000 ? 1 : 2);
                            int bonus = conbo - (3 + conboSize + addressSize);
                            if (bonus > maxBonus)
                            {
                                maxConbo = conbo;
                                maxAddress = address;
                                maxAddressSize = addressSize;
                                maxConboSize = conboSize;
                                maxBonus = bonus;
                            }
                        }
                    }

                    InsertNode(list, srcAddress);
                }

                if (maxConbo == -1)
                {
                    if (src[sp] == keycode)
                    {
                        dest[dp++] = keycode;
                        dest[dp++] = keycode;
                        dstSize += 2;
                    }
                    else
                    {
                        dest[dp++] = src[sp];
                        dstSize++;
                    }

                    sp++;
                    srcAddress++;
                }
                else
                {
                    dest[dp++] = keycode;
                    maxConbo -= MinCompress;
                    int b = ((maxConbo & 0x1f) << 3) | (maxConboSize << 2) | maxAddressSize;
                    if ((byte)b >= keycode) b += 1;
                    dest[dp++] = (byte)b;
                    if (maxConboSize == 1)
                        dest[dp++] = (byte)((maxConbo >> 5) & 0xff);
                    maxConbo += MinCompress;
                    maxAddress--;
                    dest[dp++] = (byte)maxAddress;
                    if (maxAddressSize > 0)
                    {
                        dest[dp++] = (byte)(maxAddress >> 8);
                        if (maxAddressSize == 2)
                            dest[dp++] = (byte)(maxAddress >> 16);
                    }

                    dstSize += 3 + maxAddressSize + maxConboSize;

                    // Indexa também as posições cobertas pela repetição.
                    if (srcAddress + maxConbo < srcSize)
                    {
                        int sp2 = sp + 1;
                        for (int j = 1; j < maxConbo && (long)sp2 + 2 < srcSize; j++, sp2++)
                        {
                            int list = SelectList(sp2);
                            InsertNode(list, srcAddress + (uint)j);
                        }
                    }

                    sp += maxConbo;
                    srcAddress += (uint)maxConbo;
                }
            }

            uint total = (uint)dstSize + 9;
            dest[4] = (byte)total;
            dest[5] = (byte)(total >> 8);
            dest[6] = (byte)(total >> 16);
            dest[7] = (byte)(total >> 24);
            return dest.AsSpan(0, (int)total).ToArray();
        }
    }
}