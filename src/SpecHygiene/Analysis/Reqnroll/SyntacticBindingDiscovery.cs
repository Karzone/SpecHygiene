using System.Text.RegularExpressions;
using SpecHygiene.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SpecHygiene.Analysis.Reqnroll;

/// <summary>
/// Finds Reqnroll step bindings by parsing C# SYNTAX — no compilation, no references, no build.
/// <para>
/// Syntactic, not semantic, and deliberately so. A semantic model would need Reqnroll referenced to
/// resolve <c>[Binding]</c>/<c>[Given]</c>/<c>[StepArgumentTransformation]</c>; our compilation
/// references only the BCL (dropping the Reqnroll dependency was the point), so those attributes bind
/// to error-type symbols and reading them back is fragile. Attribute names and string literals are
/// exactly what the syntax tree gives us reliably, and they are all this needs.
/// </para>
/// <para>
/// Replaces the regex parser's blind spots: it required <c>("...")</c> so bare <c>[Given]</c>
/// method-name-convention bindings were INVISIBLE (not mis-reported — absent), and its
/// <c>@?"(.+?)"</c> capture mis-reads verbatim and escaped string literals. Roslyn gives the correct
/// unescaped literal value for free.
/// </para>
/// </summary>
public sealed class SyntacticBindingDiscovery
{
    private static readonly HashSet<string> StepAttributeNames = new(StringComparer.Ordinal)
    {
        "Given", "When", "Then", "StepDefinition", "And", "But",
    };

    public sealed class Result
    {
        public List<StepDefinitionInfo> Bindings { get; } = new();

        /// <summary>Cucumber parameter-type fragments discovered in source: enums (R6) + transforms (R7).</summary>
        public Dictionary<string, string> ParameterTypeFragments { get; } =
            new(DefaultCucumberExpressionParameterTypes.Fragments, StringComparer.Ordinal);

        /// <summary>
        /// Step-attributed methods whose <c>[Binding]</c> status could not be confirmed from source
        /// (base class declared elsewhere). Reported, never dropped and never dead — we cannot tell
        /// whether Reqnroll registers them.
        /// </summary>
        public List<(StepDefinitionInfo Binding, string Reason)> Unconfirmed { get; } = new();
    }

    /// <summary>Parse every file once and extract bindings, enums and transforms.</summary>
    public Result Discover(IEnumerable<string> csFiles)
    {
        var trees = new List<(string Path, CompilationUnitSyntax Root)>();
        foreach (var file in csFiles)
        {
            try
            {
                var text = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(text, path: file);
                trees.Add((file, (CompilationUnitSyntax)tree.GetRoot()));
            }
            catch (IOException) { /* unreadable file — skip, same as the old parser */ }
        }

        // Pass 1: every class in source, by simple name, with its own [Binding] flag and base names.
        // R11: [Binding] is Inherited = true, so a class counts if any ancestor carries it.
        var classes = new Dictionary<string, (bool HasBinding, List<string> Bases)>(StringComparer.Ordinal);
        foreach (var (_, root) in trees)
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var name = cls.Identifier.ValueText;
                var hasBinding = cls.AttributeLists.SelectMany(a => a.Attributes).Any(a => AttrName(a) == "Binding");
                var bases = cls.BaseList?.Types.Select(t => SimpleTypeName(t.Type)).Where(n => n.Length > 0).ToList()
                            ?? new List<string>();
                // A partial class can be declared more than once — merge rather than overwrite.
                if (classes.TryGetValue(name, out var prev))
                    classes[name] = (prev.HasBinding || hasBinding, prev.Bases.Concat(bases).Distinct().ToList());
                else
                    classes[name] = (hasBinding, bases);
            }

        var result = new Result();

