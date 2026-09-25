using System.Buffers.Binary;
using Awb.Core.Util;

namespace Awb.Core.Rpf;

public sealed record RpfBuildInfo(string Path, int Entries, long Size, int Dirs = 1, int Files = 0);

/// <summary>
/// Streams an RPF7-OPEN archive to disk. Every file's on-disk blob is produced and
/// written one at a time (data region first, header + TOC last), so packing a
/// multi-gigabyte pack never holds more than one file in memory.
/// </summary>
internal sealed class RpfStreamBuilder
{
    /// <summary>A node of the archive tree in TOC order.</summary>
    internal sealed class Node
    {
        public required string Name;
        public bool IsDir;
        public int First, Count;               // directory children span
        public Func<Payload>? Produce;          // file payload
        public int NameOffset;
    }

    internal sealed class Payload
    {
        public RpfEntryKind Kind;
        public byte[]? Blob;                    // in-memory blob
        public string? RawFile;                 // or: stream this file verbatim (Kind = Raw)
        public long Length;
        public uint A, B;
    }

    public static long Write(string outPath, IReadOnlyList<Node> nodes)
    {
        // name table: root uses offset 0 (the leading NUL)
        var names = new MemoryStream();
        names.WriteByte(0);
        for (int i = 1; i < nodes.Count; i++)
        {
            nodes[i].NameOffset = (int)names.Length;
            names.Write(Rpf7.EncodeName(nodes[i].Name));
            names.WriteByte(0);
        }
        int namesLen = (int)Rpf7.Align(names.Length, 16);
        names.SetLength(namesLen);

        int count = nodes.Count;
        long headerRegion = 16 + (long)count * 16 + namesLen;
        long dataStart = Rpf7.Align(headerRegion, Rpf7.Sector);

        var toc = new byte[count * 16];
        var tmp = outPath + ".tmp";
        long total;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                long cur = dataStart;
                for (int i = 0; i < count; i++)
                {
                    var n = nodes[i];
                    if (n.IsDir)
                    {
                        Rpf7.DirRecord(i == 0 ? 0u : (uint)n.NameOffset, (uint)n.First, (uint)n.Count)
                            .CopyTo(toc, i * 16);
                        continue;
                    }
                    var p = n.Produce!();
                    long len;
                    fs.Position = cur;
                    if (p.RawFile is not null)
                    {
                        using var src = File.OpenRead(p.RawFile);
                        src.CopyTo(fs, 1 << 20);
                        len = src.Length;
                    }
                    else
                    {
                        fs.Write(p.Blob!);
                        len = p.Blob!.Length;
                    }
                    Rpf7.FileRecord(n.NameOffset, cur, p.Kind, len, p.A, p.B).CopyTo(toc, i * 16);
                    cur = Rpf7.Align(cur + len, Rpf7.Sector);
                }
                total = Math.Max(cur, dataStart);
                fs.SetLength(total);

                fs.Position = 0;
                fs.Write(Rpf7.Header(count, namesLen));
                fs.Write(toc);
                fs.Write(names.GetBuffer(), 0, namesLen);
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        return total;
    }

    // ---- payload producers --------------------------------------------------

    public static Payload ResourceFromFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        var (sysf, gfxf) = Rpf7.ReadRsc7Flags(raw, Path.GetFileName(path));
        var blob = Rpf7.StampBigSize(Rpf7.ResourceBlob(raw, sysf, gfxf));
        return new Payload { Kind = RpfEntryKind.Resource, Blob = blob, A = sysf, B = gfxf };
    }

    public static Payload Binary(byte[] data)
    {
        var compressed = Rpf7.Deflate(data);
        if (compressed.Length > Rpf7.BigSize)           // too big for the u24 field -> store raw
            return new Payload { Kind = RpfEntryKind.Raw, Blob = data, A = (uint)data.Length };
        return new Payload { Kind = RpfEntryKind.Binary, Blob = compressed, A = (uint)data.Length };
    }

    public static Payload RawFileStream(string path)
    {
        long len = new FileInfo(path).Length;
        if (len > uint.MaxValue)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is larger than 4 GiB");
        return new Payload { Kind = RpfEntryKind.Raw, RawFile = path, A = (uint)len };
    }
}

