using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.Core.Services;

public sealed class LyricSyncService
{
    private static readonly TimeSpan PlayerCompDefault = TimeSpan.Zero;
    private static readonly TimeSpan PlayerCompSpotify = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PlayerCompNetease = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PlayerCompQqMusic = TimeSpan.FromMilliseconds(100);

    private readonly ILyricProviderRegistry _registry;
    private TrackInfo? _currentTrack;
    private string? _currentTrackId;
    private LyricDocument? _currentDocument;
    private string? _currentLyricSourceApp;
    private bool _isUpdating;

    public string? CurrentLyricSourceApp => _currentLyricSourceApp;

    public LyricSyncService(ILyricProviderRegistry registry)
    {
        _registry = registry;
    }

    public Task<LyricDisplayFrame> GetDisplayFrameAsync(PlaybackSnapshot snapshot)
    {
        if (snapshot.Track == null)
        {
            _currentTrack = null;
            _currentTrackId = null;
            _currentDocument = null;
            return Task.FromResult(new LyricDisplayFrame("", "", "", 0, -1));
        }

        var trackId = $"{snapshot.Track.Title}|{snapshot.Track.Artist}";
        if (trackId != _currentTrackId)
        {
            _currentTrack = snapshot.Track;
            _currentTrackId = trackId;
            _currentDocument = null;
            _currentLyricSourceApp = null;
            UpdateLyricsAsync(snapshot.Track);
        }

        if (_currentDocument == null || _currentDocument.Lines.Count == 0)
        {
            return Task.FromResult(new LyricDisplayFrame(
                _isUpdating ? "Searching for lyrics..." : "",
                "",
                _currentTrack?.Title ?? "",
                0, -1));
        }

        // Apply player-specific compensation
        var sourceLead = GetSourceLeadTime(_currentLyricSourceApp);
        var position = snapshot.Position + sourceLead;

        var lines = _currentDocument.Lines;
        var currentIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= position) currentIdx = i;
            else break;
        }

        if (currentIdx == -1)
        {
            // If before first line, show the first line as prepared current
            var firstLine = lines[0];
            string firstText = firstLine.Text;
            if (!string.IsNullOrWhiteSpace(firstLine.Translation))
            {
                firstText += " (" + firstLine.Translation + ")";
            }

            var nextTxt = lines.Count > 1 ? lines[1].Text : "";
            if (lines.Count > 1 && !string.IsNullOrWhiteSpace(lines[1].Translation))
            {
                nextTxt += " (" + lines[1].Translation + ")";
            }

            return Task.FromResult(new LyricDisplayFrame(firstText, nextTxt, _currentTrack?.Title ?? "", 0, 0));
        }

        var currentLine = lines[currentIdx];
        var nextLine = (currentIdx + 1 < lines.Count) ? lines[currentIdx + 1] : null;

        // Smart text merging: if translation exists, append it.
        // This ensures the "NextLine" correctly shows the next lyric for animation,
        // while still making translations visible in the taskbar's limited space.
        string currentText = currentLine.Text;
        if (!string.IsNullOrWhiteSpace(currentLine.Translation))
        {
            // We use a small space and parens for a clean look in the taskbar
            currentText += " (" + currentLine.Translation + ")";
        }

        string nextText = nextLine?.Text ?? "";
        if (nextLine != null && !string.IsNullOrWhiteSpace(nextLine.Translation))
        {
            nextText += " (" + nextLine.Translation + ")";
        }

        // Calculate progress within line for syllable animation
        double progress = 0;
        if (nextLine != null)
        {
            var duration = nextLine.Timestamp - currentLine.Timestamp;
            var elapsed = position - currentLine.Timestamp;
            progress = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        }

        return Task.FromResult(new LyricDisplayFrame(
            currentText,
            nextText,
            _currentTrack?.Title ?? "",
            progress,
            currentIdx
        ));
    }

    private async void UpdateLyricsAsync(TrackInfo track)
    {
        if (_isUpdating) return;
        _isUpdating = true;

        try
        {
            var results = await _registry.ResolveLyricsAsync(track);
            
            // Pick the best match
            var bestResult = results
                .Where(r => r.Document != null && r.Document.Lines.Count > 0)
                .OrderByDescending(r => r.Document!.BestScore)
                .ThenBy(r => r.SourceApp == "QQMusic" || r.SourceApp == "Netease" ? 0 : 1) 
                .FirstOrDefault();

            if (bestResult != null && _currentTrackId == $"{track.Title}|{track.Artist}")
            {
                _currentDocument = bestResult.Document;
                _currentLyricSourceApp = bestResult.SourceApp;
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private TimeSpan GetSourceLeadTime(string? sourceApp)
    {
        if (string.IsNullOrEmpty(sourceApp)) return PlayerCompDefault;

        return sourceApp.ToLowerInvariant() switch
        {
            "spotify" => PlayerCompSpotify,
            "neteasemusic" or "netease" => PlayerCompNetease,
            "qqmusic" => PlayerCompQqMusic,
            _ => PlayerCompDefault
        };
    }
}
