using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudupst.Core.Pst
{
    /// <summary>A deleted item found in the file.</summary>
    public sealed class FoundItem
    {
        /// <summary>The ID KUDUPST uses for it (made up, so it can't be mixed up with the file's own items).</summary>
        public uint Id { get; set; }

        /// <summary>Its ID before it was deleted (0 when it was found without one).</summary>
        public uint OriginalNid { get; set; }

        /// <summary>The folder it was in, when that's known.</summary>
        public uint ParentNid { get; set; }

        public ulong DataBid { get; set; }
        public ulong SubBid { get; set; }

        /// <summary>Some of it couldn't be found: parts of the text, attachments or recipients may be missing.</summary>
        public bool Partial { get; set; }
    }

    /// <summary>
    /// Finds deleted items that are still inside a PST/OST file. Outlook doesn't wipe what's deleted: it marks the space
    /// as free, and the old copies of its index pages and the item's blocks stay there until they're written over (or
    /// the file is compacted). Every page and block is checked against its own checksum before it's used.
    /// <para>
    /// The quick look reads only the free space (as the file's map of used space lists it) and keeps the items that
    /// are complete. Looking deeper reads the whole file, also keeps items with missing pieces, and finds item
    /// contents whose index entry is gone too (these are always partial).
    /// </para>
    /// The file is only read.
    /// </summary>
    public sealed class Recovery
    {
        /// <summary>The made-up ID of the "Found deleted items" folder.</summary>
        public const uint FolderNid = 0xFFFFFFE2;

        private const uint FirstId = 0xF0000004;

        public bool Deep { get; private set; }
        public List<FoundItem> Items { get; } = new List<FoundItem>();
        public List<BlockRef> Blocks { get; } = new List<BlockRef>();

        public static bool IsFoundId(uint nid) => nid >= FirstId && nid != FolderNid && (nid & 0x1F) == 0x04;

        private static uint IdFor(int index) => FirstId + ((uint)index << 5);

        // ================================================================== finding

        /// <summary>
        /// Looks for deleted items. <paramref name="earlier"/> (a quick look's results) keeps their IDs the same, so
        /// what's already listed or indexed stays valid.
        /// </summary>
        public static Recovery Find(PstStore store, bool deep, Action<double> progress = null, CancellationToken cancel = default, Recovery earlier = null)
        {
            var file = store.File;
            var nodes = new List<NodeRef>();
            var blocks = new Dictionary<ulong, BlockRef>();
            var regions = deep ? null : file.FreeSpace();
            if (regions == null) regions = new List<(long, long)> { (0, file.EndOfData) };
            Scan(file, regions, nodes, blocks, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            file.AddFoundBlocks(blocks.Values);

            var live = LiveItems(store);
            var result = new Recovery { Deep = deep };
            var used = new HashSet<ulong>();
            var keys = new HashSet<string>();
            if (earlier != null)
            {
                foreach (var f in earlier.Items)
                {
                    result.Items.Add(f);
                    used.Add(f.DataBid);
                    var key = KeyOf(store, f.DataBid, f.SubBid);
                    if (key != null) keys.Add(key);
                }
            }

            // Items whose index entry is still in an old index page.
            var groups = nodes.Where(n => (n.Nid & 0x1F) == 0x04 && !file.TryGetNode(n.Nid, out _))
                .GroupBy(n => n.Nid).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
            // Items still in the index, but in no folder.
            groups.InsertRange(0, live.Orphans.Select(n => new List<NodeRef> { n }));
            foreach (var group in groups)
            {
                cancel.ThrowIfCancellationRequested();
                FoundItem best = null;
                foreach (var version in group.OrderByDescending(n => n.DataBid).GroupBy(n => (n.DataBid, n.SubBid)).Select(g => g.First()))
                {
                    if (used.Contains(version.DataBid & ~1UL)) { best = null; break; }
                    var check = Check(store, version.DataBid, version.SubBid);
                    if (check == null) continue;
                    var found = new FoundItem { OriginalNid = version.Nid, ParentNid = version.ParentNid, DataBid = version.DataBid & ~1UL, SubBid = version.SubBid & ~1UL, Partial = !check.Value };
                    if (best == null || (best.Partial && !found.Partial)) best = found;
                    if (!found.Partial) break;
                }
                if (best == null || (best.Partial && !deep)) continue;
                var key = KeyOf(store, best.DataBid, best.SubBid);
                if (key == null || live.Keys.Contains(key) || !keys.Add(key)) continue;
                used.Add(best.DataBid);
                result.Items.Add(best);
            }

            // Deeper: item contents with no index entry left, from the first block of their properties.
            if (deep)
            {
                var referenced = new HashSet<ulong>();
                foreach (var f in result.Items) CollectBids(file, f.DataBid, f.SubBid, referenced);
                foreach (var block in blocks.Values.OrderByDescending(b => b.Bid))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (PstFile.IsInternal(block.Bid) || referenced.Contains(block.Bid) || file.IsListed(block.Bid)) continue;
                    if (!LooksLikeItem(file, block.Bid)) continue;
                    var key = KeyOf(store, block.Bid, 0);
                    if (key == null || live.Keys.Contains(key) || !keys.Add(key)) continue;
                    result.Items.Add(new FoundItem { DataBid = block.Bid, Partial = true });
                }
            }

            for (var i = 0; i < result.Items.Count; i++) result.Items[i].Id = IdFor(i);
            // Keep only the blocks the items use.
            var needed = new HashSet<ulong>();
            foreach (var f in result.Items) CollectBids(file, f.DataBid, f.SubBid, needed);
            result.Blocks.AddRange(blocks.Values.Where(b => needed.Contains(b.Bid)));
            if (earlier != null)
                foreach (var b in earlier.Blocks)
                    if (!blocks.ContainsKey(b.Bid)) result.Blocks.Add(b);
            progress?.Invoke(1);
            return result;
        }

        /// <summary>Reads through the given parts of the file, collecting old node-index pages and checked blocks.</summary>
        private static void Scan(PstFile file, List<(long Offset, long Length)> regions, List<NodeRef> nodes, Dictionary<ulong, BlockRef> blocks,
            Action<double> progress, CancellationToken cancel)
        {
            const int chunk = 4 << 20;
            var back = file.MaxBlockData + file.BlockTrailerSize + file.BlockUnit;
            var unit = file.BlockUnit;
            var total = Math.Max(1, regions.Sum(r => r.Length));
            long done = 0;
            byte[] buffer = null;
            foreach (var (start, length) in regions)
            {
                var end = start + length;
                for (var pos = start; pos < end; pos += chunk)
                {
                    cancel.ThrowIfCancellationRequested();
                    var len = (int)Math.Min(chunk, end - pos);
                    var from = Math.Max(start, pos - back);
                    var to = Math.Min(end, pos + len + file.PageSize);
                    var size = (int)(to - from);
                    if (buffer == null || buffer.Length < size) buffer = new byte[size];
                    var read = file.ReadRaw(from, buffer, size);
                    if (read < size) Array.Clear(buffer, read, size - read);

                    // Index pages starting in this piece.
                    for (var p = (pos + 511) / 512 * 512; p < pos + len; p += 512)
                    {
                        var at = (int)(p - from);
                        if (at + file.PageSize > size) break;
                        file.TryNodePage(buffer, at, nodes);
                    }
                    // Blocks ending in this piece.
                    for (var e = (pos + 1 + unit - 1) / unit * unit; e <= pos + len; e += unit)
                    {
                        var at = (int)(e - from);
                        if (at > size) break;
                        if (file.TryBlockEndingAt(buffer, from, at, 0, out var block) && !blocks.ContainsKey(block.Bid))
                            blocks[block.Bid] = block;
                    }
                    done += len;
                    progress?.Invoke(Math.Min(0.99, (double)done / total));
                }
            }
        }

        private sealed class Live
        {
            public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal);
            public List<NodeRef> Orphans { get; } = new List<NodeRef>();
        }

        /// <summary>
        /// What's not deleted: every item listed in a folder (so an old copy of one, left when it was changed, isn't
        /// shown as deleted), and items still in the index but in no folder's list.
        /// </summary>
        private static Live LiveItems(PstStore store)
        {
            var live = new Live();
            var listed = new HashSet<uint>();
            var messages = new List<NodeRef>();
            foreach (var node in store.File.AllNodes())
            {
                var type = node.Nid & 0x1F;
                if (type == 0x04) messages.Add(node);
                if (type != 0x0E) continue;
                try
                {
                    var table = new TableContext(NodeData.Of(store.File, node)) { Ansi = store.Ansi };
                    for (var i = 0; i < table.RowCount; i++)
                    {
                        listed.Add(table.RowId(i));
                        live.Keys.Add(Key(table.Get(i, PropId.MessageClass) as string, table.Get(i, PropId.Subject) as string,
                            table.Get(i, PropId.MessageDeliveryTime) as DateTime? ?? table.Get(i, PropId.ClientSubmitTime) as DateTime?));
                    }
                }
                catch (PstException)
                {
                    // A damaged list: its items are simply not counted as listed.
                }
            }
            foreach (var m in messages)
            {
                if (listed.Contains(m.Nid)) continue;
                // Only where the folder is there and doesn't list it: other items (in system parts of an OST) are left alone.
                var folderTable = (m.ParentNid & ~0x1Fu) | 0x0E;
                if ((m.ParentNid & 0x1F) == 0x02 && store.File.TryGetNode(folderTable, out _)) live.Orphans.Add(m);
            }
            return live;
        }

        private static string Key(string messageClass, string subject, DateTime? date) =>
            (messageClass ?? string.Empty).ToUpperInvariant() + "|" + PstStore.CleanSubject(subject) + "|" + (date?.Ticks.ToString() ?? string.Empty);

        /// <summary>What an item is (kind, subject, date), to tell copies of the same item apart from others; null if it isn't one.</summary>
        private static string KeyOf(PstStore store, ulong dataBid, ulong subBid)
        {
            try
            {
                var pc = new PropertyContext(new NodeData(store.File, dataBid, subBid) { Lenient = true }) { Ansi = store.Ansi };
                var cls = pc.String(PropId.MessageClass);
                if (!IsShownClass(cls)) return null;
                // Something to show: a title, a sender or a date (else it's an empty leftover).
                var date = pc.Time(PropId.MessageDeliveryTime) ?? pc.Time(PropId.ClientSubmitTime);
                if (string.IsNullOrWhiteSpace(pc.String(PropId.Subject)) && string.IsNullOrWhiteSpace(pc.String(PropId.SenderName)) &&
                    string.IsNullOrWhiteSpace(pc.String(PropId.DisplayName)) && !date.HasValue) return null;
                return Key(cls, pc.String(PropId.Subject), pc.Time(PropId.MessageDeliveryTime) ?? pc.Time(PropId.ClientSubmitTime));
            }
            catch (Exception ex) when (ex is PstException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>Items people see (not Outlook's hidden settings and rules).</summary>
        private static bool IsShownClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            var c = cls.ToUpperInvariant();
            if (!c.StartsWith("IPM") && !c.StartsWith("REPORT.")) return false;
            return !c.StartsWith("IPM.MICROSOFT") && !c.StartsWith("IPM.CONFIGURATION") && !c.StartsWith("IPM.RULEORGANIZER") &&
                   !c.StartsWith("IPM.EXTENDEDRULE") && !c.StartsWith("IPM.ABCHPERSON") && !c.StartsWith("IPM.AGGREGATION") &&
                   !c.StartsWith("IPM.NETWORK") && !c.StartsWith("IPM.CONVERSATION") && !c.StartsWith("IPM.OLE.CLASS");
        }

        /// <summary>A block that starts an item's properties (not a folder's or an attachment's).</summary>
        private static bool LooksLikeItem(PstFile file, ulong bid)
        {
            try
            {
                var data = file.ReadBlock(bid);
                if (data.Length < 12 || data[2] != 0xEC || data[3] != Heap.SignaturePropertyContext) return false;
                var pc = new PropertyContext(new NodeData(file, bid, 0) { Lenient = true });
                return IsShownClass(pc.String(PropId.MessageClass)) && !pc.Has(PropId.AttachMethod);
            }
            catch (Exception ex) when (ex is PstException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>Whether an item can be read: null if not at all, true if every part of it is there, false if some are missing.</summary>
        private static bool? Check(PstStore store, ulong dataBid, ulong subBid)
        {
            try
            {
                var pc = new PropertyContext(new NodeData(store.File, dataBid, subBid) { Lenient = true }) { Ansi = store.Ansi };
                if (!IsShownClass(pc.String(PropId.MessageClass))) return null;
            }
            catch (Exception ex) when (ex is PstException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                return null;
            }
            try
            {
                var bids = new HashSet<ulong>();
                CollectBids(store.File, dataBid, subBid, bids, strict: true);
                return true;
            }
            catch (Exception ex) when (ex is PstException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>Every block an item uses: its data, its parts (recipients, attachments…) and theirs.</summary>
        private static void CollectBids(PstFile file, ulong dataBid, ulong subBid, HashSet<ulong> into, bool strict = false, int depth = 0)
        {
            if (depth > 8) return;
            try
            {
                Tree(file, dataBid, into, 0);
                if (subBid != 0)
                {
                    SubTree(file, subBid, into, 0);
                    foreach (var sub in file.ReadSubNodes(subBid).Values)
                        CollectBids(file, sub.DataBid, sub.SubBid, into, strict, depth + 1);
                }
            }
            catch (PstException) when (!strict)
            {
                // Missing parts are left out.
            }
        }

        private static void Tree(PstFile file, ulong bid, HashSet<ulong> into, int depth)
        {
            if (bid == 0 || depth > 3) return;
            into.Add(bid & ~1UL);
            var data = file.ReadBlock(bid);
            if (!PstFile.IsInternal(bid)) return;
            var count = BitConverter.ToUInt16(data, 2);
            var size = file.Unicode ? 8 : 4;
            for (var i = 0; i < count && 8 + (i + 1) * size <= data.Length; i++)
                Tree(file, file.Unicode ? BitConverter.ToUInt64(data, 8 + i * size) : BitConverter.ToUInt32(data, 8 + i * size), into, depth + 1);
        }

        private static void SubTree(PstFile file, ulong bid, HashSet<ulong> into, int depth)
        {
            if (bid == 0 || depth > 4) return;
            into.Add(bid & ~1UL);
            var data = file.ReadBlock(bid);
            if (data.Length < 4 || data[1] == 0) return;
            var count = BitConverter.ToUInt16(data, 2);
            var start = file.Unicode ? 8 : 4;
            var size = file.Unicode ? 16 : 8;
            for (var i = 0; i < count && start + (i + 1) * size <= data.Length; i++)
                SubTree(file, file.Unicode ? BitConverter.ToUInt64(data, start + i * size + 8) : BitConverter.ToUInt32(data, start + i * size + 4), into, depth + 1);
        }

        // ================================================================== remembering

        private const int CacheVersion = 1;

        /// <summary>Saves what was found (kept next to the search index, never next to the file).</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            var temp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(CacheVersion);
                w.Write(Deep);
                w.Write(Items.Count);
                foreach (var f in Items)
                {
                    w.Write(f.Id);
                    w.Write(f.OriginalNid);
                    w.Write(f.ParentNid);
                    w.Write(f.DataBid);
                    w.Write(f.SubBid);
                    w.Write(f.Partial);
                }
                w.Write(Blocks.Count);
                foreach (var b in Blocks)
                {
                    w.Write(b.Bid);
                    w.Write(b.Offset);
                    w.Write(b.Size);
                    w.Write(b.InflatedSize);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static Recovery Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != CacheVersion) return null;
                    var result = new Recovery { Deep = r.ReadBoolean() };
                    var count = r.ReadInt32();
                    for (var i = 0; i < count; i++)
                        result.Items.Add(new FoundItem { Id = r.ReadUInt32(), OriginalNid = r.ReadUInt32(), ParentNid = r.ReadUInt32(), DataBid = r.ReadUInt64(), SubBid = r.ReadUInt64(), Partial = r.ReadBoolean() });
                    count = r.ReadInt32();
                    for (var i = 0; i < count; i++)
                        result.Blocks.Add(new BlockRef { Bid = r.ReadUInt64(), Offset = r.ReadInt64(), Size = r.ReadInt32(), InflatedSize = r.ReadInt32() });
                    return result;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is EndOfStreamException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The quick look's results for a file, or the deeper look's when it was done: from the saved copy, else found now.</summary>
        public static Recovery LoadOrFind(PstStore store, string folder)
        {
            var deep = Load(System.IO.Path.Combine(folder, "found-deep.bin"));
            if (deep != null) return deep;
            var quickPath = System.IO.Path.Combine(folder, "found-quick.bin");
            var quick = Load(quickPath);
            if (quick != null) return quick;
            quick = Find(store, false);
            try { quick.Save(quickPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Found again next time.
            }
            return quick;
        }
    }
}
