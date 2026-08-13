// Smart fork modifications Copyright (c) 2026 HisenWeb
// Based on IronNestFCS by svr2kos2
// SPDX-License-Identifier: MIT

namespace IronNestFCS.Logic.Scheduling;

/// <summary>
/// Release log filter for scheduling. Normal matcher diagnostics are intentionally silent in release builds,
/// while operational dispatch/plan/order messages and all warnings remain available for troubleshooting.
/// </summary>
internal static class MelonLogger
{
    public static void Msg(string message)
    {
        if (message.StartsWith("[FCS Match]", StringComparison.Ordinal))
            return;

        global::MelonLoader.MelonLogger.Msg(message);
    }

    public static void Warning(string message)
    {
        global::MelonLoader.MelonLogger.Warning(message);
    }
}
