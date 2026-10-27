using System;
using System.Collections.Generic;
using System.Text;

namespace Kudupst.Core.Pst
{
    /// <summary>A node's data and its sub-nodes: everything needed to read the values in it.</summary>
    public sealed class NodeData
    {
        private Dictionary<uint, SubNodeRef> _subNodes;
        private List<byte[]> _blocks;
        private readonly ulong _dataBid;

        public PstFile File { get; }
        public ulong SubBid { get; }

        public NodeData(PstFile file, ulong dataBid, ulong subBid)
        {
            File = file;
            _dataBid = dataBid;
            SubBid = subBid;
        }

        public static NodeData Of(PstFile file, NodeRef node) => new NodeData(file, node.DataBid, node.SubBid);

        public List<byte[]> Blocks => _blocks ?? (_blocks = File.ReadDataBlocks(_dataBid));

        public Dictionary<uint, SubNodeRef> SubNodes => _subNodes ?? (_subNodes = File.ReadSubNodes(SubBid));

        public bool TryGetSubNode(uint nid, out NodeData data)
        {
            data = null;
            if (SubBid == 0 || !SubNodes.TryGetValue(nid, out var sub)) return false;
            data = new NodeData(File, sub.DataBid, sub.SubBid);
            return true;
        }
    }

    /// <summary>
    /// A heap on a node (MS-PST 2.3.1): small items stored inside a node's blocks, found by a heap ID (HID).
    /// Property lists and tables are built on it.
    /// </summary>
    public sealed class Heap
    {
        public const byte SignaturePropertyContext = 0xBC;
        public const byte SignatureTableContext = 0x7C;

        public NodeData Node { get; }
        public byte ClientSignature { get; }
        public uint UserRoot { get; }

        public Heap(NodeData node)
        {
            Node = node;
            var blocks = node.Blocks;
            if (blocks.Count == 0 || blocks[0].Length < 12 || blocks[0][2] != 0xEC) throw new PstException("This Outlook data file is damaged: an item's contents are unreadable.");
            ClientSignature = blocks[0][3];
            UserRoot = BitConverter.ToUInt32(blocks[0], 4);
        }

        public static bool IsHid(uint hnid) => (hnid & 0x1F) == 0;

        /// <summary>The bytes of a heap item, or null when there's none.</summary>
        public byte[] Get(uint hid)
        {
            if (hid == 0) return null;
            var index = (int)((hid >> 5) & 0x7FF);
            var blockIndex = (int)(hid >> 16);
            var blocks = Node.Blocks;
            if (index == 0 || blockIndex >= blocks.Count) return null;
            var block = blocks[blockIndex];
            if (block.Length < 2) return null;
            var map = BitConverter.ToUInt16(block, 0);
            if (map + 4 > block.Length) return null;
            var count = BitConverter.ToUInt16(block, map);
            if (index > count) return null;
            var startAt = map + 4 + (index - 1) * 2;
            if (startAt + 4 > block.Length) return null;
            int start = BitConverter.ToUInt16(block, startAt), end = BitConverter.ToUInt16(block, startAt + 2);
            if (end < start || end > block.Length) return null;
            var item = new byte[end - start];
            Buffer.BlockCopy(block, start, item, 0, item.Length);
            return item;
        }

        /// <summary>The bytes behind a heap ID or a sub-node ID (big values live in a sub-node).</summary>
        public byte[] GetHnid(uint hnid)
        {
            if (hnid == 0) return null;
            if (IsHid(hnid)) return Get(hnid);
            return Node.TryGetSubNode(hnid, out var sub) ? Join(sub.Blocks) : null;
        }

        public static byte[] Join(List<byte[]> blocks)
        {
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

        /// <summary>The records of a B-tree on this heap (MS-PST 2.3.2): (key, data) pairs.</summary>
        public List<(byte[] Key, byte[] Data)> TreeRecords(uint headerHid)
        {
            var result = new List<(byte[], byte[])>();
            var header = Get(headerHid);
            if (header == null || header.Length < 8 || header[0] != 0xB5) throw new PstException("This Outlook data file is damaged: an index inside an item is unreadable.");
            int keySize = header[1], dataSize = header[2], levels = header[3];
            var root = BitConverter.ToUInt32(header, 4);
            if (root != 0) Records(root, levels, keySize, dataSize, result, 0);
            return result;
        }

        private void Records(uint hid, int level, int keySize, int dataSize, List<(byte[], byte[])> result, int depth)
        {
            var items = Get(hid);
            if (items == null || depth > 8) return;
            if (level == 0)
            {
                var size = keySize + dataSize;
                for (var at = 0; at + size <= items.Length; at += size)
                {
                    var key = new byte[keySize];
                    var data = new byte[dataSize];
                    Buffer.BlockCopy(items, at, key, 0, keySize);
                    Buffer.BlockCopy(items, at + keySize, data, 0, dataSize);
                    result.Add((key, data));
                }
            }
            else
            {
                var size = keySize + 4;
                for (var at = 0; at + size <= items.Length; at += size)
                    Records(BitConverter.ToUInt32(items, at + keySize), level - 1, keySize, dataSize, result, depth + 1);
            }
        }
    }

    /// <summary>A property list (MS-PST "property context"): the properties of a folder, message or attachment.</summary>
    public sealed class PropertyContext : IPropertySource
    {
        private readonly Heap _heap;
        private readonly Dictionary<ushort, (ushort Type, uint Value)> _entries = new Dictionary<ushort, (ushort, uint)>();

        public NodeData Node => _heap.Node;
        public Encoding Ansi { get; set; } = PropValue.DefaultAnsi;

