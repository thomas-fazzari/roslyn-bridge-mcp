// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using AwesomeAssertions.Execution;
using AwesomeAssertions.Primitives;
using ModelContextProtocol.Protocol;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Assertions;

/// <summary>
/// Asserts the outcome of a tool response. A failure message includes the response text.
/// </summary>
[CustomAssertions]
internal sealed class CallToolResultAssertions(
    CallToolResult subject,
    AssertionChain assertionChain
) : ReferenceTypeAssertions<CallToolResult, CallToolResultAssertions>(subject, assertionChain)
{
    protected override string Identifier => "tool response";

    public AndConstraint<CallToolResultAssertions> BeError(
        string code,
        string because = "",
        params object[] becauseArgs
    )
    {
        CurrentAssertionChain
            .BecauseOf(because, becauseArgs)
            .ForCondition(Subject.IsError is true)
            .FailWith(
                "Expected {context:tool response} to be an error{reason}, but it succeeded with {0}",
                () => ResponseText(Subject)
            )
            .Then.Given(() => ErrorCode(Subject))
            .ForCondition(actual => string.Equals(actual, code, StringComparison.Ordinal))
            .FailWith(
                "Expected {context:tool response} to have error code {0}{reason}, but found {1} in {2}",
                _ => code,
                actual => actual,
                _ => ResponseText(Subject)
            );
        return new AndConstraint<CallToolResultAssertions>(this);
    }

    public AndConstraint<CallToolResultAssertions> BeSuccessful(
        string because = "",
        params object[] becauseArgs
    )
    {
        CurrentAssertionChain
            .BecauseOf(because, becauseArgs)
            .ForCondition(Subject.IsError is not true)
            .FailWith(
                "Expected {context:tool response} to be successful{reason}, but it failed with {0}",
                () => ResponseText(Subject)
            );
        return new AndConstraint<CallToolResultAssertions>(this);
    }
}

internal static class CallToolResultAssertionsExtensions
{
    extension(CallToolResult response)
    {
        public CallToolResultAssertions Should() => new(response, AssertionChain.GetOrCreate());
    }
}
