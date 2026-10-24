// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Settings;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Settings;

public sealed class ProjectSettingsReaderTests : IDisposable
{
    private const string Solution = "App.slnx";
    private const string Project = "App.csproj";
    private const string Notes = "notes.txt";
    private const string Samples = "samples";
    private const string Vendor = "vendor";
    private const int StartupSeconds = 300;
    private const int RequestSeconds = 90;

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-bridge-settings-")
        .FullName;

    public ProjectSettingsReaderTests()
    {
        foreach (var file in new[] { Solution, Project, Notes })
        {
            File.WriteAllText(Path.Combine(_root, file), string.Empty);
        }
    }

    [Fact]
    public void NoFileMeansNoSettings() =>
        ProjectSettingsReader.Read(_root).Should().BeEquivalentTo(new ProjectSettings());

    [Fact]
    public async Task ReadsEveryKeyOfAFileWithCommentsAndTrailingCommasAsync()
    {
        await WriteSettingsAsync(
            $$"""
            {
              // folders the bridge leaves out
              "exclude": ["{{Samples}}", "{{Vendor}}",],
              "startupTimeout": {{StartupSeconds}},
              "requestTimeout": {{RequestSeconds}},
              "solution": "{{Solution}}",
              "diagnostics": { "severity": "information" },
            }
            """
        );

        var settings = ProjectSettingsReader.Read(_root);

        settings.Exclude.Should().Equal(Samples, Vendor);
        settings.StartupTimeout.Should().Be(StartupSeconds);
        settings.RequestTimeout.Should().Be(RequestSeconds);
        settings.Solution.Should().Be(Solution);
        settings.Projects.Should().BeNull();
        settings.Diagnostics!.Severity.Should().Be(DiagnosticSeverity.Information);
    }

    [Fact]
    public async Task ReadsTheProjectsAsync()
    {
        await WriteSettingsAsync($$"""{ "projects": ["{{Project}}"] }""");

        var settings = ProjectSettingsReader.Read(_root);

        settings.Projects.Should().Equal(Project);
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
    [InlineData("\"solution\": \"notes.txt\"", Notes)]
    [InlineData("\"solution\": \"../Outside.slnx\"", "Outside.slnx")]
    [InlineData("\"projects\": [\"Missing.csproj\"]", "Missing.csproj")]
    [InlineData("\"diagnostics\": { \"severity\": \"loud\" }", "severity")]
    [InlineData("\"diagnostics\": { \"severity\": \"9\" }", "severity")]
    [InlineData("\"diagnostics\": { \"other\": 1 }", "other")]
    public async Task RejectsAFaultySettingAsync(string faulty, string fragment)
    {
        await WriteSettingsAsync($$"""{ {{faulty}} }""");

        var exception = FluentActions
            .Invoking(() => ProjectSettingsReader.Read(_root))
            .Should()
            .Throw<InvalidSettingsException>()
            .Which;

        exception.Faults.Should().ContainSingle().Which.Should().ContainEquivalentOf(fragment);
        exception.Message.Should().Contain(ProjectSettings.FileName);
    }

    [Fact]
    public async Task ReportsEveryFaultAtOnceAsync()
    {
        await WriteSettingsAsync(
            $$"""{ "startupTimeout": 0, "solution": "{{Solution}}", "projects": ["{{Project}}"] }"""
        );

        var exception = FluentActions
            .Invoking(() => ProjectSettingsReader.Read(_root))
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
            .Invoking(() => ProjectSettingsReader.Read(_root))
            .Should()
            .Throw<InvalidSettingsException>();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task WriteSettingsAsync(string content) =>
        File.WriteAllTextAsync(Path.Combine(_root, ProjectSettings.FileName), content, TestCt);
}
