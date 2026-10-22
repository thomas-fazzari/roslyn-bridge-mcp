// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using AwesomeAssertions.Primitives;
using static RoslynBridge.Tests.Assertions.ResultLines;

namespace RoslynBridge.Tests.Assertions;

[CustomAssertions]
internal static class ResponseTextAssertionsExtensions
{
    extension(StringAssertions assertions)
    {
        public AndConstraint<StringAssertions> BeApplied(
            string because = "",
            params object[] becauseArgs
        ) => assertions.StartWith("applied:", because, becauseArgs);

        public AndConstraint<StringAssertions> HaveSingleLocation(
            string file,
            string because = "",
            params object[] becauseArgs
        )
        {
            assertions
                .CurrentAssertionChain.BecauseOf(because, becauseArgs)
                .Given(() => Locations(assertions.Subject))
                .ForCondition(locations => locations.SequenceEqual([file]))
                .FailWith(
                    "Expected {context:response} to have the single location {0}{reason}, but found {1} in {2}",
                    _ => file,
                    locations => locations,
                    _ => assertions.Subject
                );
            return new AndConstraint<StringAssertions>(assertions);
        }
    }
}
