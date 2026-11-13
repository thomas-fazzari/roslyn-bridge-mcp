// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace RoslynBridge.Extension;

internal static partial class SymbolExtensions
{
    private const string DebuggerDisplayName = "System.Diagnostics.DebuggerDisplayAttribute";
    private const string StructLayoutName = "System.Runtime.InteropServices.StructLayoutAttribute";

    // Any other attribute may be how a framework finds the declaration, as test and serializer attributes are
    private static readonly FrozenSet<string> _neutralAttributes = new[]
    {
        "System.AttributeUsageAttribute",
        "System.CLSCompliantAttribute",
        "System.ComponentModel.EditorBrowsableAttribute",
        "System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute",
        "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute",
        "System.Diagnostics.CodeAnalysis.ExperimentalAttribute",
        "System.Diagnostics.CodeAnalysis.MemberNotNullAttribute",
        "System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute",
        "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
        "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute",
        "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute",
        "System.Diagnostics.ConditionalAttribute",
        "System.Diagnostics.Contracts.PureAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute",
        DebuggerDisplayName,
        "System.Diagnostics.DebuggerHiddenAttribute",
        "System.Diagnostics.DebuggerNonUserCodeAttribute",
        "System.Diagnostics.DebuggerStepThroughAttribute",
        "System.Diagnostics.DebuggerTypeProxyAttribute",
        "System.Diagnostics.StackTraceHiddenAttribute",
        "System.FlagsAttribute",
        "System.ObsoleteAttribute",
        "System.Runtime.CompilerServices.MethodImplAttribute",
        "System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute",
        "System.Runtime.CompilerServices.SkipLocalsInitAttribute",
        StructLayoutName,
    }.ToFrozenSet(StringComparer.Ordinal);

    extension(INamedTypeSymbol type)
    {
        /// <summary>
        /// The base classes and every interface, nearest base first.
        /// </summary>
        public IEnumerable<INamedTypeSymbol> Supertypes
        {
            get
            {
                for (
                    var baseType = type.BaseType;
                    baseType is not null;
                    baseType = baseType.BaseType
                )
                {
                    yield return baseType;
                }

                foreach (var contract in type.AllInterfaces)
                {
                    yield return contract;
                }
            }
        }
    }

    extension(ISymbol symbol)
    {
        /// <summary>
        /// The defining part of a partial member, which stands for all its parts.
        /// </summary>
        public ISymbol Definition =>
            symbol switch
            {
                IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
                IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
                IEventSymbol { PartialDefinitionPart: { } definition } => definition,
                _ => symbol,
            };

        /// <summary>
        /// The first part in path order, so every project that compiles a part agrees on it.
        /// </summary>
        public DeclarationKey? DeclarationKey =>
            symbol
                .Locations.Where(location => location.SourceTree?.FilePath is { Length: > 0 })
                .Select(location => new DeclarationKey(
                    location.SourceTree!.FilePath,
                    location.SourceSpan.Start
                ))
                .OrderBy(key => key.Path, StringComparer.Ordinal)
                .ThenBy(key => key.Start)
                .Cast<DeclarationKey?>()
                .FirstOrDefault();

        /// <summary>
        /// The declarations of the containing types, innermost first.
        /// </summary>
        public IEnumerable<DeclarationKey> ContainingTypeKeys
        {
            get
            {
                for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
                {
                    if (type.DeclarationKey is { } key)
                    {
                        yield return key;
                    }
                }
            }
        }

        /// <summary>
        /// False for the declarations that a contract or a framework reaches without a reference,
        /// such as overrides, interface implementations, constructors and operators.
        /// </summary>
        public bool CanBeUnused =>
            !symbol.IsImplicitlyDeclared
            && !symbol.IsOverride
            && !symbol.ImplementsInterfaceMember
            && !symbol.IsNamedByDebuggerDisplay
            && symbol switch
            {
                INamedTypeSymbol type => type.TypeKind
                    is TypeKind.Class
                        or TypeKind.Struct
                        or TypeKind.Interface
                        or TypeKind.Enum
                        or TypeKind.Delegate,
                // The synthesized ToString of a record calls PrintMembers
                IMethodSymbol { Name: "PrintMembers", ContainingType.IsRecord: true } => false,
                IMethodSymbol method => method.MethodKind is MethodKind.Ordinary,
                IFieldSymbol => !symbol.IsInFixedLayout,
                IPropertySymbol or IEventSymbol => true,
                _ => false,
            };

