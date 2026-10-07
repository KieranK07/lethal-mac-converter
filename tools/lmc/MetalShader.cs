using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;

namespace Lmc;

// Rewrites a D3D11 Shader object into a Metal one: every player subprogram is translated with HLSLcc and
// re-encoded in Unity 2022.3's Metal blob format. Structure, keywords and requirements are untouched; only
// the platform, the blob and each subprogram's program type, blob index and parameter index change.
static class MetalShader
{
    public const int PlatformD3D11 = 4, PlatformMetal = 14;
    const uint HlslccFlags = 0x20080; // HLSLCC_FLAG_TRANSLATE_MATRICES | HLSLCC_FLAG_INOUT_SEMANTIC_NAMES
    static readonly string[] Progs = { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" };
    const int MaxSegment = 8 << 20; // Unity splits a platform blob into LZ4 segments of at most 8 MiB

    public static byte[] Convert(DataDir d, ShaderRef s, List<string> problems)
    {
        var bf = d.Base(s.File, s.PathId);
        var platforms = bf["platforms"]["Array"].Children;
        if (platforms.Count != 1 || platforms[0].AsInt != PlatformD3D11) throw new Exception($"{s.Name}: expected only D3D11, has {string.Join(",", platforms.Select(p => p.AsInt))}");
        var blob = new ShaderBlob(bf, 0);
        var entries = new List<byte[]>();
        var dedup = new Dictionary<string, int>();
        int Add(byte[] e)
        {
            var h = System.Convert.ToHexString(SHA1.HashData(e));
            if (!dedup.TryGetValue(h, out var i)) { i = entries.Count; entries.Add(e); dedup[h] = i; }
            return i;
        }

        // Pass 1: collect the unique (code, parameters) pairs per program; pass 2 translates them in parallel
        // (HLSLcc keeps no global state); pass 3 writes the results back in a fixed order.
        // A tessellated pass has no hull or domain programs on Metal: subprogram i of the vertex, hull and domain
        // lists (same keywords) becomes one Metal vertex program, docs/METAL-TESSELLATION.md.
        var jobs = new List<(string tag, SubProgram[] subs, Params[] pars)>();
        var layouts = new CbLayouts(compute: false);
        var sites = new List<(AssetTypeValueField sp, AssetTypeValueField par, int job)>();
        var commons = new List<AssetTypeValueField>();
        var tessLists = new List<AssetTypeValueField>();
        var passes = bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children).ToList();
        foreach (var p in passes)
        {
            var names = p["m_NameIndices"]["Array"].Children.ToDictionary(x => x["second"].AsInt, x => x["first"].AsString);
            var tess = HasPrograms(p["progHull"]) || HasPrograms(p["progDomain"]);
            foreach (var group in tess ? new[] { new[] { "progVertex", "progHull", "progDomain" } }.Concat(Progs.Except(TessProgs).Select(x => new[] { x })) : Progs.Select(x => new[] { x }))
            {
                var pgs = group.Select(x => p[x]).ToArray();
                var common = pgs.Select(pg => Params.FromCommon(pg["m_CommonParameters"], names)).ToArray();
                var outer = pgs.Select(pg => pg["m_PlayerSubPrograms"]["Array"].Children).ToArray();
                var parIdx = pgs.Select(pg => pg["m_ParameterBlobIndices"]["Array"].Children).ToArray();
                var seen = new Dictionary<string, int>();
                for (var o = 0; o < outer[0].Count; o++)
                {
                    var inner = outer.Select(x => x[o]["Array"].Children).ToArray();
                    if (inner.Any(x => x.Count != inner[0].Count)) throw new Exception($"{s.Name}: tier {o} has {string.Join("/", inner.Select(x => x.Count))} vertex/hull/domain programs");
                    for (var i = 0; i < inner[0].Count; i++)
                    {
                        var sps = inner.Select(x => x[i]).ToArray();
                        if (sps.Select(Keywords).Distinct().Count() != 1) throw new Exception($"{s.Name}: vertex/hull/domain program {i} differ in keywords");
                        var keys = sps.Select((sp, k) => (blob: sp["m_BlobIndex"].AsUInt, par: parIdx[k][o]["Array"][i].AsUInt)).ToArray();
                        var key = string.Join(";", keys);
                        if (!seen.TryGetValue(key, out var job))
                        {
                            var subs = keys.Select(k => SubProgram.Read(blob.Entry((int)k.blob))).ToArray();
                            var pars = keys.Select((k, n) => Params.FromBlob(blob.Entry((int)k.par)).Merge(common[n])).ToArray();
                            seen[key] = job = jobs.Count;
                            foreach (var par in pars) par.AddTo(layouts);
                            jobs.Add(($"{s.Name}.{string.Join("+", group)}.{string.Join(".", keys.Select(k => k.blob))}", subs, pars));
                        }
                        sites.Add((sps[0], parIdx[0][o]["Array"][i], job));
                    }
                }
                foreach (var pg in pgs) commons.Add(pg["m_CommonParameters"]);
                if (group.Length > 1)
                    foreach (var pg in pgs.Skip(1)) tessLists.AddRange(new[] { pg["m_PlayerSubPrograms"], pg["m_ParameterBlobIndices"] });
            }
        }

        layouts.Freeze(); // decide every layout before the parallel part reads them
        var results = new MetalProgram[jobs.Count];
        Parallel.For(0, jobs.Count, j =>
        {
            var (tag, subs, pars) = jobs[j];
            var (ok, msl, refl) = subs.Length == 1
                ? Xlat.Translate(Xlat.Dxbc(subs[0].Code), pars[0].ToDesc(layouts), HlslccFlags)
                // each stage declares the cbuffers it uses first, for all three: give it every member the three read
                : Xlat.TranslateTess(subs.Select(x => Xlat.Dxbc(x.Code)).ToArray(), pars.Select(x => x.WithCbsOf(pars).ToDesc(layouts)).ToArray(), HlslccFlags);
            if (!ok) throw new Exception($"{tag}: {refl.Split('\n').FirstOrDefault(l => l.StartsWith("error"))}");
            Dump.Msl(tag, msl);
            results[j] = MetalProgram.From(subs[0], msl, refl, tess: subs.Length > 1);
        });

        var placed = jobs.Select((_, j) => (sub: Add(results[j].SubEntry), par: Add(results[j].ParamEntry))).ToList();
        foreach (var (sp, par, job) in sites)
        {
            sp["m_GpuProgramType"].AsSByte = (sbyte)results[job].Type;
            sp["m_BlobIndex"].AsUInt = (uint)placed[job].sub;
            par.AsUInt = (uint)placed[job].par;
        }
        // Metal parameter entries are complete per subprogram, so nothing is shared any more.
        foreach (var c in commons)
            foreach (var list in c.Children) SetArray(list, Array.Empty<AssetTypeValueField>());
        if (tessLists.Count > 0)
        {
            // Unity's Metal build: hull and domain lists have no tiers at all; stageCounts counts the stage types left
            foreach (var list in tessLists) SetArray(list, Array.Empty<AssetTypeValueField>());
            bf["stageCounts"]["Array"][0].AsUInt = (uint)Progs.Count(x => passes.Any(p => HasPrograms(p[x])));
        }

        WriteBlob(bf, entries);
        platforms[0].AsUInt = PlatformMetal;
        return bf.WriteToByteArray();
    }

