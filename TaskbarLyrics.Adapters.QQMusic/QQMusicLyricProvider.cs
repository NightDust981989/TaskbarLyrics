using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.Adapters.QQMusic;

public sealed class QQMusicLyricProvider : ILyricProvider
{
    private const bool EnableTraditionalToSimplified = false;
    private const int SearchParallelism = 3;
    private const int TitleArtistContainsBonus = 10;
    private const string OfficialSearchEndpoint = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string OfficialLyricEndpoint = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex LrcRegex = new(@"\[(\d{1,2})(?:[:\uFF1A])(\d{2})(?:[\.\uFF0E:\uFF1A](\d{1,3}))?\]([^\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex BracketSuffixRegex = new(@"\s*[\(\[\{（【].*?[\)\]\}）】]\s*", RegexOptions.Compiled);
    private static readonly Regex FeatureSuffixRegex = new(@"\s+(feat\.?|ft\.?|with)\s+.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly ConcurrentDictionary<string, (string? SyncedLyrics, string? PlainLyrics)> MemoryCache = new(StringComparer.Ordinal);
    private static readonly object DiskCacheLock = new();
    private static Dictionary<string, CachedLyrics>? _diskCache;

    public string SourceApp => "QQMusic";

    public async Task<LyricDocument?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        // Removed strict source check to allow serving lyrics for other apps (Spotify, Apple Music, etc.)
        
        if (string.Equals(track.Title, "Unknown Title", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = await FetchLyricsPayloadAsync(track, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var timed = ParseLrc(payload.Value.SyncedLyrics);
        if (timed.Count > 0)
        {
            return new LyricDocument(timed);
        }

        var plain = ParsePlainLyrics(payload.Value.PlainLyrics);
        if (plain.Count > 0)
        {
            return new LyricDocument(plain);
        }

        return null;
    }

    private static async Task<(string? SyncedLyrics, string? PlainLyrics)?> FetchLyricsPayloadAsync(
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(track.Title, track.Artist);
        if (TryGetCachedPayload(cacheKey, out var cached) && HasAnyLyrics(cached))
        {
            return cached;
        }

        var official = await FetchOfficialPayloadAsync(track, cancellationToken);
        if (HasAnyLyrics(official))
        {
            StoreCachedPayload(cacheKey, official!.Value);
            return official;
        }

        return null;
    }

    private static async Task<(string? SyncedLyrics, string? PlainLyrics)?> FetchOfficialPayloadAsync(
        TrackInfo target,
        CancellationToken cancellationToken)
    {
        var candidates = await SearchOfficialCandidatesAsync(target, cancellationToken);
        if (candidates.Count == 0) return null;

        // Fetch top 4 candidates in parallel to speed up.
        var tasks = candidates.Take(4).Select(async c =>
        {
            var p = await FetchOfficialLyricsBySongMidAsync(c.SongMid, cancellationToken);
            return p != null && HasAnyLyrics(p) ? p : null;
        });

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r != null);
    }

    private static async Task<List<OfficialSongCandidate>> SearchOfficialCandidatesAsync(
        TrackInfo target,
        CancellationToken cancellationToken)
    {
        var queries = BuildSearchQueries(target.Title, target.Artist).Take(3).ToList();
        
        // Execute queries in parallel
        var tasks = queries.Select(q => SearchOfficialSingleQueryAsync(q, target, cancellationToken));
        var batchResults = await Task.WhenAll(tasks);

        var merged = new List<OfficialSongCandidate>();
        foreach (var batch in batchResults)
        {
            if (batch.Count > 0) merged.AddRange(batch);
        }

        return merged
            .GroupBy(x => x.SongMid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Take(10) // Take top 10 for final re-ranking
            .ToList();
    }

    private static async Task<List<OfficialSongCandidate>> SearchOfficialSingleQueryAsync(
        string query,
        TrackInfo target,
        CancellationToken cancellationToken)
    {
        var searchObj = new
        {
            req_1 = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService", // Corrected module
                param = new
                {
                    num_per_page = 20,
                    page_num = 1,
                    query = query,
                    search_type = 0
                }
            },
            comm = new
            {
                ct = 11,
                cv = 120220
            }
        };

        var jsonPayload = JsonSerializer.Serialize(searchObj);
        var url = $"{OfficialSearchEndpoint}?data={Uri.EscapeDataString(jsonPayload)}";

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<OfficialSongCandidate>();
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(raw);
            
            if (!json.RootElement.TryGetProperty("req_1", out var searchResult) ||
                !searchResult.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("body", out var body) ||
                !body.TryGetProperty("song", out var song) ||
                !song.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return new List<OfficialSongCandidate>();
            }

            var result = new List<OfficialSongCandidate>();
            foreach (var item in list.EnumerateArray())
            {
                var songMid = GetStringProperty(item, "mid", "songmid");
                if (string.IsNullOrWhiteSpace(songMid)) continue;

                var songName = GetStringProperty(item, "name", "title", "songname");
                var artistName = ExtractOfficialArtistName(item);
                
                int duration = 0;
                if (item.TryGetProperty("interval", out var intervalProp)) duration = intervalProp.GetInt32();

                var score = ScoreSearchResult(target, songName, artistName, duration);
                result.Add(new OfficialSongCandidate(songMid, score));
            }

            return result;
        }
        catch
        {
            return new List<OfficialSongCandidate>();
        }
    }

    private static async Task<(string? SyncedLyrics, string? PlainLyrics)?> FetchOfficialLyricsBySongMidAsync(
        string songMid,
        CancellationToken cancellationToken)
    {
        var encodedMid = Uri.EscapeDataString(songMid);
        var pcachetime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"{OfficialLyricEndpoint}?songmid={encodedMid}&format=json&pcachetime={pcachetime}&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8&nobase64=1";

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonText = ParsePossiblyJsonp(raw);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return null;
            }

            using var json = JsonDocument.Parse(jsonText);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var lyricRaw = GetStringProperty(root, "lyric", "lrc", "lyricContent");
            var transRaw = GetStringProperty(root, "trans", "transLyric", "trans_lyric");

            var lyricText = ProcessLyricText(lyricRaw);
            var transText = ProcessLyricText(transRaw);

            var synced = LooksLikeTimedLyric(lyricText) ? lyricText : null;
            var plain = synced is null ? lyricText : transText;

            return (synced, plain);
        }
        catch
        {
            return null;
        }
    }


