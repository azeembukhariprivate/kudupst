using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Pst;

namespace Kudupst.Tests
{
    /// <summary>
    /// Writes small PST files byte by byte (MS-PST), for the formats the real sample files don't cover: the old ANSI
    /// format, "high" encryption, big values spread over several blocks, attached emails. Only what the reader needs.
    /// </summary>
    public sealed class PstWriter
    {
        private readonly bool _unicode;
        private readonly PstCryptMethod _crypt;
        private readonly SortedDictionary<ulong, byte[]> _blocks = new SortedDictionary<ulong, byte[]>();
        private readonly SortedDictionary<uint, (ulong Data, ulong Sub, uint Parent)> _nodes = new SortedDictionary<uint, (ulong, ulong, uint)>();
        private ulong _nextBid = 4;

        public PstWriter(bool unicode, PstCryptMethod crypt)
        {
            _unicode = unicode;
            _crypt = crypt;
        }

        private int MaxBlock => _unicode ? 8176 : 8180;

        // ================================================================== blocks and nodes

        public ulong DataBlock(byte[] data)
        {
            var bid = _nextBid;
            _nextBid += 4;
            _blocks[bid] = data;
            return bid;
        }

        private ulong InternalBlock(byte[] data)
        {
            var bid = _nextBid | 2;
            _nextBid += 4;
            _blocks[bid] = data;
            return bid;
        }

        /// <summary>Data of any size: one block, or a block list (XBLOCK) when it's too big for one.</summary>
        public ulong Data(byte[] data)
        {
            if (data.Length <= MaxBlock) return DataBlock(data);
            var bids = new List<ulong>();
            for (var at = 0; at < data.Length; at += MaxBlock)
                bids.Add(DataBlock(data.Skip(at).Take(Math.Min(MaxBlock, data.Length - at)).ToArray()));
            var size = _unicode ? 8 : 4;
            var x = new byte[8 + bids.Count * size];
            x[0] = 1;
            x[1] = 1;
            BitConverter.GetBytes((ushort)bids.Count).CopyTo(x, 2);
            BitConverter.GetBytes((uint)data.Length).CopyTo(x, 4);
            for (var i = 0; i < bids.Count; i++)
            {
                if (_unicode) BitConverter.GetBytes(bids[i]).CopyTo(x, 8 + i * 8);
                else BitConverter.GetBytes((uint)bids[i]).CopyTo(x, 8 + i * 4);
            }
            return InternalBlock(x);
        }

        public void Node(uint nid, ulong data, ulong sub = 0, uint parent = 0) => _nodes[nid] = (data, sub, parent);

        /// <summary>A sub-node list (SLBLOCK).</summary>
        public ulong SubNodes(IEnumerable<(uint Nid, ulong Data, ulong Sub)> entries)
        {
            var list = entries.OrderBy(e => e.Nid).ToList();
            var head = _unicode ? 8 : 4;
            var size = _unicode ? 24 : 12;
            var b = new byte[head + list.Count * size];
            b[0] = 2;
            b[1] = 0;
            BitConverter.GetBytes((ushort)list.Count).CopyTo(b, 2);
            for (var i = 0; i < list.Count; i++)
            {
                var at = head + i * size;
                if (_unicode)
                {
                    BitConverter.GetBytes((ulong)list[i].Nid).CopyTo(b, at);
                    BitConverter.GetBytes(list[i].Data).CopyTo(b, at + 8);
                    BitConverter.GetBytes(list[i].Sub).CopyTo(b, at + 16);
                }
                else
                {
                    BitConverter.GetBytes(list[i].Nid).CopyTo(b, at);
                    BitConverter.GetBytes((uint)list[i].Data).CopyTo(b, at + 4);
                    BitConverter.GetBytes((uint)list[i].Sub).CopyTo(b, at + 8);
                }
            }
            return InternalBlock(b);
        }

        // ================================================================== heap, property lists, tables

        /// <summary>Builds a heap-on-node in one block. Add items, then call Build.</summary>
        public sealed class HeapBuilder
        {
            private readonly List<byte[]> _items = new List<byte[]>();

            public uint Add(byte[] item)
            {
                _items.Add(item);
                return (uint)(_items.Count << 5);
            }

            public void Set(uint hid, byte[] item) => _items[(int)(hid >> 5) - 1] = item;

