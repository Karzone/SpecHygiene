using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DefaultCucumberExpressionParameterTypes, covering the real Reqnroll
// 3.3.3 built-in parameter-type registry ground truth.
//
// Confirmed empirically (2026-07-09, throwaway probe project driving the REAL
// Reqnroll.Bindings.CucumberExpressions.CucumberExpressionParameterTypeRegistry - not assumed):
// Reqnroll registers 20 built-in parameter types, not just the 7 (int, long, byte, float,
// double, word, string) this dictionary previously had. Every numeric C# type is registered
// under BOTH its C# keyword alias (e.g. "int") AND its CLR Type.Name (e.g. "Int32") - a real
// [Binding] pattern using "{Int32}" is exactly as valid as one using "{int}", and before this
// fix PatternCompiler.CompilePattern would throw "Unknown Cucumber Expression parameter type
// '{Int32}'" for it - a confirmed false parse-failure (and downstream false "undefined step")
// for a live binding in Acme.API.OrderProcessor.AllocationSteps.ThenNumberOfSuppliersIsReturned.
//
// Reqnroll additionally auto-registers Boolean, Char, DateTime, Decimal (+ lowercase "decimal"),
// Guid, Single (float's CLR name), Int16/Int64 (no "short"/"long CLR name" C# alias - "short" is
// NOT auto-registered, matching this project's earlier probe finding), and an anonymous ""
// (empty name) type. None of these were previously registered either.
using System.Text.RegularExpressions;

namespace SpecHygiene.Tests.Reqnroll
{
    public class DefaultCucumberExpressionParameterTypesTests
    {
        [Theory]
        [InlineData("int")]
        [InlineData("long")]
        [InlineData("byte")]
        [InlineData("Byte")]
        [InlineData("float")]
        [InlineData("double")]
        [InlineData("word")]
        [InlineData("string")]
        [InlineData("Int16")]
        [InlineData("Int32")]
        [InlineData("Int64")]
        [InlineData("decimal")]
        [InlineData("Decimal")]
        [InlineData("Double")]
        [InlineData("Single")]
        [InlineData("Boolean")]
        [InlineData("Char")]
        [InlineData("DateTime")]
        [InlineData("Guid")]
        [InlineData("")]
        public void Fragments_ContainsEveryRealReqnrollBuiltInParameterType(string typeName)
        {
            Assert.True(
                DefaultCucumberExpressionParameterTypes.Fragments.ContainsKey(typeName),
                $"Missing built-in parameter type '{{{typeName}}}' - real Reqnroll registers this by default.");
        }

        [Fact]
        public void Fragments_HasExactlyTheRealReqnrollCount()
        {
            Assert.Equal(20, DefaultCucumberExpressionParameterTypes.Fragments.Count);
        }

        [Theory]
        [InlineData("short")]
        [InlineData("biginteger")]
        [InlineData("bigdecimal")]
        public void Fragments_DoesNotContainTypesConfirmedNotRegisteredByRealReqnroll(string typeName)
        {
            Assert.False(DefaultCucumberExpressionParameterTypes.Fragments.ContainsKey(typeName));
        }

        [Fact]
        public void CompilePattern_ProductionRegression_Int32SuppliersShouldBeReturned()
        {
            // Acme.API.OrderProcessor.Steps.AllocationSteps.ThenNumberOfSuppliersIsReturned -
            // [Then("{Int32} suppliers should be returned")], bound to a plain `int` parameter.
            var regex = PatternCompiler.CompilePattern(
                "{Int32} suppliers should be returned",
                DefaultCucumberExpressionParameterTypes.Fragments);

            Assert.True(MatchingLogic.IsFullMatch(regex, "2 suppliers should be returned"));
            Assert.False(MatchingLogic.IsFullMatch(regex, "two suppliers should be returned"));
        }
    }
}
