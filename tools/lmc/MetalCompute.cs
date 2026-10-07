using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Rewrites a D3D11 ComputeShader object into a Metal one. Kernels, keyword tables and requirements stay; each
// unique kernel variant's DXBC is translated, its resource bind points become Metal slots, and the constant
// buffer table gets one Metal-laid-out entry per (kernel variant, cbuffer), as the D3D table has per-kernel
// subsets.
static class MetalCompute
{
    const int RendererD3D11 = 2, RendererMetal = 16;
    const uint HlslccFlags = 0x20080; // TRANSLATE_MATRICES | INOUT_SEMANTIC_NAMES

    record CbParam(string Name, int Type, uint Offset, uint Array, uint Rows, uint Cols);
    record CbEntry(string Name, int Size, List<CbParam> Params);

    static IEnumerable<AssetTypeValueField> A(AssetTypeValueField f) => f["Array"].Children;

    public static byte[] Convert(DataDir d, AssetsFileInstance inst, AssetFileInfo info)
    {
        var bf = d.Am.GetBaseField(inst, info);
        var name = bf["m_Name"].AsString;
        var variants = A(bf["variants"]).ToList();
        if (variants.Count != 1 || variants[0]["targetRenderer"].AsInt != RendererD3D11) throw new Exception($"{name}: expected one D3D11 variant");
        var v = variants[0];
        var d3dCbs = A(v["constantBuffers"]).Select(cb => new CbEntry(cb["name"].AsString, cb["byteSize"].AsInt,
            A(cb["params"]).Select(p => new CbParam(p["name"].AsString, p["type"].AsInt, p["offset"].AsUInt, p["arraySize"].AsUInt, p["rowCount"].AsUInt, p["colCount"].AsUInt)).ToList())).ToList();
        var metalCbs = new List<CbEntry>();
        var cbKey = new Dictionary<string, int>();
        var layouts = new CbLayouts(compute: true);
        foreach (var k in A(v["kernels"]))
            foreach (var uv in A(k["uniqueVariants"]))
            {
                var idx = A(uv["cbVariantIndices"]).Select(x => (int)x.AsUInt).ToList();
                foreach (var cb in idx.Select(i => d3dCbs[i])) layouts.Add(cb.Name, cb.Size, cb.Params.Select(ToParam));
            }

        foreach (var k in A(v["kernels"]))
            foreach (var uv in A(k["uniqueVariants"]))
            {
                var cbIdx = A(uv["cbVariantIndices"]).Select(x => (int)x.AsUInt).ToList();
                var cbs = A(uv["cbs"]).ToList();
                var kcbs = cbs.Select((c, i) => d3dCbs[cbIdx[i]]).ToList();
                var desc = Desc(uv, cbs, kcbs, layouts);
                var dxbc = uv["code"]["Array"].AsByteArray;
                var (ok, msl, refl) = Xlat.Translate(dxbc, desc, HlslccFlags);
                if (!ok) throw new Exception($"{name} {k["name"].AsString}: {refl.Split('\n').FirstOrDefault(l => l.StartsWith("error"))}");
                var lines = refl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
                Dump.Msl($"{name}.{k["name"].AsString}", msl);
                Rebind(uv, lines);

                // constant buffers: Metal layouts, in the kernel's cbs order
                var newIdx = new List<uint>();
                foreach (var c in cbs)
                {
                    var src = kcbs[cbs.IndexOf(c)];
                    var entry = MetalCb(lines, MetalName(c["name"].AsString), layouts.Get(src.Name, src.Size, src.Params.Select(ToParam)).vars);
                    var key = entry.Name + "|" + entry.Size + "|" + string.Join(";", entry.Params);
                    if (!cbKey.TryGetValue(key, out var idx)) { idx = metalCbs.Count; metalCbs.Add(entry); cbKey[key] = idx; }
                    newIdx.Add((uint)idx);
                }
                foreach (var (f, i) in A(uv["cbVariantIndices"]).Select((f, i) => (f, i))) f.AsUInt = newIdx[i];

                var tg = A(uv["threadGroupSize"]).Select(x => x.AsUInt).ToList();
                uv["code"]["Array"].AsByteArray = Code(msl, tg);
            }

        SetCbTable(v["constantBuffers"], metalCbs);
        v["targetRenderer"].AsInt = RendererMetal;
        return bf.WriteToByteArray();
    }

