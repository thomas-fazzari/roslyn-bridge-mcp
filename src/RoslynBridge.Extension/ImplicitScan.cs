// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynBridge.Extension;

/// <summary>
/// Syntaxes that can hide a call of a member without naming it.
/// </summary>
[Flags]
internal enum ImplicitScan
{
    None = 0,

    /// <summary>
    /// The Dispose of a foreach enumerator.
    /// </summary>
    ForEach = 1 << 0,

    /// <summary>
    /// Everything an await foreach calls.
    /// </summary>
    AwaitForEach = 1 << 1,

    /// <summary>
    /// The awaiter members of an await expression.
    /// </summary>
    Await = 1 << 2,

    /// <summary>
    /// The DisposeAsync of an await using.
    /// </summary>
    AwaitUsing = 1 << 3,

    /// <summary>
    /// Operator true or false, applied to a condition.
    /// </summary>
    Condition = 1 << 4,

    /// <summary>
    /// An instance operator of C# 14, such as += or ++.
    /// </summary>
    CompoundOperator = 1 << 5,

    /// <summary>
    /// The Deconstruct of a positional pattern.
    /// </summary>
    PositionalPattern = 1 << 6,

    /// <summary>
    /// A method that a query expression translates to.
    /// </summary>
    Query = 1 << 7,

    /// <summary>
    /// The ctor and the Append methods of an interpolated string handler.
    /// </summary>
    InterpolatedString = 1 << 8,

    /// <summary>
    /// The Length, Count, indexer or Slice behind ^ and ..
    /// </summary>
    ImplicitIndex = 1 << 9,
}

/// <summary>
/// Decides from the target alone which syntax can call it implicitly, without binding any code.
/// </summary>
/// <remarks>
/// A scan may find nothing, but it must never miss a call, so that an ordinary member costs nothing extra.
/// </remarks>
internal static class ImplicitScans
{
    private static readonly FrozenSet<string> _queryMethods = new[]
    {
        "Select",
        "SelectMany",
        "Where",
        "Join",
        "GroupJoin",
        "OrderBy",
        "OrderByDescending",
        "ThenBy",
        "ThenByDescending",
        "GroupBy",
        "Cast",
    }.ToFrozenSet(StringComparer.Ordinal);

    // A document without any of the strings of a scan holds no candidate of it
    // No string means any document may
    private static readonly (ImplicitScan Scan, string[] Markers)[] _markers =
    [
        (ImplicitScan.ForEach, ["foreach"]),
        (ImplicitScan.AwaitForEach, ["await"]),
        (ImplicitScan.Await, ["await"]),
        (ImplicitScan.AwaitUsing, ["await"]),
        (ImplicitScan.Condition, []),
        (
            ImplicitScan.CompoundOperator,
            ["+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "??=", "++", "--"]
        ),
        (ImplicitScan.PositionalPattern, []),
        (ImplicitScan.Query, ["from"]),
        (ImplicitScan.InterpolatedString, ["$"]),
        (ImplicitScan.ImplicitIndex, ["^", ".."]),
    ];

    private static readonly ConcurrentDictionary<ImplicitScan, SearchValues<string>?> _searches =
        new();

    /// <param name="target">A method or a property, as <see cref="CallWalker.Normalize"/> returns it.</param>
    public static ImplicitScan For(ISymbol target)
    {
        var type = target.ContainingType;
        var scans = type?.IsInterpolatedStringHandler is true
            ? ImplicitScan.InterpolatedString
            : ImplicitScan.None;
        return scans
            | (target, ImplementedName(target)) switch
            {
                (
                    IMethodSymbol
                    {
                        MethodKind: MethodKind.UserDefinedOperator,
                        Name: WellKnownMemberNames.TrueOperatorName
                            or WellKnownMemberNames.FalseOperatorName,
                    },
                    _
                ) => ImplicitScan.Condition,
                (
                    IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, IsStatic: false },
                    _
                ) => ImplicitScan.CompoundOperator,
                (IMethodSymbol, WellKnownMemberNames.DisposeMethodName) when type.IsEnumerator =>
                    ImplicitScan.ForEach,
                (IMethodSymbol, WellKnownMemberNames.DisposeAsyncMethodName) => type.IsEnumerator
                    ? ImplicitScan.AwaitForEach | ImplicitScan.AwaitUsing
                    : ImplicitScan.AwaitUsing,
                (
                    IMethodSymbol,
                    WellKnownMemberNames.GetAsyncEnumeratorMethodName
                        or WellKnownMemberNames.MoveNextAsyncMethodName
                ) => ImplicitScan.AwaitForEach,
                (IPropertySymbol, WellKnownMemberNames.CurrentPropertyName)
                    when type.IsEnumerator => ImplicitScan.AwaitForEach,
                (IMethodSymbol, WellKnownMemberNames.GetResult)
                or (IPropertySymbol, WellKnownMemberNames.IsCompleted) when type.IsAwaiter =>
                    ImplicitScan.Await | ImplicitScan.AwaitForEach,
                (IMethodSymbol, WellKnownMemberNames.DeconstructMethodName) =>
                    ImplicitScan.PositionalPattern,
                (IMethodSymbol, var name) when _queryMethods.Contains(name) => ImplicitScan.Query,


                (
                    IPropertySymbol,
                    WellKnownMemberNames.LengthPropertyName
                        or WellKnownMemberNames.CountPropertyName
                )
                or
                (IPropertySymbol { IsIndexer: true }, _)
                or (IMethodSymbol, WellKnownMemberNames.SliceMethodName) =>
                    ImplicitScan.ImplicitIndex,
                _ => ImplicitScan.None,
            };
    }

