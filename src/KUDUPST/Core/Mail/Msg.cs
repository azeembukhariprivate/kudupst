using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Pst;

namespace Kudupst.Core.Mail
{
    /// <summary>
    /// A compound file (MS-CFB), the container .msg files use: a little file system of storages (folders) and streams
    /// (files) inside one file.
    /// </summary>
    public sealed class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint NoStream = 0xFFFFFFFF;

        private readonly byte[] _data;
        private readonly int _sectorSize, _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly List<uint> _fat = new List<uint>();
        private readonly List<uint> _miniFat = new List<uint>();
        private readonly byte[] _miniStream;

        public sealed class Entry
        {
            public string Name { get; set; }
            public int Type { get; set; }
            public uint Left { get; set; }
            public uint Right { get; set; }
            public uint Child { get; set; }
            public uint Start { get; set; }
            public long Size { get; set; }
            public List<Entry> Children { get; } = new List<Entry>();

            public bool IsStorage => Type == 1 || Type == 5;
            public bool IsStream => Type == 2;

            public Entry Find(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            public override string ToString() => Name;
        }

        public Entry Root { get; }

        public static bool IsCompoundFile(byte[] data) =>
            data.Length >= 512 && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0 && data[4] == 0xA1 && data[5] == 0xB1 && data[6] == 0x1A && data[7] == 0xE1;

        public CompoundFile(byte[] data)
        {
            if (!IsCompoundFile(data)) throw new InvalidDataException("This isn't an Outlook .msg file.");
            _data = data;
            var shift = BitConverter.ToUInt16(data, 0x1E);
            if (shift != 9 && shift != 12) throw new InvalidDataException("This .msg file is damaged.");
            _sectorSize = 1 << shift;
            _miniSectorSize = 1 << BitConverter.ToUInt16(data, 0x20);
            var fatSectors = BitConverter.ToUInt32(data, 0x2C);
            var firstDirectory = BitConverter.ToUInt32(data, 0x30);
            _miniCutoff = BitConverter.ToUInt32(data, 0x38);
            var firstMiniFat = BitConverter.ToUInt32(data, 0x3C);
            var firstDifat = BitConverter.ToUInt32(data, 0x44);

            // The FAT's own sectors: 109 listed in the header, the rest in DIFAT sectors.
            var fatList = new List<uint>();
            for (var i = 0; i < 109 && fatList.Count < fatSectors; i++)
            {
                var s = BitConverter.ToUInt32(data, 0x4C + i * 4);
                if (s >= 0xFFFFFFFA) break;
                fatList.Add(s);
            }
            var difat = firstDifat;
            var guard = 0;
            while (difat < 0xFFFFFFFA && fatList.Count < fatSectors && guard++ < 100000)
            {
                var at = Offset(difat);
                var per = _sectorSize / 4 - 1;
                for (var i = 0; i < per && fatList.Count < fatSectors; i++)
                {
                    var s = BitConverter.ToUInt32(data, at + i * 4);
                    if (s < 0xFFFFFFFA) fatList.Add(s);
                }
                difat = BitConverter.ToUInt32(data, at + per * 4);
            }
            foreach (var s in fatList)
            {
                var at = Offset(s);
                for (var i = 0; i < _sectorSize / 4 && at + i * 4 + 4 <= data.Length; i++) _fat.Add(BitConverter.ToUInt32(data, at + i * 4));
            }

            var miniFatBytes = ReadChain(firstMiniFat, long.MaxValue);
            for (var i = 0; i + 4 <= miniFatBytes.Length; i += 4) _miniFat.Add(BitConverter.ToUInt32(miniFatBytes, i));

            var dirBytes = ReadChain(firstDirectory, long.MaxValue);
            var entries = new List<Entry>();
            for (var at = 0; at + 128 <= dirBytes.Length; at += 128)
            {
                var nameLength = BitConverter.ToUInt16(dirBytes, at + 0x40);
                var name = nameLength >= 2 ? Encoding.Unicode.GetString(dirBytes, at, Math.Min(64, (int)nameLength) - 2) : string.Empty;
                entries.Add(new Entry
                {
                    Name = name,
                    Type = dirBytes[at + 0x42],
                    Left = BitConverter.ToUInt32(dirBytes, at + 0x44),
                    Right = BitConverter.ToUInt32(dirBytes, at + 0x48),
                    Child = BitConverter.ToUInt32(dirBytes, at + 0x4C),
                    Start = BitConverter.ToUInt32(dirBytes, at + 0x74),
                    Size = shift == 9 ? BitConverter.ToUInt32(dirBytes, at + 0x78) : (long)BitConverter.ToUInt64(dirBytes, at + 0x78)
                });
            }
            if (entries.Count == 0 || entries[0].Type != 5) throw new InvalidDataException("This .msg file is damaged.");
            Root = entries[0];
            _miniStream = ReadChain(Root.Start, Root.Size);
            var visited = new HashSet<uint>();
            Link(Root, entries, visited);
        }

