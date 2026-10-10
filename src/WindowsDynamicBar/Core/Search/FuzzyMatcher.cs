using System;
using System.Collections.Generic;

namespace WindowsDynamicBar.Core.Search;

public static class FuzzyMatcher
{
    public static (bool Matched, int Score, List<int> MatchedIndices) Match(string target, string query, int launchCount = 0)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
            return (false, 0, new List<int>());

        string tLower = target.ToLowerInvariant();
        string qLower = query.Trim().ToLowerInvariant();

        if (qLower.Length == 0)
            return (false, 0, new List<int>());

        // 1. Substring match
        int subIdx = tLower.IndexOf(qLower, StringComparison.Ordinal);
        if (subIdx >= 0)
        {
            int baseScore = 100 - subIdx;
            if (subIdx == 0)
            {
                baseScore += 60; // beginning of string
            }
            else if (IsWordBoundary(target, subIdx))
            {
                baseScore += 25; // word boundary
            }

            baseScore += 8 * launchCount;

            var indices = new List<int>(qLower.Length);
            for (int i = 0; i < qLower.Length; i++)
            {
                indices.Add(subIdx + i);
            }

            return (true, baseScore, indices);
        }

        // 2. In-order subsequence match
        int tIdx = 0;
        int qIdx = 0;
        int firstMatch = -1;
        int lastMatch = -1;
        var subSeqIndices = new List<int>(qLower.Length);

        while (tIdx < tLower.Length && qIdx < qLower.Length)
        {
            if (tLower[tIdx] == qLower[qIdx])
            {
                if (firstMatch < 0) firstMatch = tIdx;
                lastMatch = tIdx;
                subSeqIndices.Add(tIdx);
                qIdx++;
            }
            tIdx++;
        }

        if (qIdx == qLower.Length)
        {
            // Matched all characters of query
            int span = lastMatch - firstMatch + 1;
            int skipped = span - qLower.Length;
            int score = Math.Max(5, 40 - 2 * skipped);

            if (firstMatch == 0) score += 15;
            else if (IsWordBoundary(target, firstMatch)) score += 10;

            score += 8 * launchCount;

            return (true, score, subSeqIndices);
        }

        return (false, 0, new List<int>());
    }

    private static bool IsWordBoundary(string text, int index)
    {
        if (index <= 0 || index >= text.Length) return false;
        char prev = text[index - 1];
        char curr = text[index];

        if (char.IsWhiteSpace(prev) || prev == '-' || prev == '_' || prev == '.' || prev == '/' || prev == '\\')
            return true;

        if (char.IsLower(prev) && char.IsUpper(curr))
            return true;

        return false;
    }
}
