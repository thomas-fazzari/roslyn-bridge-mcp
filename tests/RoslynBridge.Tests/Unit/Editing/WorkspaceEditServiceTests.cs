// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;
using RoslynBridge.Output;
using RoslynBridge.Results;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Editing;

public sealed class WorkspaceEditServiceTests : TempWorkspaceTests
{
    private readonly string _outside = Directory
        .CreateTempSubdirectory("roslyn-bridge-tests-outside-")
        .FullName;

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    public async Task ApplyCountsUnicodeLineSeparatorsLikeRoslynAsync(string separator)
    {
        var path = Path.Combine(Root, SourceFile);
        await File.WriteAllTextAsync(path, $"// a{separator}b\nclass C {{}}\n", TestCt);
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = WorkspaceChanges(path, TextEdit(2, 6, 2, 7, "D"));

        await service.ApplyAsync(edit, snapshot, TestCt);

        (await File.ReadAllTextAsync(path, TestCt))
            .Should()
            .Be($"// a{separator}b\nclass D {{}}\n");
    }

    [Fact]
    public async Task ApplyPreservesUtf8BomCrLfAndUtf16PositionsAsync()
    {
        var path = Path.Combine(Root, SourceFile);
        await File.WriteAllBytesAsync(
            path,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// 😀 name\r\nclass C {}\r\n")],
            TestCt
        );
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = WorkspaceChanges(path, TextEdit(0, 6, 0, 10, "value"));

        var result = await service.ApplyAsync(edit, snapshot, TestCt);

        result.Applied.Should().BeTrue();
        var bytes = await File.ReadAllBytesAsync(path, TestCt);
        bytes
            .Should()
            .Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// 😀 value\r\nclass C {}\r\n")]);
    }

