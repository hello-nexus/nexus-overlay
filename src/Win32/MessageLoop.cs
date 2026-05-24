using System;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Standard Win32 message pump. Drains the sync-context queue whenever a
/// registered drain message arrives, otherwise translates+dispatches.
/// Returns when GetMessageW receives WM_QUIT (returns FALSE).
/// </summary>
internal static class MessageLoop
{
    public static int Run(Win32SynchronizationContext syncContext)
    {
        var drainMsg = syncContext.DrainMessage;
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == drainMsg)
            {
                syncContext.Drain();
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        return 0;
    }
}
