using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Services;

public sealed class LyricProviderRegistry : ILyricProviderRegistry
{
    private readonly IEnumerable<ILyricProvider> _providers;

    public LyricProviderRegistry(IEnumerable<ILyricProvider> providers)
    {
        _providers = providers;
    }

    public async Task<List<LyricResolveResult>> ResolveLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        var results = new List<LyricResolveResult>();
        
        // Priority Hierarchy: 
        // 1. Primary source for the current app (Weight 3)
        // 2. QQ Music (Weight 2)
        // 3. Netease (Weight 1)
        // 4. Default sources like LRCLIB (Weight 0)
        var sortedProviders = _providers.OrderByDescending(p => 
            p.SourceApp == track.SourceApp ? 3 : 
            p.SourceApp == "QQMusic" ? 2 : 
            p.SourceApp == "Netease" ? 1 : 0).ToList();

        foreach (var p in sortedProviders)
        {
            try
            {
                var doc = await p.GetLyricsAsync(track, cancellationToken);
                var result = new LyricResolveResult(p.SourceApp, doc);
                results.Add(result);

                // Early Exit: If the current provider gives us a decent match (score >= 60) 
                // and it's the primary provider for this source, stop searching.
                var exitThreshold = (p.SourceApp == track.SourceApp) ? 60 : 90;
                if (doc != null && doc.BestScore >= exitThreshold)
                {
                    break;
                }
            }
            catch
            {
                results.Add(new LyricResolveResult(p.SourceApp, null));
            }
        }
        
        return results;
    }

    public async Task<LyricDocument?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        var results = await ResolveLyricsAsync(track, cancellationToken);
        return results
            .Where(r => r.Document != null)
            .OrderByDescending(r => r.Document!.BestScore)
            .FirstOrDefault()?.Document;
    }
}
