// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Queries;
using RoslynBridge.Results;

namespace RoslynBridge.Tests.Unit.Queries;

public sealed class ByRefAccessTests
{
    // The pipe marks the start of the highlight and is not part of the line.
    [Theory]
    [InlineData("Interlocked.Exchange(ref |_count, 0)", "ReadWrite")]
    [InlineData("Interlocked.Increment(ref this.|_count)", "ReadWrite")]
    [InlineData("Update(ref owner.state.|_count)", "ReadWrite")]
    [InlineData("Update(ref readonly |_count)", "ReadWrite")]
    [InlineData("int.TryParse(text, out |_count)", "Write")]
    [InlineData("Volatile.Read(ref |_count)", "Read")]
    [InlineData("System.Threading.Volatile.Read(ref this.|_count)", "Read")]
    [InlineData("Interlocked.Read(ref |_count)", "Read")]
    [InlineData("Volatile.Write(ref |_count, 1)", "Write")]
    [InlineData("_count = |_count + 1", "None")]
    [InlineData("_count |+= 1", "None")]
    [InlineData("Layout(layout |_count)", "None")]
    [InlineData("Show(my.ref |_count)", "None")]
    public void ClassifiesTheArgumentByTheTextBeforeIt(string marked, string expected)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var column = marked.IndexOf('|', StringComparison.Ordinal);
        var lines = new[] { "// first", marked.Remove(column, 1) };

        var use = ByRefAccess.Of(lines, new SourcePosition(2, column + 1));

        use.Should().Be(Enum.Parse<ByRefUse>(expected));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 0)]
    public void IgnoresPositionsOutsideTheText(int line, int character)
    {
        var use = ByRefAccess.Of(["Update(ref _count)"], new SourcePosition(line, character));

        use.Should().Be(ByRefUse.None);
    }

    [Fact]
    public void IgnoresAnUnreadableFile()
    {
        var use = ByRefAccess.Of(null, new SourcePosition(1, 12));

        use.Should().Be(ByRefUse.None);
    }
}
