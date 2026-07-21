using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SpecHygiene.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SpecHygiene.Analysis;

/// <summary>
/// Semantic (Roslyn-based) dead-code detection. Replaces the earlier text/regex heuristic, whose
/// core flaw was matching on bare names against one concatenated blob — name collisions across types
/// and incidental mentions in comments/strings made almost everything look "used".
///
/// Approach: parse every scanned .cs file into a <see cref="CSharpCompilation"/> (with the runtime
/// reference assemblies so BCL types bind), then do ONE semantic pass that resolves every identifier to
/// its <see cref="ISymbol"/> and records it in a "referenced" set. A declared method/class/interface
/// whose symbol is not in that set is unused. Because this is symbol-based, overloads, method groups,
/// interface/inheritance use, and same-named members in different types are all handled correctly.
///
/// It is deliberately conservative (deleting live code is worse than missing dead code): overrides,
/// interface implementations and abstract members are never flagged; a type is considered used if any
/// of its members is referenced. Known limits (documented, not bugs): symbols reached only via
/// reflection / DI / serialization, and mutually-recursive dead clusters, can read as used/unused
/// respectively — mitigated by the ExcludeAttributes / ExcludePatterns settings.
/// </summary>
public class UnusedCodeAnalyzer
{
    private readonly UnusedCodeAnalysisSettings _settings;

    public UnusedCodeAnalyzer(UnusedCodeAnalysisSettings settings) => _settings = settings;

    public UnusedCodeReport Analyze(IEnumerable<string> directories)
    {
        if (!_settings.Enabled)
        {
            Console.WriteLine("   Unused code analysis is DISABLED in settings");
            return new UnusedCodeReport();
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("   Unused Code Analysis (Roslyn / semantic)");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        var roots = directories.ToList();
        var report = new UnusedCodeReport { ScannedRoots = roots };

        // Path-based, not solution-aware: state the scope up front so a scoped run is never read as
        // solution-wide. A reference outside these roots is invisible to the graph.
        Console.WriteLine("   Scope (recursive, all .cs under these roots — NOT solution/project aware):");
        foreach (var r in roots) Console.WriteLine($"     - {r}");
        Console.WriteLine();

        // Phase 1: collect files.
        Console.WriteLine("Phase 1/3: Collecting C# files...");
        var files = CollectCSharpFiles(roots);
        report.TotalFilesScanned = files.Count;
        Console.WriteLine($"         OK Found {files.Count} C# files");
        if (files.Count == 0) return report;

        // Phase 2: parse + compile.
        Console.WriteLine("Phase 2/3: Parsing and building the semantic model...");
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new ConcurrentBag<SyntaxTree>();
        var parsed = 0;
        var parseChunk = Math.Max(1, files.Count / 10);
        var parseLock = new object();
        Parallel.ForEach(files, file =>
        {
            try
            {
                var text = File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text), parseOptions, path: file));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"         WARNING: could not parse {Path.GetFileName(file)} - {ex.Message}");
            }

