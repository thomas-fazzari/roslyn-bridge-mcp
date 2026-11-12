// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class WorkspaceBoundaryTests : TempWorkspaceTests
{
    private readonly string _workspace;
    private readonly string _outside;

    public WorkspaceBoundaryTests()
    {
        _workspace = Directory.CreateDirectory(Path.Combine(Root, "Workspace")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(Root, "Outside")).FullName;
    }

    [Theory]
    [InlineData("../Outside/Outside.cs")]
    [InlineData("{0}")]
    [InlineData("{1}")]
    public async Task FileOutsideTheWorkspaceIsRefusedAsync(string fileFormat)
    {
        var outside = await WriteFileAsync(_outside, "Outside.cs", "class Outside {}");
        var file = string.Format(
            CultureInfo.InvariantCulture,
            fileFormat,
            outside,
            new Uri(outside).AbsoluteUri
        );
        await using var session = CreateSession(_workspace, FakeServer());
        using var tool = CreateTool(_workspace, session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, File = file },
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }
}
