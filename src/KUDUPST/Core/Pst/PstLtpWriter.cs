using System;
using System.Collections.Generic;
using System.Linq;

namespace Kudupst.Core.Pst
{
    /// <summary>
    /// Collects what belongs to one node while it's being written: the values too big for its heap, its tables and
    /// (for a message) its attachments. They become the node's sub-nodes.
    /// </summary>
    public sealed class NodeWriter
    {
        private readonly List<(uint Nid, ulong Data, ulong Sub)> _subs = new List<(uint, ulong, ulong)>();
        private uint _nextValue = 1;

        public PstBuilder File { get; }

        public NodeWriter(PstBuilder file)
        {
            File = file;
        }

        /// <summary>Stores a big value as a sub-node and returns its ID (MS-PST: an "HNID" that isn't in the heap).</summary>
        public uint AddValue(byte[] data) => AddValue(new[] { data });

        public uint AddValue(IList<byte[]> pieces)
        {
            var nid = (_nextValue++ << 5) | 0x1F;
            _subs.Add((nid, File.Data(pieces), 0));
            return nid;
        }

        public void Add(uint nid, ulong data, ulong sub = 0) => _subs.Add((nid, data, sub));

        /// <summary>Writes the list of sub-nodes; 0 when there are none.</summary>
        public ulong Finish() => File.SubNodes(_subs);
    }

    /// <summary>
    /// Builds a heap (MS-PST 2.3.1, "heap-on-node"): many small items packed into blocks, each reached by an ID that
    /// says which block and which item. Property lists and tables are both kept in one.
    /// </summary>
    public sealed class HeapWriter
    {
        public const int MaxItem = 3580; // the biggest item a heap takes; bigger values go into sub-nodes

        private readonly List<List<byte[]>> _pages = new List<List<byte[]>> { new List<byte[]>() };
        private int _lastSize;

        private static int HeaderSize(int page) => page == 0 ? 12 : page == 8 || (page > 8 && (page - 8) % 128 == 0) ? 66 : 2;

        /// <summary>What a page takes in its block: its header, its items, a spare byte to keep the map on an even place, and the map.</summary>
        private static int PageSize(int page, int itemBytes, int items)
        {
            var body = HeaderSize(page) + itemBytes;
            return body + (body & 1) + 4 + 2 * (items + 1);
        }

        /// <summary>Adds an item and returns its ID.</summary>
        public uint Add(byte[] item)
        {
            if (item.Length > MaxItem) throw new ArgumentException("Too big for a heap: " + item.Length);
            var page = _pages.Count - 1;
            var items = _pages[page];
            if (items.Count >= 2047 || PageSize(page, _lastSize + item.Length, items.Count + 1) > PstBuilder.MaxBlockData)
            {
                _pages.Add(new List<byte[]>());
                page++;
                items = _pages[page];
                _lastSize = 0;
            }
            items.Add(item);
            _lastSize += item.Length;
            return ((uint)page << 16) | ((uint)items.Count << 5);
        }

        /// <summary>Puts other bytes of the same length where an item is (for filling in a pointer once it's known).</summary>
        public void Replace(uint hid, byte[] item)
        {
            var items = _pages[(int)(hid >> 16)];
            var index = (int)((hid >> 5) & 0x7FF) - 1;
            if (items[index].Length != item.Length) throw new ArgumentException("Replace keeps the length.");
            items[index] = item;
        }

        /// <summary>The heap as blocks, one for each page.</summary>
        public List<byte[]> Build(byte clientSignature, uint userRoot)
        {
            var blocks = new List<byte[]>();
            var levels = new List<int>();
            for (var p = 0; p < _pages.Count; p++)
            {
                var items = _pages[p];
                var bytes = items.Sum(i => i.Length);
                var block = new byte[PageSize(p, bytes, items.Count)];
                var at = HeaderSize(p);
                var starts = new List<int>();
                foreach (var item in items)
                {
                    starts.Add(at);
                    Buffer.BlockCopy(item, 0, block, at, item.Length);
                    at += item.Length;
                }
                starts.Add(at);
                var map = at + (at & 1);
                BitConverter.GetBytes((ushort)map).CopyTo(block, 0);
                BitConverter.GetBytes((ushort)items.Count).CopyTo(block, map);
                for (var i = 0; i < starts.Count; i++) BitConverter.GetBytes((ushort)starts[i]).CopyTo(block, map + 4 + i * 2);
                if (p == 0)
                {
                    block[2] = 0xEC;
                    block[3] = clientSignature;
                    BitConverter.GetBytes(userRoot).CopyTo(block, 4);
                }
                blocks.Add(block);
                levels.Add(FillLevel(PstBuilder.MaxBlockData - block.Length));
            }
            // How full each page is: four bits a page, kept in the first page (for pages 0 to 7) and in every 128th after the eighth.
            for (var p = 0; p < _pages.Count; p++)
            {
                int holder, slot, baseAt;
                if (p < 8) { holder = 0; slot = p; baseAt = 8; }
                else { holder = 8 + (p - 8) / 128 * 128; slot = p - holder; baseAt = 2; }
                blocks[holder][baseAt + slot / 2] |= (byte)(levels[p] << (slot % 2 == 0 ? 0 : 4));
            }
            return blocks;
        }

