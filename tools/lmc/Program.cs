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
    default:
        Console.Error.WriteLine("usage: lmc ls <data> | dump <data> <shader> <out>");
        return 1;
}
return 0;
