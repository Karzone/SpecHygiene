// Pure, unit-testable keyword-type compatibility check, extracted from the confirmed
// rule that the tool's current code completely ignores: Program.cs's step-line
// extraction (see `stepLineRegex` in Program.cs) captures the Given/When/Then/And/But
// keyword but then THROWS IT AWAY - `allStepTexts` only ever stores the step TEXT, and
// STEP 5's cross-reference (`SafeIsMatch`) matches purely on text vs. every evaluable
// binding's Regex, regardless of the binding's [Given]/[When]/[Then]/[StepDefinition]
// kind. That means today a `[When]`-only binding is reported ALIVE by a step whose real
// Gherkin keyword is `Given`, which Reqnroll itself would never bind at runtime -
// another confirmed false-"alive" (and therefore false-negative-for-dead) source.
//
// Confirmed ground truth (see scratchpad/reqnroll-quirks-verification/):
//   - [StepDefinition] matches a step regardless of its keyword.
//   - [Given] only matches a step whose ACTUAL RESOLVED keyword is Given (same for
//     [When]/[Then]).
//
// Assumption this function relies on (by design, documented here): `And`/`But` are not
// real keywords at the matching layer - in a real scenario they always inherit the
// nearest preceding concrete Given/When/Then keyword. Resolving that inheritance is the
// CALLER's job (walking the scenario's step list top-to-bottom); this function only
// performs the final Kind-vs-Keyword compatibility check against an already-resolved
// concrete keyword, which is why StepKeyword below has no And/But members.
using System;

namespace SpecHygiene.Analysis.Reqnroll
{
    public enum BindingKind
    {
        Given,
        When,
        Then,
        StepDefinition,
    }

    /// <summary>
    /// The step's ACTUAL, ALREADY-RESOLVED effective keyword (And/But already resolved
    /// by the caller to whichever concrete keyword precedes them in the scenario).
    /// </summary>
    public enum StepKeyword
    {
        Given,
        When,
        Then,
    }

    public static class KeywordCompatibility
    {
        /// <summary>
        /// True if a binding of <paramref name="bindingKind"/> is allowed to match a step
        /// whose resolved actual keyword is <paramref name="actualKeyword"/>.
        /// </summary>
        public static bool IsKeywordCompatible(BindingKind bindingKind, StepKeyword actualKeyword)
        {
            if (bindingKind == BindingKind.StepDefinition)
            {
                return true;
            }

            return bindingKind switch
            {
                BindingKind.Given => actualKeyword == StepKeyword.Given,
                BindingKind.When => actualKeyword == StepKeyword.When,
                BindingKind.Then => actualKeyword == StepKeyword.Then,
                _ => throw new ArgumentOutOfRangeException(nameof(bindingKind), bindingKind, null),
            };
        }
    }
}
