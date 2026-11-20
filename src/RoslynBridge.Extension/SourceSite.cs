// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis.Text;

namespace RoslynBridge.Extension;

/// <summary>
/// A span in a file, which dedupes a site that Roslyn reports once per target framework of the file.
/// </summary>
internal readonly record struct SourceSite(string Path, TextSpan Span);
