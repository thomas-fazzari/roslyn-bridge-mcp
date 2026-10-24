// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.Versioning;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit;

public sealed class WorkspacePathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-path-tests-").FullName;

    [Fact]
    public async Task EnumerateFilesSkipsExcludedSubtreesAsync()
    {
        foreach (var directory in WorkspacePaths.ExcludedDirectories)
        {
            await WriteFileAsync(
                _root,
                Path.Combine(directory, "Nested", "Generated.cs"),
                "class Source {}"
            );
        }

        var source = await WriteFileAsync(
            _root,
            Path.Combine("src", "Source.cs"),
            "class Source {}"
        );
        var paths = new WorkspacePaths(_root);

        paths.EnumerateFiles().Select(file => file.Path).Should().Equal(source);
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("research")]
    public async Task EnumerateFilesKeepsSourceDirectoriesAsync(string directory)
    {
        var source = await WriteFileAsync(
            _root,
            Path.Combine(directory, "Source.cs"),
            "class Source {}"
        );
        var paths = new WorkspacePaths(_root);

        paths.EnumerateFiles().Select(file => file.Path).Should().Equal(source);
        paths.IsWatchedDirectory(directory).Should().BeTrue();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task EnumerateFilesSkipsUnreadableDirectoriesAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions are required.");

        var source = await WriteFileAsync(_root, "Source.cs", "class Source {}");
        var locked = Path.GetDirectoryName(
            await WriteFileAsync(_root, Path.Combine("locked", "Hidden.cs"), "class Source {}")
        )!;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var paths = new WorkspacePaths(_root);

            paths.EnumerateFiles().Select(file => file.Path).Should().Equal(source);
        }
        finally
        {
            File.SetUnixFileMode(
                locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    [Fact]
    public async Task ContainsProjectFindsAProjectTwoFoldersBelowTheRootAsync()
    {
        await WriteFileAsync(_root, Path.Combine("src", "App", "App.csproj"), "<Project />");

        new WorkspacePaths(_root).ContainsProject().Should().BeTrue();
    }

    [Fact]
    public async Task ContainsProjectIgnoresSourcesExcludedDirectoriesAndDeepFoldersAsync()
    {
        await WriteFileAsync(_root, "Source.cs", "class Source {}");
        await WriteFileAsync(_root, Path.Combine("bin", "App.csproj"), "<Project />");
        await WriteFileAsync(_root, Path.Combine("a", "b", "c", "App.csproj"), "<Project />");

        new WorkspacePaths(_root).ContainsProject().Should().BeFalse();
    }

    [Fact]
    public void WorkspaceFileReadsAWindowsDriveWrittenLikeRoslyn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters exist only on Windows.");
        var file = Path.Combine(_root, "src", "Source.cs");
        var uri = $"file:///{char.ToLowerInvariant(file[0])}%3A{file[2..].Replace('\\', '/')}";

        new WorkspacePaths(_root).TryGetWorkspaceFile(uri).Should().Be(file);
    }

    [Fact]
    public void ResolveReportsAPathWithANullCharacterAsAnInvalidRequest()
    {
        FluentActions
            .Invoking(Resolve)
            .Should()
            .Throw<ToolErrorException>()
            .Which.Error.Code.Should()
            .Be(ErrorCodes.InvalidRequest);

        string Resolve() => new WorkspacePaths(_root).Resolve("src/A\0.cs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveRejectsAPathThroughASymbolicLinkAsync(bool linkDirectory)
    {
        var target = await WriteFileAsync(
            _root,
            Path.Combine("Target", "Source.cs"),
            "class Source {}"
        );
        var link = Path.Combine(_root, linkDirectory ? "Linked" : "Linked.cs");
        try
        {
            if (linkDirectory)
            {
                Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs privileges on this platform.");
        }

        FluentActions
            .Invoking(Resolve)
            .Should()
            .Throw<ToolErrorException>()
            .Which.Error.Code.Should()
            .Be(ErrorCodes.InvalidRequest);
        return;

        string Resolve() =>
            new WorkspacePaths(_root).Resolve(
                linkDirectory ? Path.Combine("Linked", "Source.cs") : "Linked.cs"
            );
    }

    [Fact]
    public async Task ExcludedFoldersAreLeftOutAndReplacedByTheNextCallAsync()
    {
        const string samples = "samples";
        const string vendor = "vendor";

        var kept = await WriteFileAsync(_root, Path.Combine("src", "Source.cs"), "class Source {}");
        var inSamples = await WriteFileAsync(
            _root,
            Path.Combine(samples, "Sample.cs"),
            "class S {}"
        );
        var inVendor = await WriteFileAsync(_root, Path.Combine(vendor, "Vendor.cs"), "class V {}");
        var paths = new WorkspacePaths(_root);

        paths.Exclude([samples]);
        var first = paths.EnumerateFiles().Select(file => file.Path).ToList();
        paths.Exclude([vendor]);
        var second = paths.EnumerateFiles().Select(file => file.Path).ToList();

        first.Should().BeEquivalentTo([kept, inVendor]);
        second.Should().BeEquivalentTo([kept, inSamples]);
        paths.IsExcluded(inVendor).Should().BeTrue();
        paths.IsExcludedDirectory(WorkspacePaths.IntermediateDirectoryName).Should().BeTrue();
    }

    [Fact]
    public void IntermediateOutputRemainsWatchedForRestoreChanges() =>
        new WorkspacePaths(_root)
            .IsWatchedDirectory(WorkspacePaths.IntermediateDirectoryName)
            .Should()
            .BeTrue();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
