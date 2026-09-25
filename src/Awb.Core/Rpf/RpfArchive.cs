using System.Buffers.Binary;

namespace Awb.Core.Rpf;

/// <summary>One decoded TOC entry.</summary>
public sealed class RpfEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    // directory
    public int FirstChild { get; init; }
    public int ChildCount { get; init; }
    // file
    public int NameOffset { get; init; }
    /// <summary>Absolute offset of the file data within the underlying stream.</summary>
    public long Offset { get; init; }
    /// <summary>Raw u24 size field of the TOC (0 = stored uncompressed, 0xFFFFFF = big resource).</summary>
    public long TocSize { get; init; }
    /// <summary>Real on-disk length (for a big resource: taken from its archived header).</summary>
    public long Size { get; init; }
    public bool IsResource { get; init; }
    public uint X8 { get; init; }
    public uint XC { get; init; }
    /// <summary>Absolute position of this entry's 16-byte TOC record.</summary>
    public long TocPos { get; init; }
    public byte[] Record { get; init; } = [];

    /// <summary>Stored uncompressed (a nested archive, or an oversized/uncompressed binary).</summary>
    public bool StoredRaw => !IsResource && TocSize == 0;
}

/// <summary>A path inside an archive tree.</summary>
public sealed record RpfTreeItem(string Path, RpfEntry Entry)
{
    public bool IsDir => Entry.IsDir;
}

