// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynBridge.Extension;

/// <param name="Callee">The member as declared. See <see cref="CallWalker.Normalize"/>.</param>
/// <param name="Site">The token or syntax node that the call is reported at.</param>
internal readonly record struct CallSite(ISymbol Callee, Location Site);

internal enum CallScope
{
    /// <summary>
    /// Every call, for the callees of a member.
    /// </summary>
    AllCalls = 0,

    /// <summary>
    /// Only the calls that FindCallers misses, so that they complete its result.
    /// </summary>
    MissedByFindCallers = 1,
}

internal enum CallOrigin
{
    /// <summary>
    /// A call that FindCallers reports.
    /// </summary>
    FoundByRoslyn = 0,

    /// <summary>
    /// A call that the compiler inserts, which FindCallers misses.
    /// </summary>
    Implicit = 1,
}

/// <summary>
/// Collects the calls of an operation tree, including those the compiler inserts without call syntax.
/// </summary>
internal sealed class CallWalker(CallScope scope) : OperationWalker
{
    private readonly List<CallSite> _calls = [];

    public IReadOnlyList<CallSite> Calls => _calls;

    /// <summary>
    /// A constructed generic is its definition, a reduced extension method its static declaration
    /// and an accessor its property or event.
    /// </summary>
    public static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } unreduced })
            symbol = unreduced;

        return (
            symbol is IMethodSymbol { AssociatedSymbol: { } owner } ? owner : symbol
        ).OriginalDefinition;
    }

    public override void VisitInvocation(IInvocationOperation operation)
    {
        var syntax = operation.Syntax;
        switch (syntax)
        {
            case QueryClauseSyntax
            or SelectOrGroupClauseSyntax
            or OrderingSyntax
            or QueryBodySyntax
            or QueryExpressionSyntax:
                Add(operation.TargetMethod, QueryToken(syntax), CallOrigin.Implicit);
                break;
            case InterpolatedStringTextSyntax or InterpolationSyntax:
                Add(operation.TargetMethod, InterpolatedStringToken(syntax), CallOrigin.Implicit);
                break;
            default:
                Add(operation.TargetMethod, syntax, CallOrigin.FoundByRoslyn);
                break;
        }

        base.VisitInvocation(operation);
    }

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        var syntax = operation.Syntax;
        if (syntax is InterpolatedStringExpressionSyntax)
            Add(operation.Constructor, syntax.GetFirstToken(), CallOrigin.Implicit);
        else
            Add(operation.Constructor, syntax, CallOrigin.FoundByRoslyn);

        base.VisitObjectCreation(operation);
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation operation)
    {
        Add(operation.Property, operation.Syntax, CallOrigin.FoundByRoslyn);
        base.VisitPropertyReference(operation);
    }

    public override void VisitFieldReference(IFieldReferenceOperation operation)
    {
        Add(operation.Field, operation.Syntax, CallOrigin.FoundByRoslyn);
        base.VisitFieldReference(operation);
    }

    public override void VisitEventReference(IEventReferenceOperation operation)
    {
        Add(operation.Event, operation.Syntax, CallOrigin.FoundByRoslyn);
        base.VisitEventReference(operation);
    }

    public override void VisitBinaryOperator(IBinaryOperation operation)
    {
        Add(operation.OperatorMethod, OperatorToken(operation.Syntax), CallOrigin.FoundByRoslyn);
        base.VisitBinaryOperator(operation);
    }

    // The compiler applies operator true or false to the condition of an if, a loop or a ?:
    public override void VisitUnaryOperator(IUnaryOperation operation)
    {
        var condition = operation.OperatorKind is UnaryOperatorKind.True or UnaryOperatorKind.False;
        var origin = condition ? CallOrigin.Implicit : CallOrigin.FoundByRoslyn;
        Add(operation.OperatorMethod, OperatorToken(operation.Syntax), origin);
        base.VisitUnaryOperator(operation);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
    {
        var method = operation.OperatorMethod;
        Add(method, OperatorToken(operation.Syntax), OriginOf(method));
        base.VisitCompoundAssignment(operation);
    }

    public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
    {
        var method = operation.OperatorMethod;
        Add(method, OperatorToken(operation.Syntax), OriginOf(method));
        base.VisitIncrementOrDecrement(operation);
    }

    public override void VisitConversion(IConversionOperation operation)
    {
        Add(operation.OperatorMethod, OperatorToken(operation.Syntax), CallOrigin.FoundByRoslyn);
        base.VisitConversion(operation);
    }

    public override void VisitUsing(IUsingOperation operation)
    {
        AddDispose(
            ResourceTypes(operation.Resources),
            operation.IsAsynchronous,
            operation.Syntax,
            operation.SemanticModel
        );
        base.VisitUsing(operation);
    }

    public override void VisitUsingDeclaration(IUsingDeclarationOperation operation)
    {
        AddDispose(
            ResourceTypes(operation.DeclarationGroup),
            operation.IsAsynchronous,
            operation.Syntax,
            operation.SemanticModel
        );
        base.VisitUsingDeclaration(operation);
    }

    // Roslyn finds the enumerator and the elements of a foreach, but not its Dispose or anything in an await foreach
    public override void VisitForEachLoop(IForEachLoopOperation operation)
    {
        if (operation is { Syntax: CommonForEachStatementSyntax syntax, SemanticModel: { } model })
        {
            var info = model.GetForEachStatementInfo(syntax);
            var site = syntax.GetFirstToken();
            var origin = operation.IsAsynchronous ? CallOrigin.Implicit : CallOrigin.FoundByRoslyn;
            Add(info.GetEnumeratorMethod, site, origin);
            Add(info.MoveNextMethod, site, origin);
            Add(info.CurrentProperty, site, origin);
            Add(EnumeratorDisposeOf(info), site, CallOrigin.Implicit);
            AddAwaitable(info.MoveNextAwaitableInfo, site, CallOrigin.Implicit);
            AddAwaitable(info.DisposeAwaitableInfo, site, CallOrigin.Implicit);
            if (syntax is ForEachVariableStatementSyntax variable)
                AddDeconstruction(model.GetDeconstructionInfo(variable), site);
        }

        base.VisitForEachLoop(operation);
    }

    public override void VisitAwait(IAwaitOperation operation)
    {
        if (operation is { Syntax: AwaitExpressionSyntax syntax, SemanticModel: { } model })
        {
            var info = model.GetAwaitExpressionInfo(syntax);
            var site = syntax.GetFirstToken();
            AddAwaitable(info, site, CallOrigin.FoundByRoslyn);
        }

        base.VisitAwait(operation);
    }

    public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
    {
        if (operation is { Syntax: AssignmentExpressionSyntax syntax, SemanticModel: { } model })
            AddDeconstruction(model.GetDeconstructionInfo(syntax), syntax.GetFirstToken());

        base.VisitDeconstructionAssignment(operation);
    }

    // The site is the type of the pattern, or its opening parenthesis without a type
    public override void VisitRecursivePattern(IRecursivePatternOperation operation)
    {
        Add(operation.DeconstructSymbol, operation.Syntax.GetFirstToken(), CallOrigin.Implicit);
        base.VisitRecursivePattern(operation);
    }

    public override void VisitImplicitIndexerReference(IImplicitIndexerReferenceOperation operation)
    {
        var site = IndexToken(operation.Syntax);
        var indexer = operation.IndexerSymbol;
        // FindCallers reports an indexer property behind ^ but not a Slice method
        var origin = indexer is IPropertySymbol ? CallOrigin.FoundByRoslyn : CallOrigin.Implicit;
        Add(operation.LengthSymbol, site, CallOrigin.Implicit);
        Add(indexer, site, origin);
        base.VisitImplicitIndexerReference(operation);
    }

    public override void VisitListPattern(IListPatternOperation operation)
    {
        var site = operation.Syntax.GetFirstToken();
        Add(operation.LengthSymbol, site, CallOrigin.Implicit);
        Add(operation.IndexerSymbol, site, CallOrigin.Implicit);
        base.VisitListPattern(operation);
    }

    public override void VisitSlicePattern(ISlicePatternOperation operation)
    {
        Add(operation.SliceSymbol, operation.Syntax.GetFirstToken(), CallOrigin.Implicit);
        base.VisitSlicePattern(operation);
    }

    private void Add(ISymbol? symbol, SyntaxNodeOrToken site, CallOrigin origin)
    {
        if (
            symbol is null
            || (origin is CallOrigin.FoundByRoslyn && scope is CallScope.MissedByFindCallers)
            || site.GetLocation() is not { } location
        )
        {
            return;
        }

        _calls.Add(new CallSite(Normalize(symbol), location));
    }

    private void AddAwaitable(AwaitExpressionInfo info, SyntaxToken site, CallOrigin awaiterOrigin)
    {
        Add(info.GetAwaiterMethod, site, awaiterOrigin);
        Add(info.IsCompletedProperty, site, CallOrigin.Implicit);
        Add(info.GetResultMethod, site, CallOrigin.Implicit);
    }

    private void AddDeconstruction(DeconstructionInfo info, SyntaxToken site)
    {
        Add(info.Method, site, CallOrigin.FoundByRoslyn);
        foreach (var nested in info.Nested)
        {
            AddDeconstruction(nested, site);
        }
    }

    private void AddDispose(
        IEnumerable<ITypeSymbol> types,
        bool isAsynchronous,
        SyntaxNode syntax,
        SemanticModel? model
    )
    {
        if (model is null)
            return;

        foreach (var type in types)
        {
            Add(
                DisposeOf(type, isAsynchronous, model.Compilation),
                syntax.GetFirstToken(),
                isAsynchronous ? CallOrigin.Implicit : CallOrigin.FoundByRoslyn
            );
        }
    }

    private static IEnumerable<ITypeSymbol> ResourceTypes(IOperation resources)
    {
        if (resources is not IVariableDeclarationGroupOperation group)
            return resources.Type is { } type ? [type] : [];

        return group
            .Declarations.SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Symbol.Type);
    }

    // The method that runs is the implementation of the interface member, or a Dispose of a ref struct
    private static IMethodSymbol? DisposeOf(
        ITypeSymbol type,
        bool isAsynchronous,
        Compilation compilation
    )
    {
        var name = isAsynchronous
            ? WellKnownMemberNames.DisposeAsyncMethodName
            : WellKnownMemberNames.DisposeMethodName;
        var contract = compilation.GetTypeByMetadataName(
            isAsynchronous ? "System.IAsyncDisposable" : "System.IDisposable"
        );
        var member = contract?.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault();
        if (
            member is not null
            && type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation
        )
        {
            return implementation;
        }

        return type.GetMembers(name)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => !method.IsStatic && method.Parameters.IsEmpty);
    }

    // Roslyn finds the static operators, so the instance operators of C# 14 are the ones it misses
    private static CallOrigin OriginOf(IMethodSymbol? method) =>
        method is { IsStatic: false } ? CallOrigin.Implicit : CallOrigin.FoundByRoslyn;

    // The compiler binds the interface member, even when the enumerator is a concrete type
    private static IMethodSymbol? EnumeratorDisposeOf(ForEachStatementInfo info)
    {
        if (info.DisposeMethod is not { } dispose)
            return null;

        return
            info.GetEnumeratorMethod?.ReturnType is { TypeKind: not TypeKind.Interface } enumerator
            && enumerator.FindImplementationForInterfaceMember(dispose)
                is IMethodSymbol implementation
            ? implementation
            : dispose;
    }

    // The keyword of the clause, or the first token of a second ordering
    private static SyntaxToken QueryToken(SyntaxNode syntax) =>
        syntax is OrderingSyntax { Parent: OrderByClauseSyntax clause } ordering
        && clause.Orderings[0] == ordering
            ? clause.OrderByKeyword
            : syntax.GetFirstToken();

    private static SyntaxToken InterpolatedStringToken(SyntaxNode syntax) =>
        syntax
            .AncestorsAndSelf()
            .OfType<InterpolatedStringExpressionSyntax>()
            .FirstOrDefault()
            ?.GetFirstToken()
        ?? syntax.GetFirstToken();

    private static SyntaxToken OperatorToken(SyntaxNode syntax) =>
        syntax switch
        {
            BinaryExpressionSyntax binary => binary.OperatorToken,
            AssignmentExpressionSyntax assignment => assignment.OperatorToken,
            PrefixUnaryExpressionSyntax prefix => prefix.OperatorToken,
            PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken,
            _ => syntax.GetFirstToken(),
        };

    // The ^ of an index, the .. of a range, otherwise the first token of the argument
    private static SyntaxToken IndexToken(SyntaxNode syntax) =>
        FirstArgument(syntax) switch
        {
            RangeExpressionSyntax range => range.OperatorToken,
            { } argument => argument.GetFirstToken(),
            _ => syntax.GetFirstToken(),
        };

    private static ExpressionSyntax? FirstArgument(SyntaxNode syntax) =>
        syntax switch
        {
            ElementAccessExpressionSyntax
            {
                ArgumentList.Arguments: [{ Expression: var argument }, ..],
            } => argument,
            ElementBindingExpressionSyntax
            {
                ArgumentList.Arguments: [{ Expression: var argument }, ..],
            } => argument,
            _ => null,
        };
}