    private static string ExtractOfficialArtistName(JsonElement songElement)
    {
        if (!songElement.TryGetProperty("singer", out var singerElement) ||
            singerElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (var singer in singerElement.EnumerateArray())
        {
            var name = GetStringProperty(singer, "name", "singerName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        return string.Join(" / ", names);
    }

    private static string ParsePossiblyJsonp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var open = trimmed.IndexOf('(');
        var close = trimmed.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return trimmed[(open + 1)..close].Trim();
        }

        return string.Empty;
    }

    private static string? ProcessLyricText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        string decrypted = raw;
        // 1. Base64 Decode if needed
        if (!LooksLikeTimedLyric(raw))
        {
            try
            {
                var bytes = Convert.FromBase64String(raw);
                decrypted = Encoding.UTF8.GetString(bytes);
                // simple heuristic for garbled UTF-8: check for common displacement markers or just try GB18030
                if (decrypted.Contains('\ufffd')) // Unicode replacement character
                {
                    try { decrypted = Encoding.GetEncoding(936).GetString(bytes); } catch { }
                }
            }
            catch { }
        }

        // 2. HTML Decode
        decrypted = WebUtility.HtmlDecode(decrypted);

        return decrypted;
    }

    private static string CleanLyricLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        
        // Remove QRC tags <00:00.000>
        var cleaned = Regex.Replace(line, @"<[^>]+>", "");
        
        // Remove control characters but keep basic whitespace structure
        cleaned = new string(cleaned.Where(c => !char.IsControl(c) || c == '\n' || c == '\r').ToArray());
        
