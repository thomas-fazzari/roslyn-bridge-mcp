// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO.Pipelines;
using RoslynBridge.Lsp;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class ProtocolTests
{
    [Fact]
    public async Task ReaderRejectsAnOversizedUnconsumedMessageAsync()
    {
        await using var stream = new MemoryStream(new byte[17]);
        var reader = new BoundedPipeReader(PipeReader.Create(stream), maximumBytes: 16);
        try
        {
            await FluentActions.Awaiting(Read).Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            await reader.CompleteAsync();
        }

        return;

        async Task Read() => await reader.ReadAsync(TestCt);
    }

    [Fact]
    public async Task ReaderReleasesConsumedBytesBeforeTheNextMessageAsync()
    {
        var pipe = new Pipe();
        var reader = new BoundedPipeReader(pipe.Reader, maximumBytes: 16);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await pipe.Writer.WriteAsync(new byte[16], TestCt);
                var read = await reader.ReadAsync(TestCt);

                read.Buffer.Length.Should().Be(16);
                reader.AdvanceTo(read.Buffer.End);
            }
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            await reader.CompleteAsync();
        }
    }
}
