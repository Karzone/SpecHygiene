using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DeadStepFinder.KeywordCompatibility, covering the confirmed rule
// that Program.cs's actual current code violates: it ignores step keyword entirely when
// deciding a match (see the comment in KeywordCompatibility.cs for exactly where).
namespace SpecHygiene.Tests.Reqnroll
{
    public class KeywordCompatibilityTests
    {
        [Fact]
        public void StepDefinitionKind_MatchesAnyActualKeyword()
        {
            Assert.True(KeywordCompatibility.IsKeywordCompatible(BindingKind.StepDefinition, StepKeyword.Given));
            Assert.True(KeywordCompatibility.IsKeywordCompatible(BindingKind.StepDefinition, StepKeyword.When));
            Assert.True(KeywordCompatibility.IsKeywordCompatible(BindingKind.StepDefinition, StepKeyword.Then));
        }

        [Fact]
        public void WhenKind_DoesNotMatchGivenActualKeyword()
        {
            // The core case: a [When]-only binding must NOT match a step whose real resolved
            // Gherkin keyword is Given, even with identical text - confirmed Reqnroll rule
            // that the tool's current text-only matching completely ignores.
            Assert.False(KeywordCompatibility.IsKeywordCompatible(BindingKind.When, StepKeyword.Given));
        }

        [Theory]
        [InlineData(BindingKind.Given, StepKeyword.Given, true)]
        [InlineData(BindingKind.Given, StepKeyword.When, false)]
        [InlineData(BindingKind.Given, StepKeyword.Then, false)]
        [InlineData(BindingKind.When, StepKeyword.When, true)]
        [InlineData(BindingKind.When, StepKeyword.Given, false)]
        [InlineData(BindingKind.When, StepKeyword.Then, false)]
        [InlineData(BindingKind.Then, StepKeyword.Then, true)]
        [InlineData(BindingKind.Then, StepKeyword.Given, false)]
        [InlineData(BindingKind.Then, StepKeyword.When, false)]
        public void ConcreteKinds_OnlyMatchTheirOwnKeyword(BindingKind bindingKind, StepKeyword actualKeyword, bool expected)
        {
            Assert.Equal(expected, KeywordCompatibility.IsKeywordCompatible(bindingKind, actualKeyword));
        }
    }
}
