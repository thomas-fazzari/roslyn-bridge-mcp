// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Extension;

/// <summary>
/// Identifies a declaration across the projects that compile its file, such as one per target framework.
/// </summary>
internal readonly record struct DeclarationKey(string Path, int Start);
