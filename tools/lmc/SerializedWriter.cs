using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Lmc;

// Rewrites a Unity serialized file (format 22) with some objects replaced: header and metadata are copied
// byte for byte except the target platform, the file size and each object's (offset, size); untouched
// objects are copied byte for byte in their original order with Unity's 8-byte alignment.
static class SerializedWriter
{
    public const uint StandaloneOSX = 2, StandaloneWindows64 = 19;

    // Rewrites a v22 serialized file: header + metadata copied byte for byte except target platform, file size and the
    // (offset, size) of replaced/shifted objects; object data copied byte for byte except the replaced objects.
    public static byte[] WriteSerialized(AssetsFileInstance inst, Dictionary<long, byte[]> repl, uint platform)
    {
        var src = File.ReadAllBytes(inst.path);
        var f = inst.file;
        if (f.Header.Version != 22) throw new Exception($"{inst.name}: serialized format {f.Header.Version}, writer only knows 22");
        if (src[16] != 0) throw new Exception($"{inst.name}: big-endian file");
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(src.AsSpan(24));
        var dataOff = BinaryPrimitives.ReadInt64BigEndian(src.AsSpan(32));
        if (fileSize != src.Length || dataOff != f.Header.DataOffset) throw new Exception($"{inst.name}: header mismatch");
        var verEnd = Array.IndexOf(src, (byte)0, 48);
        if (Encoding.ASCII.GetString(src, 48, verEnd - 48) != f.Metadata.UnityVersion) throw new Exception($"{inst.name}: version string not where expected");
        var platOff = verEnd + 1;
        if (BitConverter.ToUInt32(src, platOff) != f.Metadata.TargetPlatform) throw new Exception($"{inst.name}: platform field not where expected");

        var infos = f.Metadata.AssetInfos;
        var pat = new byte[20];
        BitConverter.TryWriteBytes(pat.AsSpan(0), infos[0].PathId);
        BitConverter.TryWriteBytes(pat.AsSpan(8), infos[0].ByteOffset);
        BitConverter.TryWriteBytes(pat.AsSpan(16), infos[0].ByteSize);
        var meta = src.AsSpan(0, (int)dataOff);
        var tbl = meta.IndexOf(pat);
        if (tbl < 0 || meta[(tbl + 1)..].IndexOf(pat) >= 0) throw new Exception($"{inst.name}: cannot locate object table uniquely");
        for (var k = 0; k < infos.Count; k++)
        {
            var e = tbl + 24 * k;
            if (BitConverter.ToInt64(src, e) != infos[k].PathId || BitConverter.ToInt64(src, e + 8) != infos[k].ByteOffset || BitConverter.ToUInt32(src, e + 16) != infos[k].ByteSize)
                throw new Exception($"{inst.name}: object table entry {k} does not match");
        }

        var order = Enumerable.Range(0, infos.Count).OrderBy(k => infos[k].ByteOffset).ToList();
        for (var i = 0; i + 1 < order.Count; i++)
        {
            var a = infos[order[i]]; var b = infos[order[i + 1]];
            if (b.ByteOffset != Align8(a.ByteOffset + a.ByteSize)) throw new Exception($"{inst.name}: objects not laid out with 8-byte alignment");
        }
        var last = infos[order[^1]];
        var tail = src.Length - (dataOff + last.ByteOffset + last.ByteSize);

        var outMs = new MemoryStream(src.Length + repl.Values.Sum(v => v.Length));
        var head = src.AsSpan(0, (int)dataOff).ToArray();
        BitConverter.TryWriteBytes(head.AsSpan(platOff), platform);
        var pos = infos[order[0]].ByteOffset;
        var newStart = new long[infos.Count]; var newSize = new uint[infos.Count];
        foreach (var k in order)
        {
            newStart[k] = pos;
            newSize[k] = repl.TryGetValue(infos[k].PathId, out var nb) ? (uint)nb.Length : infos[k].ByteSize;
            pos = Align8(pos + newSize[k]);
        }
        for (var k = 0; k < infos.Count; k++)
        {
            BitConverter.TryWriteBytes(head.AsSpan(tbl + 24 * k + 8), newStart[k]);
            BitConverter.TryWriteBytes(head.AsSpan(tbl + 24 * k + 16), newSize[k]);
        }
        outMs.Write(head);
        foreach (var k in order)
        {
            while (outMs.Length < dataOff + newStart[k]) outMs.WriteByte(0);
            if (repl.TryGetValue(infos[k].PathId, out var nb)) outMs.Write(nb);
            else outMs.Write(src, (int)(dataOff + infos[k].ByteOffset), (int)infos[k].ByteSize);
        }
        outMs.Write(src, src.Length - (int)tail, (int)tail);
        var res = outMs.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(res.AsSpan(24), res.Length);
        if (BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(4)) == src.Length) BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(4), (uint)res.Length);

        if (repl.Count == 0) // self-check: only the 4 platform bytes may differ
        {
            if (res.Length != src.Length) throw new Exception($"{inst.name}: unchanged rewrite changed size");
            for (var i = 0; i < res.Length; i++)
                if (res[i] != src[i] && (i < platOff || i >= platOff + 4)) throw new Exception($"{inst.name}: unchanged rewrite differs at {i}");
        }
        return res;
    }

    static long Align8(long x) => (x + 7) & ~7L;
}
