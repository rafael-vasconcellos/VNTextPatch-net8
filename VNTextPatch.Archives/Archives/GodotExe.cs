// GodotExe.cs
// Implementação de VNTextPatch.Archives.IArchive para executáveis do Godot com o PCK embutido no
// final do arquivo ([PCK][tamanho][magic]). Toda a leitura (Load, ListFiles, Extract) é herdada de
// GodotPck, que já detecta PCK embutido; aqui só muda a extensão e o destino de Pack.

namespace VNTextPatch.Archives
{
    /// <summary>
    /// Pack grava o PCK dentro de um executável que já existe em outputPath: se ele já tiver um PCK
    /// embutido, este é substituído; se não tiver, o PCK é anexado. O resto do executável é preservado.
    /// </summary>
    public sealed class GodotExe : GodotPck
    {
        public override string? Extension => ".exe";

        protected override FileStream OpenOutput(string tempPath, string outputFullPath, out bool embedded)
        {
            if (!File.Exists(outputFullPath))
                throw new FileNotFoundException(
                    "O executável de destino precisa existir: o PCK é embutido nele.", outputFullPath);

            using var source = new FileStream(outputFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long keep = FindEmbeddedStart(source) ?? source.Length; // tudo antes do PCK antigo

            var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            try
            {
                source.Position = 0;
                CopyExactly(source, temp, keep);
                PadTo8(temp); // o Godot alinha o início do PCK embutido em 8 bytes
                embedded = true;
                return temp;
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }
    }
}
