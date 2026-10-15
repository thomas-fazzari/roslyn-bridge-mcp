// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Output;
using RoslynBridge.Results;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Editing;

public sealed class WorkspaceEditServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-edit-tests-").FullName;

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    public async Task ApplyCountsUnicodeLineSeparatorsLikeRoslynAsync(string separator)
    {
        var path = Path.Combine(_root, "Source.cs");
        await File.WriteAllTextAsync(path, $"// a{separator}b\nclass C {{}}\n", TestCt);
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = WorkspaceChanges(path, TextEdit(2, 6, 2, 7, "D"));

        await service.ApplyAsync(edit, snapshot, TestCt);

        (await File.ReadAllTextAsync(path, TestCt))
            .Should()
            .Be($"// a{separator}b\nclass D {{}}\n");
    }

    [Fact]
    public async Task ApplyPreservesUtf8BomCrLfAndUtf16PositionsAsync()
    {
        var path = Path.Combine(_root, "Source.cs");
        await File.WriteAllBytesAsync(
            path,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// 😀 name\r\nclass C {}\r\n")],
            TestCt
        );
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
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

        var first = await WriteFileAsync(_root, "First.cs", "class First {}");
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Nested"));
        var deleted = await WriteFileAsync(
            _root,
            Path.Combine("Nested", "Delete.cs"),
            "class Delete {}"
        );
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
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
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);

        var preview = await service.PreviewAsync(
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
                _root,
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
        var snapshot = await service.CaptureAsync(TestCt);

        var preview = await service.PreviewAsync(edit, snapshot, TestCt);
        var rendered = ResponseText(
            LspTool.Response(new ProposalPreview("proposal", preview, Command: null))
        );

        preview.Should().HaveCount(fileCount);
        rendered.Should().Contain("files shown");
        rendered.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
        await AssertFileTextAsync(Path.Combine(_root, "Source0.cs"), before);

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
        var first = await WriteFileAsync(_root, "First.cs", "class A {}");
        var second = await WriteFileAsync(_root, "Second.cs", "class B {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
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
    public async Task ExternalChangeToUneditedSourceInvalidatesEditAsync()
    {
        var target = await WriteFileAsync(_root, "Target.cs", "class A {}");
        var dependency = await WriteFileAsync(_root, "Dependency.cs", "class B {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        await File.WriteAllTextAsync(dependency, "class Other {}", TestCt);

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
    public async Task ResourceOperationsUseTheirDeclaredOrderAsync()
    {
        var oldPath = await WriteFileAsync(_root, "Old.cs", "class Old {}");
        var deleted = await WriteFileAsync(_root, "Delete.cs", "class Delete {}");
        var created = Path.Combine(_root, "New.cs");
        var renamed = Path.Combine(_root, "Renamed.cs");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = DocumentChanges(
            CreateFile(created),
            DocumentEdit(created, version: null, TextEdit(0, 0, 0, 0, "class New {}")),
            RenameFile(oldPath, renamed),
            DocumentEdit(renamed, version: null, TextEdit(0, 6, 0, 9, "Renamed")),
            DeleteFile(deleted)
        );

        await service.ApplyAsync(edit, snapshot, TestCt);

        await AssertFileTextAsync(created, "class New {}");
        await AssertFileTextAsync(renamed, "class Renamed {}");
        File.Exists(oldPath).Should().BeFalse();
        File.Exists(deleted).Should().BeFalse();
    }

    [Fact]
    public async Task OverlappingRangesAreRejectedAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = WorkspaceChanges(
            path,
            TextEdit(0, 0, 0, 5, "struct"),
            TextEdit(0, 3, 0, 7, "invalid")
        );

        await AssertErrorAsync(Apply, ErrorCodes.Lsp);
        await AssertFileTextAsync(path, "class A {}");
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task VersionedDocumentEditRequiresCapturedVersionAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        snapshot = snapshot with
        {
            DocumentVersions = new Dictionary<string, int>(StringComparer.Ordinal) { [path] = 3 },
        };
        var edit = DocumentChanges(DocumentEdit(path, 2, TextEdit(0, 6, 0, 7, "B")));

        await AssertErrorAsync(Apply, ErrorCodes.StaleEdit);
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task EscapingWorkspaceIsRejectedBeforeAnyWriteAsync()
    {
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = WorkspaceChanges(
            Path.Combine(_root, "..", "outside.cs"),
            TextEdit(0, 0, 0, 0, "content")
        );

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task RecursiveDirectoryDeleteIsRejectedAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var operation = DeleteFile(_root);
        operation["options"] = new JsonObject { ["recursive"] = true };
        var edit = DocumentChanges(operation);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        File.Exists(source).Should().BeTrue();
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task PlannedFileCannotAlsoBeAParentDirectoryAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var parent = Path.Combine(_root, "Parent.cs");
        var child = Path.Combine(parent, "Child.cs");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = DocumentChanges(
            DocumentEdit(source, version: null, TextEdit(0, 6, 0, 7, "B")),
            CreateFile(parent),
            CreateFile(child)
        );

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        await AssertFileTextAsync(source, "class A {}");
        File.Exists(parent).Should().BeFalse();
        return;

        Task Apply() => service.ApplyAsync(edit, snapshot, TestCt);
    }

    [Fact]
    public async Task SamePositionInsertsPreserveProtocolOrderAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
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
        var source = await WriteFileAsync(_root, "Name.cs", "class Name {}");
        var destination = Path.Combine(_root, "name.cs");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = DocumentChanges(
            RenameFile(source, destination),
            DocumentEdit(destination, version: null, TextEdit(0, 6, 0, 10, "name"))
        );

        await service.ApplyAsync(edit, snapshot, TestCt);

        Directory.EnumerateFiles(_root).Select(Path.GetFileName).Should().Equal("name.cs");
        await AssertFileTextAsync(destination, "class name {}");
    }

    [Fact]
    public async Task RenameOnlyCaseAndBackLeavesOriginalFileAsync()
    {
        var source = await WriteFileAsync(_root, "Name.cs", "class Name {}");
        var destination = Path.Combine(_root, "name.cs");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var edit = DocumentChanges(
            RenameFile(source, destination),
            RenameFile(destination, source)
        );

        var result = await service.ApplyAsync(edit, snapshot, TestCt);

        result.Operations.Should().BeEmpty();
        Directory.EnumerateFiles(_root).Select(Path.GetFileName).Should().Equal("Name.cs");
    }

    [Fact]
    public async Task CancelledApplyWritesNothingAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);

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

    [Fact]
    public async Task CaptureRefusesSourceFilesOverTheSnapshotLimitAsync()
    {
        await using (var stream = File.Create(Path.Combine(_root, "Large.cs")))
        {
            // A sparse file reaches the limit without writing its content
            stream.SetLength(WorkspaceEditService.MaximumSnapshotBytes + 1L);
        }

        await AssertErrorAsync(Capture, ErrorCodes.InvalidRequest);
        return;

        Task Capture() => CreateService().CaptureAsync(TestCt);
    }

    [Theory]
    [InlineData("bin/Generated.cs")]
    [InlineData(".git/Hook.cs")]
    [InlineData("Notes.md")]
    public async Task EditTargetMustBeAnIncludedSourceFileAsync(string relativePath)
    {
        var target = Path.Combine(_root, relativePath);
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        File.Exists(target).Should().BeFalse();
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(CreateFile(target)), snapshot, TestCt);
    }

    [Theory]
    [InlineData("https://example.com{0}")]
    [InlineData("file://server{0}")]
    [InlineData("file://{0}?query")]
    [InlineData("file://{0}#fragment")]
    [InlineData("file://server:{0}")]
    public async Task EditRequiresALocalFileUriAsync(string targetFormat)
    {
        var target = Path.Combine(_root, "Created.cs");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
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
        var source = await WriteFileAsync(_root, "Source.cs", "class Source {}");
        var target = await WriteFileAsync(_root, "Target.cs", "class Target {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var operation = string.Equals(kind, FileOperation.Create, StringComparison.Ordinal)
            ? CreateFile(target)
            : RenameFile(source, target);

        await AssertErrorAsync(Apply, ErrorCodes.InvalidRequest);
        await AssertFileTextAsync(source, "class Source {}");
        await AssertFileTextAsync(target, "class Target {}");
        return;

        Task Apply() => service.ApplyAsync(DocumentChanges(operation), snapshot, TestCt);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorkspaceEditService CreateService() => new(new WorkspacePaths(_root));

    private static async Task AssertErrorAsync(Func<Task> apply, string code) =>
        (await apply.Should().ThrowAsync<ToolErrorException>()).Which.Error.Code.Should().Be(code);
}
