// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using RoslynBridge.Editing;
using RoslynBridge.Results;

namespace RoslynBridge.Tests.Unit.Editing;

public sealed class EditedDocumentTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    [InlineData("utf-32BE")]
    public void PreviewPreservesEncodingAndDoesNotSplitSurrogatePairs(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var prefix = new string('a', EditedDocument.MaximumPreviewCharacters - 1);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(prefix + "😀tail")];
        var replacement = new string('b', prefix.Length);
        var document = new EditedDocument("Source.cs", bytes)
        {
            Content = [.. encoding.GetPreamble(), .. encoding.GetBytes(replacement + "😀tail")],
        };

        var result = document.Describe(new WorkspacePaths("."));

        result.Changes[0].Before.Should().Be(prefix);
        result.Changes[0].After.Should().Be(replacement);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void PreviewValidatesBytesBeyondTheVisiblePrefix()
    {
        byte[] bytes =
        [
            .. Encoding.UTF8.GetBytes(new string('a', EditedDocument.MaximumPreviewCharacters + 1)),
            0xFF,
        ];
        var document = new EditedDocument("Source.cs", bytes);

        FluentActions
            .Invoking(Describe)
            .Should()
            .Throw<ToolErrorException>()
            .Which.Error.Code.Should()
            .Be(ErrorCodes.InvalidRequest);

        FilePreview Describe() => document.Describe(new WorkspacePaths("."));
    }
}