        /// <summary>MS-PST 2.3.1.2: a number from 0 (3584 bytes or more free) to 15 (less than 8 free).</summary>
        private static int FillLevel(int free)
        {
            int[] atLeast = { 3584, 2560, 2048, 1792, 1536, 1280, 1024, 768, 512, 256, 128, 64, 32, 16, 8 };
            for (var i = 0; i < atLeast.Length; i++) if (free >= atLeast[i]) return i;
            return 15;
        }

        /// <summary>
        /// Writes a sorted index into the heap (MS-PST 2.3.2, "BTree-on-heap") and returns the ID of its header. The
        /// header can be given a place beforehand (a property list's header is the first item of its heap).
        /// </summary>
        public uint Tree(int keySize, int dataSize, List<(byte[] Key, byte[] Data)> sorted, uint headerAt = 0)
        {
            uint root = 0;
            var levels = 0;
            if (sorted.Count > 0)
            {
                var size = keySize + dataSize;
                var perLeaf = Math.Max(1, MaxItem / size);
                var level = new List<(byte[] Key, uint Hid)>();
                for (var i = 0; i < sorted.Count; i += perLeaf)
                {
                    var some = sorted.Skip(i).Take(perLeaf).ToList();
                    var leaf = new byte[some.Count * size];
                    for (var k = 0; k < some.Count; k++)
                    {
                        Buffer.BlockCopy(some[k].Key, 0, leaf, k * size, keySize);
                        Buffer.BlockCopy(some[k].Data, 0, leaf, k * size + keySize, dataSize);
                    }
                    level.Add((some[0].Key, Add(leaf)));
                }
                var perBranch = Math.Max(2, MaxItem / (keySize + 4));
                while (level.Count > 1)
                {
                    var above = new List<(byte[] Key, uint Hid)>();
                    for (var i = 0; i < level.Count; i += perBranch)
                    {
                        var some = level.Skip(i).Take(perBranch).ToList();
                        var branch = new byte[some.Count * (keySize + 4)];
                        for (var k = 0; k < some.Count; k++)
                        {
                            Buffer.BlockCopy(some[k].Key, 0, branch, k * (keySize + 4), keySize);
                            BitConverter.GetBytes(some[k].Hid).CopyTo(branch, k * (keySize + 4) + keySize);
                        }
                        above.Add((some[0].Key, Add(branch)));
                    }
                    level = above;
                    levels++;
                }
                root = level[0].Hid;
            }
            var header = new byte[8];
            header[0] = 0xB5;
            header[1] = (byte)keySize;
            header[2] = (byte)dataSize;
            header[3] = (byte)levels;
            BitConverter.GetBytes(root).CopyTo(header, 4);
            if (headerAt == 0) return Add(header);
            Replace(headerAt, header);
            return headerAt;
        }

        /// <summary>Stores a value of any size: in the heap when it fits, otherwise as a sub-node. 0 for an empty value.</summary>
        public uint AddValue(NodeWriter node, byte[] value)
        {
            if (value == null || value.Length == 0) return 0;
            return value.Length <= MaxItem ? Add(value) : node.AddValue(value);
        }
    }

    /// <summary>A property to write: its ID, its type and its value as the bytes a PST keeps.</summary>
    public sealed class PropOut
    {
        public ushort Id;
        public ushort Type;
        public byte[] Value;

        public PropOut(ushort id, ushort type, byte[] value)
        {
            Id = id;
            Type = type;
            Value = value ?? new byte[0];
        }

