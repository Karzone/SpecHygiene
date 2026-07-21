using SpecHygiene.Analysis.Reqnroll;
using Xunit;
// TDD oracle for DeadStepFinder.KnownIssues - parsing the approved-issues CSV and matching it
// against dead bindings (by source-file suffix + exact method name).
namespace SpecHygiene.Tests.Reqnroll
{
    public class KnownIssuesTests
    {
        private const string Csv =
            "SourceFile,MethodName,Comment\n" +
            "# a comment line, ignored\n" +
            "\n" +
            "AllocationSteps.cs,GivenUserDoesX,\"feature usage commented out since 2024, kept intentionally\"\n" +
            "API/Acme.API.Motor/Steps/TimeLineSteps.cs,WhenSomething,tracked in GAT-12345\n" +
            ",MethodInAnyFile,matches regardless of file\n";

        [Fact]
        public void Parse_SkipsHeaderBlankAndCommentLines_AndKeepsRealRows()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Equal(3, issues.Count);
            Assert.Equal("GivenUserDoesX", issues[0].MethodName);
        }

        [Fact]
        public void Parse_PreservesCommasInsideTheComment()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Equal("feature usage commented out since 2024, kept intentionally", issues[0].Comment);
        }

        [Fact]
        public void Match_ExactFilenameSuffixAndMethod_ReturnsComment()
        {
            var issues = KnownIssues.Parse(Csv);
            var comment = KnownIssues.Match(issues,
                @"test\1FrameworkAutomatedTest\API\Acme.API.Motor\Steps\AllocationSteps.cs",
                "GivenUserDoesX");
            Assert.Equal("feature usage commented out since 2024, kept intentionally", comment);
        }

        [Fact]
        public void Match_PathSeparatorsAreNormalised_ForwardVsBackslash()
        {
            var issues = KnownIssues.Parse(Csv);
            var comment = KnownIssues.Match(issues,
                "test/1FrameworkAutomatedTest/API/Acme.API.Motor/Steps/TimeLineSteps.cs",
                "WhenSomething");
            Assert.Equal("tracked in GAT-12345", comment);
        }

        [Fact]
        public void Match_WrongMethodOnSameFile_ReturnsNull()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Null(KnownIssues.Match(issues, @"x\AllocationSteps.cs", "GivenSomethingElse"));
        }

        [Fact]
        public void Match_RightMethodButDifferentFile_ReturnsNull()
        {
            var issues = KnownIssues.Parse(Csv);
            // GivenUserDoesX is pinned to AllocationSteps.cs; a same-named method elsewhere is NOT covered.
            Assert.Null(KnownIssues.Match(issues, @"x\SomeOtherSteps.cs", "GivenUserDoesX"));
        }

        [Fact]
        public void Match_MethodNameIsCaseSensitive()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Null(KnownIssues.Match(issues, @"x\AllocationSteps.cs", "givenuserdoesx"));
        }

        [Fact]
        public void Match_EmptySourceFileRow_MatchesMethodInAnyFile()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Equal("matches regardless of file",
                KnownIssues.Match(issues, @"any\where\Whatever.cs", "MethodInAnyFile"));
        }

        [Fact]
        public void Match_NullOrEmptyBindingFile_DoesNotThrow_AndOnlyMethodOnlyRowsCanMatch()
        {
            var issues = KnownIssues.Parse(Csv);
            Assert.Equal("matches regardless of file", KnownIssues.Match(issues, null, "MethodInAnyFile"));
            Assert.Null(KnownIssues.Match(issues, null, "GivenUserDoesX")); // needs a file suffix match
        }

        [Fact]
        public void Parse_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(KnownIssues.Parse(null));
            Assert.Empty(KnownIssues.Parse(""));
            Assert.Empty(KnownIssues.Parse("   \n  \n"));
        }
    }
}
