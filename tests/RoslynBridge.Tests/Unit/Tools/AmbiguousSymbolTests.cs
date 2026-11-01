// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Fakes.LspSymbolJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class AmbiguousSymbolTests : IDisposable
{
    private const int EnumKind = 10;
    private const int EnumMemberKind = 22;
    private const int EnumMemberLine = 2;

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-ambiguous-symbol-")
        .FullName;

    [Fact]
    public async Task TypeOnlyActionsResolveANameSharedWithAnEnumMemberToTheTypeAsync()
    {
        var folder = await WriteFileAsync(_root, "Folder.cs", "class Folder {}\n");
        var kinds = await WriteFileAsync(_root, "Kinds.cs", "enum ItemKind { Folder }\n");
        await using var session = CreateSession(
            _root,
            FakeSymbolServer.With(
                workspace: new(StringComparer.Ordinal)
                {
                    ["Folder"] =
                    [
                        WorkspaceSymbol("Folder", folder),
                        WorkspaceSymbol("Folder", kinds, EnumMemberKind, EnumMemberLine, 4),
                    ],
                },
                documents: new(StringComparer.Ordinal)
                {
                    ["Folder.cs"] = [DocumentSymbol("Folder", DocumentMethod("Add()"))],
                    ["Kinds.cs"] =
                    [
                        DocumentSymbol(
                            "ItemKind",
                            EnumKind,
                            0,
                            DocumentSymbol("Folder", EnumMemberKind, EnumMemberLine)
                        ),
                    ],
                }
            )
        );
        using var tool = CreateTool(_root, session);

        var members = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Members, Symbol = "Folder" },
            TestCt
        );
        var source = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Source, Symbol = "Folder" },
            TestCt
        );

        members.Should().BeSuccessful();
        ResponseText(members).Should().Contain("method Add()");
        // Source reads any declaration, so the enum member stays a candidate
        source.Should().BeError(ErrorCodes.AmbiguousSymbol);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
