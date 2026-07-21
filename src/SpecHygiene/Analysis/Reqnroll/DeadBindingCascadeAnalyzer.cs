using SpecHygiene.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SpecHygiene.Analysis.Reqnroll;

/// <summary>One method that becomes unreachable once the dead bindings are deleted.</summary>
public sealed class CascadeOrphan
{
    public string MethodName { get; init; } = "";
    public string ContainingType { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int LineNumber { get; init; }

    /// <summary>1 = reachable only from a dead binding; 2 = only from a round-1 orphan; and so on.</summary>
    public int Round { get; init; }
}

/// <summary>
/// Answers a question <see cref="UnusedCodeAnalyzer"/> structurally cannot: which methods become
/// unreachable ONCE the dead step bindings are deleted? Bindings are reflection-invoked, so they sit in
/// ExcludeAttributes and their private helpers always look "used" — used by the very code you are about
/// to remove.
/// <para>
/// This is dead-step-finder's orphan-check idea (Program.cs) on a better engine. Theirs is
/// "current-file scope only, not repo-wide", matches bare names as text, and is explicitly
/// "single-pass, not transitive" — its comment tells you to re-run manually after each deletion. This
/// is repo-wide, symbol-based, and iterates to a fixpoint, so a helper reachable only through another
/// orphan is found in the same run.
/// </para>
/// <para>
/// SPECULATIVE BY NATURE, and reported as candidates only. It answers "what dies IF you delete the dead
/// bindings" — stacking an assumption on a dead list that is itself only as good as its inputs. Never
/// auto-delete from this. It also inherits (and compounds) the base analyzer's reflection/DI blind spot.
/// </para>
/// </summary>
public sealed class DeadBindingCascadeAnalyzer(UnusedCodeAnalysisSettings settings)
{
    private readonly UnusedCodeAnalyzer _eligibility = new(settings);