        private void Link(Entry storage, List<Entry> entries, HashSet<uint> visited)
        {
            // The children of a storage form a tree through Left/Right, starting at Child.
            var stack = new Stack<uint>();
            if (storage.Child != NoStream) stack.Push(storage.Child);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id >= entries.Count || !visited.Add(id)) continue;
                var e = entries[(int)id];
                storage.Children.Add(e);
                if (e.Left != NoStream) stack.Push(e.Left);
                if (e.Right != NoStream) stack.Push(e.Right);
                if (e.IsStorage) Link(e, entries, visited);
            }
        }

        private int Offset(uint sector)
        {
            var at = (long)(sector + 1) * _sectorSize;
            if (at < 0 || at + _sectorSize > _data.Length + _sectorSize) throw new InvalidDataException("This .msg file is damaged.");
            return (int)at;
        }

        private byte[] ReadChain(uint start, long size)
        {
            var output = new MemoryStream();
            var sector = start;
            var guard = 0;
            while (sector < 0xFFFFFFFA && sector != EndOfChain && output.Length < size && guard++ < 10000000)
            {
                var at = Offset(sector);
                var count = (int)Math.Min(_sectorSize, Math.Min(size - output.Length, _data.Length - at));
                if (count <= 0) break;
                output.Write(_data, at, count);
                if (sector >= _fat.Count) break;
                sector = _fat[(int)sector];
            }
            return output.ToArray();
        }

        private byte[] ReadMiniChain(uint start, long size)
        {
            var output = new MemoryStream();
            var sector = start;
            var guard = 0;
            while (sector < 0xFFFFFFFA && output.Length < size && guard++ < 10000000)
            {
                var at = (long)sector * _miniSectorSize;
                var count = (int)Math.Min(_miniSectorSize, Math.Min(size - output.Length, _miniStream.Length - at));
                if (count <= 0) break;
                output.Write(_miniStream, (int)at, count);
                if (sector >= _miniFat.Count) break;
                sector = _miniFat[(int)sector];
            }
            return output.ToArray();
        }

        public byte[] Read(Entry stream)
        {
            if (stream == null || !stream.IsStream) return null;
            if (stream.Size == 0) return new byte[0];
            return stream.Size < _miniCutoff ? ReadMiniChain(stream.Start, stream.Size) : ReadChain(stream.Start, stream.Size);
        }
    }

    /// <summary>The properties of a storage in an .msg file (MS-OXMSG): the property stream plus a stream per big value.</summary>
    public sealed class MsgProperties : IPropertySource
    {
        private readonly Dictionary<ushort, (ushort Type, byte[] Raw)> _props = new Dictionary<ushort, (ushort, byte[])>();

        public Encoding Ansi { get; set; } = PropValue.DefaultAnsi;
        public CompoundFile.Entry Storage { get; }

        public MsgProperties(CompoundFile file, CompoundFile.Entry storage, int headerSize)
        {
            Storage = storage;
            var stream = file.Read(storage.Find("__properties_version1.0"));
            if (stream != null)
            {
                for (var at = headerSize; at + 16 <= stream.Length; at += 16)
                {
                    var tag = BitConverter.ToUInt32(stream, at);
                    var type = (ushort)tag;
                    var id = (ushort)(tag >> 16);
                    var size = PropType.FixedSize(type);
                    if (size > 0 && size <= 8 && (type & PropType.MultiFlag) == 0)
                    {
                        _props[id] = (type, PropValue.Slice(stream, at + 8, size));
                    }
                    else
                    {
                        var data = file.Read(storage.Find($"__substg1.0_{tag:X8}"));
                        if (data != null) _props[id] = (type, data);
                        else if (type == PropType.Object) _props[id] = (type, new byte[0]);
                    }
                }
            }
            // Values with a stream but no entry in the property stream (older writers).
            foreach (var child in storage.Children)
            {
                if (!child.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase) || child.Name.Length != 20) continue;
                if (!uint.TryParse(child.Name.Substring(12), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tag)) continue;
                var id = (ushort)(tag >> 16);
                if (_props.ContainsKey(id)) continue;
                var type = (ushort)tag;
                if (child.IsStream) _props[id] = (type, file.Read(child));
                else if (type == PropType.Object) _props[id] = (type, new byte[0]);
            }
        }

        public bool Has(ushort id) => _props.ContainsKey(id);

        public byte[] Raw(ushort id) => _props.TryGetValue(id, out var p) ? p.Raw : null;

        public object Get(ushort id)
        {
            if (!_props.TryGetValue(id, out var p) || p.Type == PropType.Object) return null;
            return PropValue.Decode(p.Type, p.Raw, Ansi);
        }
    }

    /// <summary>Reads an Outlook .msg file into a <see cref="MailItem"/>.</summary>
    public static class MsgReader
    {
        public static MailItem Read(string path) => Read(File.ReadAllBytes(path));

        public static MailItem Read(byte[] data)
        {
            var file = new CompoundFile(data);
            var item = ReadMessage(file, file.Root, 32, Names(file));
            if (item.Size == 0) item.Size = data.Length;
            return item;
        }

        /// <summary>The named-property table, kept once for the whole file (MS-OXMSG 2.2.3).</summary>
        private static NameMap Names(CompoundFile file)
        {
            var storage = file.Root.Find("__nameid_version1.0");
            if (storage == null) return NameMap.Empty;
            return NameMap.Parse(file.Read(storage.Find("__substg1.0_00020102")), file.Read(storage.Find("__substg1.0_00030102")),
                file.Read(storage.Find("__substg1.0_00040102")));
        }

        private static MailItem ReadMessage(CompoundFile file, CompoundFile.Entry storage, int headerSize, NameMap names)
        {
            var props = new MsgProperties(file, storage, headerSize);
            var codePage = props.Int(PropId.InternetCodepage);
            if (codePage == 0) codePage = props.Int(PropId.MessageCodepage);
            var enc = PstStore.EncodingFor(codePage);
            if (enc != null) props.Ansi = enc;

            var item = PstStore.Fields(props);
            foreach (var child in storage.Children.Where(c => c.IsStorage && c.Name.StartsWith("__recip_version1.0_", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
            {
                var r = new MsgProperties(file, child, 8) { Ansi = props.Ansi };
                var type = r.Int(PropId.RecipientType, 1) & 0x0F;
                item.Recipients.Add(new Recipient
                {
                    Kind = type == 2 ? RecipientKind.Cc : type == 3 ? RecipientKind.Bcc : RecipientKind.To,
                    MeetingType = type,
                    Response = r.Int(PropId.RecipientTrackStatus),
                    IsOrganizer = (r.Int(PropId.RecipientFlags) & 0x2) != 0,
                    Address = PstStore.Address(r.String(PropId.DisplayName), r.String(PropId.SmtpAddress), r.String(PropId.EmailAddress), r.String(PropId.AddrType))
                });
            }
            item.LoadBody = () => PstStore.Body(props, codePage);
            item.LoadAttachments = () =>
            {
                var list = new List<Attachment>();
                foreach (var child in storage.Children.Where(c => c.IsStorage && c.Name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
                {
                    var a = new MsgProperties(file, child, 8) { Ansi = props.Ansi };
                    var embeddedStorage = child.Find("__substg1.0_3701000D");
                    list.Add(PstStore.Attachment(a, () => embeddedStorage == null ? null : ReadMessage(file, embeddedStorage, 24, names)));
                }
                return list;
            };
            PstStore.Details(item, new Props(props, names), null);
            return item;
        }
    }
}
