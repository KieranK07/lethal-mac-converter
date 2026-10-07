using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lmc;

// lmc: lethal-mac-converter's data tool. Dev commands while the shader translator is being built:
//   ls   <data dir>                         list Shader objects (file, pathID, name, platforms)
//   dump <data dir> <shader name> <out dir> write every blob entry of the shader's first platform
var cmd = args.Length > 0 ? args[0] : "";
switch (cmd)
{
    case "ls":
    {
        using var d = new DataDir(args[1]);
        foreach (var s in d.Shaders())
            Console.WriteLine($"{s.File}\t{s.PathId}\t{s.Name}\tplatforms={string.Join(",", s.Platforms)}");
        break;
    }
    case "dump":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        ShaderDump.Dump(d, s, args[3]);
        break;
    }
    case "xlat":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        XlatCmd.Run(d, s, args[3], Convert.ToUInt32(args.Length > 4 ? args[4] : "20080", 16));
        break;
    }
    case "fields":
    {
        using var d = new DataDir(args[1]);
        var s = d.Shaders().First(x => x.Name == args[2]);
        void P(AssetTypeValueField f, string ind)
        {
            foreach (var c in f.Children)
            {
                if (c.FieldName == "Array") { if (c.Children.Count > 0) P(c.Children[0], ind + "  "); continue; }
                Console.WriteLine($"{ind}{c.FieldName} : {c.TypeName}");
                P(c, ind + "  ");
            }
        }
        if (args.Length > 3)
        {
            var inst = d.Files[s.File];
            void T(AssetTypeTemplateField t, string ind) { foreach (var c in t.Children) { Console.WriteLine($"{ind}{c.Name} : {c.Type}"); T(c, ind + "  "); } }
            T(d.Am.GetTemplateBaseField(inst, inst.file.GetAssetInfo(s.PathId)), "");
        }
        else P(d.Base(s.File, s.PathId), "");
        break;
    }
    default:
        Console.Error.WriteLine("usage: lmc ls <data> | dump <data> <shader> <out>");
        return 1;
}
return 0;