/// <summary>
/// Builds a flat (single-directory) RPF7-OPEN archive: RSC7 resources (.ydr/.ytd,
/// header kept verbatim, body compressed exactly once) and binary files (DEFLATE,
/// real length in FileUncompressedSize).
/// </summary>
public sealed class RpfWriter
{
    private readonly List<(string Name, Func<RpfStreamBuilder.Payload> Produce)> _entries = [];

    /// <summary>Add an RSC7 resource file (flags are validated now, the blob is built at <see cref="Build"/>).</summary>
    public RpfWriter AddFile(string path)
    {
        Rpf7.ReadRsc7Flags(path);                        // fail early on a non-RSC7 file
        _entries.Add((Path.GetFileName(path), () => RpfStreamBuilder.ResourceFromFile(path)));
        return this;
    }

    public RpfWriter AddBinary(string name, byte[] data)
    {
        var payload = RpfStreamBuilder.Binary(data);
        _entries.Add((name, () => payload));
        return this;
    }

    public RpfWriter AddFolder(string folder, IEnumerable<string>? exts = null)
    {
        var allowed = new HashSet<string>(exts ?? Rpf7.ResourceExts, StringComparer.OrdinalIgnoreCase);
        foreach (var f in PathUtil.SortedFiles(folder))
            if (allowed.Contains(f.Extension.ToLowerInvariant()))
                AddFile(f.FullName);
        return this;
    }

    public RpfBuildInfo Build(string outPath)
    {
        var files = _entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var nodes = new List<RpfStreamBuilder.Node>
        {
            new() { Name = "", IsDir = true, First = 1, Count = files.Count },
        };
        foreach (var (name, produce) in files)
            nodes.Add(new RpfStreamBuilder.Node { Name = name, Produce = produce });
        long size = RpfStreamBuilder.Write(outPath, nodes);
        return new RpfBuildInfo(outPath, files.Count, size, 1, files.Count);
    }
}

public static class RpfPacker
{
    /// <summary>
    /// Pack an entire folder tree into a single nested RPF7-OPEN archive (the
    /// canonical dlc.rpf). Resources keep their RSC7 header with a once-compressed
    /// body; nested <c>.rpf</c> children are stored raw; everything else
    /// (xml/meta/gxt2/json) is DEFLATE-compressed. Directory children are laid out
    /// breadth-first so every directory's children are contiguous and sorted.
    /// </summary>
    public static RpfBuildInfo PackFolder(string src, string outPath)
    {
        var nodes = new List<RpfStreamBuilder.Node>();
        var paths = new List<string>();
        nodes.Add(new RpfStreamBuilder.Node { Name = "", IsDir = true });
        paths.Add(src);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].IsDir) continue;
            var dir = new DirectoryInfo(paths[i]);
            var kids = dir.EnumerateFileSystemInfos().OrderBy(k => k.Name, StringComparer.Ordinal).ToList();
            nodes[i].First = nodes.Count;
            nodes[i].Count = kids.Count;
            foreach (var k in kids)
            {
                bool isDir = k is DirectoryInfo;
                var node = new RpfStreamBuilder.Node { Name = k.Name, IsDir = isDir };
                if (!isDir)
                {
                    var full = k.FullName;
                    var ext = Path.GetExtension(k.Name).ToLowerInvariant();
                    if (Rpf7.IsResourceExt(ext))
                    {
                        Rpf7.ReadRsc7Flags(full);
                        node.Produce = () => RpfStreamBuilder.ResourceFromFile(full);
                    }
                    else if (ext == ".rpf")
                        node.Produce = () => RpfStreamBuilder.RawFileStream(full);
                    else
                        node.Produce = () => RpfStreamBuilder.Binary(File.ReadAllBytes(full));
                }
                nodes.Add(node);
                paths.Add(k.FullName);
            }
        }
        long size = RpfStreamBuilder.Write(outPath, nodes);
        int dirs = nodes.Count(n => n.IsDir);
        return new RpfBuildInfo(outPath, nodes.Count - dirs, size, dirs, nodes.Count - dirs);
    }
}

internal static class BinaryExt
{
    public static uint U32(this byte[] b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off));
}
