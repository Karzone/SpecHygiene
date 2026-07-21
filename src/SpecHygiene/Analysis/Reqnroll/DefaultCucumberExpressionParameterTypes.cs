// The built-in (no registration required) Cucumber Expression parameter types Reqnroll ships
// with. Extracted into its own testable class - previously this lived as an inline dictionary
// literal in Program.cs, which is top-level-statement code the Tests project deliberately
// can't reference (see DeadStepFinder.Tests.csproj's isolation comment), so this exact set could
// never be asserted against in a unit test - only indirectly, by running the whole tool.
//
// CORRECTED (2026-07-09): the original 7-entry set (int, long, byte, float, double, word,
// string) was itself confirmed incomplete via a fresh probe against the real
// Reqnroll.Bindings.CucumberExpressions.CucumberExpressionParameterTypeRegistry (constructed
// with a stub IBindingRegistry, same technique as this project's earlier enum/transform
// probes) - it registers 20 built-in types, not 7. Every numeric C# type is registered under
// BOTH its C# keyword alias (e.g. "int") AND its CLR Type.Name (e.g. "Int32") - missing the
// CLR-name half produced a confirmed false parse-failure (and downstream false "undefined
// step") for a live binding: Acme.Api.Orders.Steps.AllocationSteps'
// [Then("{Int32} suppliers should be returned")], bound to a plain `int` parameter. See this
// class's test file for the full theory-test enumeration and the production-regression case.
namespace SpecHygiene.Analysis.Reqnroll
{
    public static class DefaultCucumberExpressionParameterTypes
    {
        public static readonly IReadOnlyDictionary<string, string> Fragments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [""] = @".*",
            ["int"] = @"-?\d+",
            ["Int16"] = @"-?\d+",
            ["Int32"] = @"-?\d+",
            ["Int64"] = @"-?\d+",
            ["long"] = @"-?\d+",
            ["byte"] = @"-?\d+",
            ["Byte"] = @"-?\d+",
            ["float"] = FloatFragment,
            ["double"] = FloatFragment,
            ["decimal"] = FloatFragment,
            ["Decimal"] = FloatFragment,
            ["Double"] = FloatFragment,
            ["Single"] = FloatFragment,
            ["word"] = @"[^\s]+",
            ["string"] = @".*",
            ["Boolean"] = @".*",
            ["Char"] = @".*",
            ["DateTime"] = @".*",
            ["Guid"] = @".*",
        };

        private const string FloatFragment = @"(?=.*\d.*)[-+]?(?:\d+(?:[\p{Pc}\p{Po}\p{Pd} ]?\d+)*)*(?:[\p{Pc}\p{Po}](?=\d.*))?\d*(?:\d+[E]-?\d+)?";
    }
}
