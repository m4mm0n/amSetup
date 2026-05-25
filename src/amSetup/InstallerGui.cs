// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup
// Cross-platform installer UI dispatch and console fallback.

namespace AmSetup;

internal static partial class InstallerGui
{
#if !WINDOWS
    public static bool TryRun(AttachedPackage attached, string? target, string? componentArg, out int exitCode)
    {
        _ = attached;
        _ = target;
        _ = componentArg;
        exitCode = 0;
        return false;
    }
#endif
}
