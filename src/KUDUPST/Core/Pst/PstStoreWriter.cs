using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Mail;

namespace Kudupst.Core.Pst
{
    /// <summary>
    /// The named properties of the file being written (contact and calendar fields have no fixed ID: each file has a
    /// table saying which ID stands for which name). Hands out IDs and writes that table (MS-PST 2.4.7).
    /// </summary>
    public sealed class NameMapWriter
    {
        private const int Buckets = 251;
        private readonly List<Guid> _guids = new List<Guid>();
        private readonly Dictionary<(Guid, int), ushort> _byLid = new Dictionary<(Guid, int), ushort>();
        private readonly Dictionary<(Guid, string), ushort> _byName = new Dictionary<(Guid, string), ushort>();
        private readonly List<(uint Id, ushort Kind, ushort Index, uint Hash)> _entries = new List<(uint, ushort, ushort, uint)>();
        private readonly MemoryStream _strings = new MemoryStream();

        private ushort GuidIndex(Guid set)
        {
            if (set == PropSet.Mapi) return 1;
            if (set == PropSet.PublicStrings) return 2;
            var at = _guids.IndexOf(set);
            if (at < 0) { _guids.Add(set); at = _guids.Count - 1; }
            return (ushort)(at + 3);
        }

        /// <summary>The ID in the new file for a numbered property; 0 when the file can't take more.</summary>
        public ushort Id(Guid set, int lid)
        {
            if (_byLid.TryGetValue((set, lid), out var id)) return id;
            if (_entries.Count >= 0x7FFE) return 0;
            var kind = (ushort)(GuidIndex(set) << 1);
            var index = (ushort)_entries.Count;
            _entries.Add(((uint)lid, kind, index, (uint)lid));
            return _byLid[(set, lid)] = (ushort)(0x8000 + index);
        }

        /// <summary>The ID in the new file for a property with a name; 0 when the file can't take more.</summary>
        public ushort Id(Guid set, string name)
        {
            if (_byName.TryGetValue((set, name), out var id)) return id;
            if (_entries.Count >= 0x7FFE) return 0;
            var kind = (ushort)((GuidIndex(set) << 1) | 1);
            var index = (ushort)_entries.Count;
            var bytes = Encoding.Unicode.GetBytes(name);
            var at = (uint)_strings.Length;
            _strings.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            _strings.Write(bytes, 0, bytes.Length);
            while (_strings.Length % 4 != 0) _strings.WriteByte(0);
            _entries.Add((at, kind, index, Crc32.Plain(bytes)));
            return _byName[(set, name)] = (ushort)(0x8000 + index);
        }

        private static byte[] Entry(uint id, ushort kind, ushort index)
        {
            var e = new byte[8];
            BitConverter.GetBytes(id).CopyTo(e, 0);
            BitConverter.GetBytes(kind).CopyTo(e, 4);
            BitConverter.GetBytes(index).CopyTo(e, 6);
            return e;
        }

