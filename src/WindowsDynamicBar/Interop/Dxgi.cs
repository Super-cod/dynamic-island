using System.Runtime.InteropServices;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Interop;

public sealed record DxgiAdapterInfo(string Name, long Luid, long DedicatedVideoMemoryBytes, uint VendorId, uint DeviceId)
{
    public const uint VendorNvidia = 0x10DE;
    public bool IsNvidia => VendorId == VendorNvidia;
}

/// <summary>Hardware GPU adapters from DXGI (name, LUID, dedicated VRAM). Software adapters
/// (Microsoft Basic Render Driver) are skipped.</summary>
public static class DxgiService
{
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    public static List<DxgiAdapterInfo> EnumAdapters()
    {
        var result = new List<DxgiAdapterInfo>();
        Guid riid = typeof(IDXGIFactory1).GUID;
        int hr = CreateDXGIFactory1(ref riid, out IDXGIFactory1? factory);
        if (hr != 0 || factory is null)
        {
            Log.Debug("CreateDXGIFactory1 failed", ("hr", $"0x{hr:X8}"));
            return result;
        }
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                hr = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || adapter is null) break;
                try
                {
                    if (hr != 0 || adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) != 0) continue;
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                    long luid = ((long)desc.AdapterLuidHigh << 32) | desc.AdapterLuidLow;
                    result.Add(new DxgiAdapterInfo((desc.Description ?? "").Trim(), luid,
                        (long)desc.DedicatedVideoMemory, desc.VendorId, desc.DeviceId));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        catch (COMException ex) { Log.Debug("DXGI adapter enumeration failed", ("error", ex.Message)); }
        finally { Marshal.ReleaseComObject(factory); }
        return result;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    // Vtable order matters: IUnknown (implicit) -> IDXGIObject (4) -> IDXGIFactory (5) -> IDXGIFactory1.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? ppAdapter);
    }

    // IUnknown -> IDXGIObject (4) -> IDXGIAdapter (3) -> IDXGIAdapter1.
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
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
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
