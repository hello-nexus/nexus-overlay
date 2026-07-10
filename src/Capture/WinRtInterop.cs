using System;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Capture;

/// <summary>
/// combase.dll surface for WinRT activation without CsWinRT: HSTRING
/// creation and RoGetActivationFactory. Everything past the factory pointer
/// is reached through raw vtable calls (see WgcVtable). AOT-safe:
/// blittable-only signatures.
/// </summary>
internal static unsafe class WinRtInterop
{
    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(char* sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, out IntPtr factory);

    /// <summary>Owned HSTRING handle; dispose to free.</summary>
    public ref struct HString
    {
        public IntPtr Handle;

        public static HString Create(string value)
        {
            fixed (char* p = value)
            {
                var hr = WindowsCreateString(p, value.Length, out var handle);
                if (hr < 0)
                    throw new InvalidOperationException($"WindowsCreateString failed 0x{hr:X8}");
                return new HString { Handle = handle };
            }
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            WindowsDeleteString(Handle);
            Handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Activation factory for a WinRT runtime class, QI'd to
    /// <paramref name="iid"/>. Caller releases the returned pointer.
    /// </summary>
    public static IntPtr GetActivationFactory(string className, in Guid iid)
    {
        using var hClass = HString.Create(className);
        fixed (Guid* piid = &iid)
        {
            var hr = RoGetActivationFactory(hClass.Handle, piid, out var factory);
            if (hr < 0 || factory == IntPtr.Zero)
                throw new InvalidOperationException($"RoGetActivationFactory({className}) failed 0x{hr:X8}");
            return factory;
        }
    }
}