            public byte[] Build(byte clientSig, uint userRoot)
            {
                var body = new MemoryStream();
                body.Write(new byte[12], 0, 12);
                var offsets = new List<int>();
                foreach (var item in _items)
                {
                    offsets.Add((int)body.Length);
                    body.Write(item, 0, item.Length);
                }
                offsets.Add((int)body.Length);
                var mapAt = (int)body.Length;
                var w = new BinaryWriter(body);
                w.Write((ushort)_items.Count);
                w.Write((ushort)0);
                foreach (var o in offsets) w.Write((ushort)o);
                var data = body.ToArray();
                BitConverter.GetBytes((ushort)mapAt).CopyTo(data, 0);
                data[2] = 0xEC;
                data[3] = clientSig;
                BitConverter.GetBytes(userRoot).CopyTo(data, 4);
                return data;
            }
        }

        public sealed class Prop
        {
            public ushort Id;
            public ushort Type;
            public byte[] Value;
            public uint Hnid; // for values already placed (sub-nodes, objects)
        }

        public static Prop Str(ushort id, string s) => new Prop { Id = id, Type = PropType.Unicode, Value = Encoding.Unicode.GetBytes(s) };
        public static Prop Str8(ushort id, string s, Encoding enc) => new Prop { Id = id, Type = PropType.String8, Value = enc.GetBytes(s) };
        public static Prop Int(ushort id, int v) => new Prop { Id = id, Type = PropType.Int32, Value = BitConverter.GetBytes(v) };
        public static Prop Bool(ushort id, bool v) => new Prop { Id = id, Type = PropType.Boolean, Value = BitConverter.GetBytes(v ? 1 : 0) };
        public static Prop Time(ushort id, DateTime utc) => new Prop { Id = id, Type = PropType.SysTime, Value = BitConverter.GetBytes(utc.ToFileTimeUtc()) };
        public static Prop Bin(ushort id, byte[] data) => new Prop { Id = id, Type = PropType.Binary, Value = data };

        /// <summary>A property list. Values too big for the heap go to sub-nodes (returned for the node's sub-node list).</summary>
        public byte[] PropertyContext(IEnumerable<Prop> props, List<(uint Nid, ulong Data, ulong Sub)> subNodes = null)
        {
            var heap = new HeapBuilder();
            var header = heap.Add(new byte[8]);
            var records = new MemoryStream();
            var w = new BinaryWriter(records);
            var nextSub = 0x8001u;
            foreach (var p in props.OrderBy(p => p.Id))
            {
                uint value;
                if (p.Hnid != 0) value = p.Hnid;
                else if (PropType.FixedSize(p.Type) > 0 && PropType.FixedSize(p.Type) <= 4) value = BitConverter.ToUInt32(p.Value.Concat(new byte[4]).Take(4).ToArray(), 0);
                else if (p.Value.Length > 3000 && subNodes != null)
                {
                    var nid = (nextSub++ << 5) | 0x1F;
                    while (subNodes.Any(s => s.Nid == nid)) nid = (nextSub++ << 5) | 0x1F;
                    subNodes.Add((nid, Data(p.Value), 0));
                    value = nid;
                }
                else value = heap.Add(p.Value);
                w.Write(p.Id);
                w.Write(p.Type);
                w.Write(value);
            }
            var recordsHid = heap.Add(records.ToArray());
            var bth = new byte[8];
            bth[0] = 0xB5;
            bth[1] = 2;
            bth[2] = 6;
            bth[3] = 0;
            BitConverter.GetBytes(recordsHid).CopyTo(bth, 4);
            heap.Set(header, bth);
            return heap.Build(0xBC, header);
        }

