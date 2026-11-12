using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kudupst.Core.Pst
{
    /// <summary>
    /// Writes a new Outlook data file (PST, the Unicode kind Outlook 2003 and later make), from the bottom up: blocks of
    /// data, the nodes that own them, the two indexes, the maps of used space and the header (MS-PST 2.2, "NDB layer").
    /// The file is written front to back as blocks are added, so it can be far bigger than memory.
    /// What goes into the blocks (folders, messages) is built by <see cref="HeapWriter"/> and <see cref="PstStoreWriter"/>.
    /// </summary>
    public sealed class PstBuilder : IDisposable
    {
        public const int MaxBlockData = 8176;            // the most one block holds
        private const long FirstMap = 0x4400;            // where the first map of used space is
        private const long Span = 253952;                // how much of the file each map covers (496 bytes of bits, 64 bytes a bit)
        private const int PageSize = 512;

        private readonly Stream _out;
        private readonly bool _ownsStream;
        private long _at;                                 // where the next thing goes
        private long _span = -1;                          // which map's stretch of the file we're in
        private byte[] _bits = new byte[496];             // that map: a bit for every 64 bytes, set when used
        private long _freeBytes;
        private ulong _nextBid = 4;
        private ulong _nextPageBid = 1;
        private bool _finished;

        private readonly List<(ulong Bid, long Offset, ushort Size)> _blocks = new List<(ulong, long, ushort)>();
        private readonly SortedDictionary<uint, (ulong Data, ulong Sub, uint Parent)> _nodes = new SortedDictionary<uint, (ulong, ulong, uint)>();
        private readonly uint[] _nextIndex = new uint[32];

        public PstBuilder(Stream output, bool ownsStream = false)
        {
            _out = output;
            _ownsStream = ownsStream;
            _out.SetLength(0);
            for (var i = 0; i < 32; i++) _nextIndex[i] = 0x400;
            OpenSpan(0);
        }

        public void Dispose()
        {
            if (_ownsStream) _out.Dispose();
        }

        /// <summary>How big the file is so far.</summary>
        public long Length => _at;

        // ================================================================== IDs

        /// <summary>A new node ID of a kind (MS-PST 2.2.2.1: the low five bits say what the node is).</summary>
        public uint NewNid(int type) => (_nextIndex[type & 0x1F]++ << 5) | (uint)(type & 0x1F);

        /// <summary>Tells the builder an ID is taken (the fixed IDs every file has), so new ones don't collide.</summary>
        public void Reserve(uint nid)
        {
            var type = nid & 0x1F;
            if ((nid >> 5) >= _nextIndex[type]) _nextIndex[type] = (nid >> 5) + 1;
        }

        // ================================================================== space

        private static bool HasPageMap(long span) => span % 8 == 0;
        private static bool HasFreeMap(long span) => span >= 128 && (span - 128) % 496 == 0;
        private static bool HasFreePageMap(long span) => span >= 8192 && (span - 8192) % 31744 == 0;

        /// <summary>Starts the stretch of the file a map covers: the map itself comes first, then at set places the older maps.</summary>
        private void OpenSpan(long span)
        {
            _span = span;
            _bits = new byte[496];
            var start = FirstMap + span * Span;
            var reserved = PageSize;                                      // the map of used space (written when the stretch is full)
            if (HasPageMap(span))
            {
                WritePage(start + reserved, MapPage(0x83), (ulong)(start + reserved), 0x83, signed: false);
                reserved += PageSize;
            }
            if (HasFreeMap(span) || HasFreePageMap(span))
            {
                var type = (byte)(HasFreeMap(span) ? 0x82 : 0x85);
                WritePage(start + reserved, MapPage(type), (ulong)(start + reserved), type, signed: false);
                reserved += PageSize;
            }
            Mark(start, reserved);
            _at = start + reserved;
        }

        /// <summary>The older maps (of pages, of free space) aren't used by Outlook any more; they're kept filled, as it writes them.</summary>
        private static byte[] MapPage(byte type)
        {
            var page = new byte[PageSize];
            for (var i = 0; i < 496; i++) page[i] = 0xFF;
            return page;
        }

        private void CloseSpan()
        {
            var start = FirstMap + _span * Span;
            var page = new byte[PageSize];
            Buffer.BlockCopy(_bits, 0, page, 0, 496);
            WritePage(start, page, (ulong)start, 0x84, signed: false);
            foreach (var b in _bits) for (var bit = 0; bit < 8; bit++) if ((b & (1 << bit)) == 0) _freeBytes += 64;
        }

        private void Mark(long offset, int size)
        {
            var start = FirstMap + _span * Span;
            for (var unit = (offset - start) / 64; unit < (offset - start + size + 63) / 64; unit++)
                _bits[unit >> 3] |= (byte)(0x80 >> (int)(unit & 7));
        }

        /// <summary>Finds room for something, never across a map: when it doesn't fit before the next one, it goes after it.</summary>
        private long Allocate(int size, int align)
        {
            while (true)
            {
                var at = (_at + align - 1) / align * align;
                var end = FirstMap + (_span + 1) * Span;
                if (at + size <= end)
                {
                    Mark(at, size);
                    _at = (at + size + 63) / 64 * 64;
                    return at;
                }
                CloseSpan();
                OpenSpan(_span + 1);
            }
        }

        private void WritePage(long at, byte[] page, ulong bid, byte type, bool signed)
        {
            page[496] = type;
            page[497] = type;
            BitConverter.GetBytes(signed ? PstFile.Signature(at, bid) : (ushort)0).CopyTo(page, 498);
            BitConverter.GetBytes(Crc32.Plain(page, 0, 496)).CopyTo(page, 500);
            BitConverter.GetBytes(bid).CopyTo(page, 504);
            _out.Position = at;
            _out.Write(page, 0, PageSize);
        }

        // ================================================================== blocks

        private ulong WriteBlock(byte[] data, bool intern)
        {
            if (data.Length == 0 || data.Length > MaxBlockData) throw new ArgumentException("A block holds 1 to 8176 bytes.");
            var bid = _nextBid | (intern ? 2UL : 0UL);
            _nextBid += 4;
            var total = (data.Length + 16 + 63) / 64 * 64;
            var at = Allocate(total, 64);
            var block = new byte[total];
            Buffer.BlockCopy(data, 0, block, 0, data.Length);
            // The trailer: size, signature, checksum and ID (MS-PST 2.2.2.8.1).
            var t = total - 16;
            BitConverter.GetBytes((ushort)data.Length).CopyTo(block, t);
            BitConverter.GetBytes(PstFile.Signature(at, bid)).CopyTo(block, t + 2);
            BitConverter.GetBytes(Crc32.Plain(data)).CopyTo(block, t + 4);
            BitConverter.GetBytes(bid).CopyTo(block, t + 8);
            _out.Position = at;
            _out.Write(block, 0, total);
            _blocks.Add((bid, at, (ushort)data.Length));
            return bid;
        }

        /// <summary>Stores data of any size and returns the ID to reach it by: one block, or a list of blocks, or a list of lists.</summary>
        public ulong Data(byte[] data) => Data(new[] { data });

        /// <summary>
        /// Stores data given in pieces, each piece in a block of its own (a table's rows and a heap's pages must not be
        /// cut across blocks). Pieces must be 1 to 8176 bytes.
        /// </summary>
        public ulong Data(IList<byte[]> pieces)
        {
            var parts = new List<byte[]>();
            foreach (var piece in pieces)
            {
                if (piece.Length <= MaxBlockData) { if (piece.Length > 0) parts.Add(piece); continue; }
                for (var at = 0; at < piece.Length; at += MaxBlockData)
                {
                    var part = new byte[Math.Min(MaxBlockData, piece.Length - at)];
                    Buffer.BlockCopy(piece, at, part, 0, part.Length);
                    parts.Add(part);
                }
            }
            if (parts.Count == 0) return 0;
            if (parts.Count == 1) return WriteBlock(parts[0], false);

            var bids = new List<ulong>();
            long total = 0;
            foreach (var part in parts) { bids.Add(WriteBlock(part, false)); total += part.Length; }
            const int perList = (MaxBlockData - 8) / 8; // 1021 block IDs in a list
            if (bids.Count <= perList) return WriteBlock(BlockList(bids, total, 1), true);
            var lists = new List<ulong>();
            for (var i = 0; i < bids.Count; i += perList)
            {
                var some = bids.Skip(i).Take(perList).ToList();
                long size = 0;
                for (var k = i; k < i + some.Count; k++) size += parts[k].Length;
                lists.Add(WriteBlock(BlockList(some, size, 1), true));
            }
            if (lists.Count > perList) throw new PstException("This item is too big to save in an Outlook data file.");
            return WriteBlock(BlockList(lists, total, 2), true);
        }

        /// <summary>A list of block IDs (MS-PST 2.2.2.8.3.2: XBLOCK at level 1, XXBLOCK at level 2).</summary>
        private static byte[] BlockList(List<ulong> bids, long totalBytes, byte level)
        {
            var x = new byte[8 + bids.Count * 8];
            x[0] = 1;
            x[1] = level;
            BitConverter.GetBytes((ushort)bids.Count).CopyTo(x, 2);
            BitConverter.GetBytes((uint)totalBytes).CopyTo(x, 4);
            for (var i = 0; i < bids.Count; i++) BitConverter.GetBytes(bids[i]).CopyTo(x, 8 + i * 8);
            return x;
        }

        /// <summary>
        /// Stores a node's sub-nodes (the big values, tables and attachments that belong to it) and returns the ID to
        /// reach them by (MS-PST 2.2.2.8.3.3: SLBLOCK, with an SIBLOCK above when there are many).
        /// </summary>
        public ulong SubNodes(IEnumerable<(uint Nid, ulong Data, ulong Sub)> entries)
        {
            var sorted = entries.OrderBy(e => e.Nid).ToList();
            if (sorted.Count == 0) return 0;
            const int perLeaf = (MaxBlockData - 8) / 24; // 340
            var leaves = new List<(uint First, ulong Bid)>();
            for (var i = 0; i < sorted.Count; i += perLeaf)
            {
                var some = sorted.Skip(i).Take(perLeaf).ToList();
                var b = new byte[8 + some.Count * 24];
                b[0] = 2;
                b[1] = 0;
                BitConverter.GetBytes((ushort)some.Count).CopyTo(b, 2);
                for (var k = 0; k < some.Count; k++)
                {
                    BitConverter.GetBytes((ulong)some[k].Nid).CopyTo(b, 8 + k * 24);
                    BitConverter.GetBytes(some[k].Data).CopyTo(b, 16 + k * 24);
                    BitConverter.GetBytes(some[k].Sub).CopyTo(b, 24 + k * 24);
                }
                leaves.Add((some[0].Nid, WriteBlock(b, true)));
            }
            if (leaves.Count == 1) return leaves[0].Bid;
            if (leaves.Count > (MaxBlockData - 8) / 16) throw new PstException("This item has too many parts to save in an Outlook data file.");
            var top = new byte[8 + leaves.Count * 16];
            top[0] = 2;
            top[1] = 1;
            BitConverter.GetBytes((ushort)leaves.Count).CopyTo(top, 2);
            for (var k = 0; k < leaves.Count; k++)
            {
                BitConverter.GetBytes((ulong)leaves[k].First).CopyTo(top, 8 + k * 16);
                BitConverter.GetBytes(leaves[k].Bid).CopyTo(top, 16 + k * 16);
            }
            return WriteBlock(top, true);
        }

        /// <summary>Adds (or replaces) a node: something with an ID, its data, its sub-nodes and, for items in a folder, the folder.</summary>
        public void Node(uint nid, ulong data, ulong sub = 0, uint parent = 0)
        {
            Reserve(nid);
            _nodes[nid] = (data, sub, parent);
        }

        public bool HasNode(uint nid) => _nodes.ContainsKey(nid);

        // ================================================================== finishing

        /// <summary>Writes the indexes, the last map and the header. Nothing can be added afterwards.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            // The index of blocks first: writing its pages doesn't make new blocks.
            var blockEntries = _blocks.OrderBy(b => b.Bid).Select(b =>
            {
                var e = new byte[24];
                BitConverter.GetBytes(b.Bid).CopyTo(e, 0);
                BitConverter.GetBytes((ulong)b.Offset).CopyTo(e, 8);
                BitConverter.GetBytes(b.Size).CopyTo(e, 16);
                BitConverter.GetBytes((ushort)2).CopyTo(e, 18); // in use by one owner
                return (Key: b.Bid, Entry: e);
            }).ToList();
            var nodeEntries = _nodes.Select(n =>
            {
                var e = new byte[32];
                BitConverter.GetBytes((ulong)n.Key).CopyTo(e, 0);
                BitConverter.GetBytes(n.Value.Data).CopyTo(e, 8);
                BitConverter.GetBytes(n.Value.Sub).CopyTo(e, 16);
                BitConverter.GetBytes(n.Value.Parent).CopyTo(e, 24);
                return (Key: (ulong)n.Key, Entry: e);
            }).ToList();
            var blockRoot = Tree(blockEntries, 24, 0x80);
            var nodeRoot = Tree(nodeEntries, 32, 0x81);

            // The list of how full each stretch is (optional reading for Outlook; left empty, as in files it makes).
            var density = new byte[PageSize];
            var densityBid = _nextPageBid++;
            WritePage(0x4200, density, densityBid, 0x86, signed: true);

            CloseSpan();
            var lastMap = FirstMap + _span * Span;
            var end = lastMap + Span;
            _out.SetLength(end);

            var h = new byte[564];
            h[0] = (byte)'!'; h[1] = (byte)'B'; h[2] = (byte)'D'; h[3] = (byte)'N';
            h[8] = (byte)'S'; h[9] = (byte)'M';
            BitConverter.GetBytes((ushort)23).CopyTo(h, 10);    // the Unicode kind of file
            BitConverter.GetBytes((ushort)19).CopyTo(h, 12);
            h[14] = 1;
            h[15] = 1;
            BitConverter.GetBytes(_nextPageBid).CopyTo(h, 32);
            BitConverter.GetBytes(1u).CopyTo(h, 40);
            for (var i = 0; i < 32; i++) BitConverter.GetBytes(_nextIndex[i]).CopyTo(h, 44 + i * 4);
            // The root: where the file ends, the last map, how much is free, and the two indexes.
            BitConverter.GetBytes((ulong)end).CopyTo(h, 184);
            BitConverter.GetBytes((ulong)lastMap).CopyTo(h, 192);
            BitConverter.GetBytes((ulong)_freeBytes).CopyTo(h, 200);
            BitConverter.GetBytes(0UL).CopyTo(h, 208);
            BitConverter.GetBytes(nodeRoot.Bid).CopyTo(h, 216);
            BitConverter.GetBytes((ulong)nodeRoot.Offset).CopyTo(h, 224);
            BitConverter.GetBytes(blockRoot.Bid).CopyTo(h, 232);
            BitConverter.GetBytes((ulong)blockRoot.Offset).CopyTo(h, 240);
            h[248] = 2;                                          // the maps of used space can be trusted
            for (var i = 256; i < 512; i++) h[i] = 0xFF;         // the older maps kept in the header
            h[512] = 0x80;
            h[513] = 0;                                          // the data isn't scrambled
            BitConverter.GetBytes(_nextBid).CopyTo(h, 516);
            BitConverter.GetBytes(Crc32.Plain(h, 8, 471)).CopyTo(h, 4);
            BitConverter.GetBytes(Crc32.Plain(h, 8, 516)).CopyTo(h, 524);
            _out.Position = 0;
            _out.Write(h, 0, h.Length);
            _out.Flush();
        }

        /// <summary>Writes an index as pages: the entries in leaves, and pages above them until one page is the root.</summary>
        private (ulong Bid, long Offset) Tree(List<(ulong Key, byte[] Entry)> entries, int entrySize, byte type)
        {
            var level = new List<(ulong Key, ulong Bid, long Offset)>();
            var perLeaf = 488 / entrySize;
            for (var i = 0; i < Math.Max(1, entries.Count); i += perLeaf)
            {
                var some = entries.Skip(i).Take(perLeaf).ToList();
                var page = IndexPage(some.Select(s => s.Entry).ToList(), entrySize, 0, type);
                level.Add((some.Count > 0 ? some[0].Key : 0, page.Bid, page.Offset));
            }
            const int perBranch = 488 / 24;
            for (var depth = 1; level.Count > 1; depth++)
            {
                var above = new List<(ulong Key, ulong Bid, long Offset)>();
                for (var i = 0; i < level.Count; i += perBranch)
                {
                    var some = level.Skip(i).Take(perBranch).ToList();
                    var page = IndexPage(some.Select(s =>
                    {
                        var e = new byte[24];
                        BitConverter.GetBytes(s.Key).CopyTo(e, 0);
                        BitConverter.GetBytes(s.Bid).CopyTo(e, 8);
                        BitConverter.GetBytes((ulong)s.Offset).CopyTo(e, 16);
                        return e;
                    }).ToList(), 24, depth, type);
                    above.Add((some[0].Key, page.Bid, page.Offset));
                }
                level = above;
            }
            return (level[0].Bid, level[0].Offset);
        }

        private (ulong Bid, long Offset) IndexPage(List<byte[]> entries, int entrySize, int level, byte type)
        {
            var page = new byte[PageSize];
            for (var i = 0; i < entries.Count; i++) Buffer.BlockCopy(entries[i], 0, page, i * entrySize, entrySize);
            page[488] = (byte)entries.Count;
            page[489] = (byte)(488 / entrySize);
            page[490] = (byte)entrySize;
            page[491] = (byte)level;
            var at = Allocate(PageSize, PageSize);
            var bid = _nextPageBid++;
            WritePage(at, page, bid, type, signed: true);
            return (bid, at);
        }
    }
}
