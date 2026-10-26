using System;

namespace Nexus.Overlay;

/// <summary>
/// Activation state of a kiosk whose page temporarily needs the keyboard. The
/// kiosk is never activated by default, so a text field cannot receive keys;
/// while the page asks for it, a click may activate the kiosk, and when it
/// ends the foreground goes back to the window that held it.
/// </summary>
internal sealed class PanelActivation
{
    // COREWEBVIEW2_PROCESS_FAILED_KIND: browser and render exit lose the page; higher kinds leave it alive and in charge.
    private const int FatalFailureMaxKind = 1;

    private IntPtr _previous;

    public bool Activatable { get; private set; }

    /// <summary>Returns whether the state changed.</summary>
    public bool Set(bool value, IntPtr foreground, IntPtr self)
    {
        if (value == Activatable) return false;
        Activatable = value;
        if (value) _previous = foreground != self ? foreground : IntPtr.Zero;
        return true;
    }

    /// <summary>The window WM_ACTIVATE reports as deactivated is the one to hand the foreground back to.</summary>
    public void OnActivated(IntPtr deactivated, IntPtr self)
    {
        if (!Activatable) return;
        if (deactivated != IntPtr.Zero && deactivated != self) _previous = deactivated;
    }

    /// <summary>The window to return the foreground to; cleared once taken.</summary>
    public IntPtr TakeRestoreTarget()
    {
        var p = _previous;
        _previous = IntPtr.Zero;
        return p;
    }

    /// <summary>The page is gone and only a recreate recovers: it can no longer clear its own state.</summary>
    public static bool IsFatalProcessFailure(int kind) => kind >= 0 && kind <= FatalFailureMaxKind;
}