    [Fact]
    public async Task ApplyReportsCompletedWritesWhenLaterDeleteFailsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This test requires Unix directory permissions.");
            return;
        }

        Assert.SkipWhen(
            string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase),
            "The root user bypasses Unix directory permissions."
        );

        var first = await WriteFileAsync(Root, "First.cs", "class First {}");
        var directory = Directory.CreateDirectory(Path.Combine(Root, "Nested"));
        var deleted = await WriteFileAsync(
            Root,
            Path.Combine("Nested", "Delete.cs"),
            "class Delete {}"
        );
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = DocumentChanges(
            DocumentEdit(first, version: null, TextEdit(0, 6, 0, 11, "Changed")),
            DeleteFile(deleted)
        );
        var originalMode = File.GetUnixFileMode(directory.FullName);
        File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var exception = (
                await FluentActions.Awaiting(Apply).Should().ThrowAsync<EditApplicationException>()
            ).Which;

            exception.Phase.Should().Be(EditApplicationPhase.Write);
            exception.FailedPath.Should().Be(Path.Combine("Nested", "Delete.cs"));
            exception.FailedOperation.Should().Be(FileOperation.Delete);
            (exception.InnerException is IOException or UnauthorizedAccessException)
                .Should()
                .BeTrue();
            exception.Result.Applied.Should().BeFalse();
            exception
                .Result.Operations.Should()
                .Equal(new FileOperation("First.cs", FileOperation.Change));
            await AssertFileTextAsync(first, "class Changed {}");
            File.Exists(deleted).Should().BeTrue();
        }
        finally
        {
            File.SetUnixFileMode(directory.FullName, originalMode);
        }
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task PreviewDoesNotChangeFilesAsync()
    {
        var path = await WriteFileAsync(Root, SourceFile, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);

        var (preview, _) = await service.PreviewAsync(
            WorkspaceChanges(path, TextEdit(0, 6, 0, 7, "B")),
            snapshot,
            TestCt
        );

        preview.Should().ContainSingle().Which.Changes[0].After.Should().Be("class B {}");
        await AssertFileTextAsync(path, "class A {}");
    }

    [Fact]
    public async Task GlobalPreviewBudgetDoesNotLimitAppliedFilesAsync()
    {
        const int fileCount = 64;
        var before = new string('a', EditedDocument.MaximumPreviewCharacters + 100);
        var after = new string('b', before.Length);
        var changes = new JsonObject();
        var paths = new string[fileCount];
        for (var index = 0; index < fileCount; index++)
        {
            var path = await WriteFileAsync(
                Root,
                string.Create(CultureInfo.InvariantCulture, $"Source{index}.cs"),
                before
            );
            paths[index] = path;
            changes[new Uri(path).AbsoluteUri] = new JsonArray(
                TextEdit(0, 0, 0, before.Length, after)
            );
        }

        var edit = new JsonObject { ["changes"] = changes };
        var service = CreateService();
        var snapshot = Snapshot(Root);

        var (preview, _) = await service.PreviewAsync(edit, snapshot, TestCt);
        var rendered = ResponseText(
            LspTool.Response(new ProposalPreview("proposal", preview, Command: null))
        );

        preview.Should().HaveCount(fileCount);
        rendered.Should().Contain("files shown");
        rendered.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
        await AssertFileTextAsync(Path.Combine(Root, "Source0.cs"), before);

        var applied = await service.ApplyAsync(edit, snapshot, TestCt);

        applied.Applied.Should().BeTrue();
        applied.Operations.Should().HaveCount(fileCount);
        foreach (var path in paths)
        {
            await AssertFileTextAsync(path, after);
        }
    }

    [Fact]
    public async Task StaleSecondFilePreventsEveryWriteAsync()
    {
        var first = await WriteFileAsync(Root, "First.cs", "class A {}");
        var second = await WriteFileAsync(Root, "Second.cs", "class B {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = WorkspaceChanges(first, TextEdit(0, 6, 0, 7, "Changed"));
        edit["changes"]![new Uri(second).AbsoluteUri] = new JsonArray(
            TextEdit(0, 6, 0, 7, "Changed")
        );
        await File.WriteAllTextAsync(second, "class External {}", TestCt);

        await AssertErrorAsync(Apply, ErrorCodes.StaleEdit);
        await AssertFileTextAsync(first, "class A {}");
        await AssertFileTextAsync(second, "class External {}");
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task ExternalChangeToAProjectFileInvalidatesEditAsync()
    {
        var target = await WriteFileAsync(Root, "Target.cs", "class A {}");
        var project = await WriteFileAsync(Root, "Sample.csproj", "<Project />");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestCt);

        await AssertErrorAsync(Apply, ErrorCodes.StaleEdit);
        await AssertFileTextAsync(target, "class A {}");
        return;

        Task Apply() =>
            service.ApplyAsync(
                WorkspaceChanges(target, TextEdit(0, 6, 0, 7, "C")),
                snapshot,
                TestCt
            );
    }

    [Fact]
    public async Task ExternalChangeToAnUntouchedSourceKeepsTheEditAsync()
    {
        var target = await WriteFileAsync(Root, "Target.cs", "class A {}");
        var other = await WriteFileAsync(Root, "Other.cs", "class B {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        await File.WriteAllTextAsync(other, "class Other {}", TestCt);

        await service.ApplyAsync(
            WorkspaceChanges(target, TextEdit(0, 6, 0, 7, "C")),
            snapshot,
            TestCt
        );

        await AssertFileTextAsync(target, "class C {}");
    }

    [Fact]
    public async Task PreviewedFileChangedWithItsStampKeptInvalidatesTheApplyAsync()
    {
        var target = await WriteFileAsync(Root, "Target.cs", "class A {}");
        var service = CreateService();
        var edit = WorkspaceChanges(target, TextEdit(0, 6, 0, 7, "C"));
        var (_, previewed) = await service.PreviewAsync(edit, Snapshot(Root), TestCt);
        var written = File.GetLastWriteTimeUtc(target);
        await File.WriteAllTextAsync(target, "class B {}", TestCt);
        File.SetLastWriteTimeUtc(target, written);

        await AssertErrorAsync(Apply, ErrorCodes.StaleEdit);
        await AssertFileTextAsync(target, "class B {}");
        return;

        Task Apply() => service.ApplyAsync(edit, previewed, TestCt);
    }

    [Fact]
    public async Task SamePositionInsertsPreserveProtocolOrderAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = WorkspaceChanges(
            source,
            TextEdit(0, 0, 0, 0, "public "),
            TextEdit(0, 0, 0, 0, "sealed ")
        );

        await service.ApplyAsync(edit, snapshot, TestCt);

        await AssertFileTextAsync(source, "public sealed class A {}");
    }

    [Fact]
    public async Task RenameCanChangeOnlyFileNameCaseAsync()
    {
        var source = await WriteFileAsync(Root, "Name.cs", "class Name {}");
        var destination = Path.Combine(Root, "name.cs");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = DocumentChanges(
            RenameFile(source, destination),
            DocumentEdit(destination, version: null, TextEdit(0, 6, 0, 10, "name"))
        );

        await service.ApplyAsync(edit, snapshot, TestCt);

        Directory.EnumerateFiles(Root).Select(Path.GetFileName).Should().Equal("name.cs");
        await AssertFileTextAsync(destination, "class name {}");
    }

    [Fact]
    public async Task RenameOnlyCaseAndBackLeavesOriginalFileAsync()
    {
        var source = await WriteFileAsync(Root, "Name.cs", "class Name {}");
        var destination = Path.Combine(Root, "name.cs");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var edit = DocumentChanges(
            RenameFile(source, destination),
            RenameFile(destination, source)
        );

        var result = await service.ApplyAsync(edit, snapshot, TestCt);

        result.Operations.Should().BeEmpty();
        Directory.EnumerateFiles(Root).Select(Path.GetFileName).Should().Equal("Name.cs");
    }

    [Fact]
    public async Task CancelledApplyWritesNothingAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);

        await FluentActions.Awaiting(Apply).Should().ThrowAsync<OperationCanceledException>();
        await AssertFileTextAsync(source, "class A {}");
        return;

        Task Apply() =>
            service.ApplyAsync(
                WorkspaceChanges(source, TextEdit(0, 6, 0, 7, "B")),
                snapshot,
                new CancellationToken(canceled: true)
            );
    }

    [Theory]
    [InlineData("bin/Generated.cs")]
    [InlineData(".git/Hook.cs")]
    [InlineData("Notes.md")]
    public async Task EditTargetMustBeAnIncludedSourceFileAsync(string relativePath)
    {
        var target = Path.Combine(Root, relativePath);
        var service = CreateService();
        var snapshot = Snapshot(Root);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        File.Exists(target).Should().BeFalse();
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(CreateFile(target)), snapshot, TestCt);
    }

    [Theory]
    [InlineData(FileOperation.Create)]
    [InlineData(FileOperation.Rename)]
    public async Task EditTargetMustBeInsideTheWorkspaceAsync(string kind)
    {
        var source = await WriteFileAsync(Root, SourceFile, "class Source {}");
        var target = Path.Combine(_outside, "Target.cs");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var operation = string.Equals(kind, FileOperation.Create, StringComparison.Ordinal)
            ? CreateFile(target)
            : RenameFile(source, target);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        File.Exists(target).Should().BeFalse();
        await AssertFileTextAsync(source, "class Source {}");
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(operation), snapshot, TestCt);
    }

    [Theory]
    [InlineData("https://example.com{0}")]
    [InlineData("file://server{0}")]
    [InlineData("file://{0}?query")]
    [InlineData("file://{0}#fragment")]
    [InlineData("file://server:{0}")]
    public async Task EditRequiresALocalFileUriAsync(string targetFormat)
    {
        var target = Path.Combine(Root, "Created.cs");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var operation = CreateFile(target);
        operation["uri"] = string.Format(
            CultureInfo.InvariantCulture,
            targetFormat,
            // Windows paths have no leading slash in AbsolutePath
            $"/{new Uri(target).AbsolutePath.TrimStart('/')}"
        );

        await AssertErrorAsync(Apply, ErrorCodes.Lsp);
        File.Exists(target).Should().BeFalse();
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(operation), snapshot, TestCt);
    }

    [Theory]
    [InlineData(FileOperation.Create)]
    [InlineData(FileOperation.Rename)]
    public async Task ReplacingAnExistingFileRequiresOverwriteAsync(string kind)
    {
        var source = await WriteFileAsync(Root, SourceFile, "class Source {}");
        var target = await WriteFileAsync(Root, "Target.cs", "class Target {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var operation = string.Equals(kind, FileOperation.Create, StringComparison.Ordinal)
            ? CreateFile(target)
            : RenameFile(source, target);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        await AssertFileTextAsync(source, "class Source {}");
        await AssertFileTextAsync(target, "class Target {}");
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(operation), snapshot, TestCt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Directory.Delete(_outside, recursive: true);
        }

        base.Dispose(disposing);
    }

    private WorkspaceEditService CreateService() => new(new WorkspacePaths(Root));

    private static async Task AssertErrorAsync(Func<Task> apply, string code) =>
        (await apply.Should().ThrowAsync<ToolErrorException>()).Which.Error.Code.Should().Be(code);
}
