// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;

namespace RoslynBridge.Extension;

/// <summary>
/// Reports what the compiler knows about the expression or the declared name at a position.
/// </summary>
/// <remarks>
/// Hover shows the signature of a symbol. This handler also gives the type, the conversion and the constant
/// of one use, and the overload the compiler chose.
/// </remarks>
internal sealed class ExpressionHandler
    : IExtensionDocumentMessageHandler<PositionRequest, ExpressionResponse>
{
    private const int MAXIMUM_TEXT_CHARACTERS = 200;
    private const int MAXIMUM_DOCUMENTATION_CHARACTERS = 400;
    private const string ELLIPSIS = "...";

    private static readonly ExpressionResponse _notFound = new(
        Found: false,
        Text: string.Empty,
        Kind: string.Empty,
        Declaration: false
    );

    public async Task<ExpressionResponse> ExecuteAsync(
        PositionRequest message,
        ExtensionDocumentMessageContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.Solution.GetDocument(context.TextDocument.Id) is not { } document)
            return _notFound;

        if (await document.GetPositionAsync(message, cancellationToken) is not { } position)
            return _notFound;

        if (
            await document.GetSyntaxRootAsync(cancellationToken) is not { } root
            || await document.GetSemanticModelAsync(cancellationToken) is not { } model
            || TokenAt(root, position) is not { } token
        )
        {
            return _notFound;
        }

        if (
            token.IsKind(SyntaxKind.IdentifierToken)
            && token.Parent is { } declaration
            && model.GetDeclaredSymbol(declaration, cancellationToken) is { } declared
        )
        {
            return DescribeDeclaration(model, token, declared, context.Solution, cancellationToken);
        }

        return ExpressionAt(token) is { } expression
            ? DescribeExpression(model, expression, context.Solution, cancellationToken)
            : _notFound;
    }

    private static ExpressionResponse DescribeExpression(
        SemanticModel model,
        ExpressionSyntax expression,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var info = model.GetTypeInfo(expression, cancellationToken);
        var type = TypeText(info.Type, info.Nullability.Annotation);
        var convertedType = TypeText(info.ConvertedType, info.ConvertedNullability.Annotation);
        var constant = model.GetConstantValue(expression, cancellationToken);
        var facts = new ExpressionResponse(
            Found: true,
            Text: Truncated(Collapsed(expression.ToString()), MAXIMUM_TEXT_CHARACTERS),
            Kind: expression.Kind().ToString(),
            Declaration: false,
            Type: type,
            ConvertedType: string.Equals(convertedType, type, StringComparison.Ordinal)
                ? null
                : convertedType,
            Nullability: NullabilityText(info.Nullability.Annotation, info.Nullability.FlowState),
            Conversion: ConversionText(model, expression, info.ConvertedType, cancellationToken),
            Constant: constant.HasValue ? ConstantText(constant.Value) : null
        );
        var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
        return symbolInfo.Symbol is { } symbol
            ? WithSymbol(facts, symbol, solution, cancellationToken)
            : WithCandidates(facts, symbolInfo, solution, cancellationToken);
    }

    private static ExpressionResponse DescribeDeclaration(
        SemanticModel model,
        SyntaxToken name,
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var (type, annotation) = symbol switch
        {
            ILocalSymbol local => (local.Type, local.NullableAnnotation),
            IParameterSymbol parameter => (parameter.Type, parameter.NullableAnnotation),
            IFieldSymbol field => (field.Type, field.NullableAnnotation),
            IPropertySymbol property => (property.Type, property.NullableAnnotation),
            IEventSymbol @event => (@event.Type, @event.NullableAnnotation),
            IMethodSymbol method => (method.ReturnType, method.ReturnNullableAnnotation),
            _ => ((ITypeSymbol?)null, NullableAnnotation.None),
        };
        // A declaration starts with the flow state of its initializer
        var flowState = name.Parent
            is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
            ? model.GetTypeInfo(initializer, cancellationToken).Nullability.FlowState
            : NullableFlowState.None;
        var constant = symbol switch
        {
            ILocalSymbol { HasConstantValue: true } local => ConstantText(local.ConstantValue),
            IFieldSymbol { HasConstantValue: true } field => ConstantText(field.ConstantValue),
            _ => null,
        };
        return WithSymbol(
            new ExpressionResponse(
                Found: true,
                Text: name.ValueText,
                Kind: name.Parent!.Kind().ToString(),
                Declaration: true,
                Type: TypeText(type, annotation),
                Nullability: NullabilityText(annotation, flowState),
                Constant: constant
            ),
            symbol,
            solution,
            cancellationToken
        );
    }

    private static ExpressionResponse WithSymbol(
        ExpressionResponse facts,
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var definition = Definition(symbol);
        return facts with
        {
            Symbol = Item(definition, solution, cancellationToken),
            Instantiation = SymbolEqualityComparer.Default.Equals(symbol, definition)
                ? null
                : symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Documentation = Summary(definition, cancellationToken),
        };
    }

    // Roslyn does not order the candidates, so file and position order keeps the answer stable
    private static ExpressionResponse WithCandidates(
        ExpressionResponse facts,
        SymbolInfo info,
        Solution solution,
        CancellationToken cancellationToken
    ) =>
        info.CandidateReason is CandidateReason.None
            ? facts
            : facts with
            {
                UnboundReason = info.CandidateReason.ToString(),
                Candidates =
                [
                    .. info
                        .CandidateSymbols.Select(candidate =>
                            Item(Definition(candidate), solution, cancellationToken)
                        )
                        .OrderBy(item => item.Uri, StringComparer.Ordinal)
                        .ThenBy(item => item.SelectionRange?.Start.Line)
                        .ThenBy(item => item.SelectionRange?.Start.Character)
                        .ThenBy(item => item.Name, StringComparer.Ordinal),
                ],
            };

    // The declaration that a name finds, with an extension method in its static form
    private static ISymbol Definition(ISymbol symbol) =>
        symbol is IMethodSymbol { ReducedFrom: { } reduced }
            ? reduced.OriginalDefinition
            : symbol.OriginalDefinition;

    // A source-generated tree has a path relative to its generator, so its symbols have no position
    private static ExpressionSymbol Item(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken
    ) =>
        !IsGenerated(symbol, solution)
        && symbol.ToDeclarationItem(ImmutableHashSet<string>.Empty, cancellationToken) is { } item
            ? new ExpressionSymbol(item.Name, item.Kind, item.Uri, item.SelectionRange)
            : new ExpressionSymbol(
                symbol.MemberName,
                symbol.LspKind,
                Uri: null,
                SelectionRange: null
            );

    private static bool IsGenerated(ISymbol symbol, Solution solution) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault() is { } reference
        && solution.GetDocument(reference.SyntaxTree) is SourceGeneratedDocument;

    // A cursor right after a name means that name, also before a punctuation mark as in Step|;
    private static SyntaxToken? TokenAt(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        var atToken = token.Span.Contains(position);
        if (position > 0 && (!atToken || SyntaxFacts.IsPunctuation(token.Kind())))
        {
            var left = root.FindToken(position - 1);
            if (left.Span.End == position)
                return left;
        }

        return atToken ? token : null;
    }

    // A statement or a declaration comes first, so a name inside a lambda body does not report the lambda
    private static ExpressionSyntax? ExpressionAt(SyntaxToken token)
    {
        var expression = token
            .Parent?.AncestorsAndSelf()
            .TakeWhile(node =>
                node
                    is not (
                        StatementSyntax
                        or MemberDeclarationSyntax
                        or AccessorDeclarationSyntax
                        or VariableDeclaratorSyntax
                        or ParameterSyntax
                    )
            )
            .OfType<ExpressionSyntax>()
            .FirstOrDefault();
        return expression is null ? null : Widened(expression);
    }

    private static ExpressionSyntax Widened(ExpressionSyntax expression)
    {
        while (Enclosing(expression) is { } widened)
        {
            expression = widened;
        }

        return expression;
    }

    // A member name stands for its whole access, a callee for its call and a created type for its creation
    private static ExpressionSyntax? Enclosing(ExpressionSyntax expression) =>
        expression.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == expression => access,
            MemberBindingExpressionSyntax binding when binding.Name == expression => binding,
            QualifiedNameSyntax qualified when qualified.Right == expression => qualified,
            AliasQualifiedNameSyntax alias when alias.Name == expression => alias,
            InvocationExpressionSyntax invocation when invocation.Expression == expression =>
                invocation,
            ObjectCreationExpressionSyntax creation when creation.Type == expression => creation,
            _ => null,
        };

    private static string? TypeText(ITypeSymbol? type, NullableAnnotation annotation) =>
        type
            ?.WithNullableAnnotation(annotation)
            .ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    // The annotation and the flow state are None when the analysis is off
    private static string? NullabilityText(NullableAnnotation annotation, NullableFlowState flow) =>
        annotation is NullableAnnotation.None && flow is NullableFlowState.None
            ? null
            : $"{annotation}/{flow}";

    private static string? ConversionText(
        SemanticModel model,
        ExpressionSyntax expression,
        ITypeSymbol? convertedType,
        CancellationToken cancellationToken
    )
    {
        if (convertedType is null)
            return null;

        var conversion = model.GetConversion(expression, cancellationToken);
        return conversion is { Exists: true, IsIdentity: false }
            ? string.Join(' ', ConversionWords(conversion))
            : null;
    }

    private static IEnumerable<string> ConversionWords(Conversion conversion)
    {
        yield return conversion.IsImplicit ? "implicit" : "explicit";
        if (conversion.IsNullable)
            yield return "nullable";

        if (conversion.IsNumeric)
            yield return "numeric";

        if (conversion.IsEnumeration)
            yield return "enumeration";

        if (conversion.IsReference)
            yield return "reference";

        if (conversion.IsBoxing)
            yield return "boxing";

        if (conversion.IsUnboxing)
            yield return "unboxing";

        if (conversion.IsNullLiteral)
            yield return "null-literal";

        if (conversion.IsDefaultLiteral)
            yield return "default-literal";

        if (conversion.IsConstantExpression)
            yield return "constant";

        if (conversion.IsAnonymousFunction)
            yield return "anonymous-function";

        if (conversion.IsMethodGroup)
            yield return "method-group";

        if (conversion.IsInterpolatedString)
            yield return "interpolated-string";

        if (conversion.IsTupleConversion || conversion.IsTupleLiteralConversion)
            yield return "tuple";

        if (conversion.IsCollectionExpression)
            yield return "collection-expression";

        if (conversion.IsObjectCreation)
            yield return "target-typed-new";

        if (conversion.IsDynamic)
            yield return "dynamic";

        if (!conversion.IsUserDefined)
            yield break;

        yield return "user-defined";
        if (conversion.MethodSymbol is { } method)
            yield return method.Name;
    }

    private static string ConstantText(object? value) =>
        Truncated(
            value is null
                ? "null"
                : SymbolDisplay.FormatPrimitive(
                    value,
                    quoteStrings: true,
                    useHexadecimalNumbers: false
                )
                    ?? value.ToString()
                    ?? string.Empty,
            MAXIMUM_TEXT_CHARACTERS
        );

    private static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(
            expandIncludes: false,
            cancellationToken: cancellationToken
        );
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            var text = summary is null
                ? string.Empty
                : Collapsed(string.Concat(summary.Nodes().Select(PlainText)));
            return text.Length == 0 ? null : Truncated(text, MAXIMUM_DOCUMENTATION_CHARACTERS);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // A reference reads as the name it points at
    private static string PlainText(XNode node) =>
        node switch
        {
            XText text => text.Value,
            XElement { HasAttributes: true } element when element.Attribute("cref") is { } cref =>
                ShortName(cref.Value),
            XElement element when element.Attribute("langword") is { } langword => langword.Value,
            XElement element when !element.Nodes().Any() && element.Attribute("name") is { } name =>
                name.Value,
            XElement element => string.Concat(element.Nodes().Select(PlainText)),
            _ => string.Empty,
        };

    // T:System.Int32 reads as Int32, and M:Ns.Type.Method(System.Int32) as Method
    private static string ShortName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref.AsSpan(2) : cref.AsSpan();
        var end = name.IndexOfAny('(', '{', '`');
        if (end >= 0)
            name = name[..end];

        return name[(name.LastIndexOf('.') + 1)..].ToString();
    }

    private static string Collapsed(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncated(string text, int maximum)
    {
        if (text.Length <= maximum)
            return text;

        // A cut between the halves of a surrogate pair would leave invalid UTF-16
        var end = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..end] + ELLIPSIS;
    }
}
