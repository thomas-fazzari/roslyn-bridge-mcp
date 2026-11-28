// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.Versioning;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit;

public sealed class WorkspacePathsTests : TempWorkspaceTests
{
    [Fact]
    public async Task EnumerateFilesSkipsExcludedSubtreesAsync()
    {
        foreach (var directory in WorkspacePaths.ExcludedDirectories)
        {
            await WriteFileAsync(
                Root,
                Path.Combine(directory, "Nested", "Generated.cs"),
                "class Source {}"
            );
        }

        var source = await WriteFileAsync(
            Root,
            Path.Combine("src", "Source.cs"),
            "class Source {}"
        );
        var paths = new WorkspacePaths(Root);

        paths
            .EnumerateFiles(TestCt)
            .Succeeded()
            .Select(file => file.Path.FullPath)
            .Should()
            .Equal(source);
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("research")]
    public async Task EnumerateFilesKeepsSourceDirectoriesAsync(string directory)
    {
        var source = await WriteFileAsync(
            Root,
            Path.Combine(directory, "Source.cs"),
            "class Source {}"
        );
        var paths = new WorkspacePaths(Root);

        paths
            .EnumerateFiles(TestCt)
            .Succeeded()
            .Select(file => file.Path.FullPath)
            .Should()
            .Equal(source);
        paths.IsWatchedDirectory(directory).Should().BeTrue();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task EnumerateFilesSkipsUnreadableDirectoriesAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions are required.");

        var source = await WriteFileAsync(Root, SOURCE_FILE, "class Source {}");
        var locked = Path.GetDirectoryName(
            await WriteFileAsync(Root, Path.Combine("locked", "Hidden.cs"), "class Source {}")
        )!;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var paths = new WorkspacePaths(Root);

            paths
                .EnumerateFiles(TestCt)
                .Succeeded()
                .Select(file => file.Path.FullPath)
                .Should()
                .Equal(source);
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
        await WriteFileAsync(Root, Path.Combine("src", "App", "App.csproj"), "<Project />");

        new WorkspacePaths(Root).ContainsProject().Should().BeTrue();
    }

    [Fact]
    public async Task ContainsProjectIgnoresSourcesExcludedDirectoriesAndDeepFoldersAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class Source {}");
        await WriteFileAsync(Root, Path.Combine("bin", "App.csproj"), "<Project />");
        await WriteFileAsync(Root, Path.Combine("a", "b", "c", "App.csproj"), "<Project />");

        new WorkspacePaths(Root).ContainsProject().Should().BeFalse();
    }

    [Fact]
    public void WorkspaceFileReadsAWindowsDriveWrittenLikeRoslyn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters exist only on Windows.");
        var file = Path.Combine(Root, "src", "Source.cs");
        var uri = $"file:///{char.ToLowerInvariant(file[0])}%3A{file[2..].Replace('\\', '/')}";

        new WorkspacePaths(Root).TryGetWorkspaceFile(uri)!.FullPath.Should().Be(file);
    }

    [Fact]
    public async Task ResolveUsesTheOnDiskCasingOfExistingComponentsAsync()
    {
        SkipOnCaseSensitiveFileSystem();
        var path = await WriteFileAsync(Root, Path.Combine("Src", "Greeter.cs"), "class A {}");
        var paths = new WorkspacePaths(Root);

        var file = paths.Resolve("src/greeter.cs").Succeeded();
        var created = paths.Resolve("src/new.cs").Succeeded();

        file.FullPath.Should().Be(path);
        file.RelativePath.Should().Be("Src/Greeter.cs");
        created.RelativePath.Should().Be("Src/new.cs");
        new WorkspacePaths(Root.ToUpperInvariant()).Root.Should().Be(Root);
    }

    [Fact]
    public void ResolveReportsAPathWithANullCharacterAsAnInvalidRequest() =>
        new WorkspacePaths(Root)
            .Resolve("src/A\0.cs")
            .ErrorCode()
            .Should()
            .Be(ErrorCodes.INVALID_REQUEST);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAndRoslynLocationsRejectAPathThroughASymbolicLinkAsync(
        bool linkDirectory
    )
    {
        var target = await WriteFileAsync(
            Root,
            Path.Combine("Target", "Source.cs"),
            "class Source {}"
        );
        var link = Path.Combine(Root, linkDirectory ? "Linked" : "Linked.cs");
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

        var paths = new WorkspacePaths(Root);
        var linked = linkDirectory ? Path.Combine("Linked", "Source.cs") : "Linked.cs";

        paths.Resolve(linked).ErrorCode().Should().Be(ErrorCodes.INVALID_REQUEST);
        paths
            .TryGetWorkspaceFile(new Uri(Path.Combine(Root, linked)).AbsoluteUri)
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task ExcludedFoldersAreLeftOutAndReplacedByTheNextCallAsync()
    {
        const string SAMPLES = "samples";
        const string VENDOR = "vendor";

        var kept = await WriteFileAsync(Root, Path.Combine("src", "Source.cs"), "class Source {}");
        var inSamples = await WriteFileAsync(
            Root,
            Path.Combine(SAMPLES, "Sample.cs"),
            "class S {}"
        );
        var inVendor = await WriteFileAsync(Root, Path.Combine(VENDOR, "Vendor.cs"), "class V {}");
        var paths = new WorkspacePaths(Root);

        paths.Exclude([SAMPLES]);
        var first = paths.EnumerateFiles(TestCt).Succeeded().ConvertAll(file => file.Path.FullPath);
        paths.Exclude([VENDOR]);
        var second = paths
            .EnumerateFiles(TestCt)
            .Succeeded()
            .ConvertAll(file => file.Path.FullPath);

        first.Should().BeEquivalentTo([kept, inVendor]);
        second.Should().BeEquivalentTo([kept, inSamples]);
        paths.IsExcluded(paths.Resolve(inVendor).Succeeded()).Should().BeTrue();
        paths.IsExcludedDirectory(WorkspacePaths.INTERMEDIATE_DIRECTORY_NAME).Should().BeTrue();
    }

    [Fact]
    public void IntermediateOutputRemainsWatchedForRestoreChanges() =>
        new WorkspacePaths(Root)
            .IsWatchedDirectory(WorkspacePaths.INTERMEDIATE_DIRECTORY_NAME)
            .Should()
            .BeTrue();
}
