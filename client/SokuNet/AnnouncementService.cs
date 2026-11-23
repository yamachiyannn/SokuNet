using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SokuNet;

internal sealed record SokuNetAnnouncement(string Id, string Title, string Message, string? Url = null);
internal sealed record AnnouncementResult(IReadOnlyList<SokuNetAnnouncement> Announcements, string? ChangeNotification);

/// <summary>Reads public announcements from this project's GitHub repository.</summary>
internal sealed class AnnouncementService : IDisposable
{
    private const string AnnouncementUrl = "https://raw.githubusercontent.com/yamachiyannn/SokuNet/main/announcements.json";
    private const int MaximumDocumentCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _cachePath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private IReadOnlyList<SokuNetAnnouncement> _announcements = Array.Empty<SokuNetAnnouncement>();
    private string _previousHash = string.Empty;
    private bool _loaded;

    public AnnouncementService(string appDirectory)
    {
        _cachePath = Path.Combine(appDirectory, "announcements-cache.json");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SokuNet", "1.0"));
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        LoadCache();
    }

    public IReadOnlyList<SokuNetAnnouncement> GetAnnouncements() => _announcements;

    public async Task<AnnouncementResult> CheckAsync(CancellationToken cancellationToken)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                AnnouncementUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > MaximumDocumentCharacters)
                throw new InvalidDataException("Announcement document is too large.");

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (json.Length > MaximumDocumentCharacters)
                throw new InvalidDataException("Announcement document is too large.");

            AnnouncementDocument document = JsonSerializer.Deserialize<AnnouncementDocument>(json, JsonOptions)
                ?? throw new JsonException("Announcement document is empty.");
            List<SokuNetAnnouncement> cleaned = (document.Announcements ?? [])
                .Where(item => item is not null)
                .Select(item => new SokuNetAnnouncement(
                    Clean(item.Id, 64), Clean(item.Title, 80), Clean(item.Message, 240), Clean(item.Url, 256)))
                .Where(item => item.Id.Length > 0 && item.Title.Length > 0 && item.Message.Length > 0)
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .Take(10)
                .ToList();

            string normalized = JsonSerializer.Serialize(cleaned, JsonOptions);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            string? notification = null;
            if (_loaded && _previousHash.Length > 0 && !string.Equals(_previousHash, hash, StringComparison.Ordinal))
            {
                var previousById = _announcements.ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
                SokuNetAnnouncement? changed = cleaned.FirstOrDefault(item =>
                    !previousById.TryGetValue(item.Id, out SokuNetAnnouncement? previous) ||
                    previous.Title != item.Title || previous.Message != item.Message || previous.Url != item.Url);
                notification = changed is null
                    ? "SokuNetのお知らせが更新されました"
                    : $"お知らせ: {changed.Title}";
            }

            try { SaveCache(hash, cleaned); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _announcements = cleaned;
            _previousHash = hash;
            _loaded = true;
            return new AnnouncementResult(_announcements, notification);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            AnnouncementCache? cache = JsonSerializer.Deserialize<AnnouncementCache>(File.ReadAllText(_cachePath), JsonOptions);
            if (cache is null) return;
            _previousHash = cache.ContentHash ?? string.Empty;
            _announcements = cache.Announcements ?? [];
            _loaded = _previousHash.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _previousHash = string.Empty;
            _announcements = Array.Empty<SokuNetAnnouncement>();
            _loaded = false;
        }
    }

    private void SaveCache(string hash, IReadOnlyList<SokuNetAnnouncement> announcements)
    {
        string temporaryPath = _cachePath + ".tmp";
        string json = JsonSerializer.Serialize(new AnnouncementCache
        {
            ContentHash = hash,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Announcements = announcements.ToList()
        }, JsonOptions);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, _cachePath, overwrite: true);
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string clean = new(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _checkLock.Dispose();
    }

    private sealed class AnnouncementDocument
    {
        [JsonPropertyName("announcements")] public List<AnnouncementItem>? Announcements { get; set; }
    }

    private sealed class AnnouncementItem
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
        public string? Url { get; set; }
    }

    private sealed class AnnouncementCache
    {
        public string? ContentHash { get; set; }
        public DateTimeOffset CheckedAtUtc { get; set; }
        public List<SokuNetAnnouncement>? Announcements { get; set; }
    }
}
