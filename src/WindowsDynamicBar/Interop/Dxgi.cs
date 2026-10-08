using System.Runtime.InteropServices;

namespace WindowsDynamicBar.Interop;

public sealed record DxgiAdapterInfo(string Name, long Luid, long DedicatedVideoMemoryBytes);

public static class DxgiService
{
    private static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static List<DxgiAdapterInfo> EnumAdapters()
    {
        var result = new List<DxgiAdapterInfo>();
        try
        {
            Guid riid = IDXGIFactory1;
            if (CreateDXGIFactory1(ref riid, out IntPtr factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
                return result;
            object factoryObj = Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                var factory = (IDXGIFactory1ComImport)factoryObj;
                for (uint i = 0; i < 8; i++)
                {
                    if (factory.EnumAdapters1(i, out IntPtr adapterPtr) != 0 || adapterPtr == IntPtr.Zero)
                        return result;
                    object adapterObj = Marshal.GetObjectForIUnknown(adapterPtr);
                    try
                    {
                        var adapter = (IDXGIAdapter1ComImport)adapterObj;
                        var desc = default(DXGI_ADAPTER_DESC1);
                        if (adapter.GetDesc1(ref desc) == 0)
                        {
                            result.Add(new DxgiAdapterInfo(desc.Description ?? "", desc.AdapterLuid,
                                (long)desc.DedicatedVideoMemory));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapterObj);
                        Marshal.Release(adapterPtr);
                    }
                }
                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(factoryObj);
                Marshal.Release(factoryPtr);
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1ComImport
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters0();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint adapterIndex, out IntPtr dxgiAdapter);
    }

    [ComImport, Guid("29038f61-3831-4cac-912c-7f8cb7d9f2b0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1ComImport
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(ref DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}