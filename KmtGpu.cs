using System.Runtime.InteropServices;

namespace NotchHUD;

/// <summary>
/// GPU temperature straight from the Windows graphics kernel (the value Task
/// Manager shows). Works with any vendor's driver, used when the hardware
/// library exposes no temperature sensor for the card.
/// </summary>
static class KmtGpu
{
    const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [StructLayout(LayoutKind.Sequential)]
    struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int Precise; }

    [StructLayout(LayoutKind.Sequential)]
    struct EnumAdapters2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct QueryAdapterInfo { public uint hAdapter; public int Type; public IntPtr pData; public uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature; // temperature in tenths of a degree C
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SegmentSize { public ulong DedicatedVideo, DedicatedSystem, Shared; }

    [StructLayout(LayoutKind.Sequential)]
    struct CloseAdapter { public uint hAdapter; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter c);

    static uint _adapter;
    static bool _initialized;

    public static float Read()
    {
        try
        {
            if (!_initialized) { _initialized = true; Init(); }
            if (_adapter == 0) return float.NaN;
            return Query(_adapter, KMTQAITYPE_ADAPTERPERFDATA, out PerfData p) && p.Temperature > 0
                ? p.Temperature / 10f
                : float.NaN;
        }
        catch { return float.NaN; }
    }

    static void Init()
    {
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;

        int size = Marshal.SizeOf<AdapterInfo>();
        e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return;

            // Pick the adapter with the most dedicated video memory that reports a temperature.
            ulong bestMem = 0;
            var handles = new List<uint>();
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(e.pAdapters + i * size);
                handles.Add(info.hAdapter);
                bool hasTemp = Query(info.hAdapter, KMTQAITYPE_ADAPTERPERFDATA, out PerfData p) && p.Temperature > 0;
                Query(info.hAdapter, KMTQAITYPE_GETSEGMENTSIZE, out SegmentSize seg);
                Log.Write($"kmt adapter {i}: vram={seg.DedicatedVideo >> 20}MB temp={p.Temperature / 10f:0.#} ok={hasTemp}");
                if (hasTemp && (_adapter == 0 || seg.DedicatedVideo > bestMem))
                {
                    bestMem = seg.DedicatedVideo;
                    _adapter = info.hAdapter;
                }
            }
            foreach (uint h in handles)
                if (h != _adapter) { var c = new CloseAdapter { hAdapter = h }; D3DKMTCloseAdapter(ref c); }
        }
        finally
        {
            Marshal.FreeHGlobal(e.pAdapters);
        }
    }

    static bool Query<T>(uint adapter, int type, out T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(mem, i, 0);
            var q = new QueryAdapterInfo { hAdapter = adapter, Type = type, pData = mem, Size = (uint)size };
            bool ok = D3DKMTQueryAdapterInfo(ref q) == 0;
            value = Marshal.PtrToStructure<T>(mem);
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }
}
