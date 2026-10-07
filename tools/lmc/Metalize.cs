using System.Diagnostics;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// The shader stage of the converter: writes every serialized file of the game's Data folder with the
// platform set to macOS, each D3D11 shader replaced by its Metal translation, and the two graphics settings
// the player reads to pick Metal.
// With a cache dir, each translated object is kept under a hash of the game's original object and of this
// tool's binaries, so a re-run (or a game update) only translates shaders that are new or changed.
static class Metalize
{
    public static int Run(string gameData, string outDir, string? cacheDir = null)
    {
        using var d = new DataDir(gameData);
        var problems = new List<string>();
        var cache = cacheDir == null ? null : new ObjectCache(cacheDir);
        byte[] Cached(AssetsFileInstance inst, AssetFileInfo info, Func<byte[]> make)
        {
            if (cache == null) return make();
            inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
            var key = cache.Key(inst.file.Reader.ReadBytes((int)info.ByteSize));
            if (cache.TryGet(key, out var hit)) return hit;
            var before = problems.Count;
            var bytes = make();
            if (problems.Count == before) cache.Put(key, bytes);
            return bytes;
        }
        var sw = Stopwatch.StartNew();
        int shaders = 0, computes = 0;
        var only = Environment.GetEnvironmentVariable("LMC_ONLY")?.Split('|'); // dev: translate just these names
        foreach (var (key, inst) in d.Files)
        {
            var repl = new Dictionary<long, byte[]>();
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var s = new ShaderRef(key, info.PathId, bf["m_ParsedForm"]["m_Name"].AsString, new());
                if (only != null && !only.Contains(s.Name)) continue;
                try { repl[info.PathId] = Cached(inst, info, () => NeedsGeometry(bf) ? NoPrograms(bf) : MetalShader.Convert(d, s, problems)); shaders++; }
                catch (Exception e) { problems.Add($"{key}:{info.PathId} {s.Name}: {e.Message}"); }
            }
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                if (only != null && !only.Contains(d.Am.GetBaseField(inst, info)["m_Name"].AsString)) continue;
                try { repl[info.PathId] = Cached(inst, info, () => MetalCompute.Convert(d, inst, info)); computes++; }
                catch (Exception e) { problems.Add($"{key}:{info.PathId} compute: {e.Message}"); }
            }
            if (key == "globalgamemanagers") PatchGraphicsSettings(d, inst, repl);
            var outPath = key == "unity_builtin_extra" ? Path.Combine(outDir, "Resources", key) : Path.Combine(outDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, SerializedWriter.WriteSerialized(inst, repl, SerializedWriter.StandaloneOSX));
            Console.WriteLine($"{key}: {repl.Count} objects replaced ({sw.Elapsed.TotalSeconds:F0}s)");
        }
        if (cache != null && only == null && problems.Count == 0) cache.Prune();  // keep just this game version's set
        Console.WriteLine($"{shaders} shaders and {computes} compute shaders done ({(cache == null ? "no cache" : $"{cache.Hits} reused, {cache.Misses} translated")}), {problems.Count} problems, {sw.Elapsed.TotalSeconds:F0}s");
        foreach (var p in problems) Console.WriteLine("  " + p);
        return problems.Count == 0 ? 0 : 1;
    }

    static bool NeedsGeometry(AssetTypeValueField bf) =>
        bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children)
            .Any(p => p["progGeometry"]["m_PlayerSubPrograms"]["Array"].Children.Any(t => t["Array"].Children.Count > 0));

    // Metal has no geometry stage. Unity's own Metal build keeps such a shader (only Hidden/VR/BlitFromTex2DToTexArraySlice
    // here) with every pass's program lists emptied, its names and shared parameters dropped and an empty blob.
    static byte[] NoPrograms(AssetTypeValueField bf)
    {
        foreach (var p in bf["m_ParsedForm"]["m_SubShaders"]["Array"].Children.SelectMany(ss => ss["m_Passes"]["Array"].Children))
        {
            MetalShader.SetArray(p["m_NameIndices"], Array.Empty<AssetTypeValueField>());
            foreach (var prog in new[] { "progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing" })
            {
                foreach (var list in new[] { "m_PlayerSubPrograms", "m_ParameterBlobIndices" })
                    foreach (var tier in p[prog][list]["Array"].Children) MetalShader.SetArray(tier, Array.Empty<AssetTypeValueField>());
                foreach (var c in p[prog]["m_CommonParameters"].Children) MetalShader.SetArray(c, Array.Empty<AssetTypeValueField>());
            }
        }
        MetalShader.WriteBlob(bf, new());
        bf["platforms"]["Array"].Children[0].AsUInt = MetalShader.PlatformMetal;
        return bf.WriteToByteArray();
    }

    // BuildSettings.m_GraphicsAPIs: Direct3D11 (2) -> Metal (16); the player picks its renderer from this list.
    // GraphicsSettings.m_ShaderDefinesPerShaderCompiler: the D3D11 entry becomes the Metal one. Values are the
    // ones Unity 2022.3 writes for this project's settings on each platform; anything else stops the run.
    static void PatchGraphicsSettings(DataDir d, AssetsFileInstance inst, Dictionary<long, byte[]> repl)
    {
        var bs = inst.file.GetAssetsOfType(AssetClassID.BuildSettings).Single();
        var b = d.Am.GetBaseField(inst, bs);
        var apis = b["m_GraphicsAPIs"]["Array"].Children;
        if (apis.Count != 1 || apis[0].AsInt != 2) throw new Exception($"m_GraphicsAPIs: expected [2], found [{string.Join(",", apis.Select(a => a.AsInt))}]");
        apis[0].AsInt = 16;
        repl[bs.PathId] = b.WriteToByteArray();

        var gs = inst.file.GetAssetsOfType(AssetClassID.GraphicsSettings).Single();
        var g = d.Am.GetBaseField(inst, gs);
        var defs = g["m_ShaderDefinesPerShaderCompiler"]["Array"].Children;
        if (defs.Count != 1 || defs[0]["shaderPlatform"].AsInt != MetalShader.PlatformD3D11) throw new Exception("m_ShaderDefinesPerShaderCompiler: expected one D3D11 entry");
        defs[0]["shaderPlatform"].AsInt = MetalShader.PlatformMetal;
        foreach (var tier in new[] { "defines_Tier1", "defines_Tier2", "defines_Tier3" })
        {
            var w = defs[0][tier]["Array"].Children;
            if (w.Count != 2 || w[0].AsUInt != 142984712 || w[1].AsUInt != 0) throw new Exception($"{tier}: unexpected D3D11 defines {string.Join(",", w.Select(x => x.AsUInt))}");
            w[0].AsUInt = 142984728;
            w[1].AsUInt = 8;
        }
        repl[gs.PathId] = g.WriteToByteArray();
    }
}

