// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.IO.Pipelines;

namespace RoslynBridge.Lsp;

/// <summary>
/// Limits bytes retained by the JSON-RPC reader without interpreting its framing.
/// </summary>
internal sealed class BoundedPipeReader(PipeReader inner, long maximumBytes) : PipeReader
{
    public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) =>
        inner.AdvanceTo(consumed, examined);

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) =>
        inner.CompleteAsync(exception);

    public override async ValueTask<ReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => Validate(await inner.ReadAsync(cancellationToken));

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
        {
            return false;
        }

        result = Validate(result);
        return true;
    }

    private ReadResult Validate(ReadResult result) =>
        result.Buffer.Length > maximumBytes
            ? throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The Roslyn protocol buffer exceeded its {maximumBytes}-byte limit."
                )
            )
            : result;
}