    static readonly string[] TessProgs = { "progVertex", "progHull", "progDomain" };
    static bool HasPrograms(AssetTypeValueField prog) => prog["m_PlayerSubPrograms"]["Array"].Children.Any(t => t["Array"].Children.Count > 0);
    static string Keywords(AssetTypeValueField sp) => string.Join(",", sp["m_KeywordIndices"]["Array"].Children.Select(k => k.AsUShort));

    // Segment 0 holds the entry table; entries follow in order, a new segment starting when 8 MiB would be crossed.
    internal static void WriteBlob(AssetTypeValueField bf, List<byte[]> entries)
    {
        var segments = new List<MemoryStream> { new() };
        var table = new List<(int off, int len, int seg)>();
        var tableSize = 4 + 12 * entries.Count;
        long pos = tableSize;
        foreach (var e in entries)
        {
            if (pos + e.Length > MaxSegment && pos > (segments.Count == 1 ? tableSize : 0)) { segments.Add(new MemoryStream()); pos = 0; }
            table.Add(((int)pos, e.Length, segments.Count - 1));
            pos += e.Length;
            segments[^1].Write(e);
        }
        var seg0 = new MemoryStream();
        var w = new BinaryWriter(seg0);
        w.Write(entries.Count);
        foreach (var (off, len, seg) in table) { w.Write(off); w.Write(len); w.Write(seg); }
        segments[0].Position = 0;
        segments[0].CopyTo(seg0);
        segments[0] = seg0;

        var compressed = new MemoryStream();
        var offs = new List<uint>(); var clens = new List<uint>(); var dlens = new List<uint>();
        foreach (var seg in segments.Select(m => m.ToArray()))
        {
            var buf = new byte[LZ4Codec.MaximumOutputSize(seg.Length)];
            var n = LZ4Codec.Encode(seg, 0, seg.Length, buf, 0, buf.Length, LZ4Level.L12_MAX);
            offs.Add((uint)compressed.Length); clens.Add((uint)n); dlens.Add((uint)seg.Length);
            compressed.Write(buf, 0, n);
        }
        SetUInts(bf["offsets"]["Array"][0], offs);
        SetUInts(bf["compressedLengths"]["Array"][0], clens);
        SetUInts(bf["decompressedLengths"]["Array"][0], dlens);
        bf["compressedBlob"]["Array"].AsByteArray = compressed.ToArray();
    }