        public static PropOut Int(ushort id, int v) => new PropOut(id, PropType.Int32, BitConverter.GetBytes(v));
        public static PropOut Bool(ushort id, bool v) => new PropOut(id, PropType.Boolean, new[] { (byte)(v ? 1 : 0) });
        public static PropOut Text(ushort id, string s) => new PropOut(id, PropType.Unicode, System.Text.Encoding.Unicode.GetBytes(s ?? string.Empty));
        public static PropOut Bytes(ushort id, byte[] data) => new PropOut(id, PropType.Binary, data);
        public static PropOut Time(ushort id, DateTime utc) => new PropOut(id, PropType.SysTime, BitConverter.GetBytes(utc.ToFileTimeUtc()));
        public static PropOut Long(ushort id, long v) => new PropOut(id, PropType.Int64, BitConverter.GetBytes(v));
    }

    public static class LtpWriter
    {
        /// <summary>True for the types whose value sits in the property entry itself (four bytes or fewer).</summary>
        private static bool Inline(ushort type) =>
            type == PropType.Int16 || type == PropType.Int32 || type == PropType.Float || type == PropType.Error || type == PropType.Boolean;

        /// <summary>
        /// Writes a property list (MS-PST 2.3.3, "property context") and returns the ID of its data. Values too big for
        /// the heap become sub-nodes of <paramref name="node"/>. A later property with the same ID replaces an earlier one.
        /// </summary>
        public static ulong Properties(NodeWriter node, IEnumerable<PropOut> props)
        {
            var heap = new HeapWriter();
            var header = heap.Add(new byte[8]); // the index's header comes first; filled in at the end
            var byId = new SortedDictionary<ushort, PropOut>();
            foreach (var p in props) if (p != null) byId[p.Id] = p;
            var records = new List<(byte[] Key, byte[] Data)>();
            foreach (var p in byId.Values)
            {
                var data = new byte[6];
                BitConverter.GetBytes(p.Type).CopyTo(data, 0);
                if (Inline(p.Type)) Buffer.BlockCopy(p.Value, 0, data, 2, Math.Min(4, p.Value.Length));
                else BitConverter.GetBytes(heap.AddValue(node, p.Value)).CopyTo(data, 2);
                records.Add((BitConverter.GetBytes(p.Id), data));
            }
            heap.Tree(2, 6, records, header);
            return node.File.Data(heap.Build(Heap.SignaturePropertyContext, header));
        }
    }

    /// <summary>
    /// Builds a table (MS-PST 2.3.4, "table context"): columns, and rows whose cells are raw property values. Used for a
    /// folder's list of sub-folders and of messages, and a message's recipients and attachments.
    /// </summary>
    public sealed class TableWriter
    {
        private readonly List<(ushort Id, ushort Type)> _columns = new List<(ushort, ushort)>();
        private readonly List<(uint RowId, Dictionary<ushort, byte[]> Cells)> _rows = new List<(uint, Dictionary<ushort, byte[]>)>();

        /// <summary>The columns, each an ID and a type. The row's own ID and version columns are added if missing.</summary>
        public TableWriter(IEnumerable<(ushort Id, ushort Type)> columns)
        {
            _columns.Add((PropId.LtpRowId, PropType.Int32));
            _columns.Add((PropId.LtpRowVer, PropType.Int32));
            foreach (var c in columns)
                if (!_columns.Any(x => x.Id == c.Id)) _columns.Add(c);
        }

        public int RowCount => _rows.Count;

        /// <summary>Adds a row. Cells whose column the table doesn't have are left out.</summary>
        public void AddRow(uint rowId, Dictionary<ushort, byte[]> cells)
        {
            _rows.Add((rowId, cells));
        }

        private static int CellSize(ushort type)
        {
            if (type == PropType.Boolean) return 1;
            if (type == PropType.Int16) return 2;
            if (PropType.IsFixed(type) && PropType.FixedSize(type) == 8) return 8;
            return 4; // a number, or the ID of where the value is kept
        }