        /// <summary>A table: the first column must be the row ID (0x67F2, a 32-bit number).</summary>
        public byte[] TableContext(IList<(ushort Id, ushort Type)> columns, IList<object[]> rows, Encoding ansi = null)
        {
            var heap = new HeapBuilder();
            var info = heap.Add(new byte[1]);
            // Row layout: 8-byte values, then 4-byte values and heap references, then the cell-exists bitmap.
            var layout = new List<(ushort Id, ushort Type, int Offset, int Size, int Bit)>();
            var offset = 0;
            var ordered = columns.Select((c, i) => (c, i)).OrderBy(x => x.c.Id == PropId.LtpRowId ? 0 : PropType.FixedSize(x.c.Type) == 8 ? 1 : 2).ToList();
            foreach (var (c, i) in ordered)
            {
                var size = PropType.FixedSize(c.Type) == 8 ? 8 : 4;
                layout.Add((c.Id, c.Type, offset, size, i));
                offset += size;
            }
            var bitmapAt = offset;
            var rowSize = bitmapAt + (columns.Count + 7) / 8;
            var matrix = new byte[rowSize * rows.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                var row = r * rowSize;
                foreach (var col in layout)
                {
                    var value = rows[r][col.Bit];
                    if (value == null) continue;
                    matrix[row + bitmapAt + col.Bit / 8] |= (byte)(1 << (7 - col.Bit % 8));
                    byte[] bytes;
                    switch (value)
                    {
                        case int i: bytes = BitConverter.GetBytes(i); break;
                        case uint u: bytes = BitConverter.GetBytes(u); break;
                        case bool b: bytes = BitConverter.GetBytes(b ? 1 : 0); break;
                        case DateTime d: bytes = BitConverter.GetBytes(d.ToFileTimeUtc()); break;
                        case string s:
                            bytes = BitConverter.GetBytes(heap.Add(col.Type == PropType.String8 ? (ansi ?? Encoding.ASCII).GetBytes(s) : Encoding.Unicode.GetBytes(s)));
                            break;
                        case byte[] data: bytes = BitConverter.GetBytes(heap.Add(data)); break;
                        default: throw new ArgumentException("value type");
                    }
                    Array.Copy(bytes, 0, matrix, row + col.Offset, Math.Min(bytes.Length, col.Size));
                }
            }
            var rowIndex = heap.Add(new byte[] { 0xB5, 4, 4, 0, 0, 0, 0, 0 });
            var rowsHid = rows.Count > 0 ? heap.Add(matrix) : 0;
            var tc = new MemoryStream();
            var w = new BinaryWriter(tc);
            w.Write((byte)0x7C);
            w.Write((byte)columns.Count);
            w.Write((ushort)bitmapAt); // end of 4- and 8-byte values
            w.Write((ushort)bitmapAt); // end of 2-byte values
            w.Write((ushort)bitmapAt); // end of 1-byte values
            w.Write((ushort)rowSize);
            w.Write(rowIndex);
            w.Write(rowsHid);
            w.Write(0u);
            foreach (var col in layout.OrderBy(c => c.Id))
            {
                w.Write(((uint)col.Id << 16) | col.Type);
                w.Write((ushort)col.Offset);
                w.Write((byte)col.Size);
                w.Write((byte)col.Bit);
            }
            heap.Set(info, tc.ToArray());
            return heap.Build(0x7C, info);
        }

        // ================================================================== the file

