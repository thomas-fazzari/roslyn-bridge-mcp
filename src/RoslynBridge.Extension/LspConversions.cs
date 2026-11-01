// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynBridge.Extension;

internal static class LspConversions
{
    extension(Document document)
    {
        public async Task<int> GetPositionAsync(
            PositionRequest request,
            CancellationToken cancellationToken
        )
        {
            var text = await document.GetTextAsync(cancellationToken);
            return text.Lines.GetPosition(new LinePosition(request.Line, request.Character));
        }
    }

    extension(Location location)
    {
        public LspRange ToLspRange()
        {
            var span = location.GetLineSpan().Span;
            return new LspRange(
                new LspPosition(span.Start.Line, span.Start.Character),
                new LspPosition(span.End.Line, span.End.Character)
            );
        }
    }

    extension(string filePath)
    {
        public string ToLspUri() => new Uri(filePath).AbsoluteUri;
    }

    extension(ISymbol symbol)
    {
        /// <summary>
        /// The LSP SymbolKind of a caller.
        /// </summary>
        public int LspKind =>
            (int)(
                symbol switch
                {
                    IMethodSymbol
                    {
                        MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor,
                    } => LspSymbolKind.Constructor,
                    IMethodSymbol
                    {
                        MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion,
                    } => LspSymbolKind.Operator,
                    IMethodSymbol { MethodKind: MethodKind.LocalFunction } =>
                        LspSymbolKind.Function,
                    IMethodSymbol => LspSymbolKind.Method,
                    IPropertySymbol => LspSymbolKind.Property,
                    IFieldSymbol { IsConst: true } => LspSymbolKind.Constant,
                    IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } =>
                        LspSymbolKind.EnumMember,
                    IFieldSymbol => LspSymbolKind.Field,
                    IEventSymbol => LspSymbolKind.Event,
                    INamedTypeSymbol { TypeKind: TypeKind.Interface } => LspSymbolKind.Interface,
                    INamedTypeSymbol { TypeKind: TypeKind.Struct } => LspSymbolKind.Struct,
                    INamedTypeSymbol { TypeKind: TypeKind.Enum } => LspSymbolKind.Enum,
                    _ => LspSymbolKind.Class,
                }
            );
    }

    private enum LspSymbolKind
    {
        Class = 5,
        Method = 6,
        Property = 7,
        Field = 8,
        Constructor = 9,
        Enum = 10,
        Interface = 11,
        Function = 12,
        Constant = 14,
        EnumMember = 22,
        Struct = 23,
        Event = 24,
        Operator = 25,
    }
}
