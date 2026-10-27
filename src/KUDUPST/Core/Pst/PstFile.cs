using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Kudupst.Core.Pst
{
    public enum PstFormat
    {
        /// <summary>Outlook 97–2002: 32-bit offsets, 2 GB at most.</summary>
        Ansi,
        /// <summary>Outlook 2003 and later.</summary>
        Unicode,
        /// <summary>Outlook 2013 and later OST files: 4 KB pages, compressed blocks.</summary>
        Unicode4K
    }

    public enum PstCryptMethod { None = 0, Compressible = 1, High = 2 }

    /// <summary>Where a block is in the file.</summary>
    public struct BlockRef
    {
        public ulong Bid;
        public long Offset;
        public int Size;
        public int InflatedSize;
    }

    /// <summary>A node: an item in the file (a folder, a message, a table…), its data and its sub-nodes.</summary>
    public struct NodeRef
    {
        public uint Nid;
        public ulong DataBid;
        public ulong SubBid;
        public uint ParentNid;
    }

    /// <summary>A sub-node: part of a node, like a message's recipient table or an attachment.</summary>
    public struct SubNodeRef
    {
        public uint Nid;
        public ulong DataBid;
        public ulong SubBid;
    }

    /// <summary>
    /// The lowest layer of a PST or OST file (MS-PST "node database"): the header, the two index trees (blocks by ID and
    /// nodes by ID), and reading a node's data, which may be spread over many blocks, scrambled or compressed. The file is
    /// only ever read, and can be read from several threads.
    /// </summary>
    public sealed class PstFile : IDisposable
    {
        private readonly Stream _stream;
        private readonly object _sync = new object();
        private readonly Dictionary<long, byte[]> _pages = new Dictionary<long, byte[]>();
        private readonly long _nbtRoot, _bbtRoot;
        private readonly int _pageSize;
        private readonly Dictionary<ulong, BlockRef> _foundBlocks = new Dictionary<ulong, BlockRef>();

        public PstFormat Format { get; }
        public PstCryptMethod Crypt { get; }
        public bool IsOst { get; }
        public long Length { get; }
        public string Path { get; }

        public bool Unicode => Format != PstFormat.Ansi;

        /// <summary>The size of an index page: 512 bytes, or 4 KB in newer OST files.</summary>
        public int PageSize => _pageSize;

        /// <summary>Whether the file's map of used and free space can be trusted (Outlook marks it when it's up to date).</summary>
        public bool FreeMapValid { get; }

        /// <summary>Where the file's own data ends (anything after it is unused).</summary>
        public long EndOfData { get; }

        public static PstFile Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
            try
            {
                return new PstFile(stream, path);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public PstFile(Stream stream, string path = null)
        {
            _stream = stream;
            Path = path;
            Length = stream.Length;
            var header = new byte[0x204];
            if (Length < header.Length || ReadAt(0, header, header.Length) < header.Length)
                throw new PstException("This file is too small to be an Outlook data file.");
            if (header[0] != (byte)'!' || header[1] != (byte)'B' || header[2] != (byte)'D' || header[3] != (byte)'N')
                throw new PstException("This isn't an Outlook data file (.pst or .ost).");
            IsOst = header[8] == (byte)'S' && header[9] == (byte)'O';
            var version = BitConverter.ToUInt16(header, 10);
            if (version == 14 || version == 15) Format = PstFormat.Ansi;
            else if (version >= 23 && version < 36) Format = PstFormat.Unicode;
            else if (version >= 36 && version < 40) Format = PstFormat.Unicode4K;
            else throw new PstException($"This Outlook data file uses a format KUDUPST doesn't know (version {version}).");

            if (Format == PstFormat.Ansi)
            {
                _nbtRoot = BitConverter.ToUInt32(header, 188);
                _bbtRoot = BitConverter.ToUInt32(header, 196);
                Crypt = (PstCryptMethod)header[0x1CD];
                _pageSize = 512;
                FreeMapValid = header[200] != 0;
                EndOfData = BitConverter.ToUInt32(header, 168);
            }
            else
            {
                _nbtRoot = (long)BitConverter.ToUInt64(header, 224);
                _bbtRoot = (long)BitConverter.ToUInt64(header, 240);
                Crypt = (PstCryptMethod)header[0x201];
                _pageSize = Format == PstFormat.Unicode4K ? 4096 : 512;
                FreeMapValid = header[248] != 0;
                EndOfData = (long)BitConverter.ToUInt64(header, 184);
            }
            if (Crypt != PstCryptMethod.None && Crypt != PstCryptMethod.Compressible && Crypt != PstCryptMethod.High)
                throw new PstException("This Outlook data file uses an unknown kind of encryption.");
            if (EndOfData <= 0 || EndOfData > Length) EndOfData = Length;
            if (_nbtRoot <= 0 || _nbtRoot >= Length || _bbtRoot <= 0 || _bbtRoot >= Length)
                throw new PstException("This Outlook data file is damaged: its index is missing.");
        }

        public void Dispose()
        {
            lock (_sync) _stream.Dispose();
        }

        /// <summary>Reads bytes from the file as they are (for looking through its free space).</summary>
        public int ReadRaw(long offset, byte[] buffer, int count) => ReadAt(offset, buffer, count);

        private int ReadAt(long offset, byte[] buffer, int count)
        {
            lock (_sync)
            {
                _stream.Position = offset;
                var total = 0;
                while (total < count)
                {
                    var n = _stream.Read(buffer, total, count - total);
                    if (n <= 0) break;
                    total += n;
                }
                return total;
            }
        }

        // ================================================================== index pages

        private byte[] Page(long offset)
        {
            lock (_sync)
            {
                if (_pages.TryGetValue(offset, out var cached)) return cached;
            }
            if (offset <= 0 || offset + _pageSize > Length) throw new PstException("This Outlook data file is damaged: an index page is outside the file.");
            var page = new byte[_pageSize];
            if (ReadAt(offset, page, _pageSize) < _pageSize) throw new PstException("This Outlook data file is cut short.");
            lock (_sync)
            {
                if (_pages.Count > 8192) _pages.Clear();
                _pages[offset] = page;
            }
            return page;
        }

        private struct PageInfo
        {
            public int Count, EntrySize, Level;
            public byte Type;
        }

        private PageInfo Info(byte[] page)
        {
            switch (Format)
            {
                case PstFormat.Ansi:
                    return new PageInfo { Count = page[496], EntrySize = page[498], Level = page[499], Type = page[500] };
                case PstFormat.Unicode:
                    return new PageInfo { Count = page[488], EntrySize = page[490], Level = page[491], Type = page[496] };
                default:
                    return new PageInfo { Count = BitConverter.ToUInt16(page, 4056), EntrySize = page[4060], Level = page[4061], Type = page[4072] };
            }
        }

        private ulong Key(byte[] page, int offset) => Unicode ? BitConverter.ToUInt64(page, offset) : BitConverter.ToUInt32(page, offset);

        private long ChildOffset(byte[] page, int entry)
        {
            // A branch entry: key, then a block reference (ID, offset).
            return Unicode ? (long)BitConverter.ToUInt64(page, entry + 16) : BitConverter.ToUInt32(page, entry + 8);
        }

        /// <summary>Finds the leaf entry for a key: in branch pages, the last entry whose key is not above it.</summary>
        private bool FindLeaf(long root, ulong key, out byte[] page, out int entry)
        {
            var offset = root;
            for (var depth = 0; depth < 16; depth++)
            {
                page = Page(offset);
                var info = Info(page);
                if (info.EntrySize <= 0 || info.Count * info.EntrySize > _pageSize) throw new PstException("This Outlook data file is damaged: an index page is unreadable.");
                if (info.Level == 0)
                {
                    int lo = 0, hi = info.Count - 1;
                    while (lo <= hi)
                    {
                        var mid = (lo + hi) / 2;
                        var k = Key(page, mid * info.EntrySize);
                        if (k == key)
                        {
                            entry = mid * info.EntrySize;
                            return true;
                        }
                        if (k < key) lo = mid + 1;
                        else hi = mid - 1;
                    }
                    entry = -1;
                    return false;
                }
                var chosen = -1;
                for (var i = 0; i < info.Count; i++)
                {
                    if (Key(page, i * info.EntrySize) <= key) chosen = i;
                    else break;
                }
                if (chosen < 0)
                {
                    entry = -1;
                    return false;
                }
                offset = ChildOffset(page, chosen * info.EntrySize);
            }
            throw new PstException("This Outlook data file is damaged: its index is too deep.");
        }

        /// <summary>
        /// Blocks found in the file's free space (left over from deleted items), used when the index no longer lists
        /// them. Each one was checked against its own checksum when it was found.
        /// </summary>
        public void AddFoundBlocks(IEnumerable<BlockRef> blocks)
        {
            lock (_sync)
            {
                foreach (var b in blocks)
                    if (!_foundBlocks.ContainsKey(b.Bid)) _foundBlocks[b.Bid] = b;
            }
        }

        /// <summary>Whether the file's index lists a block (it's in use).</summary>
        public bool IsListed(ulong bid) => FindLeaf(_bbtRoot, bid & ~1UL, out _, out _);

        public bool TryGetBlock(ulong bid, out BlockRef block)
        {
            bid &= ~1UL; // the lowest bit isn't part of the ID
            block = default;
            if (!FindLeaf(_bbtRoot, bid, out var page, out var e))
            {
                lock (_sync) return _foundBlocks.TryGetValue(bid, out block);
            }
            switch (Format)
            {
                case PstFormat.Ansi:
                    block = new BlockRef { Bid = bid, Offset = BitConverter.ToUInt32(page, e + 4), Size = BitConverter.ToUInt16(page, e + 8) };
                    break;
                case PstFormat.Unicode:
                    block = new BlockRef { Bid = bid, Offset = (long)BitConverter.ToUInt64(page, e + 8), Size = BitConverter.ToUInt16(page, e + 16) };
                    break;
                default:
                    block = new BlockRef { Bid = bid, Offset = (long)BitConverter.ToUInt64(page, e + 8), Size = BitConverter.ToUInt16(page, e + 16), InflatedSize = BitConverter.ToUInt16(page, e + 18) };
                    break;
            }
            if (block.InflatedSize == 0) block.InflatedSize = block.Size;
            return true;
        }

        public bool TryGetNode(uint nid, out NodeRef node)
        {
            node = default;
            if (!FindLeaf(_nbtRoot, nid, out var page, out var e)) return false;
            node = Unicode
                ? new NodeRef { Nid = nid, DataBid = BitConverter.ToUInt64(page, e + 8), SubBid = BitConverter.ToUInt64(page, e + 16), ParentNid = BitConverter.ToUInt32(page, e + 24) }
                : new NodeRef { Nid = nid, DataBid = BitConverter.ToUInt32(page, e + 4), SubBid = BitConverter.ToUInt32(page, e + 8), ParentNid = BitConverter.ToUInt32(page, e + 12) };
            return true;
        }

        public NodeRef GetNode(uint nid)
        {
            if (!TryGetNode(nid, out var node)) throw new PstException($"This Outlook data file is damaged: item {nid} is missing.");
            return node;
        }

        /// <summary>Every node in the file, in ID order (for checking a whole file, and for finding deleted items).</summary>
        public IEnumerable<NodeRef> AllNodes() => Walk(_nbtRoot, 0);

        private IEnumerable<NodeRef> Walk(long offset, int depth)
        {
            if (depth > 16) yield break;
            var page = Page(offset);
            var info = Info(page);
            for (var i = 0; i < info.Count; i++)
            {
                var e = i * info.EntrySize;
                if (info.Level > 0)
                {
                    foreach (var n in Walk(ChildOffset(page, e), depth + 1)) yield return n;
                }
                else
                {
                    yield return Unicode
                        ? new NodeRef { Nid = (uint)BitConverter.ToUInt64(page, e), DataBid = BitConverter.ToUInt64(page, e + 8), SubBid = BitConverter.ToUInt64(page, e + 16), ParentNid = BitConverter.ToUInt32(page, e + 24) }
                        : new NodeRef { Nid = BitConverter.ToUInt32(page, e), DataBid = BitConverter.ToUInt32(page, e + 4), SubBid = BitConverter.ToUInt32(page, e + 8), ParentNid = BitConverter.ToUInt32(page, e + 12) };
                }
            }
        }

        // ================================================================== recognising leftovers

        /// <summary>The checksum-like signature of a block or index page, from where it is and its ID (MS-PST 5.5).</summary>
        public static ushort Signature(long offset, ulong bid)
        {
            var x = (uint)offset ^ (uint)bid;
            return (ushort)((x >> 16) ^ x);
        }

        /// <summary>The size of a block's trailer: the size, signature, checksum and ID kept after its data.</summary>
        public int BlockTrailerSize => Format == PstFormat.Ansi ? 12 : Format == PstFormat.Unicode ? 16 : 24;

        /// <summary>Blocks take up whole units of this many bytes.</summary>
        public int BlockUnit => Format == PstFormat.Unicode4K ? 512 : 64;

        /// <summary>The most data one block holds.</summary>
        public int MaxBlockData => Format == PstFormat.Unicode4K ? 65536 - 24 : 8192 - BlockTrailerSize;

        /// <summary>
        /// Checks whether a block ends at <paramref name="end"/> (an index into <paramref name="buffer"/>, which holds the
        /// file from <paramref name="bufferOffset"/>): its trailer must be whole, and its signature and checksum right.
        /// </summary>
        public bool TryBlockEndingAt(byte[] buffer, long bufferOffset, int end, int lowest, out BlockRef block)
        {
            block = default;
            var tr = BlockTrailerSize;
            var t = end - tr;
            if (t < lowest) return false;
            int cb = BitConverter.ToUInt16(buffer, t);
            if (cb == 0 || cb > MaxBlockData) return false;
            ulong bid;
            uint crc;
            if (Format == PstFormat.Ansi)
            {
                bid = BitConverter.ToUInt32(buffer, t + 4);
                crc = BitConverter.ToUInt32(buffer, t + 8);
            }
            else
            {
                crc = BitConverter.ToUInt32(buffer, t + 4);
                bid = BitConverter.ToUInt64(buffer, t + 8);
            }
            if (bid == 0) return false;
            var unit = BlockUnit;
            var total = (cb + tr + unit - 1) / unit * unit;
            var start = end - total;
            if (start < lowest) return false;
            if (BitConverter.ToUInt16(buffer, t + 2) != Signature(bufferOffset + start, bid)) return false;
            if (Crc32.Plain(buffer, start, cb) != crc) return false;
            var inflated = Format == PstFormat.Unicode4K ? BitConverter.ToUInt16(buffer, t + 18) : 0;
            block = new BlockRef { Bid = bid & ~1UL, Offset = bufferOffset + start, Size = cb, InflatedSize = inflated == 0 ? cb : inflated };
            return true;
        }

        /// <summary>
        /// Reads the node entries of an index page that lists nodes (a leaf of the node tree) starting at
        /// <paramref name="at"/> in <paramref name="buffer"/>, if it is one and its checksum is right.
        /// </summary>
        public bool TryNodePage(byte[] buffer, int at, List<NodeRef> into)
        {
            if (at + _pageSize > buffer.Length) return false;
            int typeAt, crcAt, dataSize;
            switch (Format)
            {
                case PstFormat.Ansi: typeAt = at + 500; crcAt = at + 508; dataSize = 500; break;
                case PstFormat.Unicode: typeAt = at + 496; crcAt = at + 500; dataSize = 496; break;
                default: typeAt = at + 4072; crcAt = at + 4076; dataSize = 4072; break;
            }
            if (buffer[typeAt] != 0x81 || buffer[typeAt + 1] != 0x81) return false;
            if (Crc32.Plain(buffer, at, dataSize) != BitConverter.ToUInt32(buffer, crcAt)) return false;
            var page = new byte[_pageSize];
            Buffer.BlockCopy(buffer, at, page, 0, _pageSize);
            var info = Info(page);
            if (info.Level != 0 || info.EntrySize <= 0 || info.Count * info.EntrySize > dataSize) return false;
            for (var i = 0; i < info.Count; i++)
            {
                var e = i * info.EntrySize;
                into.Add(Unicode
                    ? new NodeRef { Nid = (uint)BitConverter.ToUInt64(page, e), DataBid = BitConverter.ToUInt64(page, e + 8), SubBid = BitConverter.ToUInt64(page, e + 16), ParentNid = BitConverter.ToUInt32(page, e + 24) }
                    : new NodeRef { Nid = BitConverter.ToUInt32(page, e), DataBid = BitConverter.ToUInt32(page, e + 4), SubBid = BitConverter.ToUInt32(page, e + 8), ParentNid = BitConverter.ToUInt32(page, e + 12) });
            }
            return true;
        }

        /// <summary>
        /// The parts of the file marked free in its map of used space (each map page covers 253,952 bytes, a bit for
        /// every 64), as (offset, length). Null when there's no map to go by (it's out of date, or a newer OST file).
        /// </summary>
        public List<(long Offset, long Length)> FreeSpace()
        {
            if (Format == PstFormat.Unicode4K || !FreeMapValid) return null;
            const long first = 0x4400, span = 253952;
            var runs = new List<(long, long)>();
            long runStart = -1;
            var page = new byte[512];
            for (var map = first; map < EndOfData; map += span)
            {
                if (ReadAt(map, page, 512) < 512) break;
                var type = Format == PstFormat.Ansi ? page[500] : page[496];
                if (type != 0x84) return null;
                var units = (int)(Math.Min(span, EndOfData - map) / 64);
                for (var i = 0; i < units; i++)
                {
                    var used = (page[i >> 3] >> (7 - (i & 7)) & 1) != 0;
                    var at = map + i * 64L;
                    if (!used && runStart < 0) runStart = at;
                    else if (used && runStart >= 0)
                    {
                        runs.Add((runStart, at - runStart));
                        runStart = -1;
                    }
                }
            }
            if (runStart >= 0) runs.Add((runStart, EndOfData - runStart));
            return runs;
        }

        // ================================================================== blocks

        public static bool IsInternal(ulong bid) => (bid & 2) != 0;

        /// <summary>A block's contents: decrypted (data blocks) and decompressed (newer OST files).</summary>
        public byte[] ReadBlock(ulong bid)
        {
            if (!TryGetBlock(bid, out var block)) throw new PstException($"This Outlook data file is damaged: block {bid} is missing.");
            if (block.Offset < 0 || block.Offset + block.Size > Length) throw new PstException("This Outlook data file is damaged: a block is outside the file.");
            var data = new byte[block.Size];
            if (ReadAt(block.Offset, data, block.Size) < block.Size) throw new PstException("This Outlook data file is cut short.");
            if (Format == PstFormat.Unicode4K && block.InflatedSize != block.Size) data = Inflate(data, block.InflatedSize);
            if (!IsInternal(bid))
            {
                if (Crypt == PstCryptMethod.Compressible) PstCrypt.DecodeCompressible(data, 0, data.Length);
                else if (Crypt == PstCryptMethod.High) PstCrypt.DecodeCyclic(data, 0, data.Length, (uint)bid);
            }
            return data;
        }

        private static byte[] Inflate(byte[] data, int size)
        {
            // zlib: a 2-byte header, then deflate data.
            var result = new byte[size];
            using (var input = new MemoryStream(data, 2, data.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var total = 0;
                while (total < size)
                {
                    var n = deflate.Read(result, total, size - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total != size) throw new PstException("This Outlook data file is damaged: a compressed block is unreadable.");
            }
            return result;
        }

        /// <summary>
        /// The data blocks of a node, in order. Small data is one block; bigger data is a tree of block lists
        /// (XBLOCK, XXBLOCK).
        /// </summary>
        public List<byte[]> ReadDataBlocks(ulong bid)
        {
            var blocks = new List<byte[]>();
            if (bid == 0) return blocks;
            Collect(bid, blocks, 0);
            return blocks;
        }

        private void Collect(ulong bid, List<byte[]> blocks, int depth)
        {
            var data = ReadBlock(bid);
            if (!IsInternal(bid))
            {
                blocks.Add(data);
                return;
            }
            if (depth > 2 || data.Length < 8 || data[0] != 1) throw new PstException("This Outlook data file is damaged: a block list is unreadable.");
            var count = BitConverter.ToUInt16(data, 2);
            var size = Unicode ? 8 : 4;
            for (var i = 0; i < count; i++)
            {
                var at = 8 + i * size;
                if (at + size > data.Length) break;
                Collect(Unicode ? BitConverter.ToUInt64(data, at) : BitConverter.ToUInt32(data, at), blocks, depth + 1);
            }
        }

        /// <summary>All the data of a node in one array.</summary>
        public byte[] ReadData(ulong bid)
        {
            var blocks = ReadDataBlocks(bid);
            if (blocks.Count == 1) return blocks[0];
            var total = 0;
            foreach (var b in blocks) total += b.Length;
            var all = new byte[total];
            var at = 0;
            foreach (var b in blocks)
            {
                Buffer.BlockCopy(b, 0, all, at, b.Length);
                at += b.Length;
            }
            return all;
        }

        /// <summary>A node's sub-nodes (SLBLOCK / SIBLOCK tree), by ID.</summary>
        public Dictionary<uint, SubNodeRef> ReadSubNodes(ulong bid)
        {
            var result = new Dictionary<uint, SubNodeRef>();
            if (bid != 0) CollectSub(bid, result, 0);
            return result;
        }

        private void CollectSub(ulong bid, Dictionary<uint, SubNodeRef> result, int depth)
        {
            var data = ReadBlock(bid);
            if (depth > 4 || data.Length < 4 || data[0] != 2) throw new PstException("This Outlook data file is damaged: a list of item parts is unreadable.");
            var level = data[1];
            var count = BitConverter.ToUInt16(data, 2);
            var start = Unicode ? 8 : 4;
            if (level == 0)
            {
                var size = Unicode ? 24 : 12;
                for (var i = 0; i < count; i++)
                {
                    var at = start + i * size;
                    if (at + size > data.Length) break;
                    var sub = Unicode
                        ? new SubNodeRef { Nid = (uint)BitConverter.ToUInt64(data, at), DataBid = BitConverter.ToUInt64(data, at + 8), SubBid = BitConverter.ToUInt64(data, at + 16) }
                        : new SubNodeRef { Nid = BitConverter.ToUInt32(data, at), DataBid = BitConverter.ToUInt32(data, at + 4), SubBid = BitConverter.ToUInt32(data, at + 8) };
                    result[sub.Nid] = sub;
                }
            }
            else
            {
                var size = Unicode ? 16 : 8;
                for (var i = 0; i < count; i++)
                {
                    var at = start + i * size;
                    if (at + size > data.Length) break;
                    CollectSub(Unicode ? BitConverter.ToUInt64(data, at + 8) : BitConverter.ToUInt32(data, at + 4), result, depth + 1);
                }
            }
        }
    }

    public sealed class PstException : Exception
    {
        public PstException(string message) : base(message)
        {
        }
    }
}
