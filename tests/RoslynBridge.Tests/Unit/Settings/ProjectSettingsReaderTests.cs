// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Settings;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Settings;

public sealed class ProjectSettingsReaderTests : TempWorkspaceTests
{
    private const string SOLUTION = "App.slnx";
    private const string PROJECT = "App.csproj";
    private const string NOTES = "notes.txt";
    private const string SAMPLES = "samples";
    private const string VENDOR = "vendor";
    private const int STARTUP_SECONDS = 300;
    private const int REQUEST_SECONDS = 90;

    public ProjectSettingsReaderTests()
    {
        foreach (var file in new[] { SOLUTION, PROJECT, NOTES })
        {
            File.WriteAllText(Path.Combine(Root, file), string.Empty);
        }
    }

    [Fact]
    public void NoFileMeansNoSettings() =>
        ProjectSettingsReader.Read(Root).Should().BeEquivalentTo(new ProjectSettings());

    [Fact]
    public async Task ReadsEveryKeyOfAFileWithCommentsAndTrailingCommasAsync()
    {
        await WriteSettingsAsync(
            $$"""
            {
              // folders the bridge leaves out
              "exclude": ["{{SAMPLES}}", "{{VENDOR}}",],
              "startupTimeout": {{STARTUP_SECONDS}},
              "requestTimeout": {{REQUEST_SECONDS}},
              "solution": "{{SOLUTION}}",
              "diagnostics": { "severity": "information" },
            }
            """
        );

        var settings = ProjectSettingsReader.Read(Root);

        settings.Exclude.Should().Equal(SAMPLES, VENDOR);
        settings.StartupTimeout.Should().Be(STARTUP_SECONDS);
        settings.RequestTimeout.Should().Be(REQUEST_SECONDS);
        settings.Solution.Should().Be(SOLUTION);
        settings.Projects.Should().BeNull();
        settings.Diagnostics!.Severity.Should().Be(DiagnosticSeverity.Information);
    }

    [Fact]
    public async Task ReadsTheProjectsAsync()
    {
        await WriteSettingsAsync($$"""{ "projects": ["{{PROJECT}}"] }""");

        var settings = ProjectSettingsReader.Read(Root);

        settings.Projects.Should().Equal(PROJECT);
        settings.Solution.Should().BeNull();
    }

    [Theory]
    [InlineData("\"startupTimeout\": 0", "startupTimeout")]
    [InlineData("\"startupTimeout\": 601", "startupTimeout")]
    [InlineData("\"requestTimeout\": 0", "requestTimeout")]
    [InlineData("\"startupTimeout\": \"fast\"", "startupTimeout")]
    [InlineData("\"startupTimeout\": 1.5", "startupTimeout")]
    [InlineData("\"unknown\": 1", "unknown")]
    [InlineData("\"exclude\": [\"a/b\"]", "a/b")]
    [InlineData("\"exclude\": [\"\"]", "exclude")]
    [InlineData("\"solution\": \"Missing.slnx\"", "Missing.slnx")]
    [InlineData("\"solution\": \"notes.txt\"", NOTES)]
    [InlineData("\"solution\": \"../Outside.slnx\"", "Outside.slnx")]
    [InlineData("\"projects\": [\"Missing.csproj\"]", "Missing.csproj")]
    [InlineData("\"diagnostics\": { \"severity\": \"loud\" }", "severity")]
    [InlineData("\"diagnostics\": { \"severity\": \"9\" }", "severity")]
    [InlineData("\"diagnostics\": { \"other\": 1 }", "other")]
    public async Task RejectsAFaultySettingAsync(string faulty, string fragment)
    {
        await WriteSettingsAsync($$"""{ {{faulty}} }""");

        var exception = FluentActions
            .Invoking(() => ProjectSettingsReader.Read(Root))
            .Should()
            .Throw<InvalidSettingsException>()
            .Which;

        exception.Faults.Should().ContainSingle().Which.Should().ContainEquivalentOf(fragment);
    }

    [Fact]
    public async Task ReportsEveryFaultAtOnceAsync()
    {
        await WriteSettingsAsync(
            $$"""{ "startupTimeout": 0, "solution": "{{SOLUTION}}", "projects": ["{{PROJECT}}"] }"""
        );

        var exception = FluentActions
            .Invoking(() => ProjectSettingsReader.Read(Root))
            .Should()
            .Throw<InvalidSettingsException>()
            .Which;

        exception.Faults.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1]")]
    [InlineData("")]
    public async Task RejectsAFileThatIsNotAnObjectAsync(string content)
    {
        await WriteSettingsAsync(content);

        FluentActions
            .Invoking(() => ProjectSettingsReader.Read(Root))
            .Should()
            .Throw<InvalidSettingsException>();
    }

    private Task WriteSettingsAsync(string content) =>
        File.WriteAllTextAsync(Path.Combine(Root, ProjectSettings.FILE_NAME), content, TestCt);
}
