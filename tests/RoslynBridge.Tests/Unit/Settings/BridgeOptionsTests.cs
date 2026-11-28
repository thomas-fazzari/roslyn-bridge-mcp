// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Settings;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Settings;

public sealed class BridgeOptionsTests : TempWorkspaceTests
{
    private const string SAMPLES = "samples";
    private const string VENDOR = "vendor";
    private const int FILE_STARTUP_SECONDS = 300; // as in FileSettings
    private const int FILE_REQUEST_SECONDS = 90; // as in FileSettings
    private const int ARGUMENT_STARTUP_SECONDS = 10;

    private const string FILE_SETTINGS = $$"""
        { "exclude": ["{{SAMPLES}}"], "startupTimeout": 300, "requestTimeout": 90 }
        """;

    [Fact]
    public async Task TakesTheSettingsOfTheFileAsync()
    {
        await WriteSettingsAsync(FILE_SETTINGS);

        var options = Parse();

        options.StartupTimeout.Should().Be(TimeSpan.FromSeconds(FILE_STARTUP_SECONDS));
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(FILE_REQUEST_SECONDS));
        options.Exclude.Should().BeEquivalentTo([SAMPLES]);
    }

    [Fact]
    public async Task AnArgumentWinsOverTheFileAsync()
    {
        await WriteSettingsAsync(FILE_SETTINGS);

        var options = Parse(
            LaunchArguments.STARTUP_TIMEOUT_ARGUMENT,
            ARGUMENT_STARTUP_SECONDS.ToString(CultureInfo.InvariantCulture)
        );

        options.StartupTimeout.Should().Be(TimeSpan.FromSeconds(ARGUMENT_STARTUP_SECONDS));
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(FILE_REQUEST_SECONDS));
    }

    [Fact]
    public async Task ExcludedFoldersOfTheFileAndTheArgumentsAddUpAsync()
    {
        await WriteSettingsAsync(FILE_SETTINGS);

        var options = Parse(
            LaunchArguments.EXCLUDE_ARGUMENT,
            VENDOR,
            LaunchArguments.EXCLUDE_ARGUMENT,
            SAMPLES
        );

        options.Exclude.Should().BeEquivalentTo([SAMPLES, VENDOR]);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void RejectsAnExcludedFolderThatIsNotAName(string value) =>
        FluentActions
            .Invoking(() => Parse(LaunchArguments.EXCLUDE_ARGUMENT, value))
            .Should()
            .Throw<InvalidLaunchArgumentsException>();

    private BridgeOptions Parse(params string[] arguments) =>
        BridgeOptions.From(
            LaunchArguments.Parse([LaunchArguments.WORKSPACE_ARGUMENT, Root, .. arguments]),
            ProjectSettingsReader.Read(Root)
        );

    private Task WriteSettingsAsync(string content) =>
        File.WriteAllTextAsync(Path.Combine(Root, ProjectSettings.FILE_NAME), content, TestCt);
}
