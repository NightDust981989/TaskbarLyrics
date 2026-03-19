using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Core.Utilities;

public static class LyricMatcher
{
    private static readonly Regex BracketSuffixRegex = new(@"\s*[\(\[\{（【].*?[\)\]\}）】]\s*", RegexOptions.Compiled);
    private static readonly Regex FeatureSuffixRegex = new(@"\s+(feat\.?|ft\.?|with)\s+.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ConflictKeywords = { "live", "remix", "acoustic", "demo", "instrumental", "vma", "award", "现场", "演唱会", "颁奖", "典礼" };

    public static int Score(TrackInfo target, string resultTitle, string resultArtist, int resultDurationInSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(resultTitle)) return 0;

        // 1. 时长硬约束 (Duration Constraint) - 偏差超过 40 秒直接否决
        int durationScore = 100;
        if (target.Duration.TotalSeconds > 1 && resultDurationInSeconds > 0)
        {
            var diff = Math.Abs(target.Duration.TotalSeconds - resultDurationInSeconds);
            if (diff > 45) return 0;
            if (diff > 10) durationScore -= 40;
            else if (diff > 5) durationScore -= 15;
        }

        // 2. 版本冲突检测 (Conflict Detection)
        foreach (var keyword in ConflictKeywords)
        {
            if (HasVersionConflict(target.Title, resultTitle, keyword)) return 0;
        }

        // 3. 归一化对比 (Normalization 2.0)
        var cleanedTargetTitle = NormalizeForSearch(target.Title);
        var cleanedTargetArtist = NormalizeForSearch(target.Artist);
        var cleanedResultTitle = NormalizeForSearch(resultTitle);
        var cleanedResultArtist = NormalizeForSearch(resultArtist);

        // 4. 相似度计算 (Levenshtein)
        double titleSim = CalculateSimilarity(cleanedTargetTitle, cleanedResultTitle);
        double artistSim = CalculateSimilarity(cleanedTargetArtist, cleanedResultArtist);

        // 5. 准入阈值：标题相似度必须达到 0.7 或 包含关系
        if (titleSim < 0.7 && !cleanedResultTitle.Contains(cleanedTargetTitle) && !cleanedTargetTitle.Contains(cleanedResultTitle))
            return 0;

        // 6. 综合评分 (权重: Title 60, Artist 25, Duration 15)
        int score = 0;
        score += (int)(titleSim * 60);
        score += (int)(artistSim * 25);
        score += (int)(durationScore * 0.15);

        // 7. 特殊奖励
        if (cleanedTargetTitle == cleanedResultTitle) score += 5;
        if (cleanedTargetArtist == cleanedResultArtist) score += 5;

        // 8. 歌手包含奖励 (处理合唱情况)
        if (artistSim < 1.0 && (cleanedResultArtist.Contains(cleanedTargetArtist) || cleanedTargetArtist.Contains(cleanedResultArtist)))
            score += 10;

        return Math.Clamp(score, 0, 100);
    }

    public static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        
        var normalized = ChineseScriptConverter.ToSimplified(value).ToLowerInvariant();
        normalized = RemoveDiacritics(normalized);

        // 移除常见平台噪声标签
        var noNoise = Regex.Replace(normalized, @"\s*[\(\[（【](explicit|deluxe|digital|premium|album|edit|version|special|anniversary|studio|remastered)[\)\]）】]\s*", " ", RegexOptions.IgnoreCase);
        
        // 移除所有括号内容进行纯基准对比（相似度算法对噪声敏感）
        var pureTitle = BracketSuffixRegex.Replace(noNoise, " ");
        
        // 移除歌手后缀
        var noFeatures = FeatureSuffixRegex.Replace(pureTitle, string.Empty);
        
        var sb = new StringBuilder();
        foreach (var ch in noFeatures)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)) sb.Append(ch);
            else sb.Append(' ');
        }
        
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder();
        foreach (var c in normalizedString)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                stringBuilder.Append(c);
        }
        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool HasVersionConflict(string target, string result, string keyword)
    {
        bool targetHas = target.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        bool resultHas = result.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        return targetHas != resultHas;
    }

    private static double CalculateSimilarity(string s, string t)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t)) return 0;
        if (s == t) return 1.0;
        int n = s.Length, m = t.Length;
        int[,] d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; d[i, 0] = i++) ;
        for (int j = 0; j <= m; d[0, j] = j++) ;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return 1.0 - ((double)d[n, m] / Math.Max(s.Length, t.Length));
    }
}
