using System.Text;
using CharacterDto = VNTextPatch.Shared.Scripts.MarkdownScript.VndbMarkdownGenerator.CharacterDto;


namespace VNTextPatch.Shared.Scripts.MarkdownScript
{
    /// <summary>
    /// Encapsula um <see cref="StringBuilder"/> com operações de alto nível
    /// para montar documentos Markdown.
    /// </summary>
    internal class MarkdownBuilder
    {
        private readonly StringBuilder _sb = new();

        /// <summary>Título (<c>#</c> a <c>######</c>) seguido de uma linha em branco.</summary>
        public MarkdownBuilder Heading(int level, string text)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);

            _sb.Append('#', level)
                .Append(' ')
                .AppendLine(text)
                .AppendLine();

            return this;
        }

        /// <summary>Linha de texto simples.</summary>
        public MarkdownBuilder Line(string text)
        {
            _sb.AppendLine(text);
            return this;
        }

        /// <summary>Linha em branco.</summary>
        public MarkdownBuilder BlankLine()
        {
            _sb.AppendLine();
            return this;
        }

        /// <summary>Item de lista (<c>- texto</c>).</summary>
        public MarkdownBuilder ListItem(string text)
        {
            _sb.Append("- ").AppendLine(text);
            return this;
        }

        /// <summary>
        /// Imagem seguida de uma linha em branco.
        /// Ignorada se a URL estiver vazia.
        /// </summary>
        public MarkdownBuilder Image(string alt, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return this;

            _sb.Append("![")
                .Append(alt)
                .Append("](")
                .Append(url)
                .AppendLine(")")
                .AppendLine();

            return this;
        }

        /// <summary>
        /// Campo no formato <c>**Rótulo:** valor</c>, com quebra de linha forçada
        /// (dois espaços no fim). Ignorado se o valor estiver vazio.
        /// </summary>
        public MarkdownBuilder Metadata(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return this;

            _sb.Append("**")
                .Append(label)
                .Append(":** ")
                .Append(value.Trim())
                .AppendLine("  ");

            return this;
        }

        public override string ToString() => _sb.ToString();
    }

    /// <summary>
    /// <see cref="MarkdownBuilder"/> com as seções específicas do modelo de
    /// Markdown usado para tradução de visual novels (personagens e notas).
    /// </summary>
    internal sealed class VndbMarkdownBuilder : MarkdownBuilder
    {
        public void AppendCharacter(
            CharacterDto character,
            string vnId)
        {
            Heading(3, character.Name ?? "Unknown character");

            Image("banner", character.Image?.Url);

            Metadata("Original name", character.Original);

            var aliases = character.Aliases?
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases is { Count: > 0 })
            {
                Metadata("Aliases", string.Join(" - ", aliases));
            }

            Metadata("Gender", GetGender(character));
            Metadata("Role", GetRole(character, vnId));

            if (!string.IsNullOrWhiteSpace(character.Description))
            {
                Metadata("Description", character.Description);
            }

            BlankLine();
        }

        private static string? GetGender(CharacterDto character)
        {
            var gender = FirstValue(character.Gender)
                ?? FirstValue(character.Sex);

            if (gender is null)
                return null;

            return gender.ToLowerInvariant() switch
            {
                "m" => "Male",
                "f" => "Female",
                "b" => "Both",
                "n" => "Sexless",
                "o" => "Non-binary",
                "a" => "Ambiguous",
                _ => gender
            };
        }

        private static string? FirstValue(List<string?>? values)
        {
            return values?.FirstOrDefault(
                v => !string.IsNullOrWhiteSpace(v)
            );
        }

        private static string? GetRole(CharacterDto character, string vnId)
        {
            var role = character.Vns?
                .Where(v =>
                    string.Equals(
                        v.Id,
                        vnId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(v => v.Role)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

            if (role is null)
                return null;

            return role.ToLowerInvariant() switch
            {
                "main" => "Main",
                "primary" => "Primary",
                "side" => "Side",
                "appears" => "Appears",
                _ => role
            };
        }

        public void AppendTranslationNotes()
        {
            Heading(2, "Translation Notes");

            ListItem(
                "Use English as the default translation target language, " +
                "unless the user explicitly specifies another language."
            );

            ListItem(
                "Maintain Japanese honorifics " +
                "(e.g -san, -senpai, -chan, etc.) in translations."
            );

            ListItem(
                "Avoid having any romanji or Japanese text in the response."
            );

            ListItem(
                "If a line is already translated, leave it as is and " +
                "include it in your response."
            );

            ListItem("Maintain any spacing in the translation.");
        }
    }
}