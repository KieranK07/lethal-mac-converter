using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

record ShaderRef(string File, long PathId, string Name, List<int> Platforms);

// All serialized files of a player Data folder, loaded with AssetsTools.NET and Unity's class database
// (player builds carry no type trees).
sealed class DataDir : IDisposable
{
    public readonly AssetsManager Am = new();
    public readonly Dictionary<string, AssetsFileInstance> Files = new();

    public DataDir(string dir)
    {
        Am.LoadClassPackage(Tpk());
        foreach (var path in Directory.EnumerateFiles(dir).Concat(Directory.EnumerateFiles(Path.Combine(dir, "Resources")).Where(p => Path.GetFileName(p) == "unity_builtin_extra")))
        {
            if (!IsSerialized(path)) continue;
            var inst = Am.LoadAssetsFile(path, false);
            Am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            Files[Path.GetFileName(path)] = inst;
        }
    }

    // Unity serialized files have no extension (levelN, globalgamemanagers) or end in .assets
    static bool IsSerialized(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".assets") || name.StartsWith("level") || name is "globalgamemanagers" or "unity_builtin_extra") return !name.Contains('.') || name.EndsWith(".assets");
        return false;
    }

    static string Tpk() => Path.Combine(Environment.GetEnvironmentVariable("LMC_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Caches/lethal-mac-converter"), "classdata.tpk");

    public AssetTypeValueField Base(string file, long pid)
    {
        var inst = Files[file];
        return Am.GetBaseField(inst, inst.file.GetAssetInfo(pid));
    }

    public IEnumerable<ShaderRef> Shaders()
    {
        foreach (var (key, inst) in Files)
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var bf = Am.GetBaseField(inst, info);
                yield return new ShaderRef(key, info.PathId, bf["m_ParsedForm"]["m_Name"].AsString, bf["platforms"]["Array"].Children.Select(p => p.AsInt).ToList());
            }
    }

    public void Dispose() => Am.UnloadAll();
}