        /// <summary>Writes the table and returns the ID of its data. Rows and big values that don't fit become sub-nodes of <paramref name="node"/>.</summary>
        public ulong Write(NodeWriter node)
        {
            // Where each column sits in a row: the row's ID and version, then the 4- and 8-byte cells, the 2-byte ones,
            // the 1-byte ones, and a bit for each column saying whether the row has a value there.
            var layout = new List<(ushort Id, ushort Type, int Offset, int Size, int Bit)>();
            var at = 0;
            var bit = 0;
            foreach (var size in new[] { 4, 8, 2, 1 })
            {
                foreach (var c in _columns.Where(c => CellSize(c.Type) == size))
                {
                    layout.Add((c.Id, c.Type, at, size, bit++));
                    at += size;
                }
                if (size == 8) EndOf(0, at);
                if (size == 2) EndOf(1, at);
                if (size == 1) EndOf(2, at);
            }
            var bitmapAt = at;
            var rowSize = at + (_columns.Count + 7) / 8;

            var heap = new HeapWriter();
            var infoAt = heap.Add(new byte[22 + _columns.Count * 8]);

            var sortedRows = _rows.OrderBy(r => r.RowId).ToList();
            var matrix = new List<byte[]>();
            var perBlock = Math.Max(1, PstBuilder.MaxBlockData / rowSize);
            byte[] block = null;
            var index = new List<(byte[] Key, byte[] Data)>();
            for (var r = 0; r < sortedRows.Count; r++)
            {
                if (r % perBlock == 0)
                {
                    block = new byte[Math.Min(perBlock, sortedRows.Count - r) * rowSize];
                    matrix.Add(block);
                }
                var rowAt = r % perBlock * rowSize;
                var cells = new Dictionary<ushort, byte[]>(sortedRows[r].Cells)
                {
                    [PropId.LtpRowId] = BitConverter.GetBytes(sortedRows[r].RowId)
                };
                if (!cells.ContainsKey(PropId.LtpRowVer)) cells[PropId.LtpRowVer] = BitConverter.GetBytes(1);
                foreach (var c in layout)
                {
                    if (!cells.TryGetValue(c.Id, out var value) || value == null) continue;
                    if (c.Size == 4 && !(PropType.IsFixed(c.Type) && PropType.FixedSize(c.Type) <= 4))
                    {
                        var hnid = heap.AddValue(node, value);
                        if (hnid == 0 && c.Type != PropType.Unicode && c.Type != PropType.String8 && c.Type != PropType.Binary) continue;
                        BitConverter.GetBytes(hnid).CopyTo(block, rowAt + c.Offset);
                    }
                    else
                    {
                        Buffer.BlockCopy(value, 0, block, rowAt + c.Offset, Math.Min(c.Size, value.Length));
                    }
                    block[rowAt + bitmapAt + c.Bit / 8] |= (byte)(1 << (7 - c.Bit % 8));
                }
                index.Add((BitConverter.GetBytes(sortedRows[r].RowId), BitConverter.GetBytes((uint)r)));
            }

            var rowIndex = heap.Tree(4, 4, index);
            uint rowsAt = 0;
            if (matrix.Count == 1 && matrix[0].Length <= HeapWriter.MaxItem) rowsAt = heap.Add(matrix[0]);
            else if (matrix.Count > 0) rowsAt = node.AddValue(matrix);

            var info = new byte[22 + _columns.Count * 8];
            info[0] = 0x7C;
            info[1] = (byte)_columns.Count;
            BitConverter.GetBytes((ushort)_ends[0]).CopyTo(info, 2);
            BitConverter.GetBytes((ushort)_ends[1]).CopyTo(info, 4);
            BitConverter.GetBytes((ushort)_ends[2]).CopyTo(info, 6);
            BitConverter.GetBytes((ushort)rowSize).CopyTo(info, 8);
            BitConverter.GetBytes(rowIndex).CopyTo(info, 10);
            BitConverter.GetBytes(rowsAt).CopyTo(info, 14);
            var k = 0;
            foreach (var c in layout.OrderBy(c => ((uint)c.Id << 16) | c.Type))
            {
                var d = 22 + k++ * 8;
                BitConverter.GetBytes(((uint)c.Id << 16) | c.Type).CopyTo(info, d);
                BitConverter.GetBytes((ushort)c.Offset).CopyTo(info, d + 4);
                info[d + 6] = (byte)c.Size;
                info[d + 7] = (byte)c.Bit;
            }
            heap.Replace(infoAt, info);
            return node.File.Data(heap.Build(Heap.SignatureTableContext, infoAt));
        }

        private readonly int[] _ends = new int[3];
        private void EndOf(int group, int at) => _ends[group] = at;
    }
}