        public PropertyContext(NodeData node)
        {
            _heap = new Heap(node);
            if (_heap.ClientSignature != Heap.SignaturePropertyContext) throw new PstException("This Outlook data file is damaged: an item's properties are unreadable.");
            foreach (var (key, data) in _heap.TreeRecords(_heap.UserRoot))
            {
                if (key.Length < 2 || data.Length < 6) continue;
                _entries[BitConverter.ToUInt16(key, 0)] = (BitConverter.ToUInt16(data, 0), BitConverter.ToUInt32(data, 2));
            }
        }

        public IEnumerable<ushort> Ids => _entries.Keys;

        public bool Has(ushort id) => _entries.ContainsKey(id);

        public ushort TypeOf(ushort id) => _entries.TryGetValue(id, out var e) ? e.Type : (ushort)0;

        /// <summary>The raw bytes of a property's value.</summary>
        public byte[] Raw(ushort id)
        {
            if (!_entries.TryGetValue(id, out var e)) return null;
            var type = e.Type;
            switch (type)
            {
                case PropType.Int16:
                case PropType.Int32:
                case PropType.Float:
                case PropType.Error:
                case PropType.Boolean:
                    return BitConverter.GetBytes(e.Value);
                default:
                    return _heap.GetHnid(e.Value);
            }
        }

        public object Get(ushort id)
        {
            if (!_entries.TryGetValue(id, out var e)) return null;
            if (e.Type == PropType.Object) return null; // see ObjectNid
            return PropValue.Decode(e.Type, Raw(id), Ansi);
        }

        /// <summary>For an embedded item (PtypObject): the sub-node that holds it.</summary>
        public uint ObjectNid(ushort id)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Type != PropType.Object) return 0;
            var data = _heap.GetHnid(e.Value);
            return data != null && data.Length >= 4 ? BitConverter.ToUInt32(data, 0) : 0;
        }
    }

    /// <summary>A table (MS-PST "table context"): the rows of a folder's contents, sub-folders, recipients or attachments.</summary>
    public sealed class TableContext
    {
        private readonly Heap _heap;
        private readonly int _rowSize, _bitmapAt;
        private readonly List<(byte[] Block, int Offset)> _rows = new List<(byte[], int)>();

        public Encoding Ansi { get; set; } = PropValue.DefaultAnsi;
        public List<(uint Tag, int Offset, int Size, int Bit)> Columns { get; } = new List<(uint, int, int, int)>();
        public int RowCount => _rows.Count;

        public TableContext(NodeData node)
        {
            _heap = new Heap(node);
            if (_heap.ClientSignature != Heap.SignatureTableContext) throw new PstException("This Outlook data file is damaged: a list of items is unreadable.");
            var info = _heap.Get(_heap.UserRoot);
            if (info == null || info.Length < 22 || info[0] != 0x7C) throw new PstException("This Outlook data file is damaged: a list of items is unreadable.");
            int columns = info[1];
            _bitmapAt = BitConverter.ToUInt16(info, 2 + 2 * 2);
            _rowSize = BitConverter.ToUInt16(info, 2 + 3 * 2);
            var rowsHnid = BitConverter.ToUInt32(info, 14);
            for (var i = 0; i < columns; i++)
            {
                var at = 22 + i * 8;
                if (at + 8 > info.Length) break;
                Columns.Add((BitConverter.ToUInt32(info, at), BitConverter.ToUInt16(info, at + 4), info[at + 6], info[at + 7]));
            }
            if (rowsHnid == 0 || _rowSize == 0) return;

            // Rows never cross a block, so each block holds as many whole rows as fit.
            List<byte[]> blocks;
            if (Heap.IsHid(rowsHnid))
            {
                var data = _heap.Get(rowsHnid);
                blocks = data == null ? new List<byte[]>() : new List<byte[]> { data };
            }
            else
            {
                blocks = node.TryGetSubNode(rowsHnid, out var sub) ? sub.Blocks : new List<byte[]>();
            }
            foreach (var block in blocks)
                for (var at = 0; at + _rowSize <= block.Length; at += _rowSize)
                    _rows.Add((block, at));
        }

        public bool HasColumn(ushort id) => Columns.Exists(c => (c.Tag >> 16) == id);

        /// <summary>A cell's value, or null when the row has none.</summary>
        public object Get(int row, ushort id)
        {
            var raw = Raw(row, id, out var type);
            return raw == null ? null : PropValue.Decode(type, raw, Ansi);
        }

        public byte[] Raw(int row, ushort id, out ushort type)
        {
            type = 0;
            var (block, offset) = _rows[row];
            foreach (var c in Columns)
            {
                if ((c.Tag >> 16) != id) continue;
                type = (ushort)c.Tag;
                // The cell-exists bitmap: one bit per column, highest bit first.
                var bitByte = offset + _bitmapAt + c.Bit / 8;
                if (bitByte >= block.Length || (block[bitByte] & (1 << (7 - c.Bit % 8))) == 0) return null;
                var at = offset + c.Offset;
                if (at + c.Size > block.Length) return null;
                if (PropType.IsFixed(type) && PropType.FixedSize(type) <= c.Size)
                {
                    var bytes = new byte[PropType.FixedSize(type)];
                    Buffer.BlockCopy(block, at, bytes, 0, bytes.Length);
                    return bytes;
                }
                if (c.Size != 4) return null;
                return _heap.GetHnid(BitConverter.ToUInt32(block, at));
            }
            return null;
        }

        public uint RowId(int row) => Get(row, PropId.LtpRowId) is int v ? (uint)v : 0;
    }
}
