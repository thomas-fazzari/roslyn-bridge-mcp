// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Settings;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Settings;

public sealed class BridgeOptionsTests : TempWorkspaceTests
{
    private const string Samples = "samples";
    private const string Vendor = "vendor";
    private const int FileStartupSeconds = 300; // as in FileSettings
    private const int FileRequestSeconds = 90; // as in FileSettings
    private const int ArgumentStartupSeconds = 10;

    private const string FileSettings = $$"""
        { "exclude": ["{{Samples}}"], "startupTimeout": 300, "requestTimeout": 90 }
        """;

    [Fact]
    public async Task TakesTheSettingsOfTheFileAsync()
    {
        await WriteSettingsAsync(FileSettings);

        var options = Parse();

        options.StartupTimeout.Should().Be(TimeSpan.FromSeconds(FileStartupSeconds));
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(FileRequestSeconds));
        options.Exclude.Should().BeEquivalentTo([Samples]);
    }

    [Fact]
    public async Task AnArgumentWinsOverTheFileAsync()
    {
        await WriteSettingsAsync(FileSettings);

        var options = Parse(
            LaunchArguments.StartupTimeoutArgument,
            ArgumentStartupSeconds.ToString(CultureInfo.InvariantCulture)
        );

        options.StartupTimeout.Should().Be(TimeSpan.FromSeconds(ArgumentStartupSeconds));
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(FileRequestSeconds));
    }

    [Fact]
    public async Task ExcludedFoldersOfTheFileAndTheArgumentsAddUpAsync()
    {
        await WriteSettingsAsync(FileSettings);

        var options = Parse(
            LaunchArguments.ExcludeArgument,
            Vendor,
            LaunchArguments.ExcludeArgument,
            Samples
        );

        options.Exclude.Should().BeEquivalentTo([Samples, Vendor]);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void RejectsAnExcludedFolderThatIsNotAName(string value) =>
        FluentActions
            .Invoking(() => Parse(LaunchArguments.ExcludeArgument, value))
            .Should()
            .Throw<InvalidLaunchArgumentsException>();

    private BridgeOptions Parse(params string[] arguments) =>
        BridgeOptions.From(
            LaunchArguments.Parse([LaunchArguments.WorkspaceArgument, Root, .. arguments]),
            ProjectSettingsReader.Read(Root)
        );

    private Task WriteSettingsAsync(string content) =>
        File.WriteAllTextAsync(Path.Combine(Root, ProjectSettings.FileName), content, TestCt);
}
