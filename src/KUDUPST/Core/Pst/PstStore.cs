using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kudupst.Core.Mail;

namespace Kudupst.Core.Pst
{
    /// <summary>A folder in a PST/OST file.</summary>
    public sealed class PstFolder
    {
        public uint Nid { get; set; }
        public uint ParentNid { get; set; }
        public string Name { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public int UnreadCount { get; set; }
        public bool HasSubfolders { get; set; }
        public string ContainerClass { get; set; } = string.Empty;
        public bool IsSearchFolder => (Nid & 0x1F) == 0x03;

        public override string ToString() => Name;
    }

    /// <summary>One row of a folder's contents: enough to list an item without reading all of it.</summary>
    public sealed class ItemSummary
    {
        public uint Nid { get; set; }
        public uint FolderNid { get; set; }
        public string MessageClass { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public long Size { get; set; }
        public bool Read { get; set; } = true;
        public bool HasAttachments { get; set; }
        public int Importance { get; set; } = 1;

        // Calendar items: when and where (UTC).
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public bool AllDay { get; set; }
        public bool Recurring { get; set; }
        public string Location { get; set; } = string.Empty;

        // Contacts: how they're filed, their company and first email address.
        public string FileAs { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public ItemKind Kind => ItemKinds.Of(MessageClass);
    }

    /// <summary>
    /// The mail layer of a PST/OST file (MS-PST 2.4): the folder tree, the items in each folder, and full items with
    /// their recipients, bodies and attachments.
    /// </summary>
    public sealed class PstStore : IDisposable
    {
        public const uint StoreNid = 0x21;
        public const uint RootFolderNid = 0x122;
        private const uint NameMapNid = 0x61;
        private const uint RecipientTableNid = 0x692;
        private const uint AttachmentTableNid = 0x671;

        private readonly Dictionary<uint, PstFolder> _folders = new Dictionary<uint, PstFolder>();
        private readonly Dictionary<uint, List<uint>> _children = new Dictionary<uint, List<uint>>();
        private readonly object _sync = new object();

        public PstFile File { get; }
        public string DisplayName { get; }
        public uint PasswordCrc { get; }
        /// <summary>OST files have no password of their own (they're always marked with all bits set).</summary>
        public bool HasPassword => PasswordCrc != 0 && !File.IsOst;
        public uint TopNid { get; }
        public Encoding Ansi { get; }
        private NameMap _names;

        /// <summary>The file's table of named properties (contact and calendar fields).</summary>
        public NameMap Names
        {
            get
            {
                if (_names != null) return _names;
                var names = NameMap.Empty;
                try
                {
                    if (File.TryGetNode(NameMapNid, out var node))
                    {
                        var pc = new PropertyContext(NodeData.Of(File, node));
                        names = NameMap.Parse(pc.Raw(PropId.NameIdGuids), pc.Raw(PropId.NameIdEntries), pc.Raw(PropId.NameIdStrings));
                    }
                }
                catch (PstException)
                {
                    // Without it, contacts and calendar items show only their basic fields.
                }
                return _names = names;
            }
        }

        public PstStore(PstFile file)
        {
            File = file;
            Ansi = PropValue.DefaultAnsi;
            var store = new PropertyContext(NodeData.Of(file, file.GetNode(StoreNid))) { Ansi = Ansi };
            DisplayName = store.String(PropId.DisplayName) ?? string.Empty;
            PasswordCrc = (uint)store.Int(PropId.PstPassword);
            var subtree = store.Bytes(PropId.IpmSubtreeEntryId);
            TopNid = subtree != null && subtree.Length >= 24 ? BitConverter.ToUInt32(subtree, 20) : 0;
            if (TopNid == 0 || !file.TryGetNode(TopNid, out _)) TopNid = FindSubtree();
        }

        /// <summary>
        /// OST files don't say where the folders people see start: it's the IPM_SUBTREE folder of "Root - Mailbox"
        /// (not the one of "Root - Public").
        /// </summary>
        private uint FindSubtree()
        {
            try
            {
                var root = Folder(RootFolderNid);
                var level1 = SubFolders(root);
                foreach (var parent in level1.OrderBy(f => f.Name.IndexOf("Public", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0))
                {
                    var subtree = SubFolders(parent).FirstOrDefault(f => string.Equals(f.Name, "IPM_SUBTREE", StringComparison.OrdinalIgnoreCase));
                    if (subtree != null && subtree.HasSubfolders) return subtree.Nid;
                }
                var direct = level1.FirstOrDefault(f => string.Equals(f.Name, "IPM_SUBTREE", StringComparison.OrdinalIgnoreCase));
                if (direct != null) return direct.Nid;
            }
            catch (PstException)
            {
                // Fall back to the root folder.
            }
            return RootFolderNid;
        }

        public static PstStore Open(string path)
        {
            var file = PstFile.Open(path);
            try
            {
                return new PstStore(file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void Dispose() => File.Dispose();

        // ================================================================== password

        /// <summary>
        /// Checks a password against the one set in Outlook. Outlook keeps only a checksum (CRC-32) of it, and the
        /// emails themselves aren't scrambled with it.
        /// </summary>
        public bool CheckPassword(string password)
        {
            if (!HasPassword) return true;
            if (string.IsNullOrEmpty(password)) return false;
            foreach (var bytes in new[] { Ansi.GetBytes(password), Encoding.Unicode.GetBytes(password) })
            {
                if (Crc32.Standard(bytes) == PasswordCrc || Crc32.Plain(bytes) == PasswordCrc) return true;
            }
            return false;
        }

        // ================================================================== folders

        public PstFolder Root => Folder(RootFolderNid);

        /// <summary>The top of the folders people see in Outlook ("Top of Personal Folders", or IPM_SUBTREE in an OST).</summary>
        public PstFolder Top => Folder(TopNid);

        public PstFolder Folder(uint nid)
        {
            lock (_sync)
            {
                if (_folders.TryGetValue(nid, out var cached)) return cached;
            }
            var pc = new PropertyContext(NodeData.Of(File, File.GetNode(nid))) { Ansi = Ansi };
            File.TryGetNode(nid, out var node);
            var folder = new PstFolder
            {
                Nid = nid,
                ParentNid = node.ParentNid,
                Name = pc.String(PropId.DisplayName) ?? string.Empty,
                ItemCount = pc.Int(PropId.ContentCount),
                UnreadCount = pc.Int(PropId.ContentUnreadCount),
                HasSubfolders = pc.Bool(PropId.Subfolders),
                ContainerClass = pc.String(PropId.ContainerClass) ?? string.Empty
            };
            lock (_sync) _folders[nid] = folder;
            return folder;
        }

        /// <summary>A folder's sub-folders, in the order Outlook keeps them.</summary>
        public List<PstFolder> SubFolders(PstFolder folder)
        {
            List<uint> ids;
            lock (_sync) _children.TryGetValue(folder.Nid, out ids);
            if (ids == null)
            {
                ids = new List<uint>();
                var tableNid = (folder.Nid & ~0x1Fu) | 0x0D;
                if (File.TryGetNode(tableNid, out var tableNode))
                {
                    var table = new TableContext(NodeData.Of(File, tableNode)) { Ansi = Ansi };
                    for (var i = 0; i < table.RowCount; i++)
                    {
                        var id = table.RowId(i);
                        if (id != 0 && id != folder.Nid && File.TryGetNode(id, out _)) ids.Add(id);
                    }
                }
                lock (_sync) _children[folder.Nid] = ids;
            }
            var result = new List<PstFolder>();
            foreach (var id in ids)
            {
                try { result.Add(Folder(id)); }
                catch (PstException) { /* a damaged folder is left out */ }
            }
            return result;
        }

        /// <summary>A folder and everything below it, depth first.</summary>
        public IEnumerable<PstFolder> AllFolders(PstFolder start)
        {
            var stack = new Stack<PstFolder>();
            stack.Push(start);
            var seen = new HashSet<uint>();
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (!seen.Add(f.Nid)) continue;
                yield return f;
                var subs = SubFolders(f);
                for (var i = subs.Count - 1; i >= 0; i--) stack.Push(subs[i]);
            }
        }

        /// <summary>The path of folder names from the top folder down to a folder.</summary>
        public List<PstFolder> PathOf(uint nid)
        {
            var path = new List<PstFolder>();
            var seen = new HashSet<uint>();
            while (nid != 0 && seen.Add(nid))
            {
                PstFolder f;
                try { f = Folder(nid); }
                catch (PstException) { break; }
                path.Insert(0, f);
                if (nid == TopNid || nid == RootFolderNid) break;
                nid = f.ParentNid;
            }
            return path;
        }

        // ================================================================== folder contents

        /// <summary>The items in a folder, from its contents table (fast: the items themselves aren't read).</summary>
        public List<ItemSummary> Items(PstFolder folder)
        {
            var list = new List<ItemSummary>();
            if (folder.IsSearchFolder) return list; // a search folder only points at items in other folders
            var tableNid = (folder.Nid & ~0x1Fu) | 0x0E;
            if (!File.TryGetNode(tableNid, out var tableNode)) return list;
            var table = new TableContext(NodeData.Of(File, tableNode)) { Ansi = Ansi };
            for (var i = 0; i < table.RowCount; i++)
            {
                var nid = table.RowId(i);
                if (nid == 0) continue;
                var flags = table.Get(i, PropId.MessageFlags) is int f ? f : 0;
                var item = new ItemSummary
                {
                    Nid = nid,
                    FolderNid = folder.Nid,
                    MessageClass = table.Get(i, PropId.MessageClass) as string ?? "IPM.Note",
                    Subject = CleanSubject(table.Get(i, PropId.Subject) as string),
                    From = table.Get(i, PropId.SentRepresentingName) as string ?? table.Get(i, PropId.SenderName) as string ?? string.Empty,
                    To = table.Get(i, PropId.DisplayTo) as string ?? string.Empty,
                    Date = table.Get(i, PropId.MessageDeliveryTime) as DateTime? ?? table.Get(i, PropId.ClientSubmitTime) as DateTime?,
                    Size = table.Get(i, PropId.MessageSize) is int s ? s : 0,
                    Read = table.HasColumn(PropId.MessageFlags) ? (flags & 0x01) != 0 : true,
                    HasAttachments = (flags & 0x10) != 0,
                    Importance = table.Get(i, PropId.Importance) is int imp ? imp : 1
                };
                if (item.Kind == ItemKind.Appointment || item.Kind == ItemKind.Meeting || item.Kind == ItemKind.Contact || item.Kind == ItemKind.DistributionList)
                    AddDetails(item, table, i);
                list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// The calendar and contact fields to list, from the folder's table when Outlook keeps them there (it usually
        /// does), else from the item itself.
        /// </summary>
        private void AddDetails(ItemSummary item, TableContext table, int row)
        {
            var calendar = item.Kind == ItemKind.Appointment || item.Kind == ItemKind.Meeting;
            object Column(Guid set, int lid)
            {
                var id = Names.Id(set, lid);
                return id != 0 && table.HasColumn(id) ? table.Get(row, id) : null;
            }
            var hasColumns = calendar
                ? Names.Id(PropSet.Appointment, Lid.AppointmentStartWhole) is var startId && startId != 0 && table.HasColumn(startId)
                : item.Kind == ItemKind.DistributionList || Names.Id(PropSet.Address, Lid.Email1EmailAddress) is var emailId && emailId != 0 && table.HasColumn(emailId);
            if (hasColumns)
            {
                if (calendar)
                {
                    item.Start = Column(PropSet.Appointment, Lid.AppointmentStartWhole) as DateTime?;
                    item.End = Column(PropSet.Appointment, Lid.AppointmentEndWhole) as DateTime?;
                    item.AllDay = Column(PropSet.Appointment, Lid.AppointmentSubType) is bool allDay && allDay;
                    item.Recurring = Column(PropSet.Appointment, Lid.Recurring) is bool recurring && recurring;
                    item.Location = Column(PropSet.Appointment, Lid.Location) as string ?? string.Empty;
                }
                else
                {
                    item.FileAs = Column(PropSet.Address, Lid.FileUnder) as string ?? string.Empty;
                    item.Company = table.Get(row, 0x3A16) as string ?? string.Empty;
                    item.Email = Column(PropSet.Address, Lid.Email1EmailAddress) as string ?? string.Empty;
                    if (table.Get(row, PropId.DisplayName) is string name && name.Length > 0) item.Subject = name;
                }
                return;
            }
            try
            {
                var full = ReadItem(item.Nid);
                if (full.Appointment != null)
                {
                    item.Start = full.Appointment.Start;
                    item.End = full.Appointment.End;
                    item.AllDay = full.Appointment.AllDay;
                    item.Recurring = full.Appointment.Recurring;
                    item.Location = full.Appointment.Location;
                }
                if (full.Contact != null)
                {
                    item.FileAs = full.Contact.FileAs;
                    item.Company = full.Contact.Company;
                    item.Email = full.Contact.Emails.FirstOrDefault()?.Address ?? string.Empty;
                    if (full.Contact.Name.Length > 0) item.Subject = full.Contact.Name;
                }
            }
            catch (PstException)
            {
                // Listed with what the table has.
            }
        }

        /// <summary>
        /// Outlook may keep a subject with a marker in front: character 1, then a character whose code is the length of
        /// the "RE: " style prefix.
        /// </summary>
        public static string CleanSubject(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return string.Empty;
            if (subject[0] == '\u0001') return subject.Length >= 2 ? subject.Substring(2) : string.Empty;
            return subject;
        }

        // ================================================================== items

        public bool Exists(uint nid) => File.TryGetNode(nid, out _);

        /// <summary>Reads a whole item. The body and attachment contents are read when first asked for.</summary>
        public MailItem ReadItem(uint nid)
        {
            var node = File.GetNode(nid);
            return ReadItem(NodeData.Of(File, node));
        }

        private MailItem ReadItem(NodeData data)
        {
            var pc = new PropertyContext(data) { Ansi = Ansi };
            var codePage = pc.Int(PropId.InternetCodepage);
            if (codePage == 0) codePage = pc.Int(PropId.MessageCodepage);
            if (codePage > 0)
            {
                var enc = EncodingFor(codePage);
                if (enc != null) pc.Ansi = enc;
            }
            var item = Fields(pc);
            item.Recipients = ReadRecipients(data, pc.Ansi);
            item.LoadBody = () => Body(pc, codePage);
            item.LoadAttachments = () => ReadAttachments(data, pc.Ansi);
            Details(item, new Props(pc, Names), ContactAddress);
            return item;
        }

        /// <summary>A group member that's a contact in this file: the contact's first email address.</summary>
        private MailAddress ContactAddress(uint nid)
        {
            try
            {
                if (!File.TryGetNode(nid, out var node)) return null;
                var pc = new PropertyContext(NodeData.Of(File, node)) { Ansi = Ansi };
                var p = new Props(pc, Names);
                var address = p.String(PropSet.Address, Lid.Email1EmailAddress);
                var name = p.String(PropSet.Address, Lid.Email1DisplayName);
                if (name.Length == 0) name = p.String(PropId.DisplayName);
                return new MailAddress(name, address);
            }
            catch (PstException)
            {
                return null;
            }
        }

        /// <summary>The fields that need the named-property table: categories, and contact, group and calendar details.</summary>
        public static void Details(MailItem item, Props p, Func<uint, MailAddress> contactByNid)
        {
            item.Categories = p.Strings(PropSet.PublicStrings, "Keywords");
            item.Sensitivity = p.Source.Int(PropId.Sensitivity);
            switch (item.Kind)
            {
                case ItemKind.Contact:
                    item.Contact = ContactInfo.Read(p, item.Attachments);
                    break;
                case ItemKind.DistributionList:
                    item.Group = GroupInfo.Read(p, contactByNid);
                    break;
                case ItemKind.Appointment:
                case ItemKind.Meeting:
                    item.Appointment = AppointmentInfo.Read(p, item);
                    break;
            }
        }

        /// <summary>The fields of an item that work the same in PST and MSG files.</summary>
        public static MailItem Fields(IPropertySource p)
        {
            var flags = p.Int(PropId.MessageFlags);
            var sender = Address(p.String(PropId.SenderName), p.String(PropId.SenderSmtpAddress), p.String(PropId.SenderEmail), p.String(PropId.SenderAddrType));
            var from = Address(p.String(PropId.SentRepresentingName), p.String(PropId.SentRepresentingSmtpAddress), p.String(PropId.SentRepresentingEmail), p.String(PropId.SentRepresentingAddrType));
            if (from.IsEmpty) from = sender;
            var headers = p.String(PropId.TransportHeaders) ?? string.Empty;
            // An Exchange-only address (no SMTP address kept): the From header has the real one.
            if (!from.Address.Contains("@") && headers.Length > 0)
            {
                var header = Mime.ParseHeaders(headers).Get("From");
                var parsed = Mime.ParseAddresses(header).FirstOrDefault();
                if (parsed != null && parsed.Address.Contains("@")) from = new MailAddress(from.Name.Length > 0 ? from.Name : parsed.Name, parsed.Address);
            }
            return new MailItem
            {
                MessageClass = p.String(PropId.MessageClass) ?? "IPM.Note",
                Subject = CleanSubject(p.String(PropId.Subject)),
                From = from,
                Sender = sender,
                DisplayTo = p.String(PropId.DisplayTo) ?? string.Empty,
                DisplayCc = p.String(PropId.DisplayCc) ?? string.Empty,
                DisplayBcc = p.String(PropId.DisplayBcc) ?? string.Empty,
                Sent = p.Time(PropId.ClientSubmitTime),
                Received = p.Time(PropId.MessageDeliveryTime),
                Size = p.Long(PropId.MessageSize),
                Read = !p.Has(PropId.MessageFlags) || (flags & 0x01) != 0,
                Importance = p.Has(PropId.Importance) ? p.Int(PropId.Importance) : 1,
                MessageId = p.String(PropId.InternetMessageId) ?? string.Empty,
                InReplyTo = p.String(PropId.InReplyTo) ?? string.Empty,
                References = p.String(PropId.References) ?? string.Empty,
                Headers = headers
            };
        }

        /// <summary>Prefers an SMTP address; an Exchange address (/O=…) is kept only when there's nothing better.</summary>
        public static MailAddress Address(string name, string smtp, string email, string addrType)
        {
            name = name ?? string.Empty;
            string address;
            if (!string.IsNullOrEmpty(smtp)) address = smtp;
            else if (!string.IsNullOrEmpty(email) && (email.Contains("@") || !string.Equals(addrType, "EX", StringComparison.OrdinalIgnoreCase))) address = email;
            else address = string.Empty;
            return new MailAddress(name.Trim('\'', ' '), address.Trim());
        }

        public static Encoding EncodingFor(int codePage)
        {
            if (codePage <= 0 || codePage == 1200 || codePage == 1201) return null;
            try { return Encoding.GetEncoding(codePage); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { return null; }
        }

        /// <summary>The body: text, HTML (stored as bytes in the message's code page) and RTF.</summary>
        public static MailBody Body(IPropertySource p, int codePage)
        {
            var body = new MailBody { Text = p.String(PropId.Body) };
            var html = p.Get(PropId.Html);
            if (html is string s) body.Html = s;
            else if (html is byte[] bytes) body.Html = DecodeHtml(bytes, codePage);
            var rtf = p.Bytes(PropId.RtfCompressed);
            if (rtf != null)
            {
                try
                {
                    body.Rtf = RtfBody.FromCompressed(rtf);
                    if (string.IsNullOrEmpty(body.Html)) body.Html = RtfBody.ExtractHtml(body.Rtf);
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException)
                {
                    body.Rtf = null;
                }
            }
            return body;
        }

        /// <summary>HTML stored as bytes: the code page it says it uses (a meta tag), else the message's, else UTF-8.</summary>
        public static string DecodeHtml(byte[] bytes, int codePage)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var m = System.Text.RegularExpressions.Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Encoding enc = null;
            if (m.Success)
            {
                try { enc = Encoding.GetEncoding(m.Groups[1].Value); }
                catch (ArgumentException) { enc = null; }
            }
            enc = enc ?? EncodingFor(codePage) ?? Encoding.UTF8;
            return enc.GetString(bytes);
        }

        private List<Recipient> ReadRecipients(NodeData data, Encoding ansi)
        {
            var list = new List<Recipient>();
            if (!data.TryGetSubNode(RecipientTableNid, out var sub)) return list;
            TableContext table;
            try { table = new TableContext(sub) { Ansi = ansi }; }
            catch (PstException) { return list; }
            for (var i = 0; i < table.RowCount; i++)
            {
                var type = table.Get(i, PropId.RecipientType) is int t ? t & 0x0F : 1;
                list.Add(new Recipient
                {
                    Kind = type == 2 ? RecipientKind.Cc : type == 3 ? RecipientKind.Bcc : RecipientKind.To,
                    MeetingType = type,
                    Response = table.Get(i, PropId.RecipientTrackStatus) is int status ? status : 0,
                    IsOrganizer = table.Get(i, PropId.RecipientFlags) is int rf && (rf & 0x2) != 0,
                    Address = Address(table.Get(i, PropId.DisplayName) as string, table.Get(i, PropId.SmtpAddress) as string,
                        table.Get(i, PropId.EmailAddress) as string, table.Get(i, PropId.AddrType) as string)
                });
            }
            return list;
        }

        private List<Attachment> ReadAttachments(NodeData data, Encoding ansi)
        {
            var list = new List<Attachment>();
            if (!data.TryGetSubNode(AttachmentTableNid, out var sub)) return list;
            TableContext table;
            try { table = new TableContext(sub) { Ansi = ansi }; }
            catch (PstException) { return list; }
            for (var i = 0; i < table.RowCount; i++)
            {
                var nid = table.RowId(i);
                if (nid == 0 || !data.TryGetSubNode(nid, out var attachmentData)) continue;
                try
                {
                    var pc = new PropertyContext(attachmentData) { Ansi = ansi };
                    list.Add(Attachment(pc, () =>
                    {
                        var embeddedNid = pc.ObjectNid(PropId.AttachDataBinary);
                        if (embeddedNid == 0 || !attachmentData.TryGetSubNode(embeddedNid, out var embedded)) return null;
                        return ReadItem(embedded);
                    }));
                }
                catch (PstException)
                {
                    // A damaged attachment is left out.
                }
            }
            return list;
        }

        /// <summary>An attachment's details from its properties (PST or MSG).</summary>
        public static Attachment Attachment(IPropertySource p, Func<MailItem> embedded)
        {
            var method = p.Int(PropId.AttachMethod, 1);
            var name = p.String(PropId.AttachLongFilename);
            if (string.IsNullOrEmpty(name)) name = p.String(PropId.AttachFilename);
            if (string.IsNullOrEmpty(name)) name = p.String(PropId.DisplayName);
            var flags = p.Int(PropId.AttachFlags);
            var a = new Attachment
            {
                FileName = name ?? string.Empty,
                MimeType = p.String(PropId.AttachMimeTag) ?? string.Empty,
                ContentId = (p.String(PropId.AttachContentId) ?? string.Empty).Trim('<', '>', ' '),
                ContentLocation = p.String(PropId.AttachContentLocation) ?? string.Empty,
                Size = p.Long(PropId.AttachSize),
                Hidden = p.Bool(PropId.AttachmentHidden),
                IsContactPhoto = p.Bool(PropId.AttachmentContactPhoto),
                Inline = (flags & 0x04) != 0
            };
            if (method == 5)
            {
                // An attached email.
                a.Embedded = embedded;
                if (a.FileName.Length == 0) a.FileName = "Attached email";
            }
            else
            {
                a.Load = () => p.Bytes(PropId.AttachDataBinary) ?? new byte[0];
            }
            if (a.FileName.Length == 0) a.FileName = "attachment";
            return a;
        }
    }
}
