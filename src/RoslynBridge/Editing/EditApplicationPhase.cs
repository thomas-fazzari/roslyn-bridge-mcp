// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Editing;

internal enum EditApplicationPhase
{
    Write = 0,

    Cleanup = 1,

    Command = 2,

    Synchronize = 3,
}