// Translated objects on disk, keyed by sha256(tool binaries, original object bytes).
sealed class ObjectCache
{
    readonly string dir;
    readonly byte[] version;
    readonly HashSet<string> used = new();
    public int Hits, Misses;

    public ObjectCache(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(dir);
        // any change to lmc, AssetsTools or the translator library gives every object a new key
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory).Where(f => f.EndsWith(".dll") || f.EndsWith(".dylib")).Order())
            h.AppendData(File.ReadAllBytes(f));
        version = h.GetHashAndReset();
    }

    public string Key(byte[] original) => Convert.ToHexStringLower(SHA256.HashData(version.Concat(original).ToArray()));

    public bool TryGet(string key, out byte[] bytes)
    {
        var path = Path.Combine(dir, key + ".bin");
        used.Add(key);
        if (File.Exists(path)) { bytes = File.ReadAllBytes(path); Hits++; return true; }
        bytes = Array.Empty<byte>();
        Misses++;
        return false;
    }

    public void Put(string key, byte[] bytes)
    {
        var tmp = Path.Combine(dir, key + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path.Combine(dir, key + ".bin"), true);
    }

    public void Prune()
    {
        foreach (var f in Directory.GetFiles(dir))
            if (!used.Contains(Path.GetFileNameWithoutExtension(f))) File.Delete(f);
    }
}