        cleaned = cleaned.Replace("\uFEFF", string.Empty)
                         .Replace("\u200B", string.Empty) // Zero width space
                         .Trim();

        return EnableTraditionalToSimplified
            ? ChineseScriptConverter.ToSimplified(cleaned)
            : cleaned;
    }

    private static bool LooksLikeTimedLyric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains('[', StringComparison.Ordinal) &&
               value.Contains(':', StringComparison.Ordinal);
    }


    private static IEnumerable<string> BuildSearchQueries(string title, string artist)
    {
        var queries = new List<string>();

        void Add(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (!queries.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) queries.Add(trimmed);
        }

        // Query 1: 原标题 + 原歌手 (最精确)
        Add($"{title} {artist}".Trim());

        // Query 2: 清洗后标题 + 第一歌手 (处理 Feat 情况)
        var normalizedTitle = NormalizeTitleForQuery(title);
        var primaryArtist = GetPrimaryArtist(artist);
        Add($"{normalizedTitle} {primaryArtist}".Trim());

        // Query 3: 原标题 (仅限标题较长且独特时)
        if (!string.IsNullOrWhiteSpace(title) && title.Length > 5)
        {
            Add(title);
        }

        return queries;
    }

    private static string NormalizeTitleForQuery(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var value = BracketSuffixRegex.Replace(title, " ");
        value = FeatureSuffixRegex.Replace(value, string.Empty);
        return CollapseWhitespace(value);
    }

    private static string NormalizeArtistForQuery(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        return GetPrimaryArtist(artist);
    }

    private static string GetPrimaryArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var separators = new[] { "、", "/", ",", "，", "&", " x ", " X ", " feat. ", " feat ", " ft. ", " ft " };
        foreach (var separator in separators)
        {
            var index = artist.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return CollapseWhitespace(artist[..index]);
            }
        }

        return CollapseWhitespace(artist);
    }

    private static IEnumerable<string> SplitByDash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var separators = new[] { " - ", " – ", " — ", "-", "–", "—" };
        foreach (var separator in separators)
        {
            var parts = value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                foreach (var part in parts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        yield return part;
                    }
                }

                yield break;
            }
        }
    }

    private static int ScoreSearchResult(TrackInfo target, string? resultTitle, string? resultArtist, int resultDurationInSeconds)
    {
        return LyricMatcher.Score(target, resultTitle ?? string.Empty, resultArtist ?? string.Empty, resultDurationInSeconds);
    }

    private static string NormalizeForMatch(string? value) => LyricMatcher.NormalizeForSearch(value);

    private static string BuildCacheKey(string title, string artist)
    {
        var titleKey = NormalizeForMatch(title);
        var artistKey = NormalizeForMatch(artist);
        return $"{titleKey}|{artistKey}";
    }

    private static bool TryGetCachedPayload(string cacheKey, out (string? SyncedLyrics, string? PlainLyrics) payload)
    {
        if (MemoryCache.TryGetValue(cacheKey, out payload))
        {
            return true;
        }

        lock (DiskCacheLock)
        {
            EnsureDiskCacheLoaded();
            if (_diskCache is not null && _diskCache.TryGetValue(cacheKey, out var cached))
            {
                payload = (cached.SyncedLyrics, cached.PlainLyrics);
                MemoryCache[cacheKey] = payload;
                return true;
            }
        }

        payload = default;
        return false;
    }

    private static void StoreCachedPayload(string cacheKey, (string? SyncedLyrics, string? PlainLyrics) payload)
    {
        MemoryCache[cacheKey] = payload;

        lock (DiskCacheLock)
        {
            EnsureDiskCacheLoaded();
            _diskCache ??= new Dictionary<string, CachedLyrics>(StringComparer.Ordinal);
            _diskCache[cacheKey] = new CachedLyrics
            {
                SyncedLyrics = payload.SyncedLyrics,
                PlainLyrics = payload.PlainLyrics
            };

            try
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(_diskCache);
                File.WriteAllText(CacheFilePath, json);
            }
            catch
            {
                // Ignore disk cache write failures.
            }
        }
    }

    private static void EnsureDiskCacheLoaded()
    {
        if (_diskCache is not null)
        {
            return;
        }

        try
        {
            if (!File.Exists(CacheFilePath))
            {
                _diskCache = new Dictionary<string, CachedLyrics>(StringComparer.Ordinal);
                return;
            }

            var json = File.ReadAllText(CacheFilePath);
            _diskCache = JsonSerializer.Deserialize<Dictionary<string, CachedLyrics>>(json)
                ?? new Dictionary<string, CachedLyrics>(StringComparer.Ordinal);
        }
        catch
        {
            _diskCache = new Dictionary<string, CachedLyrics>(StringComparer.Ordinal);
        }
    }

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarLyrics",
        "cache",
        "qqmusic-lyrics.json");

    private static string CollapseWhitespace(string value)
    {
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? GetStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        return null;
    }

    private static (string? SyncedLyrics, string? PlainLyrics)? ExtractPayload(JsonElement element)
    {
        string? synced = null;
        string? plain = null;

        if (element.TryGetProperty("syncedLyrics", out var syncedElement) &&
            syncedElement.ValueKind == JsonValueKind.String)
        {
            synced = syncedElement.GetString();
        }

        if (element.TryGetProperty("plainLyrics", out var plainElement) &&
            plainElement.ValueKind == JsonValueKind.String)
        {
            plain = plainElement.GetString();
        }

        return (synced, plain);
    }

    private static bool HasAnyLyrics((string? SyncedLyrics, string? PlainLyrics)? payload)
    {
        return payload is not null &&
               (!string.IsNullOrWhiteSpace(payload.Value.SyncedLyrics) ||
                !string.IsNullOrWhiteSpace(payload.Value.PlainLyrics));
    }

    private static List<LyricLine> ParseLrc(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return new List<LyricLine>();
        }

        var result = new List<LyricLine>();
        var lines = lrc.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var matches = LrcRegex.Matches(rawLine);
            if (matches.Count == 0) continue;

            // Extract the text part (Group 4) and clean it line-by-line
            var text = CleanLyricLine(matches[^1].Groups[4].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match match in matches)
            {
                var minute = int.Parse(match.Groups[1].Value);
                var second = int.Parse(match.Groups[2].Value);
                var fractionRaw = match.Groups[3].Value;
                var millisecond = ParseMillisecond(fractionRaw);

                result.Add(new LyricLine(new TimeSpan(0, 0, minute, second, millisecond), text));
            }
        }

        return result.OrderBy(x => x.Timestamp).ToList();
    }

    private static List<LyricLine> ParsePlainLyrics(string? plainLyrics)
    {
        if (string.IsNullOrWhiteSpace(plainLyrics))
        {
            return new List<LyricLine>();
        }

        var result = new List<LyricLine>();
        var lines = plainLyrics.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var index = 0;
        foreach (var rawLine in lines)
        {
            var text = CleanLyricLine(rawLine);
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new LyricLine(TimeSpan.FromSeconds(index * 3), text));
            index++;
        }

        return result;
    }

    private static int ParseMillisecond(string fractionRaw)
    {
        if (string.IsNullOrWhiteSpace(fractionRaw))
        {
            return 0;
        }

        return fractionRaw.Length switch
        {
            1 => int.Parse(fractionRaw) * 100,
            2 => int.Parse(fractionRaw) * 10,
            _ => int.Parse(fractionRaw[..3])
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://y.qq.com/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://y.qq.com");
        return client;
    }

    public static void ClearCache()
    {
        MemoryCache.Clear();

        lock (DiskCacheLock)
        {
            _diskCache = new Dictionary<string, CachedLyrics>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    File.Delete(CacheFilePath);
                }
            }
            catch
            {
                // Ignore cache delete failures.
            }
        }
    }

    private sealed record OfficialSongCandidate(string SongMid, int Score);

    private sealed class CachedLyrics
    {
        public string? SyncedLyrics { get; set; }

        public string? PlainLyrics { get; set; }
    }
}

