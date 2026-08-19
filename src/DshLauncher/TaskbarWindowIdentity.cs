using System.Runtime.InteropServices;

namespace DshLauncher;

internal static class TaskbarWindowIdentity
{
    private const ushort VariantTypeUnicodeString = 31;
    private static readonly Guid PropertyStoreInterfaceId =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public static bool TrySetAppUserModelId(IntPtr windowHandle, string appUserModelId)
    {
        if (!OperatingSystem.IsWindows()
            || windowHandle == IntPtr.Zero
            || string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        IPropertyStore? propertyStore = null;
        var value = PropVariant.FromString(appUserModelId);
        try
        {
            var interfaceId = PropertyStoreInterfaceId;
            if (SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out propertyStore) != 0
                || propertyStore is null)
            {
                return false;
            }

            var key = AppUserModelIdKey;
            return propertyStore.SetValue(ref key, ref value) == 0
                && propertyStore.Commit() == 0;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            value.Dispose();
            if (propertyStore is not null)
            {
                Marshal.FinalReleaseComObject(propertyStore);
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)]
        private ushort _valueType;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        public static PropVariant FromString(string value)
        {
            return new PropVariant
            {
                _valueType = VariantTypeUnicodeString,
                _pointerValue = Marshal.StringToCoTaskMemUni(value)
            };
        }

        public void Dispose()
        {
            if (_pointerValue == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeCoTaskMem(_pointerValue);
            _pointerValue = IntPtr.Zero;
            _valueType = 0;
        }
    }
}
