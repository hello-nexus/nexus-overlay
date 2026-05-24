using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Interface every window-owning managed object implements. The static
/// WNDPROC looks up the HWND in the registry and forwards messages to
/// the owner's <c>HandleMessage</c>. Returning <c>null</c> means "let
/// DefWindowProc handle it"; returning an <c>IntPtr</c> uses that value
/// as the result.
/// </summary>
internal interface IWin32WindowOwner
{
    IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// AOT-clean window-class registration + WNDPROC dispatch table. The
/// single static WNDPROC is exported as [UnmanagedCallersOnly] so the
/// runtime does not need a managed-delegate thunk (which NativeAOT
/// cannot generate). Owner registration is keyed by HWND, populated
/// before <c>CreateWindowExW</c> can return via a pre-CreateWindow
/// thread-local "pending owner" slot.
/// </summary>
internal static unsafe class Win32Window
{
    private static readonly ConcurrentDictionary<IntPtr, IWin32WindowOwner> Owners = new();
    [ThreadStatic] private static IWin32WindowOwner? _pendingOwner;

    private static readonly ConcurrentDictionary<string, bool> RegisteredClasses = new();

    public static void EnsureClassRegistered(string className, IntPtr hbrBackground = default, IntPtr hIcon = default)
    {
        if (RegisteredClasses.ContainsKey(className)) return;
        fixed (char* cn = className)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                style = 0,
                lpfnWndProc = &WndProc,
                hInstance = Native.GetModuleHandleW(null),
                hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)32512 /* IDC_ARROW */),
                hbrBackground = hbrBackground,
                hIcon = hIcon,
                hIconSm = hIcon,
                lpszClassName = (IntPtr)cn,
            };
            var atom = Native.RegisterClassExW(&wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"RegisterClassExW failed for '{className}' err={err}");
            }
            RegisteredClasses[className] = true;
        }
    }

    /// <summary>
    /// Creates a top-level window and registers the owner against its
    /// HWND before <c>CreateWindowExW</c> returns - this lets the owner
    /// receive WM_NCCREATE / WM_CREATE messages that fire synchronously
    /// during the call. Pass a non-default <paramref name="hbrBackground"/>
    /// to avoid the unpainted flash that visible top-level windows show
    /// before the first WM_PAINT - the overlay uses default (no brush)
    /// because its layered/transparent compositing makes the brush moot,
    /// but a normal opaque window wants COLOR_WINDOW+1 or similar.
    /// </summary>
    public static IntPtr Create(string className, string title, uint style, uint exStyle,
        int x, int y, int width, int height, IWin32WindowOwner owner,
        IntPtr hbrBackground = default, IntPtr hIcon = default)
    {
        EnsureClassRegistered(className, hbrBackground, hIcon);
        _pendingOwner = owner;
        try
        {
            var hwnd = Native.CreateWindowExW(exStyle, className, title, style,
                x, y, width, height, IntPtr.Zero, IntPtr.Zero,
                Native.GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowExW failed err={err}");
            }
            // The pending-owner slot may already have been claimed by the
            // WM_NCCREATE path; idempotent registration here covers cases
            // where no message fired before CreateWindowExW returned.
            Owners.TryAdd(hwnd, owner);
            return hwnd;
        }
        finally
        {
            _pendingOwner = null;
        }
    }

    public static void Unregister(IntPtr hwnd) => Owners.TryRemove(hwnd, out _);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Adopt the pending owner the first time we see a message for an
        // unknown HWND - this covers the WM_NCCREATE that fires inside
        // CreateWindowExW before our Create method completes.
        if (!Owners.TryGetValue(hwnd, out var owner))
        {
            var pending = _pendingOwner;
            if (pending is not null)
            {
                Owners.TryAdd(hwnd, pending);
                owner = pending;
            }
        }

        if (owner is not null)
        {
            var result = owner.HandleMessage(hwnd, msg, wParam, lParam);
            if (result.HasValue) return result.Value;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
