// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class WorkspaceBoundaryTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("roslyn-boundary-").FullName;

    private readonly string _root;
    private readonly string _outside;

    public WorkspaceBoundaryTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_parent, "Workspace")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_parent, "Outside")).FullName;
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
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, File = file },
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);
}