    // Header Unity 2022.3 writes for Metal kernels: magic, format 2, source offset 20, thread group size as
    // three uint16 plus a 1, then the source (entry point computeMain).
    static byte[] Code(string msl, List<uint> tg)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xdeadcafe); w.Write(2u); w.Write(20u);
        w.Write((ushort)tg[0]); w.Write((ushort)tg[1]); w.Write((ushort)tg[2]); w.Write((ushort)1);
        w.Write(Encoding.UTF8.GetBytes(msl));
        return ms.ToArray();
    }

    // ComputeShaderParam (type: Unity ShaderParamType 0 float, 1 int, 2 bool, 5 uint) as a cbuffer value
    static Param ToParam(CbParam p) => new(p.Name, p.Type, (int)p.Rows, (int)p.Cols, p.Rows > 1, (int)p.Array, (int)p.Offset);

    static string Desc(AssetTypeValueField uv, List<AssetTypeValueField> cbs, List<CbEntry> kcbs, CbLayouts layouts)
    {
        var sb = new StringBuilder();
        void Line(params object[] f) => sb.Append(string.Join('\t', f)).Append('\n');
        for (var i = 0; i < cbs.Count; i++)
        {
            var c = cbs[i];
            var reg = c["bindPoint"].AsInt;
            var (size, vars) = layouts.Get(c["name"].AsString, kcbs[i].Size, kcbs[i].Params.Select(ToParam));
            Line("cb", reg, c["name"].AsString, size);
            foreach (var p in vars) sb.Append(Params.VarRecord("v", reg, p)).Append('\n');
        }
        foreach (var t in A(uv["textures"])) Line("t", t["bindPoint"].AsInt, t["name"].AsString);
        foreach (var b in A(uv["inBuffers"])) Line("t", b["bindPoint"].AsInt, b["name"].AsString);
        foreach (var b in A(uv["outBuffers"])) Line("u", b["bindPoint"].AsInt, b["name"].AsString);
        var inline = A(uv["builtinSamplers"]).ToList();
        var used = new HashSet<string>();
        foreach (var s in inline) Line("s", s["bindPoint"].AsInt, Params.InlineSamplerName(s["sampler"].AsUInt, used));
        foreach (var g in A(uv["textures"]).Where(t => t["samplerBindPoint"].AsInt >= 0 && inline.All(s => s["bindPoint"].AsInt != t["samplerBindPoint"].AsInt)).GroupBy(t => t["samplerBindPoint"].AsInt))
            Line("s", g.Key, "sampler" + g.First()["name"].AsString);
        return sb.ToString();
    }

    static string MetalName(string cb) => cb == "$Globals" ? "Globals" : cb;

    // Resource bind points -> the Metal slots HLSLcc assigned; inline samplers become constexpr samplers.
    static void Rebind(AssetTypeValueField uv, List<string[]> refl)
    {
        var tex = refl.Where(l => l[0] == "tex").ToDictionary(l => l[1], l => (bind: int.Parse(l[2]), sampler: int.Parse(l[3])));
        var buf = refl.Where(l => l[0] == "buf").ToDictionary(l => l[1], l => int.Parse(l[2]));
        var cb = refl.Where(l => l[0] == "cbbind").ToDictionary(l => l[1], l => int.Parse(l[2]));
        int Slot(AssetTypeValueField r)
        {
            var n = r["name"].AsString;
            if (n == "$Globals") r["name"].AsString = n = MetalName(n); // HLSLcc (and Unity's Metal build) call it "Globals"
            if (tex.TryGetValue(n, out var t)) { r["samplerBindPoint"].AsInt = t.sampler; return t.bind; }
            if (buf.TryGetValue(n, out var b)) return b;
            if (cb.TryGetValue(n, out var c)) return c;
            throw new Exception($"resource {n} not in the Metal translation");
        }
        foreach (var list in new[] { "cbs", "textures", "inBuffers", "outBuffers" })
            foreach (var r in A(uv[list])) r["bindPoint"].AsInt = Slot(r);
        var samplers = uv["builtinSamplers"]["Array"];
        samplers.Children = new();
        samplers.AsArray = new AssetTypeArrayInfo(0);
    }

    // One cbuffer as HLSLcc declared it for this kernel (the shader-wide layout; compute rules: arrays and
    // matrices are float4 arrays). Padding members advance the offset but are not parameters.
    static CbEntry MetalCb(List<string[]> refl, string name, List<Param> layout)
    {
        var start = refl.FindIndex(l => l[0] == "cb" && l[1] == name);
        if (start < 0) throw new Exception($"cbuffer {name} not in the Metal translation");
        var ps = new List<CbParam>();
        int off = 0, maxAlign = 4;
        for (var i = start + 1; i < refl.Count && refl[i][0] == "const"; i++)
        {
            var l = refl[i];
            var src = layout.FirstOrDefault(p => p.Name == l[1]) ?? throw new Exception($"{name}.{l[1]}: not in the layout");
            var (size, align) = CbLayouts.Metal(src, compute: true);
            off = (off + align - 1) / align * align;
            maxAlign = Math.Max(maxAlign, align);
            if (!src.Name.StartsWith(CbLayouts.PadPrefix))
                ps.Add(new CbParam(src.Name, src.Type, (uint)off, (uint)src.Array, (uint)src.Rows, (uint)src.Cols));
            off += size;
        }
        return new CbEntry(name, (off + maxAlign - 1) / maxAlign * maxAlign, ps);
    }

    static void SetCbTable(AssetTypeValueField vector, List<CbEntry> cbs)
    {
        var arr = vector["Array"];
        var items = new List<AssetTypeValueField>();
        foreach (var cb in cbs)
        {
            var f = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            f["name"].AsString = cb.Name;
            f["byteSize"].AsInt = cb.Size;
            var parr = f["params"]["Array"];
            var plist = new List<AssetTypeValueField>();
            foreach (var p in cb.Params)
            {
                var pf = ValueBuilder.DefaultValueFieldFromArrayTemplate(parr);
                pf["name"].AsString = p.Name; pf["type"].AsInt = p.Type; pf["offset"].AsUInt = p.Offset;
                pf["arraySize"].AsUInt = p.Array; pf["rowCount"].AsUInt = p.Rows; pf["colCount"].AsUInt = p.Cols;
                plist.Add(pf);
            }
            parr.Children = plist; parr.AsArray = new AssetTypeArrayInfo(plist.Count);
            items.Add(f);
        }
        arr.Children = items;
        arr.AsArray = new AssetTypeArrayInfo(items.Count);
    }
}
