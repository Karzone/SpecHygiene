namespace SpecHygiene.Analysis.Reqnroll;

/// <summary>
/// Prunes the parametric-binding scan. The scan is O(unique steps × parametric bindings) regex
/// matches; at scale (the sample suite: ~160k steps, thousands of bindings) that dominates. This indexes each
/// binding by the literal words it REQUIRES, so a step only tests bindings whose required words all
/// appear in it — cutting the candidate set from "every binding" to "a handful".
/// <para>
/// SAFETY — the only failure that matters is a false SKIP (dropping a binding that would have matched
/// = a false dead). Two guarantees prevent it:
/// </para>
/// <list type="number">
/// <item>Required-word extraction is SOUND: a word is treated as required only when it must appear, as
/// a whitespace/anchor-delimited token, in every string the pattern matches. Anything uncertain
/// (top-level <c>|</c> alternation, a word glued to a parameter/group/optional/quantifier/escape, a
/// short word) is dropped or the whole binding is sent to the always-scan bucket. Dropping only ever
/// WEAKENS the filter — it can add false candidates, never remove a real one.</item>
/// <item>The index only decides which bindings to TEST. The verdict still comes from
/// <see cref="BindingMatcher.Matches"/>, so an over-included candidate is simply rejected there.</item>
/// </list>
/// <para>The definitive test is that indexed matching returns exactly what brute-force matching does.</para>
/// </summary>
public sealed class LiteralTokenIndex
{
    private const int MinWordLen = 3;   // shorter words are poor filters and often regex artifacts; dropping is safe

    private readonly List<CompiledBinding> _alwaysScan = new();
    private readonly Dictionary<string, List<Entry>> _byKey = new(StringComparer.Ordinal);

    private sealed record Entry(CompiledBinding Binding, string[] Required);

    public int IndexedCount { get; }
    public int AlwaysScanCount => _alwaysScan.Count;

    public LiteralTokenIndex(IReadOnlyList<CompiledBinding> parametric)
    {
        var withReq = new List<(CompiledBinding Binding, string[] Req)>();
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var b in parametric)
        {
            var req = RequiredWords(b);
            if (req.Length == 0) { _alwaysScan.Add(b); continue; }
            withReq.Add((b, req));
            foreach (var w in req) freq[w] = freq.GetValueOrDefault(w) + 1;
        }

        foreach (var (binding, req) in withReq)
        {
            // Key on the RAREST required word — the most selective, so buckets stay small.
            var key = req.MinBy(w => freq[w])!;
            if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<Entry>();
            list.Add(new Entry(binding, req));
            IndexedCount++;
        }
    }

    /// <summary>
    /// Bindings that must be tested against a step with these tokens: every always-scan binding, plus
    /// each indexed binding all of whose required words are present. Read-only after construction, so
    /// safe to call concurrently. Callers still apply keyword compatibility and Matches.
    /// </summary>
    public IEnumerable<CompiledBinding> Candidates(HashSet<string> stepTokens)
    {
        foreach (var b in _alwaysScan) yield return b;

        foreach (var tok in stepTokens)
        {
            if (!_byKey.TryGetValue(tok, out var list)) continue;
            foreach (var e in list)
                if (AllPresent(e.Required, stepTokens))
                    yield return e.Binding;   // each binding sits under exactly one key, so no dedupe needed
        }
    }

    private static bool AllPresent(string[] required, HashSet<string> stepTokens)
    {
        foreach (var w in required) if (!stepTokens.Contains(w)) return false;
        return true;
    }

    /// <summary>All alphanumeric runs of a step, lowercased. Keeps every length — a required word
    /// (min length 3) is checked for membership here, so the step set must be complete.</summary>
    public static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int i = 0, n = text.Length;
        while (i < n)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int j = i;
            while (j < n && char.IsLetterOrDigit(text[j])) j++;
            set.Add(text.Substring(i, j - i).ToLowerInvariant());
            i = j;
        }
        return set;
    }

    // ---- sound required-word extraction ------------------------------------------------------

    private static readonly string[] Empty = System.Array.Empty<string>();

    private static string[] RequiredWords(CompiledBinding b)
    {
        var pattern = b.Definition.Pattern;

        // A bare [Given] (R9) binds by its method-name convention — a pure space-separated literal.
        if (string.IsNullOrEmpty(pattern))
        {
            var conv = MethodNameConvention.GenerateConventionPattern(b.Definition.MethodName, b.Definition.ParameterCount);
            return conv is null ? Empty : DelimitedWords(conv);   // null == can never bind -> always scan
        }

        // A top-level '|' (raw-regex alternation) means no literal is guaranteed. Bailing is safe.
        // ('|' is a literal in a Cucumber expression, but bailing there just always-scans it — safe.)
        if (pattern.IndexOf('|') >= 0) return Empty;

        return DelimitedWords(pattern);
    }

    /// <summary>
    /// Alphanumeric runs bounded on BOTH sides by a boundary (start, end, whitespace, '^' or '$'),
    /// length ≥ <see cref="MinWordLen"/>, lowercased and deduped. Bounded-ness is what makes token
    /// membership sound: a word glued to a parameter, group, optional, quantifier or escape has a
    /// non-boundary neighbour and is dropped, so we never claim a word that could be optional or that
    /// might appear only as a substring of a step token.
    /// </summary>
    private static string[] DelimitedWords(string pattern)
    {
        var words = new List<string>();
        int i = 0, n = pattern.Length;
        while (i < n)
        {
            if (!char.IsLetterOrDigit(pattern[i])) { i++; continue; }
            int j = i;
            while (j < n && char.IsLetterOrDigit(pattern[j])) j++;

            var beforeOk = i == 0 || IsBoundary(pattern[i - 1]);
            var afterOk = j == n || IsBoundary(pattern[j]);
            if (beforeOk && afterOk && j - i >= MinWordLen)
                words.Add(pattern.Substring(i, j - i).ToLowerInvariant());

            i = j;
        }
        return words.Count == 0 ? Empty : words.Distinct().ToArray();
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c == '^' || c == '$';
}