    static void SetUInts(AssetTypeValueField vector, List<uint> values)
    {
        var arr = vector["Array"];
        SetArray(vector, values.Select(v =>
        {
            var f = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            f.AsUInt = v;
            return f;
        }).ToList());
    }

    internal static void SetArray(AssetTypeValueField vector, IList<AssetTypeValueField> items)
    {
        var arr = vector["Array"];
        arr.Children = items.ToList();
        arr.AsArray = new AssetTypeArrayInfo(items.Count);
    }
}

// One translated subprogram in Unity's Metal encoding.
sealed class MetalProgram
{
    public int Type; public byte[] SubEntry = Array.Empty<byte>(), ParamEntry = Array.Empty<byte>();
    const int BlobVersion = 202012090;

    // tess: msl/refl are a whole tessellated program (vertex + hull + domain stages), d3d its vertex stage.
    public static MetalProgram From(SubProgram d3d, string msl, string refl, bool tess = false)
    {
        var lines = refl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var type = d3d.Type switch
        {
            15 or 16 => 23, // DX11 vertex SM4/SM5 -> Metal vertex
            17 or 18 => 24, // DX11 pixel -> Metal fragment
            _ => throw new Exception($"no Metal equivalent for D3D program type {d3d.Type}"),
        };
        if (tess && type != 23) throw new Exception($"tessellated program from D3D program type {d3d.Type}");
        return new MetalProgram { Type = type, SubEntry = Sub(d3d, type, msl, lines, tess), ParamEntry = MetalParams.Build(lines) };
    }

