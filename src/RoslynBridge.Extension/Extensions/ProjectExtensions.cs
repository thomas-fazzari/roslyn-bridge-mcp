// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;

namespace RoslynBridge.Extension;

internal static class ProjectExtensions
{
    // A project that references one of these assemblies holds tests
    private static readonly string[] _testFrameworks =
    [
        "xunit.",
        "nunit.framework",
        "MSTest.TestFramework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "TUnit.Core",
    ];

    private static readonly string[] _generatedSuffixes =
    [
        ".g.cs",
        ".g.i.cs",
        ".designer.cs",
        ".generated.cs",
    ];

    extension(Project project)
    {
        public bool IsTestProject =>
            project
                .MetadataReferences.OfType<PortableExecutableReference>()
                .Any(reference =>
                    Path.GetFileName(reference.FilePath) is { } name
                    && _testFrameworks.Any(framework =>
                        name.StartsWith(framework, StringComparison.OrdinalIgnoreCase)
                    )
                );
    }

    extension(Document document)
    {
        /// <summary>
        /// True for a file named like generated code, such as Form1.Designer.cs.
        /// </summary>
        public bool HasGeneratedName =>
            document.FilePath is { } path
            && _generatedSuffixes.Any(suffix =>
                path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            );
    }
}
