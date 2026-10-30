using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;


namespace VNTextPatch.Shared.Scripts.MarkdownScript
{
    public sealed partial class VndbMarkdownGenerator : IDisposable
    {
        private const string BaseUrl = "https://api.vndb.org/kana";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public VndbMarkdownGenerator(HttpClient? httpClient = null)
        {
            _ownsHttpClient = httpClient is null;

            _httpClient = httpClient ?? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (_ownsHttpClient)
            {
                _httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json")
                );
            }
        }

        /// <summary>
        /// Consulta a visual novel e seus personagens e retorna o Markdown.
        /// </summary>
        public async Task<string> GenerateAsync(
            string vnId,
            CancellationToken cancellationToken = default)
        {
            var task = await GenerateBuilderAsync(vnId, cancellationToken);
            return task.ToString();
        }

        /// <summary>
        /// Consulta a visual novel e seus personagens e retorna o Markdown Builder.
        /// </summary>
        public async Task<VndbMarkdownBuilder> GenerateBuilderAsync(
            string vnId,
            CancellationToken cancellationToken = default)
        {
            ValidateVnId(vnId);

            var vn = await GetVisualNovelAsync(vnId, cancellationToken);

            var characters = await GetCharactersAsync(
                vnId,
                cancellationToken
            );

            return BuildMarkdown(vn, characters, vnId);
        }

        /// <summary>
        /// Gera o Markdown e salva o resultado em um arquivo.
        /// Cria o diretório de destino caso ele ainda não exista.
        /// </summary>
        public async Task GenerateToFileAsync(
            string vnId,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            var markdown = await GenerateAsync(vnId, cancellationToken);
            await CreateFileAsync(markdown, outputPath, cancellationToken);
        }

        public static async Task CreateFileAsync(
            string markdown,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentNullException.ThrowIfNull(markdown);
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);