        public IEnumerable<PropOut> Properties()
        {
            var guids = new byte[_guids.Count * 16];
            for (var i = 0; i < _guids.Count; i++) _guids[i].ToByteArray().CopyTo(guids, i * 16);
            var entries = new byte[_entries.Count * 8];
            var buckets = new SortedDictionary<int, List<byte[]>>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                Entry(e.Id, e.Kind, e.Index).CopyTo(entries, i * 8);
                var bucket = (int)((e.Hash ^ e.Kind) % Buckets);
                if (!buckets.TryGetValue(bucket, out var list)) buckets[bucket] = list = new List<byte[]>();
                list.Add(Entry(e.Hash, e.Kind, e.Index));
            }
            yield return PropOut.Int(0x0001, Buckets);
            yield return PropOut.Bytes(PropId.NameIdGuids, guids);
            yield return PropOut.Bytes(PropId.NameIdEntries, entries);
            yield return PropOut.Bytes(PropId.NameIdStrings, _strings.ToArray());
            foreach (var b in buckets)
                yield return PropOut.Bytes((ushort)(0x1000 + b.Key), b.Value.SelectMany(x => x).ToArray());
        }
    }

    /// <summary>
    /// Writes a new Outlook data file (.pst) with folders and items: the "save as PST" export. Items from a PST or OST
    /// file are copied with every property they have; single emails (.msg, .eml) are written from what was read of them.
    /// The source files are only read.
    /// </summary>
    public sealed class PstStoreWriter : IDisposable
    {
        private const uint StoreNid = 0x21, NameMapNid = 0x61, RootNid = 0x122, TopNid = 0x8022, SearchRootNid = 0x8042, DeletedNid = 0x8062;
        private const uint RecipientTableNid = 0x692, AttachmentTableNid = 0x671;
        private const int MaxDepth = 16; // emails attached to emails attached to emails...

        private static readonly (ushort, ushort)[] HierarchyColumns =
        {
            (0x0E30, PropType.Binary), (0x0E33, PropType.Int64), (0x0E34, PropType.Binary), (0x0E38, PropType.Int32),
            (0x3001, PropType.Unicode), (0x3602, PropType.Int32), (0x3603, PropType.Int32), (0x360A, PropType.Boolean),
            (0x3613, PropType.Unicode), (0x6635, PropType.Int32), (0x6636, PropType.Int32)
        };

        private static readonly (ushort, ushort)[] ContentsColumns =
        {
            (0x0017, PropType.Int32), (0x001A, PropType.Unicode), (0x0036, PropType.Int32), (0x0037, PropType.Unicode),
            (0x0039, PropType.SysTime), (0x0042, PropType.Unicode), (0x0057, PropType.Boolean), (0x0058, PropType.Boolean),
            (0x0070, PropType.Unicode), (0x0071, PropType.Binary), (0x0E03, PropType.Unicode), (0x0E04, PropType.Unicode),
            (0x0E06, PropType.SysTime), (0x0E07, PropType.Int32), (0x0E08, PropType.Int32), (0x0E17, PropType.Int32),
            (0x0E30, PropType.Binary), (0x0E33, PropType.Int64), (0x0E34, PropType.Binary), (0x0E38, PropType.Int32),
            (0x0E3C, PropType.Binary), (0x0E3D, PropType.Binary), (0x1097, PropType.Int32), (0x3008, PropType.SysTime),
            (0x3013, PropType.Binary), (0x65C6, PropType.Int32)
        };

        private static readonly (ushort, ushort)[] AssocColumns =
        {
            (0x001A, PropType.Unicode), (0x0E07, PropType.Int32), (0x0E17, PropType.Int32), (0x3001, PropType.Unicode),
            (0x6800, PropType.Unicode), (0x6803, PropType.Boolean), (0x6805, (ushort)(PropType.MultiFlag | PropType.Int32)),
            (0x682F, PropType.Unicode), (0x7003, PropType.Int32), (0x7004, PropType.Binary), (0x7005, PropType.Binary),
            (0x7006, PropType.Unicode), (0x7007, PropType.Int32)
        };

        private static readonly (ushort, ushort)[] SearchContentsColumns =
        {
            (0x0017, PropType.Int32), (0x001A, PropType.Unicode), (0x0036, PropType.Int32), (0x0037, PropType.Unicode),
            (0x0042, PropType.Unicode), (0x0057, PropType.Boolean), (0x0058, PropType.Boolean), (0x0E03, PropType.Unicode),
            (0x0E04, PropType.Unicode), (0x0E05, PropType.Unicode), (0x0E06, PropType.SysTime), (0x0E07, PropType.Int32),
            (0x0E08, PropType.Int32), (0x0E17, PropType.Int32), (0x0E2A, PropType.Boolean), (0x3008, PropType.SysTime),
            (0x67F1, PropType.Int32)
        };

        private static readonly (ushort, ushort)[] AttachmentColumns =
        {
            (0x0E20, PropType.Int32), (0x3704, PropType.Unicode), (0x3705, PropType.Int32), (0x370B, PropType.Int32)
        };

        private static readonly (ushort, ushort)[] RecipientColumns =
        {
            (0x0C15, PropType.Int32), (0x0E0F, PropType.Boolean), (0x0FF9, PropType.Binary), (0x0FFE, PropType.Int32),
            (0x0FFF, PropType.Binary), (0x3001, PropType.Unicode), (0x3002, PropType.Unicode), (0x3003, PropType.Unicode),
            (0x300B, PropType.Binary), (0x3900, PropType.Int32), (0x39FF, PropType.Unicode), (0x3A40, PropType.Boolean)
        };

        private sealed class Folder
        {
            public uint Nid, Parent;
            public string Name = string.Empty, Class = string.Empty;
            public readonly List<Folder> Children = new List<Folder>();
            public readonly List<(uint Nid, Dictionary<ushort, byte[]> Cells)> Rows = new List<(uint, Dictionary<ushort, byte[]>)>();
            public int Unread;
        }

        /// <summary>A message ready to go into the file: its data, its parts, and its properties (for the folder's list).</summary>
        private struct Written
        {
            public ulong Data, Sub;
            public List<PropOut> Props;
            public long Size;
        }

        private readonly PstBuilder _file;
        private readonly string _name;
        private readonly Guid _key = Guid.NewGuid();
        private readonly NameMapWriter _names = new NameMapWriter();
        private readonly Dictionary<uint, Folder> _folders = new Dictionary<uint, Folder>();
        private bool _finished;

        /// <summary>How many items have been put in.</summary>
        public int Count { get; private set; }

        public PstStoreWriter(Stream output, string displayName, bool ownsStream = false)
        {
            _file = new PstBuilder(output, ownsStream);
            _name = string.IsNullOrWhiteSpace(displayName) ? "Personal Folders" : displayName;
            // The IDs every Outlook data file has, and where the IDs of new folders and messages start.
            foreach (var nid in new uint[] { StoreNid, NameMapNid, RootNid, 0x1E1, 0x201, 0x261, 0x281, 0x2A1, 0x321, 0x60D, 0x60E, 0x60F, 0x610, 0x671, 0x692, DeletedNid, (0xFFFFu << 5) | 4 })
                _file.Reserve(nid);
            // Outlook's own files always name some fields; other readers refuse a file whose list of names is empty.
            _names.Id(PropSet.PublicStrings, "Keywords");
            _names.Id(PropSet.Common, Lid.ReminderSet);
            Add(new Folder { Nid = RootNid, Parent = RootNid });
            Add(new Folder { Nid = TopNid, Parent = RootNid, Name = "Top of Personal Folders" });
            Add(new Folder { Nid = SearchRootNid, Parent = RootNid, Name = "Search Root" });
            Add(new Folder { Nid = DeletedNid, Parent = TopNid, Name = "Deleted Items", Class = "IPF.Note" });
        }

        public void Dispose() => _file.Dispose();

        /// <summary>How big the file is so far.</summary>
        public long Length => _file.Length;

        private void Add(Folder f)
        {
            _folders[f.Nid] = f;
            if (f.Nid != f.Parent) _folders[f.Parent].Children.Add(f);
        }

        // ================================================================== folders

        /// <summary>The top folder people see in Outlook; new folders go below it.</summary>
        public uint Top => TopNid;

        /// <summary>The folder with this name inside another, made when it isn't there yet.</summary>
        public uint FolderIn(uint parent, string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim();
            if (name.Length > 255) name = name.Substring(0, 255);
            var p = _folders[parent];
            var found = p.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found.Nid;
            var f = new Folder { Nid = _file.NewNid(2), Parent = parent, Name = name };
            Add(f);
            return f.Nid;
        }

        /// <summary>The folder at the end of a path of names below the top folder, making what's missing.</summary>
        public uint FolderAt(IEnumerable<string> path)
        {
            var at = TopNid;
            foreach (var name in path ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(name)) at = FolderIn(at, name);
            return at;
        }

        private static string ClassFor(string messageClass)
        {
            switch (ItemKinds.Of(messageClass))
            {
                case ItemKind.Contact:
                case ItemKind.DistributionList: return "IPF.Contact";
                case ItemKind.Appointment: return "IPF.Appointment";
                case ItemKind.Task: return "IPF.Task";
                case ItemKind.Note: return "IPF.StickyNote";
                case ItemKind.Journal: return "IPF.Journal";
                default: return "IPF.Note";
            }
        }

        // ================================================================== adding items

        /// <summary>Copies an item out of an opened PST or OST file, with every property it has.</summary>
        public void AddCopy(uint folder, PstStore source, NodeData data)
        {
            Put(folder, Copy(source, data, 0));
        }

        /// <summary>Writes an item from what was read of it (a single .msg or .eml email).</summary>
        public void Add(uint folder, MailItem item)
        {
            Put(folder, Build(item, 0));
        }

        private void Put(uint folderNid, Written w)
        {
            if (_finished) throw new InvalidOperationException("The file is already finished.");
            var folder = _folders[folderNid];
            var nid = _file.NewNid(4);
            _file.Node(nid, w.Data, w.Sub, folderNid);

            var cells = new Dictionary<ushort, byte[]>();
            foreach (var c in ContentsColumns)
            {
                var p = w.Props.LastOrDefault(x => x.Id == c.Item1 && x.Type == c.Item2);
                if (p != null) cells[c.Item1] = p.Value;
            }
            if (!cells.ContainsKey(PropId.MessageFlags)) cells[PropId.MessageFlags] = BitConverter.GetBytes(1);
            if (!cells.ContainsKey(PropId.MessageSize)) cells[PropId.MessageSize] = BitConverter.GetBytes((int)Math.Min(int.MaxValue, w.Size));
            if (!cells.ContainsKey(0x0E17)) cells[0x0E17] = BitConverter.GetBytes(0);
            folder.Rows.Add((nid, cells));
            if ((BitConverter.ToInt32(cells[PropId.MessageFlags], 0) & 1) == 0) folder.Unread++;
            if (folder.Class.Length == 0 && folder.Nid != TopNid)
            {
                var cls = w.Props.LastOrDefault(x => x.Id == PropId.MessageClass && x.Type == PropType.Unicode);
                folder.Class = ClassFor(cls == null ? "IPM.Note" : Encoding.Unicode.GetString(cls.Value));
            }
            Count++;
        }

        // ------------------------------------------------------------------ copying from a PST or OST file

        private static byte[] ToUnicode(byte[] raw, Encoding ansi) => Encoding.Unicode.GetBytes(ansi.GetString(raw).TrimEnd('\0'));

        /// <summary>A list of texts kept the old way (one byte a letter) as a list kept the new way.</summary>
        private static byte[] ToUnicodeList(byte[] raw, Encoding ansi)
        {
            if (raw.Length < 4) return new byte[4];
            var count = BitConverter.ToInt32(raw, 0);
            if (count < 0 || 4 + count * 4L > raw.Length) return new byte[4];
            var texts = new List<byte[]>();
            for (var i = 0; i < count; i++)
            {
                var from = BitConverter.ToInt32(raw, 4 + i * 4);
                var to = i + 1 < count ? BitConverter.ToInt32(raw, 8 + i * 4) : raw.Length;
                if (from < 0 || to < from || to > raw.Length) { texts.Add(new byte[0]); continue; }
                texts.Add(Encoding.Unicode.GetBytes(ansi.GetString(raw, from, to - from).TrimEnd('\0')));
            }
            var result = new byte[4 + count * 4 + texts.Sum(t => t.Length)];
            BitConverter.GetBytes(count).CopyTo(result, 0);
            var at = 4 + count * 4;
            for (var i = 0; i < count; i++)
            {
                BitConverter.GetBytes(at).CopyTo(result, 4 + i * 4);
                texts[i].CopyTo(result, at);
                at += texts[i].Length;
            }
            return result;
        }

        /// <summary>The ID a property gets in the new file: the same, or for a named property the new file's ID for that name. 0 when unknown.</summary>
        private ushort NewId(ushort id, NameMap names)
        {
            if (id < 0x8000 || id == 0xFFFF) return id;
            if (!names.TryGetName(id, out var set, out var lid, out var name)) return 0;
            return name != null ? _names.Id(set, name) : _names.Id(set, lid);
        }

        /// <summary>A value as the new file keeps it: text always two bytes a letter.</summary>
        private static bool Convert(ref ushort type, ref byte[] raw, Encoding ansi)
        {
            if (raw == null) return false;
            if (type == PropType.String8) { type = PropType.Unicode; raw = ToUnicode(raw, ansi); }
            else if (type == (PropType.MultiFlag | PropType.String8)) { type = (ushort)(PropType.MultiFlag | PropType.Unicode); raw = ToUnicodeList(raw, ansi); }
            var size = PropType.FixedSize(type);
            return size == 0 || raw.Length >= size;
        }

        /// <summary>Every property of a property list except the ones that point at other nodes.</summary>
        private List<PropOut> CopyProps(PropertyContext pc, NameMap names, Encoding ansi, ref long size)
        {
            var list = new List<PropOut>();
            foreach (var id in pc.Ids.ToList())
            {
                var type = pc.TypeOf(id);
                if (type == PropType.Object) continue;
                byte[] raw;
                try { raw = pc.Raw(id); }
                catch (PstException) { continue; } // a damaged value is left out
                var newId = NewId(id, names);
                if (newId == 0 || !Convert(ref type, ref raw, ansi)) continue;
                list.Add(new PropOut(newId, type, raw));
                size += raw.Length + 8;
            }
            return list;
        }

        private Written Copy(PstStore source, NodeData data, int depth)
        {
            var pc = new PropertyContext(data) { Ansi = source.Ansi };
            var codePage = pc.Int(PropId.InternetCodepage);
            if (codePage == 0) codePage = pc.Int(PropId.MessageCodepage);
            var ansi = (codePage > 0 ? PstStore.EncodingFor(codePage) : null) ?? source.Ansi;
            var names = source.Names;

            var w = new Written();
            var node = new NodeWriter(_file);
            w.Props = CopyProps(pc, names, ansi, ref w.Size);

            // Who it was sent to.
            TableContext recipients = null;
            if (data.TryGetSubNode(RecipientTableNid, out var recipientData))
            {
                try { recipients = new TableContext(recipientData) { Ansi = ansi }; }
                catch (PstException) { recipients = null; }
            }
            WriteTable(node, RecipientTableNid, CopyTable(recipients, RecipientColumns, names, ansi));

            // What's attached.
            TableContext attachments = null;
            if (data.TryGetSubNode(AttachmentTableNid, out var attachmentData))
            {
                try { attachments = new TableContext(attachmentData) { Ansi = ansi }; }
                catch (PstException) { attachments = null; }
            }
            if (attachments != null && attachments.RowCount > 0)
            {
                var table = new TableWriter(AttachmentColumns);
                for (var i = 0; i < attachments.RowCount; i++)
                {
                    var oldNid = attachments.RowId(i);
                    if (oldNid == 0 || !data.TryGetSubNode(oldNid, out var one)) continue;
                    try
                    {
                        var attPc = new PropertyContext(one) { Ansi = ansi };
                        var attNode = new NodeWriter(_file);
                        long attSize = 0;
                        var props = CopyProps(attPc, names, ansi, ref attSize);
                        if (attPc.TypeOf(PropId.AttachDataBinary) == PropType.Object)
                        {
                            var inner = attPc.ObjectNid(PropId.AttachDataBinary);
                            if (inner == 0 || !one.TryGetSubNode(inner, out var innerData)) continue;
                            uint newInner;
                            long innerSize;
                            if (attPc.Int(PropId.AttachMethod, 1) == 5)
                            {
                                if (depth >= MaxDepth) continue;
                                var email = Copy(source, innerData, depth + 1);
                                newInner = _file.NewNid(4);
                                attNode.Add(newInner, email.Data, email.Sub);
                                innerSize = email.Size;
                            }
                            else
                            {
                                // Something another program put in (a drawing, a sheet): kept as it is.
                                var blocks = innerData.Blocks;
                                newInner = attNode.AddValue(blocks);
                                innerSize = blocks.Sum(b => (long)b.Length);
                            }
                            props.Add(ObjectProp(PropId.AttachDataBinary, newInner, innerSize));
                            attSize += innerSize;
                        }
                        var attNid = _file.NewNid(5);
                        var attData = LtpWriter.Properties(attNode, props);
                        node.Add(attNid, attData, attNode.Finish());
                        table.AddRow(attNid, Cells(props, AttachmentColumns));
                        w.Size += attSize;
                    }
                    catch (PstException)
                    {
                        // A damaged attachment is left out.
                    }
                }
                if (table.RowCount > 0) WriteTable(node, AttachmentTableNid, table);
            }

            w.Data = LtpWriter.Properties(node, w.Props);
            w.Sub = node.Finish();
            return w;
        }

        private static PropOut ObjectProp(ushort id, uint nid, long size)
        {
            var value = new byte[8];
            BitConverter.GetBytes(nid).CopyTo(value, 0);
            BitConverter.GetBytes((uint)Math.Min(uint.MaxValue, size)).CopyTo(value, 4);
            return new PropOut(id, PropType.Object, value);
        }

        private static Dictionary<ushort, byte[]> Cells(List<PropOut> props, (ushort, ushort)[] columns)
        {
            var cells = new Dictionary<ushort, byte[]>();
            foreach (var c in columns)
            {
                var p = props.LastOrDefault(x => x.Id == c.Item1 && x.Type == c.Item2);
                if (p != null) cells[c.Item1] = p.Value;
            }
            return cells;
        }

        /// <summary>A table with the usual columns plus whatever others the table copied from has, and its rows.</summary>
        private TableWriter CopyTable(TableContext from, (ushort, ushort)[] usual, NameMap names, Encoding ansi)
        {
            if (from == null) return new TableWriter(usual);
            var columns = new List<(ushort Id, ushort Type)>(usual.Select(c => (c.Item1, c.Item2)));
            var take = new List<(ushort OldId, ushort NewId, ushort Type)>();
            foreach (var c in from.Columns)
            {
                var oldId = (ushort)(c.Tag >> 16);
                var type = (ushort)(c.Tag & 0xFFFF);
                if (oldId == PropId.LtpRowId || oldId == PropId.LtpRowVer || type == PropType.Object) continue;
                var newId = NewId(oldId, names);
                if (newId == 0) continue;
                if (type == PropType.String8) type = PropType.Unicode;
                else if (type == (PropType.MultiFlag | PropType.String8)) type = (ushort)(PropType.MultiFlag | PropType.Unicode);
                var known = columns.FindIndex(x => x.Id == newId);
                if (known >= 0 && columns[known].Type != type) continue; // the same field kept another way: left out
                if (known < 0) columns.Add((newId, type));
                take.Add((oldId, newId, type));
            }
            var table = new TableWriter(columns);
            var used = new HashSet<uint>();
            for (var i = 0; i < from.RowCount; i++)
            {
                var cells = new Dictionary<ushort, byte[]>();
                foreach (var c in take)
                {
                    byte[] raw;
                    ushort type;
                    try { raw = from.Raw(i, c.OldId, out type); }
                    catch (PstException) { continue; }
                    if (!Convert(ref type, ref raw, ansi) || type != c.Type) continue;
                    cells[c.NewId] = raw;
                }
                var rowId = from.RowId(i);
                if (!used.Add(rowId)) { rowId = (uint)i; while (!used.Add(rowId)) rowId++; }
                table.AddRow(rowId, cells);
            }
            return table;
        }

        /// <summary>Puts a table among a node's parts, with the table's own big values below it.</summary>
        private void WriteTable(NodeWriter node, uint nid, TableWriter table)
        {
            var own = new NodeWriter(_file);
            var data = table.Write(own);
            node.Add(nid, data, own.Finish());
        }

        // ------------------------------------------------------------------ writing from what was read (.msg, .eml)

        private static byte[] OneOffEntryId(string name, string address)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[4], 0, 4);
            ms.Write(new byte[] { 0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02 }, 0, 16);
            ms.Write(new byte[] { 0x00, 0x00, 0x01, 0x90 }, 0, 4); // version 0; Unicode texts, no rich text
            foreach (var s in new[] { name, "SMTP", address })
            {
                var b = Encoding.Unicode.GetBytes(s + "\0");
                ms.Write(b, 0, b.Length);
            }
            return ms.ToArray();
        }

        private static byte[] SearchKey(string address) => Encoding.ASCII.GetBytes("SMTP:" + address.ToUpperInvariant() + "\0");

        private static void AddPerson(List<PropOut> props, MailAddress a, ushort name, ushort type, ushort email, ushort smtp, ushort entryId, ushort searchKey)
        {
            if (a == null || a.IsEmpty) return;
            props.Add(PropOut.Text(name, a.Short));
            if (a.Address.Length == 0) return;
            props.Add(PropOut.Text(type, "SMTP"));
            props.Add(PropOut.Text(email, a.Address));
            props.Add(PropOut.Text(smtp, a.Address));
            props.Add(PropOut.Bytes(entryId, OneOffEntryId(a.Short, a.Address)));
            props.Add(PropOut.Bytes(searchKey, SearchKey(a.Address)));
        }

        /// <summary>Formatted text as Outlook keeps it, without squeezing it (MS-OXRTFCP: the "MELA" form).</summary>
        private static byte[] RtfContainer(string rtf)
        {
            var body = Encoding.GetEncoding(1252).GetBytes(rtf);
            var all = new byte[16 + body.Length];
            BitConverter.GetBytes(body.Length + 12).CopyTo(all, 0);
            BitConverter.GetBytes(body.Length).CopyTo(all, 4);
            BitConverter.GetBytes(0x414C454D).CopyTo(all, 8);
            body.CopyTo(all, 16);
            return all;
        }

        private Written Build(MailItem item, int depth)
        {
            var w = new Written();
            var node = new NodeWriter(_file);
            var p = new List<PropOut>();
            var body = item.Body;
            var attachments = item.Attachments;
            var from = item.From == null || item.From.IsEmpty ? item.Sender : item.From;
            var sender = item.Sender == null || item.Sender.IsEmpty ? from : item.Sender;

            p.Add(PropOut.Text(PropId.MessageClass, string.IsNullOrEmpty(item.MessageClass) ? "IPM.Note" : item.MessageClass));
            p.Add(PropOut.Text(PropId.Subject, item.Subject));
            p.Add(PropOut.Text(PropId.NormalizedSubject, item.Subject));
            p.Add(PropOut.Text(PropId.ConversationTopic, item.Subject));
            p.Add(PropOut.Int(PropId.Importance, item.Importance));
            p.Add(PropOut.Int(PropId.Sensitivity, item.Sensitivity));
            p.Add(PropOut.Int(PropId.MessageFlags, (item.Read ? 1 : 0) | (attachments.Any(a => !a.Inline && !a.Hidden) ? 0x10 : 0)));
            p.Add(PropOut.Int(0x0E17, 0));
            p.Add(PropOut.Bool(0x0E1B, attachments.Any(a => !a.Inline && !a.Hidden)));
            if (item.Sent.HasValue) p.Add(PropOut.Time(PropId.ClientSubmitTime, item.Sent.Value));
            var arrived = item.Received ?? item.Sent;
            if (arrived.HasValue)
            {
                p.Add(PropOut.Time(PropId.MessageDeliveryTime, arrived.Value));
                p.Add(PropOut.Time(PropId.CreationTime, arrived.Value));
                p.Add(PropOut.Time(PropId.LastModificationTime, arrived.Value));
            }
            AddPerson(p, from, PropId.SentRepresentingName, PropId.SentRepresentingAddrType, PropId.SentRepresentingEmail, PropId.SentRepresentingSmtpAddress, 0x0041, 0x003B);
            AddPerson(p, sender, PropId.SenderName, PropId.SenderAddrType, PropId.SenderEmail, PropId.SenderSmtpAddress, 0x0C19, 0x0C1D);
            p.Add(PropOut.Text(PropId.DisplayTo, item.Recipients.Count > 0 ? string.Join("; ", item.To.Select(a => a.Short)) : item.DisplayTo));
            p.Add(PropOut.Text(PropId.DisplayCc, item.Recipients.Count > 0 ? string.Join("; ", item.Cc.Select(a => a.Short)) : item.DisplayCc));
            p.Add(PropOut.Text(PropId.DisplayBcc, item.Recipients.Count > 0 ? string.Join("; ", item.Bcc.Select(a => a.Short)) : item.DisplayBcc));
            if (!string.IsNullOrEmpty(item.MessageId)) p.Add(PropOut.Text(PropId.InternetMessageId, item.MessageId));
            if (!string.IsNullOrEmpty(item.InReplyTo)) p.Add(PropOut.Text(PropId.InReplyTo, item.InReplyTo));
            if (!string.IsNullOrEmpty(item.References)) p.Add(PropOut.Text(PropId.References, item.References));
            if (!string.IsNullOrEmpty(item.Headers)) p.Add(PropOut.Text(PropId.TransportHeaders, item.Headers));
            if (item.Categories != null && item.Categories.Length > 0)
            {
                var id = _names.Id(PropSet.PublicStrings, "Keywords");
                if (id != 0) p.Add(new PropOut(id, (ushort)(PropType.MultiFlag | PropType.Unicode), TextList(item.Categories)));
            }

            var text = body.PlainText ?? string.Empty;
            p.Add(PropOut.Text(PropId.Body, text));
            if (body.HasHtml)
            {
                p.Add(PropOut.Bytes(PropId.Html, Encoding.UTF8.GetBytes(body.Html)));
                p.Add(PropOut.Int(PropId.InternetCodepage, 65001));
            }
            else if (!string.IsNullOrEmpty(body.Rtf))
            {
                p.Add(PropOut.Bytes(PropId.RtfCompressed, RtfContainer(body.Rtf)));
                p.Add(PropOut.Bool(0x0E1F, true));
            }

            // Who it was sent to.
            var recipients = new TableWriter(RecipientColumns.Concat(new[] { (PropId.SmtpAddress, PropType.Unicode) }));
            uint row = 0;
            foreach (var r in item.Recipients)
            {
                var a = r.Address ?? new MailAddress();
                var cells = new Dictionary<ushort, byte[]>
                {
                    [PropId.RecipientType] = BitConverter.GetBytes((int)r.Kind),
                    [0x0E0F] = new byte[] { 1 },
                    [0x0FFE] = BitConverter.GetBytes(6),
                    [PropId.DisplayName] = Encoding.Unicode.GetBytes(a.Short),
                    [0x3900] = BitConverter.GetBytes(0),
                    [0x3A40] = new byte[] { 0 }
                };
                if (a.Address.Length > 0)
                {
                    cells[PropId.AddrType] = Encoding.Unicode.GetBytes("SMTP");
                    cells[PropId.EmailAddress] = Encoding.Unicode.GetBytes(a.Address);
                    cells[PropId.SmtpAddress] = Encoding.Unicode.GetBytes(a.Address);
                    cells[0x0FFF] = OneOffEntryId(a.Short, a.Address);
                    cells[0x0FF9] = cells[0x0FFF];
                    cells[0x300B] = SearchKey(a.Address);
                }
                recipients.AddRow(row++, cells);
            }
            WriteTable(node, RecipientTableNid, recipients);

            // What's attached.
            var table = new TableWriter(AttachmentColumns);
            var number = 0;
            foreach (var a in attachments)
            {
                var ap = new List<PropOut>();
                var attNode = new NodeWriter(_file);
                var name = string.IsNullOrEmpty(a.FileName) ? "Attachment" : a.FileName;
                long size;
                if (a.IsEmail)
                {
                    if (depth >= MaxDepth) continue;
                    var inside = a.Embedded();
                    if (inside == null) continue;
                    var email = Build(inside, depth + 1);
                    var inner = _file.NewNid(4);
                    attNode.Add(inner, email.Data, email.Sub);
                    ap.Add(ObjectProp(PropId.AttachDataBinary, inner, email.Size));
                    ap.Add(PropOut.Int(PropId.AttachMethod, 5));
                    size = email.Size;
                }
                else
                {
                    var bytes = a.Data();
                    ap.Add(PropOut.Bytes(PropId.AttachDataBinary, bytes));
                    ap.Add(PropOut.Int(PropId.AttachMethod, 1));
                    size = bytes.Length;
                }
                ap.Add(PropOut.Int(PropId.AttachSize, (int)Math.Min(int.MaxValue, size)));
                ap.Add(PropOut.Text(PropId.AttachFilename, name));
                ap.Add(PropOut.Text(PropId.AttachLongFilename, name));
                ap.Add(PropOut.Text(PropId.DisplayName, name));
                var ext = Path.GetExtension(name);
                if (!string.IsNullOrEmpty(ext)) ap.Add(PropOut.Text(PropId.AttachExtension, ext));
                ap.Add(PropOut.Int(PropId.RenderingPosition, -1));
                ap.Add(PropOut.Int(0x0E21, number++));
                if (!string.IsNullOrEmpty(a.MimeType)) ap.Add(PropOut.Text(PropId.AttachMimeTag, a.MimeType));
                if (!string.IsNullOrEmpty(a.ContentId)) ap.Add(PropOut.Text(PropId.AttachContentId, a.ContentId));
                if (!string.IsNullOrEmpty(a.ContentLocation)) ap.Add(PropOut.Text(PropId.AttachContentLocation, a.ContentLocation));
                if (a.Inline) ap.Add(PropOut.Int(PropId.AttachFlags, 4));
                if (a.Hidden || a.Inline) ap.Add(PropOut.Bool(PropId.AttachmentHidden, true));
                if (a.IsContactPhoto) ap.Add(PropOut.Bool(PropId.AttachmentContactPhoto, true));

                var attNid = _file.NewNid(5);
                var attData = LtpWriter.Properties(attNode, ap);
                node.Add(attNid, attData, attNode.Finish());
                table.AddRow(attNid, Cells(ap, AttachmentColumns));
                w.Size += size;
            }
            if (table.RowCount > 0) WriteTable(node, AttachmentTableNid, table);

            w.Size += p.Sum(x => (long)x.Value.Length + 8);
            p.Add(PropOut.Int(PropId.MessageSize, (int)Math.Min(int.MaxValue, w.Size)));
            w.Props = p;
            w.Data = LtpWriter.Properties(node, p);
            w.Sub = node.Finish();
            return w;
        }

        private static byte[] TextList(string[] texts)
        {
            var parts = texts.Select(t => Encoding.Unicode.GetBytes(t ?? string.Empty)).ToList();
            var result = new byte[4 + parts.Count * 4 + parts.Sum(x => x.Length)];
            BitConverter.GetBytes(parts.Count).CopyTo(result, 0);
            var at = 4 + parts.Count * 4;
            for (var i = 0; i < parts.Count; i++)
            {
                BitConverter.GetBytes(at).CopyTo(result, 4 + i * 4);
                parts[i].CopyTo(result, at);
                at += parts[i].Length;
            }
            return result;
        }

        // ================================================================== finishing

        private byte[] EntryId(uint nid)
        {
            var id = new byte[24];
            _key.ToByteArray().CopyTo(id, 4);
            BitConverter.GetBytes(nid).CopyTo(id, 20);
            return id;
        }

        private void TopLevel(uint nid, IEnumerable<PropOut> props)
        {
            var node = new NodeWriter(_file);
            var data = LtpWriter.Properties(node, props);
            _file.Node(nid, data, node.Finish());
        }

        private void TopLevel(uint nid, TableWriter table)
        {
            var node = new NodeWriter(_file);
            var data = table.Write(node);
            _file.Node(nid, data, node.Finish());
        }

        /// <summary>Writes the folders, their lists and the file's own details, then closes the file's indexes. Nothing can be added afterwards.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            foreach (var f in _folders.Values)
            {
                var props = new List<PropOut>
                {
                    PropOut.Text(PropId.DisplayName, f.Name),
                    PropOut.Int(PropId.ContentCount, f.Rows.Count),
                    PropOut.Int(PropId.ContentUnreadCount, f.Unread),
                    PropOut.Bool(PropId.Subfolders, f.Children.Count > 0)
                };
                if (f.Class.Length > 0) props.Add(PropOut.Text(PropId.ContainerClass, f.Class));
                var node = new NodeWriter(_file);
                var data = LtpWriter.Properties(node, props);
                _file.Node(f.Nid, data, node.Finish(), f.Parent);

                var hierarchy = new TableWriter(HierarchyColumns);
                foreach (var c in f.Children)
                {
                    var cells = new Dictionary<ushort, byte[]>
                    {
                        [PropId.DisplayName] = Encoding.Unicode.GetBytes(c.Name),
                        [PropId.ContentCount] = BitConverter.GetBytes(c.Rows.Count),
                        [PropId.ContentUnreadCount] = BitConverter.GetBytes(c.Unread),
                        [PropId.Subfolders] = new[] { (byte)(c.Children.Count > 0 ? 1 : 0) }
                    };
                    if (c.Class.Length > 0) cells[PropId.ContainerClass] = Encoding.Unicode.GetBytes(c.Class);
                    hierarchy.AddRow(c.Nid, cells);
                }
                TopLevel((f.Nid & ~0x1Fu) | 0x0D, hierarchy);

                var contents = new TableWriter(ContentsColumns);
                foreach (var r in f.Rows) contents.AddRow(r.Nid, r.Cells);
                TopLevel((f.Nid & ~0x1Fu) | 0x0E, contents);
                f.Rows.Clear();

                TopLevel((f.Nid & ~0x1Fu) | 0x0F, new TableWriter(AssocColumns));
            }

            // The empty tables Outlook copies when it makes a new folder or message.
            TopLevel(0x60D, new TableWriter(HierarchyColumns));
            TopLevel(0x60E, new TableWriter(ContentsColumns));
            TopLevel(0x60F, new TableWriter(AssocColumns));
            TopLevel(0x610, new TableWriter(SearchContentsColumns));
            TopLevel(0x671, new TableWriter(AttachmentColumns));
            TopLevel(0x692, new TableWriter(RecipientColumns));

            // Outlook's own notes about searches: there, and empty.
            foreach (var nid in new uint[] { 0x1E1, 0x201, 0x261, 0x281, 0x2A1, 0x321 }) _file.Node(nid, 0);

            TopLevel(NameMapNid, _names.Properties());
            TopLevel(StoreNid, new[]
            {
                PropOut.Bytes(0x0FF9, _key.ToByteArray()),
                PropOut.Text(PropId.DisplayName, _name),
                PropOut.Int(0x35DF, 0x89), // which of the folders below are there: the top, Deleted Items, the search folder
                PropOut.Bytes(PropId.IpmSubtreeEntryId, EntryId(TopNid)),
                PropOut.Bytes(PropId.IpmWastebasketEntryId, EntryId(DeletedNid)),
                PropOut.Bytes(0x35E7, EntryId(SearchRootNid)),
                PropOut.Bool(0x6633, true),
                PropOut.Int(0x66FA, 0x000E0011),
                PropOut.Int(PropId.PstPassword, 0)
            });

            _file.Finish();
        }
    }
}
