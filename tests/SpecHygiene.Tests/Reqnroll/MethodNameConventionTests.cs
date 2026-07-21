using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD test-oracle for DeadStepFinder.MethodNameConvention, covering the confirmed rule
// that Program.cs currently gets wrong: it blanket-excludes EVERY method-name-convention
// binding from the dead/alive verdict, regardless of parameter count - including the
// zero-parameter case, which Reqnroll actually binds via a literal, case-insensitive,
// trailing-period-tolerant text match.
namespace SpecHygiene.Tests.Reqnroll
{
    public class MethodNameConventionTests
    {
        // --- (a) parameterCount > 0 must always yield null, regardless of method name. ---
        [Theory]
        [InlineData("GivenTheUserIsLoggedIn", 1)]
        [InlineData("WhenIClickSubmit", 2)]
        [InlineData("Then", 1)]
        [InlineData("X", 5)]
        public void GenerateConventionPattern_AnyNonZeroParameterCount_ReturnsNull(string methodName, int parameterCount)
        {
            Assert.Null(MethodNameConvention.GenerateConventionPattern(methodName, parameterCount));
        }

        // --- (b) the critical case: zero parameters must NOT be excluded (this is the bug). ---
        [Fact]
        public void GenerateConventionPattern_ZeroParameters_ReturnsNonNullPattern()
        {
            var pattern = MethodNameConvention.GenerateConventionPattern("GivenTheUserIsLoggedIn", parameterCount: 0);

            Assert.NotNull(pattern);
            Assert.Equal("The User Is Logged In", pattern);
        }

        [Theory]
        [InlineData("GivenTheUserIsLoggedIn", "The User Is Logged In")]
        [InlineData("WhenIClickSubmit", "I Click Submit")]
        [InlineData("ThenTheOrderIsConfirmed", "The Order Is Confirmed")]
        [InlineData("AndTheReceiptIsPrinted", "The Receipt Is Printed")]
        [InlineData("ButNoErrorOccurs", "No Error Occurs")]
        public void GenerateConventionPattern_StripsLeadingKeywordPrefixWord(string methodName, string expected)
        {
            Assert.Equal(expected, MethodNameConvention.GenerateConventionPattern(methodName, parameterCount: 0));
        }

        [Theory]
        [InlineData("the user is logged in", true)]
        [InlineData("The User Is Logged In", true)]
        [InlineData("THE USER IS LOGGED IN", true)]
        [InlineData("the user is logged in.", true)]   // tolerates ONE trailing period
        [InlineData("the user is logged in..", false)]  // still requires exact word match otherwise
        [InlineData("the user is logged in extra", false)] // full-match required, no partial binding
        [InlineData("user is logged in", false)]
        public void CompileConventionRegex_MatchesCaseInsensitivelyAndTolerantOfOneTrailingPeriod(string stepText, bool expected)
        {
            var literalPattern = MethodNameConvention.GenerateConventionPattern("GivenTheUserIsLoggedIn", parameterCount: 0);
            var regex = MethodNameConvention.CompileConventionRegex(literalPattern!);

            Assert.Equal(expected, MatchingLogic.IsFullMatch(regex, stepText));
        }
    }
}
