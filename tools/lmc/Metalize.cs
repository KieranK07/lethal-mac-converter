using System.Diagnostics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// The shader stage of the converter: writes every serialized file of the game's Data folder with the
// platform set to macOS, each D3D11 shader replaced by its Metal translation, and the two graphics settings
// the player reads to pick Metal.
static class Metalize
{
    public static int Run(string gameData, string outDir)
    {
        using var d = new DataDir(gameData);
        var problems = new List<string>();
        var sw = Stopwatch.StartNew();
        int shaders = 0, computes = 0;
        foreach (var (key, inst) in d.Files)
        {
            var repl = new Dictionary<long, byte[]>();
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var bf = d.Am.GetBaseField(inst, info);
                var s = new ShaderRef(key, info.PathId, bf["m_ParsedForm"]["m_Name"].AsString, new());
                try { repl[info.PathId] = MetalShader.Convert(d, s, problems); shaders++; }
                catch (Exception e) { problems.Add($"{key}:{info.PathId} {s.Name}: {e.Message}"); }
            }
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.ComputeShader))
            {
                try { repl[info.PathId] = MetalCompute.Convert(d, inst, info); computes++; }
                catch (Exception e) { problems.Add($"{key}:{info.PathId} compute: {e.Message}"); }
            }
            if (key == "globalgamemanagers") PatchGraphicsSettings(d, inst, repl);
            var outPath = key == "unity_builtin_extra" ? Path.Combine(outDir, "Resources", key) : Path.Combine(outDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, SerializedWriter.WriteSerialized(inst, repl, SerializedWriter.StandaloneOSX));
            Console.WriteLine($"{key}: {repl.Count} objects replaced ({sw.Elapsed.TotalSeconds:F0}s)");
        }
        File.WriteAllLines(Path.Combine(outDir, "metalize-problems.txt"), problems);
        Console.WriteLine($"{shaders} shaders and {computes} compute shaders translated, {problems.Count} problems, {sw.Elapsed.TotalSeconds:F0}s");
        foreach (var p in problems.Take(20)) Console.WriteLine("  " + p);
        return problems.Count == 0 ? 0 : 1;
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