    /// <param name="csFiles">The same source set the coverage run scanned.</param>
    /// <param name="deadBindings">Bindings confirmed unused. Indeterminate ones MUST NOT be passed —
    /// seeding on "we could not tell" would cascade a guess into deletion advice.</param>
    public List<CascadeOrphan> Analyze(IEnumerable<string> csFiles, IEnumerable<StepDefinitionInfo> deadBindings)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var file in csFiles)
        {
            try { trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file)), parseOptions, path: file)); }
            catch (IOException) { /* unreadable — skip */ }
        }

        var compilation = CSharpCompilation.Create("CascadeScan", trees, GetRuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Outgoing references per enclosing method, over EVERY method — bindings included. The seed is
        // excluded from the base analyzer's candidates, so a graph built only over candidates would omit
        // the binding bodies and removing the seed would change nothing. Eligibility gates what we
        // REPORT, never what participates in the graph.
        var refsBy = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var rootRefs = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var declared = new Dictionary<IMethodSymbol, (SyntaxNode Decl, string File, int Line)>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is MethodDeclarationSyntax md && model.GetDeclaredSymbol(md) is IMethodSymbol declSym)
                {
                    declared.TryAdd(declSym.OriginalDefinition, (md, tree.FilePath,
                        md.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    continue;
                }

                if (node is not SimpleNameSyntax name) continue;

                var info = model.GetSymbolInfo(name);
                var targets = new List<ISymbol?> { info.Symbol };
                targets.AddRange(info.CandidateSymbols);

                // Attribute the reference to its nearest enclosing method. Anything outside a method —
                // a field/property initializer, a constructor, an attribute argument — is a LIVE ROOT:
                // deleting bindings never removes it, so whatever it names stays alive.
                var enclosing = EnclosingMethod(node, model);
                foreach (var t in targets)
                {
                    if (t is null) continue;
                    var target = t.OriginalDefinition;
                    if (enclosing is null) rootRefs.Add(target);
                    else
                    {
                        if (!refsBy.TryGetValue(enclosing, out var set))
                            refsBy[enclosing] = set = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                        set.Add(target);
                    }
                }
            }
        }

        var seed = SeedDeadBindings(deadBindings, declared.Keys);
        if (seed.Count == 0) return new List<CascadeOrphan>();

        // The orphan question is DIFFERENTIAL: a helper is orphaned by the seed only if it is reachable
        // WHILE the seed is alive but unreachable ONCE it is removed. Computing "no live caller after the
        // seed is deleted" alone over-reports — it also flags methods that are dead for their OWN reasons
        // (called by nothing, or only by a caller outside the scanned files). Those belong to the base
        // unused-code analysis, not the cascade.
        //
        // So: (1) baseline fixpoint with the seed ALIVE finds everything independently dead; (2) the
        // seeded fixpoint starts from seed ∪ baseline, so only methods killed BECAUSE of the seed removal
        // are newly recorded, with round numbers that count seed-driven waves cleanly. This also cancels
        // the out-of-scope-caller blind spot: such a method is dead in the baseline too, so it drops out.
        var baseline = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        RunToFixpoint(baseline, refsBy, rootRefs, declared);   // seed stays live here

        var seededDead = new HashSet<IMethodSymbol>(seed, SymbolEqualityComparer.Default);
        seededDead.UnionWith(baseline);
        var orphanRounds = RunToFixpoint(seededDead, refsBy, rootRefs, declared);

        return orphanRounds
            .Select(kv => new CascadeOrphan
            {
                MethodName = kv.Key.Name,
                ContainingType = kv.Key.ContainingType?.Name ?? "",
                FilePath = declared[kv.Key].File,
                LineNumber = declared[kv.Key].Line,
                Round = kv.Value,
            })
            .OrderBy(o => o.Round).ThenBy(o => o.FilePath).ThenBy(o => o.LineNumber)
            .ToList();
    }

    /// <summary>
    /// Grows <paramref name="dead"/> to its fixpoint: repeatedly kill every reportable method that no
    /// live reference reaches, until none remain. Live references are recomputed each round from the
    /// roots plus every still-live method, so a method killed one round stops propagating the next — that
    /// is what makes it transitive. Returns the round in which each NEWLY killed method fell (methods
    /// already in <paramref name="dead"/> on entry are not recorded). <paramref name="dead"/> is mutated.
    /// </summary>
    private Dictionary<IMethodSymbol, int> RunToFixpoint(
        HashSet<IMethodSymbol> dead,
        Dictionary<IMethodSymbol, HashSet<ISymbol>> refsBy,
        HashSet<ISymbol> rootRefs,
        Dictionary<IMethodSymbol, (SyntaxNode Decl, string File, int Line)> declared)
    {
        var rounds = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var round = 0;

        // `dead` only grows and is bounded by the declared set, so this terminates.
        while (true)
        {
            round++;

            var live = new HashSet<ISymbol>(rootRefs, SymbolEqualityComparer.Default);
            foreach (var (m, refs) in refsBy)
                if (!dead.Contains(m)) live.UnionWith(refs);

            var newlyDead = new List<IMethodSymbol>();
            foreach (var (m, _) in declared)
            {
                if (dead.Contains(m)) continue;
                if (live.Contains(m)) continue;
                if (!_eligibility.IsReportableMethod(m)) continue;   // same conservatism as the base analyzer
                newlyDead.Add(m);
            }

            if (newlyDead.Count == 0) break;

            foreach (var m in newlyDead)
            {
                dead.Add(m);
                rounds[m] = round;
            }
        }

        return rounds;
    }

    /// <summary>
    /// Map dead bindings onto method symbols. Matched on file + containing type + name, then
    /// disambiguated by line. An ambiguity we cannot resolve (overloads on the same line) is NOT
    /// seeded — a missed cascade is a safe miss; killing the wrong overload is not.
    /// </summary>
    private static HashSet<IMethodSymbol> SeedDeadBindings(
        IEnumerable<StepDefinitionInfo> deadBindings, IEnumerable<IMethodSymbol> declared)
    {
        var byKey = new Dictionary<(string File, string Type, string Name), List<IMethodSymbol>>();
        foreach (var m in declared)
        {
            var file = m.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "";
            var key = (file, m.ContainingType?.Name ?? "", m.Name);
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<IMethodSymbol>();
            list.Add(m);
        }

        var seed = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var b in deadBindings)
        {
            if (!byKey.TryGetValue((b.FilePath, b.ClassName, b.MethodName), out var matches)) continue;
            if (matches.Count == 1) { seed.Add(matches[0]); continue; }

            // Overloads: the binding's line is the ATTRIBUTE's line, so pick the nearest declaration
            // at or after it. Still ambiguous -> skip rather than guess.
            var best = matches
                .Select(m => (Sym: m, Line: m.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0))
                .Where(x => x.Line >= b.LineNumber)
                .OrderBy(x => x.Line)
                .ToList();
            if (best.Count > 0) seed.Add(best[0].Sym);
        }
        return seed;
    }

    private static IMethodSymbol? EnclosingMethod(SyntaxNode node, SemanticModel model)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is MethodDeclarationSyntax md)
                return model.GetDeclaredSymbol(md)?.OriginalDefinition;
            // A local function's references belong to its containing method; keep walking.
            // Anything else (initializers, ctors, attributes) falls through to null = live root.
        }
        return null;
    }

    private static IEnumerable<MetadataReference> GetRuntimeReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { /* skip unreadable */ }
        }
        return refs;
    }
}
