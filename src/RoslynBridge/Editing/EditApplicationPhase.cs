// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Editing;

internal enum EditApplicationPhase
{
    Write = 0,

    Cleanup = 1,

    Command = 2,

    Synchronize = 3,
}
