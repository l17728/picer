using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Picer.App.Infrastructure;

internal static class TaskbarIdentity
{
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const string AppId = "Picer.Desktop";

    internal static void Apply(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        try
        {
            var interfaceId = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out var store));
            try
            {
                var executable = Environment.ProcessPath;
                // Set relaunch details before the window AppID: assigning the ID
                // tells Explorer to refresh its taskbar representation.
                SetString(store, 2, $"\"{executable}\"");
                SetString(store, 4, "Picer 照片与视频管理");
                SetString(store, 3, $"{executable},0");
                SetString(store, 5, AppId);
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            Trace.WriteLine($"Picer taskbar identity unavailable: {ex.HResult:X8}");
        }
    }

    private static void SetString(IPropertyStore store, uint propertyId, string text)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = propertyId };
        var value = new PropVariant
        {
            Type = 31, // VT_LPWSTR
            StringPointer = Marshal.StringToCoTaskMemUni(text)
        };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.StringPointer); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle, ref Guid interfaceId, out IPropertyStore store);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr StringPointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