        // Pass 2: enums (R6) and transforms (R7) — the fragments a Cucumber pattern may reference.
        foreach (var (_, root) in trees)
        {
            foreach (var e in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var members = e.Members.Select(m => Regex.Escape(m.Identifier.ValueText)).ToList();
                if (members.Count == 0) continue;
                // Reqnroll auto-registers {EnumName} as a case-insensitive alternation of member names.
                result.ParameterTypeFragments[e.Identifier.ValueText] = $"(?i:{string.Join("|", members)})";
            }

            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var attr = m.AttributeLists.SelectMany(a => a.Attributes)
                            .FirstOrDefault(a => AttrName(a) == "StepArgumentTransformation");
                if (attr is null) continue;

                var regex = FirstStringArgument(attr);
                if (regex is null) continue;   // no regex -> Reqnroll infers from the method; not modelled

                // Name = "x" wins; otherwise the placeholder is named after the return type's simple name.
                var named = attr.ArgumentList?.Arguments
                    .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Name");
                var name = named is not null && named.Expression is LiteralExpressionSyntax lit
                    ? lit.Token.ValueText
                    : SimpleTypeName(m.ReturnType);
                if (name.Length > 0) result.ParameterTypeFragments[name] = regex;
            }
        }

        // Pass 3: the bindings themselves.
        foreach (var (path, root) in trees)
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var className = cls.Identifier.ValueText;
                var binding = ResolveBindingStatus(className, classes);

                foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
                    foreach (var attr in method.AttributeLists.SelectMany(a => a.Attributes))
                    {
                        var attrName = AttrName(attr);
                        if (!StepAttributeNames.Contains(attrName)) continue;
                        if (binding == BindingStatus.NotABinding) continue;   // Reqnroll never scans it

                        // Roslyn hands back the UNESCAPED literal value — correct for @"..." and "".
                        // An argument we cannot read (interpolated string / const) is NOT a convention
                        // binding: flag it so it becomes indeterminate, never a false "unused".
                        var patternKind = ClassifyPattern(attr, out var patternValue);

                        var info = new StepDefinitionInfo
                        {
                            MethodName = method.Identifier.ValueText,
                            ClassName = className,
                            FilePath = path,
                            Pattern = patternValue,
                            Type = ToType(attrName),
                            LineNumber = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            Project = ExtractProject(path),
                            ParameterCount = method.ParameterList.Parameters.Count,   // R9: convention needs this
                            UnresolvablePattern = patternKind == PatternKind.Unresolvable,
                        };
                        info.RegexPattern = info.Pattern;

                        if (binding == BindingStatus.Confirmed) result.Bindings.Add(info);
                        else result.Unconfirmed.Add((info,
                            $"[Binding] not confirmable from source on {className} or its base types"));
                    }
            }

        return result;
    }

    private enum BindingStatus { Confirmed, NotABinding, Unconfirmable }

    /// <summary>
    /// Walk the base chain by simple name. Confirmed when the class or an in-source ancestor carries
    /// [Binding]. If the chain leaves the source we scanned, we cannot tell — Unconfirmable, which the
    /// caller must treat as indeterminate rather than dead.
    /// </summary>
    private static BindingStatus ResolveBindingStatus(
        string className, Dictionary<string, (bool HasBinding, List<string> Bases)> classes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(className);
        var sawUnknownBase = false;

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            if (!classes.TryGetValue(name, out var info)) { sawUnknownBase = true; continue; }
            if (info.HasBinding) return BindingStatus.Confirmed;
            foreach (var b in info.Bases) queue.Enqueue(b);
        }

        // Every ancestor was in source and none had [Binding] -> Reqnroll genuinely never scans it.
        // An out-of-source ancestor might carry it, so we must not claim either way.
        return sawUnknownBase ? BindingStatus.Unconfirmable : BindingStatus.NotABinding;
    }

    /// <summary>Attribute name without namespace or the "Attribute" suffix.</summary>
    private static string AttrName(AttributeSyntax attr)
    {
        var name = attr.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attr.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "Attribute".Length)
            : name;
    }

    /// <summary>The pattern argument of a step attribute, classified so callers can tell a genuine
    /// method-name-convention binding (no argument) apart from one whose pattern we simply cannot read
    /// (interpolated string, const, concat).</summary>
    private enum PatternKind { None, Literal, Unresolvable }

    /// <summary>
    /// Classifies the first positional argument. <see cref="PatternKind.None"/> = no argument, a bare
    /// [Given] i.e. the method-name convention (R9). <see cref="PatternKind.Literal"/> = a plain string
    /// literal, its unescaped value in <paramref name="value"/>. <see cref="PatternKind.Unresolvable"/>
    /// = an argument is present but is not a plain literal (e.g. <c>$@"…{Const}…"</c>), so its real
    /// pattern is unknown to the syntactic pass.
    /// </summary>
    private static PatternKind ClassifyPattern(AttributeSyntax attr, out string value)
    {
        value = "";
        var args = attr.ArgumentList?.Arguments;
        if (args is null) return PatternKind.None;
        foreach (var a in args)
        {
            if (a.NameEquals is not null) continue;   // named arg, e.g. Name = "x"
            if (a.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                value = lit.Token.ValueText;
                return PatternKind.Literal;
            }
            return PatternKind.Unresolvable;   // interpolated string / const / concat / nameof — not readable here
        }
        return PatternKind.None;   // only named args (unusual) — treat as bare convention
    }

    /// <summary>
    /// The first positional string-literal argument's VALUE (unescaped by Roslyn). Null when the
    /// attribute has no readable literal argument. Retained for the [StepArgumentTransformation] path.
    /// </summary>
    private static string? FirstStringArgument(AttributeSyntax attr)
        => ClassifyPattern(attr, out var v) == PatternKind.Literal ? v : null;

    private static string SimpleTypeName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        PredefinedTypeSyntax p => p.Keyword.ValueText,
        _ => type.ToString(),
    };

    private static StepDefinitionType ToType(string attrName) => attrName switch
    {
        "Given" => StepDefinitionType.Given,
        "When" => StepDefinitionType.When,
        "Then" => StepDefinitionType.Then,
        "And" => StepDefinitionType.And,
        "But" => StepDefinitionType.But,
        _ => StepDefinitionType.StepDefinition,
    };

    /// <summary>Project name from the path — same convention the existing analyzer uses.</summary>
    private static string ExtractProject(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.csproj").Length > 0)
                return Path.GetFileNameWithoutExtension(Directory.GetFiles(dir, "*.csproj")[0]);
            dir = Path.GetDirectoryName(dir);
        }
        return "Unknown";
    }
}
