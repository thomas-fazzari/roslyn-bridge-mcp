// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Tools;

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

        result["changes"]![0]!["before"]!["text"]!.GetValue<string>().Should().Be(prefix);
        result["changes"]![0]!["after"]!["text"]!.GetValue<string>().Should().Be(replacement);
        result["previewTruncated"]!.GetValue<bool>().Should().BeTrue();
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
            .Be(LspTool.InvalidRequestErrorCode);

        JsonObject Describe() => document.Describe(new WorkspacePaths("."));
    }
}
