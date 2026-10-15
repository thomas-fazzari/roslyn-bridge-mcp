// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using RoslynBridge.Editing;

namespace RoslynBridge.Tests.Unit.Editing;

public sealed class TextPreviewTests
{
    [Fact]
    public void LateChangeOnALongLineAppearsWithItsActualPosition()
    {
        var prefix = new string('a', EditedDocument.MaximumPreviewCharacters + 100);

        var preview = TextPreview.Create(prefix + "OldName", prefix + "NewName");

        preview.Truncated.Should().BeFalse();
        preview.Changes.Should().ContainSingle();
        var change = preview.Changes[0];
        change.Before.Should().EndWith("OldName");
        change.After.Should().EndWith("NewName");
        change
            .BeforeStart.Should()
            .Be(
                new SourcePosition(
                    1,
                    prefix.Length + 1 - change.Before.IndexOf("OldName", StringComparison.Ordinal)
                )
            );
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    [InlineData("\u0085")]
    public void SeparatedChangesHaveSeparateRanges(string newline)
    {
        var lines = Enumerable
            .Range(1, 140)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"line {index}"))
            .ToArray();
        var before = string.Join(newline, lines);
        lines[69] = "changed seventy";
        lines[109] = "changed one hundred ten";

        var preview = TextPreview.Create(before, string.Join(newline, lines));

        preview.Truncated.Should().BeFalse();
        preview.Changes.Should().HaveCount(2);
        preview.Changes[0].BeforeStart.Line.Should().Be(70);
        preview.Changes[1].AfterStart.Line.Should().Be(110);
        preview.Changes[0].Before.Should().Contain("line 70");
        preview.Changes[1].After.Should().Contain("changed one hundred ten");
    }

    [Fact]
    public void InsertionShiftsTheLaterAfterRange()
    {
        var preview = TextPreview.Create("one\nkeep\nold\nend", "inserted\none\nkeep\nnew\nend");

        preview.Truncated.Should().BeFalse();
        preview.Changes.Should().HaveCount(2);
        var change = preview.Changes[1];
        change.Before.Should().Be("old\n");
        change.BeforeStart.Line.Should().Be(3);
        change.After.Should().Be("new\n");
        change.AfterStart.Line.Should().Be(4);
    }

    [Fact]
    public void DeletionHasAnEmptyAfterRange()
    {
        var preview = TextPreview.Create("one\nremove\nend\n", "one\nend\n");

        preview.Changes.Should().ContainSingle();
        var change = preview.Changes[0];
        change.Before.Should().Be("remove\n");
        change.BeforeStart.Line.Should().Be(2);
        change.After.Should().BeEmpty();
        change.AfterStart.Line.Should().Be(2);
    }

    [Fact]
    public void LineEndingChangesRemainVisibleNextToContentChanges()
    {
        var preview = TextPreview.Create("one\r\ntwo\r\nold", "one\ntwo\nnew");

        preview.Truncated.Should().BeFalse();
        var before = string.Concat(preview.Changes.Select(change => change.Before));
        var after = string.Concat(preview.Changes.Select(change => change.After));
        before.Should().Contain("one\r\n").And.Contain("old");
        after.Should().Contain("one\n").And.Contain("new").And.NotContain("\r");
    }

    [Fact]
    public void RemainingBudgetDoesNotProduceEmptySurrogateRanges()
    {
        const int length = EditedDocument.MaximumPreviewCharacters - 2;
        var preview = TextPreview.Create(
            new string('a', length) + "\nkeep\n😀old\nend\n😀last",
            new string('b', length) + "\nkeep\n😁new\nend\n😁tail"
        );

        preview.Truncated.Should().BeTrue();
        preview.Changes.Should().ContainSingle();
        preview.Changes[0].Before.Should().NotBeEmpty();
    }

    [Fact]
    public void OversizedComparisonReportsAnIncompletePreview()
    {
        var preview = TextPreview.Create(
            new string('a', TextPreview.MaximumDiffCharacters),
            new string('b', TextPreview.MaximumDiffCharacters)
        );

        preview.Truncated.Should().BeTrue();
        preview.Changes.Should().ContainSingle();
    }
}