        public byte[] Build()
        {
            var output = new MemoryStream();
            output.Write(new byte[0x400], 0, 0x400);
            var placed = new List<(ulong Bid, long Offset, int Size)>();
            foreach (var kv in _blocks)
            {
                var data = (byte[])kv.Value.Clone();
                if (!PstFile.IsInternal(kv.Key))
                {
                    if (_crypt == PstCryptMethod.Compressible) PstCrypt.EncodeCompressible(data, 0, data.Length);
                    else if (_crypt == PstCryptMethod.High) PstCrypt.EncodeCyclic(data, 0, data.Length, (uint)kv.Key);
                }
                var at = Align(output, 64);
                output.Write(data, 0, data.Length);
                placed.Add((kv.Key, at, data.Length));
            }

            var bbtEntrySize = _unicode ? 24 : 12;
            var nbtEntrySize = _unicode ? 32 : 16;
            var bbtLeaves = placed.Select(p =>
            {
                var e = new byte[bbtEntrySize];
                if (_unicode)
                {
                    BitConverter.GetBytes(p.Bid).CopyTo(e, 0);
                    BitConverter.GetBytes((ulong)p.Offset).CopyTo(e, 8);
                    BitConverter.GetBytes((ushort)p.Size).CopyTo(e, 16);
                    BitConverter.GetBytes((ushort)2).CopyTo(e, 18);
                }
                else
                {
                    BitConverter.GetBytes((uint)p.Bid).CopyTo(e, 0);
                    BitConverter.GetBytes((uint)p.Offset).CopyTo(e, 4);
                    BitConverter.GetBytes((ushort)p.Size).CopyTo(e, 8);
                    BitConverter.GetBytes((ushort)2).CopyTo(e, 10);
                }
                return (Key: p.Bid, Entry: e);
            }).ToList();
            var nbtLeaves = _nodes.Select(n =>
            {
                var e = new byte[nbtEntrySize];
                if (_unicode)
                {
                    BitConverter.GetBytes((ulong)n.Key).CopyTo(e, 0);
                    BitConverter.GetBytes(n.Value.Data).CopyTo(e, 8);
                    BitConverter.GetBytes(n.Value.Sub).CopyTo(e, 16);
                    BitConverter.GetBytes(n.Value.Parent).CopyTo(e, 24);
                }
                else
                {
                    BitConverter.GetBytes(n.Key).CopyTo(e, 0);
                    BitConverter.GetBytes((uint)n.Value.Data).CopyTo(e, 4);
                    BitConverter.GetBytes((uint)n.Value.Sub).CopyTo(e, 8);
                    BitConverter.GetBytes(n.Value.Parent).CopyTo(e, 12);
                }
                return (Key: (ulong)n.Key, Entry: e);
            }).ToList();
            var bbtRoot = Tree(output, bbtLeaves, bbtEntrySize, 0x80);
            var nbtRoot = Tree(output, nbtLeaves, nbtEntrySize, 0x81);

            var file = output.ToArray();
            Encoding.ASCII.GetBytes("!BDN").CopyTo(file, 0);
            file[8] = (byte)'S';
            file[9] = (byte)'M';
            BitConverter.GetBytes((ushort)(_unicode ? 23 : 14)).CopyTo(file, 10);
            if (_unicode)
            {
                BitConverter.GetBytes((ulong)nbtRoot).CopyTo(file, 224);
                BitConverter.GetBytes((ulong)bbtRoot).CopyTo(file, 240);
                file[0x201] = (byte)_crypt;
            }
            else
            {
                BitConverter.GetBytes((uint)nbtRoot).CopyTo(file, 188);
                BitConverter.GetBytes((uint)bbtRoot).CopyTo(file, 196);
                file[0x1CD] = (byte)_crypt;
            }
            return file;
        }

        private static long Align(MemoryStream s, int to)
        {
            while (s.Length % to != 0) s.WriteByte(0);
            s.Position = s.Length;
            return s.Length;
        }

        /// <summary>Index pages: leaves, and one branch page above them when there's more than one leaf.</summary>
        private long Tree(MemoryStream output, List<(ulong Key, byte[] Entry)> entries, int entrySize, byte type)
        {
            var perPage = (_unicode ? 488 : 496) / entrySize;
            var leaves = new List<(ulong Key, long Offset)>();
            for (var i = 0; i < Math.Max(1, entries.Count); i += perPage)
            {
                var chunk = entries.Skip(i).Take(perPage).ToList();
                leaves.Add((chunk.Count > 0 ? chunk[0].Key : 0, Page(output, chunk.Select(c => c.Entry).ToList(), entrySize, 0, type)));
            }
            if (leaves.Count == 1) return leaves[0].Offset;
            var branchSize = _unicode ? 24 : 12;
            var branch = leaves.Select(l =>
            {
                var e = new byte[branchSize];
                if (_unicode)
                {
                    BitConverter.GetBytes(l.Key).CopyTo(e, 0);
                    BitConverter.GetBytes((ulong)l.Offset).CopyTo(e, 16);
                }
                else
                {
                    BitConverter.GetBytes((uint)l.Key).CopyTo(e, 0);
                    BitConverter.GetBytes((uint)l.Offset).CopyTo(e, 8);
                }
                return e;
            }).ToList();
            if (branch.Count > (_unicode ? 488 : 496) / branchSize) throw new InvalidOperationException("too many pages for this test writer");
            return Page(output, branch, branchSize, 1, type);
        }

        private long Page(MemoryStream output, List<byte[]> entries, int entrySize, int level, byte type)
        {
            var page = new byte[512];
            for (var i = 0; i < entries.Count; i++) entries[i].CopyTo(page, i * entrySize);
            if (_unicode)
            {
                page[488] = (byte)entries.Count;
                page[489] = (byte)(488 / entrySize);
                page[490] = (byte)entrySize;
                page[491] = (byte)level;
                page[496] = type;
                page[497] = type;
            }
            else
            {
                page[496] = (byte)entries.Count;
                page[497] = (byte)(496 / entrySize);
                page[498] = (byte)entrySize;
                page[499] = (byte)level;
                page[500] = type;
                page[501] = type;
            }
            var at = Align(output, 512);
            output.Write(page, 0, page.Length);
            return at;
        }
    }
}
