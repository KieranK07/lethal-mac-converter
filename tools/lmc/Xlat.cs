using System.Runtime.InteropServices;

namespace Lmc;

// P/Invoke into tools/xlat (HLSLcc + our RDEF rebuild), built as libxlat.dylib.
static class Xlat
{
    [DllImport("xlat")] static extern int lmc_xlat_unity(byte[] dxbc, string desc, uint flags, out IntPtr msl, out IntPtr reflection);
    [DllImport("xlat")] static extern void lmc_xlat_free(IntPtr p);

    public static (bool ok, string msl, string refl) Translate(byte[] dxbc, string desc, uint flags)
    {
        var ok = lmc_xlat_unity(dxbc, desc, flags, out var m, out var r) != 0;
        var res = (ok, Marshal.PtrToStringUTF8(m) ?? "", Marshal.PtrToStringUTF8(r) ?? "");
        lmc_xlat_free(m); lmc_xlat_free(r);
        return res;
    }

    // Unity's D3D11 program data: a small header, then the DXBC container.
    public static byte[] Dxbc(byte[] programData)
    {
        var at = programData.AsSpan().IndexOf("DXBC"u8);
        if (at < 0) throw new Exception("no DXBC in program data");
        return programData[at..];
    }
}
