namespace RensaioBackend.Utils
{
    /// <summary>
    /// Shared helpers for building normalized, deduplicated title lists across scrobbler /
    /// metadata providers. Providers frequently report the same title under different keys
    /// (e.g. canonical title, romaji/english/native maps, synonyms, aliases, alt-titles),
    /// which historically produced inconsistent results: the main title sometimes appeared
    /// inside the alternate-title list, and alternates could contain duplicates.
    ///
    /// Convention used everywhere in Rensaio:
    ///   - A series has exactly ONE primary <c>title</c>.
    ///   - <c>alternateTitles</c> / <c>alternativeTitles</c> lists contain ONLY distinct
    ///     alternates — the primary title is never included (compared case-insensitively).
    /// </summary>
    public static class TitleListBuilder
    {
        /// <summary>
        /// Trims whitespace; returns the raw input when blank, or null for null/empty input.
        /// </summary>
        private static string? Clean(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();

        /// <summary>
        /// Deduplicates a candidate list case-insensitively, dropping null/blank entries.
        /// The first occurrence wins (stable), so callers should order candidates by priority.
        /// </summary>
        public static List<string> Distinct(IEnumerable<string?>? candidates)
        {
            if (candidates == null) return [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var raw in candidates)
            {
                var t = Clean(raw);
                if (t == null) continue;
                if (seen.Add(t)) result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// Picks the primary title from a priority-ordered candidate list, falling back to
        /// <paramref name="fallback"/> when every candidate is blank/null. Returns null if even
        /// the fallback is blank.
        /// </summary>
        public static string? ResolvePrimary(IEnumerable<string?>? candidates, string? fallback = null)
        {
            var list = Distinct(candidates);
            if (list.Count > 0) return list[0];
            return Clean(fallback);
        }

        /// <summary>
        /// Builds the distinct alternate-title list for a series: every non-blank candidate
        /// except the primary title (case-insensitive compare). The result never contains the
        /// primary title and never contains duplicates.
        /// </summary>
        /// <param name="primary">The resolved primary title (null/blank → nothing is excluded).</param>
        /// <param name="candidates">All title candidates (primary + alternates, any order).</param>
        public static List<string> BuildAlternates(string? primary, IEnumerable<string?>? candidates)
        {
            var result = Distinct(candidates);
            if (string.IsNullOrWhiteSpace(primary))
                return result;
            return result
                .Where(t => !string.Equals(t, primary, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}