/// <summary>
/// Reader for RPF7-OPEN archives (GTA V Legacy / OpenIV). Works directly on a
/// stream: only the header and TOC are read up front, file contents on demand, and
/// a nested archive stored raw is opened in place as a window of the parent stream.
/// </summary>
public sealed class RpfArchive : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _names;

    public string Name { get; }
    /// <summary>Absolute offset of this archive's header within the stream.</summary>
    public long BaseOffset { get; }
    public long Length { get; }
    public IReadOnlyList<RpfEntry> Entries { get; }

    private RpfArchive(Stream stream, bool owns, long baseOffset, long length, string name)
    {
        _stream = stream;
        _ownsStream = owns;
        BaseOffset = baseOffset;
        Length = length;
        Name = name;

        var hdr = ReadAt(baseOffset, 16);
        if (hdr.Length < 16)
            throw new RpfFormatException($"{name}: not an RPF7 archive (file too short)");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
        int namesLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
        uint enc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
        if (magic != Rpf7.Magic)
            throw new RpfFormatException($"not an RPF7 archive (magic=0x{magic:x})");
        if (enc != Rpf7.EncOpen)
            throw new RpfFormatException(
                $"{name}: RPF encryption 0x{enc:x} is not OPEN — only unencrypted " +
                "(GTA V Legacy / OpenIV) archives are supported.");
        if (count < 0 || count > 10_000_000 || namesLen < 0)
            throw new RpfFormatException($"{name}: corrupt RPF header");

        long tocPos = baseOffset + 16;
        var toc = ReadAt(tocPos, count * 16);
        _names = ReadAt(tocPos + count * 16L, namesLen);
        if (toc.Length < count * 16)
            throw new RpfFormatException($"{name}: truncated RPF table of contents");

        var entries = new List<RpfEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int p = i * 16;
            uint x0 = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(p));
            uint x4 = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(p + 4));
            uint x8 = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(p + 8));
            uint xC = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(p + 12));
            int noff = (int)(x0 & 0xFFFF);
            string nm = i == 0 ? "" : Rpf7.DecodeName(_names, noff);
            if (x4 == Rpf7.DirMarker)
            {
                entries.Add(new RpfEntry
                {
                    Index = i, Name = nm, IsDir = true, NameOffset = noff,
                    FirstChild = (int)x8, ChildCount = (int)xC,
                    TocPos = tocPos + p, Record = toc.AsSpan(p, 16).ToArray(),
                });
                continue;
            }
            long tocSize = (x0 >> 16) | ((long)(x4 & 0xFF) << 16);
            bool isRes = (x4 & 0x80000000) != 0;
            long off = baseOffset + ((x4 >> 8) & 0x7FFFFF) * (long)Rpf7.Sector;
            long size = tocSize;
            if (isRes && tocSize == Rpf7.BigSize)
            {
                var h = ReadAt(off, 16);
                if (h.Length == 16)
                    size = h[7] | ((long)h[14] << 8) | ((long)h[5] << 16) | ((long)h[2] << 24);
            }
            entries.Add(new RpfEntry
            {
                Index = i, Name = nm, NameOffset = noff, Offset = off, TocSize = tocSize,
                Size = size, IsResource = isRes, X8 = x8, XC = xC,
                TocPos = tocPos + p, Record = toc.AsSpan(p, 16).ToArray(),
            });
        }
        Entries = entries;
    }

    public static RpfArchive Open(string path, bool writable = false)
    {
        var fs = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
                                writable ? FileShare.Read : FileShare.ReadWrite, 1 << 16);
        try
        {
            return new RpfArchive(fs, true, 0, fs.Length, Path.GetFileName(path));
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static RpfArchive Open(byte[] data, string name) =>
        new(new MemoryStream(data, writable: false), true, 0, data.Length, name);

    /// <summary>Open a nested archive entry: in place when stored raw, otherwise inflated to memory.</summary>
    public RpfArchive OpenNested(RpfEntry e)
    {
        if (e.StoredRaw)
            return new RpfArchive(_stream, false, e.Offset, e.X8, e.Name);
        return Open(ReadContent(e), e.Name);
    }

    public byte[] ReadAt(long offset, int count)
    {
        if (count <= 0) return [];
        var buf = new byte[count];
        lock (_stream)
        {
            _stream.Position = offset;
            int n = _stream.ReadAtLeast(buf, count, throwOnEndOfStream: false);
            if (n < count) Array.Resize(ref buf, n);
        }
        return buf;
    }

    /// <summary>
    /// Decompressed content of a file entry. A resource comes back as a loose RSC7
    /// file (header + page image); a big resource's header is rebuilt from the TOC,
    /// since its archived copy carries the scattered length.
    /// </summary>
    public byte[] ReadContent(RpfEntry e)
    {
        if (e.IsDir) throw new InvalidOperationException($"{e.Name} is a directory");
        if (e.IsResource)
        {
            var hdr = e.TocSize == Rpf7.BigSize ? Rpf7.Rsc7Header(e.X8, e.XC) : ReadAt(e.Offset, 16);
            var body = ReadAt(e.Offset + 16, (int)Math.Max(0, e.Size - 16));
            var inflated = Rpf7.Inflate(body, 0, body.Length);
            var outBuf = new byte[hdr.Length + inflated.Length];
            hdr.CopyTo(outBuf, 0);
            inflated.CopyTo(outBuf, hdr.Length);
            return outBuf;
        }
        if (e.TocSize == 0)
            return ReadAt(e.Offset, (int)e.X8);
        var comp = ReadAt(e.Offset, (int)e.Size);
        return Rpf7.Inflate(comp, 0, comp.Length);
    }

    /// <summary>Every file entry in TOC order (Python <c>read_rpf</c>, flat).</summary>
    public IEnumerable<RpfEntry> Files() => Entries.Where(e => !e.IsDir);

    /// <summary>The directory tree with full '/'-separated paths (Python <c>read_rpf_tree</c>).</summary>
    public List<RpfTreeItem> Tree()
    {
        var output = new List<RpfTreeItem>();
        var visiting = new HashSet<int>();

        void Walk(int idx, string prefix)
        {
            if (idx < 0 || idx >= Entries.Count || !visiting.Add(idx)) return;
            var e = Entries[idx];
            string full = idx == 0 ? "" : (prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}");
            if (e.IsDir)
            {
                if (idx != 0) output.Add(new RpfTreeItem(full, e));
                for (int c = e.FirstChild; c < e.FirstChild + e.ChildCount; c++)
                    Walk(c, full);
            }
            else
            {
                output.Add(new RpfTreeItem(full, e));
            }
        }

        Walk(0, "");
        return output;
    }

    /// <summary>
    /// Descend the directory tree and return the file entry at <paramref name="innerPath"/>
    /// ('/' or '\\' separated, case-insensitive), or null.
    /// </summary>
    public RpfEntry? Locate(string innerPath)
    {
        var parts = innerPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Entries.Count == 0) return null;
        int first = Entries[0].FirstChild, count = Entries[0].ChildCount;
        for (int depth = 0; depth < parts.Length; depth++)
        {
            bool last = depth == parts.Length - 1;
            (int, int)? descend = null;
            for (int c = first; c < first + count && c < Entries.Count; c++)
            {
                var ce = Entries[c];
                if (!string.Equals(ce.Name, parts[depth], StringComparison.OrdinalIgnoreCase)) continue;
                if (last && !ce.IsDir) return ce;
                if (!last && ce.IsDir)
                {
                    descend = (ce.FirstChild, ce.ChildCount);
                    break;
                }
            }
            if (descend is null) return null;
            (first, count) = descend.Value;
        }
        return null;
    }

    internal void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        lock (_stream)
        {
            _stream.Position = offset;
            _stream.Write(data);
        }
    }

    internal void ZeroRange(long offset, long count)
    {
        lock (_stream)
        {
            long end = Math.Min(offset + count, _stream.Length);
            if (end <= offset) return;
            _stream.Position = offset;
            var zeros = new byte[Math.Min(end - offset, 1 << 16)];
            long left = end - offset;
            while (left > 0)
            {
                int n = (int)Math.Min(left, zeros.Length);
                _stream.Write(zeros, 0, n);
                left -= n;
            }
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
