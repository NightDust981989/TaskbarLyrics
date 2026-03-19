using System.Text.Json;
using System.Net.Http;
using System.Web;

var query = "七里香";
var searchObj = new
{
    req_1 = new
    {
        method = "DoSearchForQQMusicDesktop",
        module = "music.search.SearchCgiService",
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
var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?data={HttpUtility.UrlEncode(jsonPayload)}";

using var http = new HttpClient();
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
http.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");

try {
    Console.WriteLine($"Requesting: {url}");
    var raw = await http.GetStringAsync(url);
    // Console.WriteLine("--- RAW RESPONSE START ---");
    // Console.WriteLine(raw.Length > 1000 ? raw[..1000] + "..." : raw);
    // Console.WriteLine("--- RAW RESPONSE END ---");

    using var json = JsonDocument.Parse(raw);
    if (json.RootElement.TryGetProperty("req_1", out var req1) && 
        req1.TryGetProperty("data", out var data) &&
        data.TryGetProperty("body", out var body) &&
        body.TryGetProperty("song", out var song) &&
        song.TryGetProperty("list", out var list)) 
    {
        Console.WriteLine($"Found list with {list.GetArrayLength()} items");
        foreach (var item in list.EnumerateArray().Take(3)) {
            var title = item.GetProperty("title").GetString();
            var mid = item.GetProperty("mid").GetString();
            Console.WriteLine($"- {title} (MID: {mid})");
        }
    }

    // Lyric Fetch Test
    var songMid = "004Z8Ihr0JIu5s"; // 七里香
    var lyricUrl = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songMid}&format=json&nobase64=1";
    Console.WriteLine($"\nRequesting Lyrics: {lyricUrl}");
    
    // BetterLyrics headers
    http.DefaultRequestHeaders.Clear();
    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    http.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
    
    var lyrRaw = await http.GetStringAsync(lyricUrl);
    Console.WriteLine("--- LYRIC RESPONSE START ---");
    Console.WriteLine(lyrRaw.Length > 500 ? lyrRaw[..500] + "..." : lyrRaw);
    Console.WriteLine("--- LYRIC RESPONSE END ---");

} catch (Exception ex) {
    Console.WriteLine("Error: " + ex.ToString());
}