            if (directory is not null)
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(
                fullPath,
                markdown,
                new UTF8Encoding(false),
                cancellationToken
            );
        }

        // ========================================================
        // VNDB API
        // ========================================================

        private async Task<VisualNovelDto> GetVisualNovelAsync(
            string vnId,
            CancellationToken cancellationToken)
        {
            var request = new JsonObject
            {
                ["filters"] = new JsonArray("id", "=", vnId),
                ["fields"] =
                    "id, title, description, tags.name, tags.rating, " +
                    "image.url, developers.name, rating, average",
                ["results"] = 1,
                ["compact_filters"] = false,
                ["normalized_filters"] = false
            };

            var response = await PostAsync(
                "vn",
                request,
                VndbJsonContext.Default.VisualNovelResponse,
                cancellationToken
            );

            return response.Results.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Visual novel '{vnId}' not found."
                );
        }

        private async Task<List<CharacterDto>> GetCharactersAsync(
            string vnId,
            CancellationToken cancellationToken)
        {
            const int pageSize = 100;

            var characters = new List<CharacterDto>();

            for (var page = 1; ; page++)
            {
                var request = new JsonObject
                {
                    ["filters"] = new JsonArray(
                        "vn", "=", new JsonArray("id", "=", vnId)
                    ),
                    ["fields"] =
                        "id, name, original, aliases, description, image.url, " +
                        "sex, gender, vns.id, vns.role, vns.title",
                    ["results"] = pageSize,
                    ["page"] = page,
                    ["compact_filters"] = false,
                    ["normalized_filters"] = false
                };

                var response = await PostAsync(
                    "character",
                    request,
                    VndbJsonContext.Default.CharacterResponse,
                    cancellationToken
                );

                characters.AddRange(response.Results);

                if (!response.More || response.Results.Count == 0)
                    break;
            }

            // Remove possíveis duplicatas pelo ID do personagem.
            return characters
                .GroupBy(
                    c => c.Id ?? c.Name ?? Guid.NewGuid().ToString(),
                    StringComparer.OrdinalIgnoreCase
                )
                .Select(g => g.First())
                .OrderBy(c => c.GetRole(vnId) switch
                {
                    "main" => 0,
                    "primary" => 1,
                    "side" => 2,
                    "appears" => 3,
                    _ => 4
                })
                .ThenBy(c => string.IsNullOrWhiteSpace(c.Image?.Url) ? 1 : 0)
                .ToList();
        }

        private async Task<ApiResponse<T>> PostAsync<T>(
            string endpoint,
            JsonNode request,
            JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
            CancellationToken cancellationToken)
        {
            var json = request.ToJsonString();

            using var content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"
            );

            var url = $"{BaseUrl}/{endpoint}";

            using var response = await _httpClient.PostAsync(
                url,
                content,
                cancellationToken
            );

            var responseBody = await response.Content.ReadAsStringAsync(
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"VNDB API Error: HTTP {(int)response.StatusCode} " +
                    $"({response.ReasonPhrase}).\n{responseBody}"
                );
            }

            return JsonSerializer.Deserialize(
                responseBody,
                responseTypeInfo
            ) ?? throw new InvalidOperationException("The VNDB API returned a invalid JSON response.");
        }

        // ========================================================
        // MARKDOWN
        // ========================================================

        private static VndbMarkdownBuilder BuildMarkdown(
            VisualNovelDto vn,
            List<CharacterDto> characters,
            string requestedVnId)
        {
            var md = new VndbMarkdownBuilder();

            // Cabeçalho
            md.Heading(1, vn.Title ?? requestedVnId);

            md.Image("banner", vn.Image?.Url);

            md.UnorderedList().Metadata("VNDB ID", vn.Id ?? requestedVnId);

            md.UnorderedList().Metadata(
                "Developer(s)",
                JoinNames(vn.Developers?.Select(d => d.Name))
            );

            md.UnorderedList().Metadata("Rating", FormatNumber(vn.Rating));
            md.UnorderedList().Metadata("Average", FormatNumber(vn.Average)).BlankLine();

            if (!string.IsNullOrWhiteSpace(vn.Description))
            {
                md.Metadata("Description", vn.Description);
            }

            md.BlankLine();

            // Tags
            md.Heading(2, "Tags");

            var tags = vn.Tags?
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .OrderByDescending(t => t.Rating ?? double.MinValue)
                .Select(t => t.Name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            md.Line(
                tags.Count > 0
                    ? string.Join(" - ", tags)
                    : "No tags available."
            );

            md.BlankLine();

            // Personagens
            md.Heading(2, "Characters");

            foreach (var character in characters)
            {
                md.AppendCharacter(character, requestedVnId);
            }

            // Notas fixas do modelo de Markdown
            md.AppendTranslationNotes();

            return md;
        }

        private static string? JoinNames(IEnumerable<string?>? names)
        {
            if (names is null)
                return null;

            var values = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count > 0
                ? string.Join(" - ", values)
                : null;
        }

        private static string? FormatNumber(double? value)
        {
            return value?.ToString(
                "0.##",
                CultureInfo.InvariantCulture
            );
        }

        // ========================================================
        // UTILITÁRIOS
        // ========================================================

        private static void ValidateVnId(string vnId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(vnId);

            if (!Regex.IsMatch(vnId, @"^v\d+$", RegexOptions.IgnoreCase))
            {
                throw new ArgumentException(
                    "The ID must be in the format v7014.",
                    nameof(vnId)
                );
            }
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        // ========================================================
        // DTOs INTERNOS
        // ========================================================

        [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
        [JsonSerializable(typeof(ApiResponse<VisualNovelDto>), TypeInfoPropertyName = "VisualNovelResponse")]
        [JsonSerializable(typeof(ApiResponse<CharacterDto>), TypeInfoPropertyName = "CharacterResponse")]
        private partial class VndbJsonContext : JsonSerializerContext
        {
        }

        private sealed class ApiResponse<T>
        {
            [JsonPropertyName("results")]
            public List<T> Results { get; set; } = new();

            [JsonPropertyName("more")]
            public bool More { get; set; }
        }

        private sealed class VisualNovelDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("image")]
            public ImageDto? Image { get; set; }

            [JsonPropertyName("tags")]
            public List<TagDto>? Tags { get; set; }

            [JsonPropertyName("developers")]
            public List<DeveloperDto>? Developers { get; set; }

            [JsonPropertyName("rating")]
            public double? Rating { get; set; }

            [JsonPropertyName("average")]
            public double? Average { get; set; }
        }

        internal sealed class CharacterDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("original")]
            public string? Original { get; set; }

            [JsonPropertyName("aliases")]
            public List<string?>? Aliases { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("image")]
            public ImageDto? Image { get; set; }

            [JsonPropertyName("sex")]
            public List<string?>? Sex { get; set; }

            [JsonPropertyName("gender")]
            public List<string?>? Gender { get; set; }

            [JsonPropertyName("vns")]
            public List<CharacterVnDto>? Vns { get; set; }

            public string? GetRole(string vnId)
            {
                return Vns?
                    .Where(v =>
                        string.Equals(
                            v.Id,
                            vnId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(v => v.Role)
                    .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
            }
        }

        internal sealed class ImageDto
        {
            [JsonPropertyName("url")]
            public string? Url { get; set; }
        }

        private sealed class TagDto
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("rating")]
            public double? Rating { get; set; }
        }

        private sealed class DeveloperDto
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        internal sealed class CharacterVnDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("role")]
            public string? Role { get; set; }
        }
    }
}