    extension(ImplicitScan scans)
    {
        public bool MayOccurIn(SourceText text) =>
            _searches.GetOrAdd(scans, Markers) is not { } markers
            || text.ToString().AsSpan().ContainsAny(markers);

        public bool IsCandidate(SyntaxNode node) =>
            node switch
            {
                CommonForEachStatementSyntax statement => scans.HasFlag(ImplicitScan.ForEach)
                    || (
                        scans.HasFlag(ImplicitScan.AwaitForEach)
                        && statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                    ),
                AwaitExpressionSyntax => scans.HasFlag(ImplicitScan.Await),
                UsingStatementSyntax statement => scans.HasFlag(ImplicitScan.AwaitUsing)
                    && statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                LocalDeclarationStatementSyntax statement => scans.HasFlag(ImplicitScan.AwaitUsing)
                    && statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                IfStatementSyntax
                or WhileStatementSyntax
                or DoStatementSyntax
                or ForStatementSyntax
                or ConditionalExpressionSyntax => scans.HasFlag(ImplicitScan.Condition),
                AssignmentExpressionSyntax assignment => scans.HasFlag(
                    ImplicitScan.CompoundOperator
                ) && !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression),
                PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => scans.HasFlag(
                    ImplicitScan.CompoundOperator
                )
                    && node.Kind()
                        is SyntaxKind.PreIncrementExpression
                            or SyntaxKind.PreDecrementExpression
                            or SyntaxKind.PostIncrementExpression
                            or SyntaxKind.PostDecrementExpression,
                RecursivePatternSyntax { PositionalPatternClause: not null } => scans.HasFlag(
                    ImplicitScan.PositionalPattern
                ),
                QueryExpressionSyntax => scans.HasFlag(ImplicitScan.Query),
                InterpolatedStringExpressionSyntax => scans.HasFlag(
                    ImplicitScan.InterpolatedString
                ),
                ElementAccessExpressionSyntax access => scans.HasFlag(ImplicitScan.ImplicitIndex)
                    && access.ArgumentList.Arguments.Any(argument =>
                        argument.Expression.IsKind(SyntaxKind.IndexExpression)
                        || argument.Expression.IsKind(SyntaxKind.RangeExpression)
                    ),
                _ => false,
            };
    }

    extension(INamedTypeSymbol? type)
    {
        public bool IsInterpolatedStringHandler =>
            type
                ?.GetAttributes()
                .Any(attribute =>
                    attribute.AttributeClass is { Name: "InterpolatedStringHandlerAttribute" }
                )
                is true;

        // A Current property with MoveNext, or an IEnumerator
        private bool IsEnumerator =>
            type is not null
            && (
                (
                    type.GetMembers(WellKnownMemberNames.CurrentPropertyName).Any()
                    && type.GetMembers()
                        .Any(member =>
                            member
                                is IMethodSymbol
                                {
                                    Name: WellKnownMemberNames.MoveNextMethodName
                                        or WellKnownMemberNames.MoveNextAsyncMethodName,
                                }
                        )
                )
                || type.AllInterfaces.Prepend(type)
                    .Any(contract => contract.Name is "IEnumerator" or "IAsyncEnumerator")
            );

        // An IsCompleted property with GetResult, or an INotifyCompletion
        private bool IsAwaiter =>
            type is not null
            && (
                (
                    type.GetMembers(WellKnownMemberNames.IsCompleted).Any()
                    && type.GetMembers(WellKnownMemberNames.GetResult).Any()
                )
                || type.AllInterfaces.Any(contract =>
                    contract.Name is "INotifyCompletion" or "ICriticalNotifyCompletion"
                )
            );
    }

    // Null when a selected scan has no marker
    private static SearchValues<string>? Markers(ImplicitScan scans)
    {
        var selected = _markers.Where(marker => scans.HasFlag(marker.Scan)).ToList();
        return selected.Exists(marker => marker.Markers.Length == 0)
            ? null
            : SearchValues.Create(
                [.. selected.SelectMany(marker => marker.Markers).Distinct(StringComparer.Ordinal)],
                StringComparison.Ordinal
            );
    }

    // An explicit implementation is named after the interface, such as System.IDisposable.Dispose
    private static string ImplementedName(ISymbol member) =>
        member switch
        {
            IMethodSymbol { ExplicitInterfaceImplementations: [var contract, ..] } => contract.Name,
            IPropertySymbol { ExplicitInterfaceImplementations: [var contract, ..] } =>
                contract.Name,
            _ => member.Name,
        };
}