    // 48-byte header, entry point name, then the source. Header fields as Unity 2022.3 writes them:
    // magic, name offset, format 6, source offset, 5 tessellation values (else 0: MTLTessellationPartitionMode,
    // MTLWinding, max factor, patches per threadgroup, the patch kernel's buffer count), flags (37, +2 when the
    // vertex stage writes the render target array index), colour write mask (4 bits per output; ~0 except
    // unwritten components), 0.
    static byte[] Code(string msl, List<string[]> refl, int type)
    {
        const string entry = "xlatMtlMain";
        uint flags = 37, mask = 0xffffffff;
        var tess = new uint[5];
        foreach (var l in refl)
        {
            if (l[0] == "tess") for (var i = 0; i < 4; i++) tess[i] = uint.Parse(l[1 + i]);
            if (l[0] == "tesskernel") tess[4] = uint.Parse(l[1]);
            if (l[0] == "builtin" && int.Parse(l[1]) == 4 /* NAME_RENDER_TARGET_ARRAY_INDEX */ && type == 23) flags |= 2;
            if (l[0] == "fout")
            {
                int comps = int.Parse(l[1]), idx = int.Parse(l[2]);
                for (var c = comps; c < 4; c++) mask &= ~(1u << (4 * idx + c));
            }
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var src = 48 + entry.Length + 1;
        foreach (var v in new uint[] { 0xf00dcafe, 48, 6, (uint)src }.Concat(tess).Concat(new[] { flags, mask, 0u })) w.Write(v);
        w.Write(Encoding.ASCII.GetBytes(entry)); w.Write((byte)0);
        w.Write(Encoding.UTF8.GetBytes(msl));
        return ms.ToArray();
    }

    // D3D vertex channel targets name a semantic; Metal targets are attribute slots (13 + attribute index).
    static readonly Dictionary<int, string> D3DTargetSemantic = new()
    {
        [0] = "POSITION0", [1] = "NORMAL0", [2] = "TANGENT0", [3] = "COLOR0",
        [5] = "TEXCOORD0", [6] = "TEXCOORD1", [7] = "TEXCOORD2", [8] = "TEXCOORD3",
        [9] = "TEXCOORD4", [10] = "TEXCOORD5", [11] = "TEXCOORD6", [12] = "TEXCOORD7",
    };
    const int MetalAttrib0 = 13;

    // Unity's vertex channel for a semantic: 0 position, 1 normal, 2 tangent, 3 colour, 4 + n texcoord n.
    static int? Channel(string semantic) => semantic switch
    {
        "POSITION0" => 0, "NORMAL0" => 1, "TANGENT0" => 2, "COLOR0" => 3,
        _ when semantic.StartsWith("TEXCOORD") && int.TryParse(semantic[8..], out var n) && n < 8 => 4 + n,
        _ => null,
    };

    static byte[] Sub(SubProgram d3d, int type, string msl, List<string[]> refl, bool tess)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Align() { while (ms.Length % 4 != 0) w.Write((byte)0); }
        w.Write(BlobVersion); w.Write(type);
        for (var i = 0; i < 4; i++) w.Write(0); // Unity reports no stats for Metal
        w.Write(d3d.Keywords.Count);
        foreach (var k in d3d.Keywords) { var b = Encoding.UTF8.GetBytes(k); w.Write(b.Length); w.Write(b); Align(); }
        var code = Code(msl, refl, type);
        w.Write(code.Length); w.Write(code); Align();

        var bySemantic = d3d.Channels.ToDictionary(c => D3DTargetSemantic.TryGetValue(c.Target, out var sem) ? sem : throw new Exception($"D3D channel target {c.Target}"), c => c.Source);
        var channels = new List<(int, int)>();
        var sourceMap = d3d.SourceMap;
        if (!tess)
            foreach (var l in refl.Where(l => l[0] == "input"))
                channels.Add((bySemantic.TryGetValue(l[1], out var src) ? src : throw new Exception($"vertex input {l[1]} has no D3D channel"), MetalAttrib0 + int.Parse(l[2])));
        else
            // Unity lists every stage_in attribute of the combined program whose semantic is a vertex channel, the hull's
            // control points and patch constants included, by attribute; INTERNALTESSPOS, CUSTOM_INSTANCE_ID etc. are not
            foreach (var l in refl.Where(l => l[0] == "input").DistinctBy(l => int.Parse(l[2])).OrderBy(l => int.Parse(l[2])))
                if ((bySemantic.TryGetValue(l[1], out var src) ? src : Channel(l[1])) is int ch)
                {
                    channels.Add((ch, MetalAttrib0 + int.Parse(l[2])));
                    sourceMap |= 1 << ch;
                }
        w.Write(sourceMap);
        w.Write(channels.Count);
        foreach (var (src, dst) in channels) { w.Write(src); w.Write(dst); }
        return ms.ToArray();
    }
}

// LMC_DUMP_MSL=<dir>: also write every translated program, so it can be compile-checked (tools/xlat/mtlcheck).
static class Dump
{
    static readonly string? Dir = Environment.GetEnvironmentVariable("LMC_DUMP_MSL");
    public static void Msl(string tag, string msl)
    {
        if (Dir == null) return;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_')) + ".metal"), msl);
    }
}
