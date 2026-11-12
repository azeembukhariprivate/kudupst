using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Tests
{
    public static partial class Program
    {
        /// <summary>Every item of a data file, as the window hands them to an export.</summary>
        private static List<ExportItem> AllItems(PstStore store)
        {
            var list = new List<ExportItem>();
            foreach (var f in store.AllFolders(store.Top))
            {
                var path = string.Join("/", store.PathOf(f.Nid).Skip(1).Select(x => x.Name));
                foreach (var s in store.Items(f))
                {
                    var nid = s.Nid;
                    list.Add(new ExportItem { Load = () => store.ReadItem(nid), FolderPath = path, Subject = s.Subject, Date = s.Date, Store = store, Nid = nid });
                }
            }
            return list;
        }

        private static string SaveAsPst(IList<ExportItem> items, string name, bool keepFolders = true)
        {
            var path = Path.Combine(_root, name);
            var result = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Pst, Destination = path, KeepFolders = keepFolders }, null, default);
            Check(result.Problems.Count == 0, "no problems: " + string.Join(" | ", result.Problems.Take(3)));
            Equal(items.Count, result.Exported, "items saved");
            Check(File.Exists(path) && !File.Exists(path + ".unfinished"), "the file has its own name when done");
            var keep = Environment.GetEnvironmentVariable("KUDUPST_KEEP");
            if (!string.IsNullOrEmpty(keep)) File.Copy(path, Path.Combine(keep, name), true);
            return path;
        }

        /// <summary>What matters of an item, as one text, for comparing a copy with the original.</summary>
        private static string Picture(MailItem m)
        {
            var sb = new StringBuilder();
            sb.Append(m.MessageClass).Append('|').Append(m.Subject).Append('|').Append(m.From.Display).Append('|').Append(m.ToLine).Append('|').Append(m.CcLine)
              .Append('|').Append(m.Sent?.Ticks).Append('|').Append(m.Received?.Ticks).Append('|').Append(m.Read).Append('|').Append(m.Importance)
              .Append('|').Append(m.MessageId).Append('|').Append(string.Join(",", m.Categories));
            sb.Append("|T:").Append(m.Body.Text).Append("|H:").Append(m.Body.Html).Append("|R:").Append(m.Body.Rtf?.Length);
            foreach (var r in m.Recipients) sb.Append("|r:").Append((int)r.Kind).Append(r.Address.Display);
            foreach (var a in m.Attachments)
            {
                sb.Append("|a:").Append(a.FileName).Append(':').Append(a.MimeType).Append(':').Append(a.ContentId).Append(':').Append(a.Inline);
                if (a.IsEmail) sb.Append("{").Append(Picture(a.Embedded())).Append("}");
                else sb.Append(':').Append(System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(a.Data())));
            }
            if (m.Contact != null) sb.Append("|c:").Append(m.Contact.Name).Append(':').Append(m.Contact.FileAs).Append(':').Append(string.Join(",", m.Contact.Emails.Select(e => e.Address)));
            if (m.Group != null) sb.Append("|g:").Append(string.Join(",", m.Group.Members.Select(e => e.Display)));
            if (m.Appointment != null) sb.Append("|p:").Append(m.Appointment.Start?.Ticks).Append(':').Append(m.Appointment.End?.Ticks).Append(':').Append(m.Appointment.Location).Append(':').Append(m.Appointment.Recurring);
            return sb.ToString();
        }

        /// <summary>The copy has the same folders with the same items, each the same in every part KUDUPST reads.</summary>
        private static void SameAsOriginal(PstStore original, string copyPath)
        {
            using (var copy = PstStore.Open(copyPath))
            {
                var folders = copy.AllFolders(copy.Top).ToDictionary(f => string.Join("/", copy.PathOf(f.Nid).Skip(1).Select(x => x.Name)), f => f);
                var checkedItems = 0;
                foreach (var f in original.AllFolders(original.Top))
                {
                    var items = original.Items(f);
                    if (items.Count == 0) continue;
                    var path = string.Join("/", original.PathOf(f.Nid).Skip(1).Select(x => x.Name));
                    Check(folders.TryGetValue(path, out var twin), "folder is in the copy: " + path);
                    Equal(items.Count, twin.ItemCount, "item count of " + path);
                    Equal(items.Count(i => !i.Read), twin.UnreadCount, "unread count of " + path);
                    var theirs = copy.Items(twin);
                    Equal(items.Count, theirs.Count, "listed items of " + path);
                    var want = items.Select(i => Picture(original.ReadItem(i.Nid))).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var got = theirs.Select(i => Picture(copy.ReadItem(i.Nid))).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    for (var i = 0; i < want.Count; i++)
                        if (want[i] != got[i]) throw new Exception($"an item of {path} differs:\n  {Short(want[i])}\n  {Short(got[i])}");
                    // The folder's own list says the same as the items.
                    var listed = theirs.Select(i => i.Subject + "|" + i.Date?.Ticks + "|" + i.Read + "|" + i.HasAttachments).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var listedWant = items.Select(i => i.Subject + "|" + i.Date?.Ticks + "|" + i.Read + "|" + i.HasAttachments).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    for (var i = 0; i < listed.Count; i++)
                        if (listed[i] != listedWant[i]) throw new Exception($"the folder list of {path} differs:\n  {Short(listedWant[i])}\n  {Short(listed[i])}");
                    checkedItems += items.Count;
                }
                Check(checkedItems > 0, "something was compared");
            }
        }

        private static string Short(string s)
        {
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length > 400 ? s.Substring(0, 400) + "..." : s;
        }

        private static void TestSaveAsPstKeepsFoldersAndItems()
        {
            using (var store = PstStore.Open(Samples.DistList))
            {
                var path = SaveAsPst(AllItems(store), "copy-dist-list.pst");
                SameAsOriginal(store, path);
            }
        }

        private static void TestSaveAsPstCopiesAWholeMailbox()
        {
            using (var store = PstStore.Open(Samples.Enron))
            {
                var items = AllItems(store);
                Check(items.Count > 50, "the sample has many items: " + items.Count);
                var path = SaveAsPst(items, "copy-enron.pst");
                SameAsOriginal(store, path);
            }
        }

        private static void TestSaveAsPstFromAnOstFile()
        {
            using (var store = PstStore.Open(Samples.Example2013))
            {
                var path = SaveAsPst(AllItems(store), "copy-ost.pst");
                SameAsOriginal(store, path);
            }
        }
            private static void TestSaveAsPstFromAnOldStyleFile()
        {
            // The old kind of file keeps text one byte a letter, in the sender's alphabet; the new file keeps it as real text.
            foreach (var crypt in new[] { PstCryptMethod.None, PstCryptMethod.Compressible })
                using (var store = OpenBytes(MakePst(false, crypt)))
                {
                    var path = SaveAsPst(AllItems(store), "copy-old-" + crypt + ".pst");
                    SameAsOriginal(store, path);
                }
        }

        private static void TestSaveAsPstTakesDeletedItemsThatWereFound()
        {
            using (var store = OpenBytes(MakePstWithDeleted(true, PstCryptMethod.None)))
            {
                store.UseFound(Recovery.Find(store, true));
                var found = store.SubFolders(store.Top).Last();
                var rows = store.Items(found);
                Check(rows.Count > 0, "deleted items were found");
                var items = rows.Select(r => new ExportItem { Load = () => store.ReadItem(r.Nid), FolderPath = found.Name, Subject = r.Subject, Store = store, Nid = r.Nid }).ToList();
                var path = SaveAsPst(items, "copy-found.pst");
                using (var copy = PstStore.Open(path))
                {
                    var folder = copy.SubFolders(copy.Top).Single(f => f.Name == PstStore.FoundFolderName);
                    var theirs = copy.Items(folder);
                    Equal(rows.Count, theirs.Count, "every found item is in the new file");
                    var whole = copy.ReadItem(theirs.Single(r => r.Subject == "Deleted but whole").Nid);
                    Equal("Ann Deleter <ann@example.com>", whole.From.Display, "sender of a found item");
                    Check(whole.Attachments.Count > 0 && whole.Attachments[0].Data().Length > 0, "its attachment came along");
                    Check(!whole.Deleted, "in the new file it's an ordinary item again");
                }
            }
        }

        private static void TestSaveAsPstWritesSingleEmails()
        {
            var sources = new[] { Samples.MultiTo, Samples.UnicodeMsg, Samples.UnicodeHeader, Samples.StrangeDate };
            var originals = sources.Select(MsgReader.Read).ToList();
            var eml = EmlReader.Read(EmlWriter.Write(originals[1]));
            originals.Add(eml);
            var items = originals.Select(m => new ExportItem { Load = () => m, Subject = m.Subject, Date = m.Date }).ToList();
            var path = SaveAsPst(items, "copy-singles.pst");
            using (var copy = PstStore.Open(path))
            {
                var folder = copy.SubFolders(copy.Top).Single(f => f.Name == Exporter.LooseFolder);
                var rows = copy.Items(folder);
                Equal(originals.Count, rows.Count, "every single email is in the new file");
                Equal(originals.Count, folder.ItemCount, "the folder's count");
                var back = rows.Select(r => copy.ReadItem(r.Nid)).ToList();
                foreach (var m in originals)
                {
                    var twin = back.FirstOrDefault(b => b.Subject == m.Subject && b.Sent?.Ticks == m.Sent?.Ticks && b.Attachments.Count == m.Attachments.Count);
                    Check(twin != null, "found again: " + m.Subject);
                    Equal(m.From.Address, twin.From.Address, "sender of " + m.Subject);
                    Equal(m.From.Short, twin.From.Short, "sender's name of " + m.Subject);
                    Equal(string.Join(";", m.Recipients.Select(r => (int)r.Kind + r.Address.Address)), string.Join(";", twin.Recipients.Select(r => (int)r.Kind + r.Address.Address)), "recipients of " + m.Subject);
                    Equal(m.Body.PlainText.Trim(), twin.Body.PlainText.Trim(), "text of " + m.Subject);
                    if (m.Body.HasHtml) Equal(m.Body.Html, twin.Body.Html, "web version of " + m.Subject);
                    Equal(m.MessageId, twin.MessageId, "message ID of " + m.Subject);
                    for (var i = 0; i < m.Attachments.Count; i++)
                    {
                        Equal(m.Attachments[i].FileName, twin.Attachments[i].FileName, "attachment name in " + m.Subject);
                        Equal(m.Attachments[i].IsEmail, twin.Attachments[i].IsEmail, "attached email in " + m.Subject);
                        if (m.Attachments[i].IsEmail) Equal(m.Attachments[i].Embedded().Subject, twin.Attachments[i].Embedded().Subject, "the attached email's subject");
                        else Check(m.Attachments[i].Data().SequenceEqual(twin.Attachments[i].Data()), "attachment contents in " + m.Subject);
                    }
                }
                var listed = rows.First(r => r.Subject == originals[0].Subject);
                Equal(originals[0].From.Short, listed.From, "the folder list names the sender");
            }
        }

        private static void TestSaveAsPstBigFilesAndBigItems()
        {
            // A file bigger than one "map of maps" covers (about 32 MB), with an attachment bigger than one list of blocks holds (8 MB).
            var big = new byte[9 * 1024 * 1024];
            new Random(7).NextBytes(big);
            var items = new List<ExportItem>();
            for (var i = 0; i < 5; i++)
            {
                var n = i;
                var mail = new MailItem
                {
                    Subject = "Big " + n, From = new MailAddress("Sender", "sender@example.com"), Sent = new DateTime(2024, 5, 1 + n, 9, 0, 0, DateTimeKind.Utc), Read = n % 2 == 0,
                    LoadBody = () => new MailBody { Text = new string('x', 20000) + n },
                    LoadAttachments = () => new List<Attachment> { new Attachment { FileName = "big" + n + ".bin", Load = () => big } }
                };
                mail.Recipients.Add(new Recipient { Address = new MailAddress("Reader", "reader@example.com") });
                items.Add(new ExportItem { Load = () => mail, FolderPath = "Big/Deeper", Subject = mail.Subject });
            }
            for (var i = 0; i < 3000; i++)
            {
                var n = i;
                var mail = new MailItem { Subject = "Small " + n, From = new MailAddress("S", "s@example.com"), Sent = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n), LoadBody = () => new MailBody { Text = "Body " + n } };
                items.Add(new ExportItem { Load = () => mail, FolderPath = "Many", Subject = mail.Subject });
            }
            var path = SaveAsPst(items, "copy-big.pst");
            Check(new FileInfo(path).Length > 40L * 1024 * 1024, "the file is big");
            Equal(0L, (new FileInfo(path).Length - 0x4400) % 253952, "the file ends where a map's stretch ends");
            using (var copy = PstStore.Open(path))
            {
                Check(copy.File.FreeMapValid, "the maps of used space are marked as good");
                var bigFolder = copy.AllFolders(copy.Top).Single(f => f.Name == "Deeper");
                Equal("Big", copy.PathOf(bigFolder.Nid)[1].Name, "folders inside folders");
                var rows = copy.Items(bigFolder);
                Equal(5, rows.Count, "big items");
                Equal(2, bigFolder.UnreadCount, "unread count");
                foreach (var r in rows)
                {
                    var m = copy.ReadItem(r.Nid);
                    Check(m.Attachments.Single().Data().SequenceEqual(big), "a 9 MB attachment comes back whole: " + r.Subject);
                    Equal(20001, m.Body.Text.Length, "a long text comes back whole");
                    Check(r.HasAttachments, "listed with an attachment");
                }
                var many = copy.AllFolders(copy.Top).Single(f => f.Name == "Many");
                var small = copy.Items(many);
                Equal(3000, small.Count, "a folder list of 3,000");
                Equal(3000, small.Select(r => r.Subject).Distinct().Count(), "each listed once");
                foreach (var r in small) Equal("Body " + r.Subject.Substring(6), copy.ReadItem(r.Nid).Body.Text, "each one reads: " + r.Subject);
                // Nothing in the file overlaps anything else, and every map says "used" for what is used.
                var free = copy.File.FreeSpace().OrderBy(f => f.Offset).ToList();
                var starts = free.Select(f => f.Offset).ToList();
                var looked = 0;
                foreach (var node in copy.File.AllNodes())
                    foreach (var bid in new[] { node.DataBid, node.SubBid })
                    {
                        if (bid == 0 || !copy.File.TryGetBlock(bid, out var block)) continue;
                        var at = starts.BinarySearch(block.Offset);
                        if (at < 0) at = ~at - 1;
                        Check(at < 0 || free[at].Offset + free[at].Length <= block.Offset, "a block lies in space marked as free");
                        if (at + 1 < free.Count) Check(free[at + 1].Offset >= block.Offset + block.Size, "a block runs into space marked as free");
                        looked++;
                    }
                Check(looked > 3000, "blocks were checked against the maps: " + looked);
            }
        }

        private static void TestSaveAsPstStoppedLeavesNoFile()
        {
            using (var store = PstStore.Open(Samples.Enron))
            {
                var items = AllItems(store);
                var path = Path.Combine(_root, "stopped.pst");
                using (var cancel = new System.Threading.CancellationTokenSource())
                {
                    var result = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Pst, Destination = path }, (done, _) => { if (done == 10) cancel.Cancel(); }, cancel.Token);
                    Check(result.Cancelled && result.Files == 0 && result.FirstFile == null, "stopped");
                }
                Check(!File.Exists(path) && !File.Exists(path + ".unfinished"), "nothing is left behind");
            }
        }

        private static void TestSaveAsPstNameTableMatchesOutlooks()
        {
            // Outlook finds a named field through a table of "buckets". A real Outlook file's entries must land in the
            // buckets our rule gives, or Outlook wouldn't find contact and calendar fields in files we write.
            using (var file = PstFile.Open(Samples.DistList))
            {
                var pc = new PropertyContext(NodeData.Of(file, file.GetNode(0x61)));
                var entries = pc.Raw(PropId.NameIdEntries);
                var strings = pc.Raw(PropId.NameIdStrings);
                var buckets = BitConverter.ToInt32(pc.Raw(0x0001), 0);
                Equal(251, buckets, "bucket count");
                var names = 0;
                for (var at = 0; at + 8 <= entries.Length; at += 8)
                {
                    var id = BitConverter.ToUInt32(entries, at);
                    var kind = BitConverter.ToUInt16(entries, at + 4);
                    var index = BitConverter.ToUInt16(entries, at + 6);
                    var hash = id;
                    if ((kind & 1) != 0)
                    {
                        var length = BitConverter.ToInt32(strings, (int)id);
                        var bytes = new byte[length];
                        Buffer.BlockCopy(strings, (int)id + 4, bytes, 0, length);
                        hash = Crc32.Plain(bytes);
                        names++;
                    }
                    var bucket = pc.Raw((ushort)(0x1000 + (hash ^ kind) % (uint)buckets));
                    var there = false;
                    for (var b = 0; bucket != null && b + 8 <= bucket.Length; b += 8)
                        if (BitConverter.ToUInt32(bucket, b) == hash && BitConverter.ToUInt16(bucket, b + 4) == kind && BitConverter.ToUInt16(bucket, b + 6) == index) there = true;
                    Check(there, $"entry {index} (kind {kind:X}) is in the bucket our rule gives");
                }
                Check(names > 10, "fields with names were checked too");
            }
        }
    }
}