        /// <summary>
        /// True for a declaration that a framework may reach by reflection: one with a framework attribute,
        /// a type with a framework base, a public member of such a type, or a public settable property.
        /// </summary>
        public bool IsReachedByFramework =>
            symbol.IsFrameworkType
            || symbol
                is IPropertySymbol
                {
                    DeclaredAccessibility: Accessibility.Public,
                    SetMethod.DeclaredAccessibility: Accessibility.Public,
                }
            || (
                symbol.DeclaredAccessibility is Accessibility.Public
                && symbol.ContainingTypes.Any(type => type.IsFrameworkType)
            );

        public bool ImplementsInterfaceMember =>
            symbol is IMethodSymbol or IPropertySymbol or IEventSymbol
            && symbol.ContainingType is { } type
            && type.AllInterfaces.Any(contract =>
                contract
                    .GetMembers(symbol.Name)
                    .Any(member =>
                        type.FindImplementationForInterfaceMember(member) is { } implementation
                        && SymbolEqualityComparer.Default.Equals(implementation.Definition, symbol)
                    )
            );

        public bool HasFrameworkAttribute =>
            symbol
                .GetAttributes()
                .Any(attribute =>
                    attribute.AttributeClass is not { } type
                    || !_neutralAttributes.Contains(type.ToDisplayString())
                );

        private bool IsInFixedLayout =>
            symbol is IFieldSymbol { IsStatic: false, IsConst: false }
            && symbol.ContainingType is { } type
            && type.GetAttributes()
                .Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() is StructLayoutName
                    && attribute.ConstructorArguments
                        is [{ Value: (int)LayoutKind.Sequential or (int)LayoutKind.Explicit }, ..]
                );

        private bool IsNamedByDebuggerDisplay =>
            symbol is IPropertySymbol or IFieldSymbol or IMethodSymbol
            && symbol.ContainingType is { } type
            && type.GetAttributes()
                .Where(attribute =>
                    attribute.AttributeClass?.ToDisplayString() is DebuggerDisplayName
                )
                .SelectMany(DisplayedNames)
                .Contains(symbol.Name, StringComparer.Ordinal);

        private IEnumerable<INamedTypeSymbol> ContainingTypes
        {
            get
            {
                for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
                {
                    yield return type;
                }
            }
        }

        private bool IsFrameworkType =>
            symbol.HasFrameworkAttribute
            || (
                symbol
                    is INamedTypeSymbol
                    {
                        TypeKind: TypeKind.Class,
                        IsAbstract: false,
                        IsStatic: false
                    } type
                && type.Supertypes.Any(supertype => supertype.IsFromFramework)
            );

        // An assembly outside the base class library, as a host, web or plugin framework ships
        private bool IsFromFramework =>
            !symbol.Locations.Any(location => location.IsInSource)
            && symbol.ContainingAssembly?.Name is { } name
            && name is not ("mscorlib" or "netstandard" or "System")
            && !name.StartsWith("System.", StringComparison.Ordinal);

        /// <summary>
        /// Null for a symbol without a declaration in a file. Prefers a part in <paramref name="files"/>.
        /// </summary>
        public CallItem? ToDeclarationItem(
            IReadOnlySet<string> files,
            CancellationToken cancellationToken
        )
        {
            var references = symbol.DeclaringSyntaxReferences;
            if (
                (
                    references.FirstOrDefault(reference =>
                        files.Contains(reference.SyntaxTree.FilePath)
                    ) ?? references.FirstOrDefault()
                )
                    is not { } reference
                || reference.SyntaxTree.FilePath is not { Length: > 0 } path
            )
            {
                return null;
            }

            var declaration = reference.GetSyntax(cancellationToken).WholeDeclaration.GetLocation();
            var name = symbol.Locations.SingleOrDefault(location =>
                location.SourceTree == reference.SyntaxTree
                && declaration.SourceSpan.Contains(location.SourceSpan)
            ) ?? declaration;
            return new CallItem(
                symbol.MemberName,
                symbol.LspKind,
                path.ToLspUri(),
                declaration.ToLspRange(),
                name.ToLspRange()
            );
        }
    }

    private static IEnumerable<string> DisplayedNames(AttributeData attribute) =>
        attribute
            .ConstructorArguments.Concat(attribute.NamedArguments.Select(pair => pair.Value))
            .Select(argument => argument.Value as string)
            .OfType<string>()
            .SelectMany(format =>
                LeadingIdentifier().Matches(format).Select(match => match.Groups["name"].Value)
            );

    // The first identifier of each brace expression, such as Inner in "{Inner.Name,nq}"
    [GeneratedRegex(@"\{(?<name>\w+)", RegexOptions.NonBacktracking)]
    private static partial Regex LeadingIdentifier();
}