            // Coarse progress so the phase never looks hung. Interlocked gives each thread a unique
            // count, so only one crosses each 10% boundary; the lock just keeps prints from interleaving.
            var done = System.Threading.Interlocked.Increment(ref parsed);
            if (done % parseChunk == 0 || done == files.Count)
                lock (parseLock) Console.WriteLine($"         Parsing: {done * 100 / files.Count}% ({done:N0}/{files.Count:N0} files)");
        });

        var compilation = CSharpCompilation.Create(
            "DeadCodeScan",
            trees,
            GetRuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Console.WriteLine($"         OK Compiled {trees.Count} syntax tree(s)");

        // Phase 3: one semantic pass — gather references and candidate declarations together.
        Console.WriteLine("Phase 3/3: Resolving references...");
        var referenced = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var candidates = new Dictionary<ISymbol, (SyntaxNode Decl, SymbolKind Kind)>(SymbolEqualityComparer.Default);
        var gate = new object();

        // Every raw string-literal value in the source, keyed to where it occurs. The one hidden-usage
        // channel a semantic reference cannot see is reflection by name (GetMethod("X"), Type.GetType,
        // a DI key). nameof is already a real reference — Roslyn resolves it — so only *string* literals
        // are collected here. Used to QUALIFY an unused verdict, never to overturn it.
        var literalLocations = new Dictionary<string, string>(StringComparer.Ordinal);
        var literalGate = new object();

        var totalTrees = trees.Count;
        var resolved = 0;
        var resolveChunk = Math.Max(1, totalTrees / 20);
        var resolveLock = new object();

        Parallel.ForEach(compilation.SyntaxTrees, tree =>
        {
            var model = compilation.GetSemanticModel(tree);
            var localRefs = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var localCands = new List<(ISymbol Sym, SyntaxNode Decl, SymbolKind Kind)>();
            var localLiterals = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                // References: every name that resolves to a symbol (calls, method groups, type uses,
                // base lists, attributes, nameof/typeof, generic args — all are SimpleNameSyntax).
                if (node is SimpleNameSyntax name)
                {
                    var info = model.GetSymbolInfo(name);
                    AddRef(localRefs, info.Symbol);
                    foreach (var c in info.CandidateSymbols) AddRef(localRefs, c);
                    continue;
                }

                // Identifier-shaped string literals only. A value that is not a legal identifier can't
                // name a member, so it is noise; this also keeps the map small.
                if (node is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var v = lit.Token.ValueText;
                    if (IsIdentifierLike(v) && !localLiterals.ContainsKey(v))
                        localLiterals[v] = $"{Path.GetFileName(tree.FilePath)}:{lit.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                    continue;
                }

                // Declarations we might flag.
                switch (node)
                {
                    case MethodDeclarationSyntax md when _settings.AnalyzeMethods:
                        if (model.GetDeclaredSymbol(md) is IMethodSymbol ms)
                            localCands.Add((ms, md, SymbolKind.Method));
                        break;
                    case ClassDeclarationSyntax cd when _settings.AnalyzeClasses:
                        if (model.GetDeclaredSymbol(cd) is INamedTypeSymbol cs)
                            localCands.Add((cs, cd, SymbolKind.NamedType));
                        break;
                    case RecordDeclarationSyntax rd when _settings.AnalyzeClasses:
                        if (model.GetDeclaredSymbol(rd) is INamedTypeSymbol rs)
                            localCands.Add((rs, rd, SymbolKind.NamedType));
                        break;
                    case InterfaceDeclarationSyntax id when _settings.AnalyzeInterfaces:
                        if (model.GetDeclaredSymbol(id) is INamedTypeSymbol isym)
                            localCands.Add((isym, id, SymbolKind.NamedType));
                        break;
                }
            }

            lock (gate)
            {
                referenced.UnionWith(localRefs);
                foreach (var c in localCands)
                    if (!candidates.ContainsKey(c.Sym)) candidates[c.Sym] = (c.Decl, c.Kind);   // dedupe partials
            }
            lock (literalGate)
                foreach (var kv in localLiterals) literalLocations.TryAdd(kv.Key, kv.Value);

            // Coarse progress — this pass (semantic model + node walk) is the slow, previously-silent
            // part; print ~20 steps so it visibly advances instead of looking hung.
            var done = System.Threading.Interlocked.Increment(ref resolved);
            if (done % resolveChunk == 0 || done == totalTrees)
                lock (resolveLock) Console.WriteLine($"         Resolving: {done * 100 / totalTrees}% ({done:N0}/{totalTrees:N0} files)");
        });

        // Classify.
        var methodCands = candidates.Where(c => c.Value.Kind == SymbolKind.Method).ToList();
        var typeCands = candidates.Where(c => c.Value.Kind == SymbolKind.NamedType).ToList();

        var consideredMethods = 0; var consideredClasses = 0; var consideredInterfaces = 0;

        foreach (var (sym, info) in methodCands)
        {
            var m = (IMethodSymbol)sym;
            if (!ShouldConsiderMethod(m)) continue;
            consideredMethods++;
            if (!referenced.Contains(m))
                report.UnusedMethods.Add(ToInfo(m, info.Decl, CodeElementType.Method,
                    "No references found (semantic)"));
        }

        foreach (var (sym, info) in typeCands)
        {
            var t = (INamedTypeSymbol)sym;
            var isInterface = t.TypeKind == TypeKind.Interface;
            if (isInterface) { if (_settings.AnalyzeInterfaces) consideredInterfaces++; }
            else { if (_settings.AnalyzeClasses) consideredClasses++; }

            if (!ShouldConsiderType(t)) continue;
            if (referenced.Contains(t)) continue;

            if (isInterface)
                report.UnusedInterfaces.Add(ToInfo(t, info.Decl, CodeElementType.Interface,
                    "No implementations or references found"));
            else
                report.UnusedClasses.Add(ToInfo(t, info.Decl, CodeElementType.Class,
                    "No instantiation or references found"));
        }

        report.TotalMethodsScanned = consideredMethods;
        report.TotalClassesScanned = consideredClasses;
        report.TotalInterfacesScanned = consideredInterfaces;

        // Qualify each finding whose name appears as a string literal — the reflection channel the
        // reference graph cannot see. This annotates; it does not remove, so a genuinely-dead method
        // whose name coincides with an unrelated string still shows (with a note), never silently
        // dropped. Its OWN declaration is not a string literal, so no self-match.
        foreach (var info in report.UnusedMethods.Concat(report.UnusedClasses).Concat(report.UnusedInterfaces))
            if (literalLocations.TryGetValue(info.Name, out var where))
                info.StringLiteralHint = where;

        report.UnusedMethods = report.UnusedMethods.OrderBy(u => u.Project).ThenBy(u => u.ContainingType).ThenBy(u => u.Name).ToList();
        report.UnusedClasses = report.UnusedClasses.OrderBy(u => u.Project).ThenBy(u => u.Name).ToList();
        report.UnusedInterfaces = report.UnusedInterfaces.OrderBy(u => u.Project).ThenBy(u => u.Name).ToList();

        sw.Stop();
        report.AnalysisDurationMs = sw.ElapsedMilliseconds;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("   Unused Code Analysis Results:");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"   Unused methods:    {report.UnusedMethods.Count} of {consideredMethods} ({report.UnusedMethodPercentage:F1}%)");
        Console.WriteLine($"   Unused classes:    {report.UnusedClasses.Count} of {consideredClasses}");
        Console.WriteLine($"   Unused interfaces: {report.UnusedInterfaces.Count} of {consideredInterfaces}");
        if (report.StringLiteralHintCount > 0)
            Console.WriteLine($"   NOTE: {report.StringLiteralHintCount} finding(s) share a name with a string literal - check for reflection before deleting");
        Console.WriteLine($"   Scope: {report.ScannedRoots.Count} root(s), {report.TotalFilesScanned} files (path-based, not solution-wide)");
        Console.WriteLine($"   Analysis time:     {sw.ElapsedMilliseconds}ms");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();

        return report;
    }

    // ----- reference collection ------------------------------------------------------------------

    /// <summary>Record a referenced symbol (its original definition), and — for members — mark the
    /// containing type used too, so e.g. a static class whose extension methods are called isn't flagged.</summary>
    private static void AddRef(HashSet<ISymbol> set, ISymbol? symbol)
    {
        if (symbol is null) return;

        // An extension method invoked as `receiver.Method()` resolves to its REDUCED form, whose symbol
        // identity differs from the original static definition the declaration scan records. Unreduce it
        // (ReducedFrom is null for everything else) so the reference matches the declaration — otherwise
        // EVERY receiver-syntax extension call is invisible and the method reads as falsely unused.
        if (symbol is IMethodSymbol { ReducedFrom: { } original })
            symbol = original;

        var def = symbol.OriginalDefinition;
        set.Add(def);
        if (def.ContainingType is { } ct &&
            def.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)
            set.Add(ct.OriginalDefinition);
    }

    // ----- candidacy rules -----------------------------------------------------------------------

    /// <summary>
    /// Eligibility for being REPORTED unused. Exposed so the dead-binding cascade applies exactly the
    /// same conservatism (no overrides, no interface impls, no public unless opted in, same exclude
    /// patterns and DI hooks) rather than reimplementing it and drifting.
    /// </summary>
    internal bool IsReportableMethod(IMethodSymbol m) => ShouldConsiderMethod(m);

    private bool ShouldConsiderMethod(IMethodSymbol m)
    {
        if (m.MethodKind != MethodKind.Ordinary) return false;    // skip ctors, operators, accessors, finalizers
        if (m.IsImplicitlyDeclared) return false;
        if (m.IsAbstract) return false;                            // contract members; used via overrides
        if (m.IsOverride) return false;
        if (m.ExplicitInterfaceImplementations.Length > 0) return false;
        if (m.ContainingType is null || m.ContainingType.TypeKind == TypeKind.Interface) return false;
        if (IsInterfaceImplementation(m)) return false;            // implicit interface impl; used via the interface

        switch (m.DeclaredAccessibility)
        {
            case Accessibility.Private when !_settings.IncludePrivateMethods: return false;
            case Accessibility.Internal or Accessibility.ProtectedAndInternal or Accessibility.ProtectedOrInternal
                when !_settings.IncludeInternalMethods: return false;
            case Accessibility.Public or Accessibility.Protected when !_settings.IncludePublicMembers: return false;
        }

        if (IsExcludedByPattern(m.Name)) return false;
        if (HasExcludedAttribute(m)) return false;
        // DI registration / setup methods take a container (IObjectContainer, IServiceCollection, …) and
        // are invoked by the framework/host, never by a direct call — so they'd always look unused. Treat
        // them as used (e.g. RegisterOrderContext(IObjectContainer) in a Reqnroll test-dependencies class).
        if (m.Parameters.Any(p => DiContainerTypeNames.Contains(p.Type.Name))) return false;
        return true;
    }

    // Well-known DI/host container types. A method that takes one is a registration/setup hook the
    // container calls by reflection — matched by simple type name so namespace/version doesn't matter.
    private static readonly HashSet<string> DiContainerTypeNames = new(StringComparer.Ordinal)
    {
        "IObjectContainer",       // Reqnroll / SpecFlow BoDi
        "IServiceCollection", "IServiceProvider", "IServiceScope",   // Microsoft.Extensions.DependencyInjection
        "ContainerBuilder", "IContainer",                            // Autofac
        "IHostBuilder", "IWebHostBuilder", "WebApplicationBuilder", "IApplicationBuilder", // ASP.NET host
        "IWindsorContainer", "IUnityContainer", "IKernel",           // Castle Windsor / Unity / Ninject
    };

    private bool ShouldConsiderType(INamedTypeSymbol t)
    {
        if (t.IsImplicitlyDeclared) return false;
        // Public/protected types are the external surface — same trust argument as public methods.
        if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected && !_settings.IncludePublicMembers)
            return false;
        if (IsExcludedByPattern(t.Name)) return false;
        if (HasExcludedAttribute(t)) return false;
        // Entry-point container is never dead.
        if (t.GetMembers("Main").OfType<IMethodSymbol>().Any(x => x.IsStatic)) return false;
        return true;
    }

    /// <summary>True when this method implicitly implements an interface member of its containing type.</summary>
    private static bool IsInterfaceImplementation(IMethodSymbol m)
    {
        var type = m.ContainingType;
        if (type is null) return false;
        foreach (var iface in type.AllInterfaces)
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = type.FindImplementationForInterfaceMember(member);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(impl, m)) return true;
            }
        return false;
    }

    private bool HasExcludedAttribute(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var full = attr.AttributeClass?.Name ?? "";
            var shortName = full.EndsWith("Attribute", StringComparison.Ordinal) ? full[..^9] : full;
            if (_settings.ExcludeAttributes.Contains(shortName, StringComparer.OrdinalIgnoreCase) ||
                _settings.ExcludeAttributes.Contains(full, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ----- report building -----------------------------------------------------------------------

    /// <summary>A value that could legally be a C# member name — so worth checking as a reflection key.</summary>
    private static bool IsIdentifierLike(string s) =>
        !string.IsNullOrEmpty(s)
        && (char.IsLetter(s[0]) || s[0] == '_')
        && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private UnusedCodeInfo ToInfo(ISymbol sym, SyntaxNode decl, CodeElementType kind, string reason)
    {
        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource) ?? decl.GetLocation();
        var filePath = loc.SourceTree?.FilePath ?? decl.SyntaxTree.FilePath;
        var line = loc.GetLineSpan().StartLinePosition.Line + 1;

        return new UnusedCodeInfo
        {
            Name = sym.Name,
            ElementType = kind,
            ContainingType = sym is IMethodSymbol m ? (m.ContainingType?.Name ?? "") : "",
            FilePath = filePath,
            LineNumber = line,
            Project = ExtractProjectName(filePath),
            Visibility = AccessibilityText(sym.DeclaredAccessibility),
            Signature = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IsStatic = sym.IsStatic,
            Reason = reason,
        };
    }

    private static string AccessibilityText(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "internal",
    };

    // ----- references from the runtime so BCL/framework types bind -------------------------------

    private static IEnumerable<MetadataReference> GetRuntimeReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { /* skip unreadable */ }
        }
        return refs;
    }

    // ----- file collection + settings helpers (unchanged behaviour) ------------------------------

    private List<string> CollectCSharpFiles(List<string> directories)
    {
        var files = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Console.WriteLine($"         WARNING: directory not found: {directory}");
                continue;
            }
            files.AddRange(Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedFile(f)));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsExcludedFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        foreach (var excludeDir in _settings.ExcludeDirectories)
            if (filePath.Contains(Path.DirectorySeparatorChar + excludeDir + Path.DirectorySeparatorChar))
                return true;
        foreach (var pattern in _settings.ExcludeFilePatterns)
            if (MatchesWildcard(fileName, pattern))
                return true;
        return false;
    }

    private bool IsExcludedByPattern(string name)
    {
        foreach (var pattern in _settings.ExcludePatterns)
            if (MatchesWildcard(name, pattern)) return true;
        return false;
    }

    private static string ExtractProjectName(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var csproj = Directory.GetFiles(directory, "*.csproj");
            if (csproj.Length > 0) return Path.GetFileNameWithoutExtension(csproj[0]);
            directory = Path.GetDirectoryName(directory);
        }
        var parts = filePath.Split(Path.DirectorySeparatorChar);
        return parts.Length >= 2 ? parts[^2] : "Unknown";
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
    }
}
