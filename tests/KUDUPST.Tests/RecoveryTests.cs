using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Tests
{
    // Finding deleted items left inside a file.
    public static partial class Program
    {
        private static readonly DateTime DeletedDate = new DateTime(2024, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The made-up PST, plus what Outlook leaves behind when items are deleted: two deleted emails and a deleted
        /// contact whose index entries are in an old index page (one email's attachment has since been written over),
        /// an email whose index entry is gone too, and old copies of the email that's still there (which aren't deleted).
        /// </summary>
        private static byte[] MakePstWithDeleted(bool unicode, PstCryptMethod crypt, bool freeMap = true) => MakePst(unicode, crypt, null, w =>
        {
            w.FreeMap = freeMap;
            var ansi = Encoding.GetEncoding(1252);
            PstWriter.Prop S(ushort id, string s) => unicode ? PstWriter.Str(id, s) : PstWriter.Str8(id, s, ansi);
            var stringType = unicode ? PropType.Unicode : PropType.String8;
            var recipientColumns = new List<(ushort, ushort)>
            {
                (PropId.LtpRowId, PropType.Int32), (PropId.DisplayName, stringType), (PropId.EmailAddress, stringType),
                (PropId.AddrType, stringType), (PropId.RecipientType, PropType.Int32)
            };
            var attachmentColumns = new List<(ushort, ushort)> { (PropId.LtpRowId, PropType.Int32), (PropId.AttachMethod, PropType.Int32) };

            (ulong Data, ulong Sub, ulong AttachmentBlock) Email(string subject, DateTime date, byte[] attachment)
            {
                var subs = new List<(uint, ulong, ulong)>();
                var data = w.Data(w.PropertyContext(new[]
                {
                    S(PropId.MessageClass, "IPM.Note"), S(PropId.Subject, subject), S(PropId.SentRepresentingName, "Ann Deleter"),
                    S(PropId.SentRepresentingEmail, "ann@example.com"), S(PropId.SentRepresentingAddrType, "SMTP"),
                    PstWriter.Time(PropId.MessageDeliveryTime, date), S(PropId.Body, "Text of " + subject),
                    PstWriter.Int(PropId.MessageFlags, 0x11)
                }, subs));
                subs.Add((0x692, w.DataBlock(w.TableContext(recipientColumns, new List<object[]>
                {
                    new object[] { 1, "Bob", "bob@example.com", "SMTP", 1 }, new object[] { 2, "Cy", "cy@example.com", "SMTP", 2 }
                }, ansi)), 0));
                var attSubs = new List<(uint, ulong, ulong)>();
                var att = w.DataBlock(w.PropertyContext(new[]
                {
                    S(PropId.AttachLongFilename, "notes.bin"), PstWriter.Int(PropId.AttachMethod, 1), PstWriter.Bin(PropId.AttachDataBinary, attachment),
                    PstWriter.Int(PropId.AttachSize, attachment.Length)
                }, attSubs));
                subs.Add((0x8025, att, attSubs.Count > 0 ? w.SubNodes(attSubs) : 0));
                subs.Add((0x671, w.DataBlock(w.TableContext(attachmentColumns, new List<object[]> { new object[] { 0x8025, 1 } }, ansi)), 0));
                var sub = w.SubNodes(subs);
                w.Forget(w.BlocksOf(data, sub).ToArray());
                return (data, sub, att);
            }

            var whole = Email("Deleted but whole", DeletedDate, Attachment(20000));
            w.DeletedNode(0x200044, whole.Data, whole.Sub, InboxNid);

            var damaged = Email("Deleted, attachment written over", DeletedDate.AddDays(1), Attachment(500));
            w.Damage(damaged.AttachmentBlock);
            w.DeletedNode(0x200064, damaged.Data, damaged.Sub, InboxNid);

            var contact = w.DataBlock(w.PropertyContext(new[]
            {
                S(PropId.MessageClass, "IPM.Contact"), S(PropId.Subject, "Deleted Friend"), S(PropId.DisplayName, "Deleted Friend"),
                S(0x3A06, "Deleted"), S(0x3A11, "Friend"), S(0x3A08, "+44 20 7946 0000")
            }));
            w.Forget(contact);
            w.DeletedNode(0x200084, contact, 0, InboxNid);

            // No index entry left at all: only its property block.
            var gone = w.DataBlock(w.PropertyContext(new[]
            {
                S(PropId.MessageClass, "IPM.Note"), S(PropId.Subject, "Deleted long ago"), S(PropId.SentRepresentingName, "Old Sender"),
                PstWriter.Time(PropId.MessageDeliveryTime, DeletedDate.AddDays(-30)), S(PropId.Body, "Only this is left")
            }));
            w.Forget(gone);

            // Old copies of the email that's still in the Inbox (left when it was changed): not deleted.
            var received = new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Utc);
            var oldCopy = w.DataBlock(w.PropertyContext(new[]
            {
                S(PropId.MessageClass, "IPM.Note"), S(PropId.Subject, "\u0001\u0004RE: Café meeting"), PstWriter.Time(PropId.MessageDeliveryTime, received),
                PstWriter.Int(PropId.MessageFlags, 0x10)
            }));
            w.Forget(oldCopy);
            w.DeletedNode(MessageNid, oldCopy, 0, InboxNid);
            var olderCopy = w.DataBlock(w.PropertyContext(new[]
            {
                S(PropId.MessageClass, "IPM.Note"), S(PropId.Subject, "\u0001\u0004RE: Café meeting"), PstWriter.Time(PropId.MessageDeliveryTime, received)
            }));
            w.Forget(olderCopy);
        });

        private static byte[] Attachment(int size)
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            return data;
        }

        private static void CheckDeleted(byte[] file, string what)
        {
            using (var store = OpenBytes(file))
            {
                var quick = Recovery.Find(store, false);
                var names = string.Join(", ", quick.Items.Select(i => $"{i.OriginalNid:X}{(i.Partial ? " partial" : string.Empty)}"));
                Equal(2, quick.Items.Count, what + ": quick look finds the two whole items (" + names + ")");
                Check(quick.Items.All(i => !i.Partial), what + ": all whole");

                var deep = Recovery.Find(store, true, null, default, quick);
                Equal(4, deep.Items.Count, what + ": deeper look also finds the partial ones");
                for (var i = 0; i < quick.Items.Count; i++) Equal(quick.Items[i].Id, deep.Items[i].Id, what + ": the same IDs as the quick look");

                store.UseFound(deep);
                var found = store.SubFolders(store.Top).Last();
                Equal(PstStore.FoundFolderName, found.Name, what + ": the folder at the end");
                Equal(Recovery.FolderNid, found.Nid, what + ": its ID");
                var rows = store.Items(found);
                Equal(4, rows.Count, what + ": rows");
                Check(rows.All(r => r.Deleted), what + ": marked deleted");
                Check(!rows.Any(r => r.Subject.Contains("Café")), what + ": old copies of a kept email aren't deleted");

                var whole = rows.Single(r => r.Subject == "Deleted but whole");
                Check(!whole.Partial && whole.FoundIn == "Inbox" && whole.HasAttachments, what + ": whole email row");
                var m = store.ReadItem(whole.Nid);
                Check(m.Deleted && !m.Partial, what + ": read as deleted");
                Equal("Ann Deleter <ann@example.com>", m.From.Display, what + ": sender");
                Equal("Text of Deleted but whole", m.Body.Text, what + ": text");
                Equal(2, m.Recipients.Count, what + ": recipients");
                Check(m.Attachments.Single().Data().SequenceEqual(Attachment(20000)), what + ": the attachment, over several blocks");

                var damaged = rows.Single(r => r.Subject == "Deleted, attachment written over");
                Check(damaged.Partial, what + ": partial when a piece was written over");
                var dm = store.ReadItem(damaged.Nid);
                Equal("Text of Deleted, attachment written over", dm.Body.Text, what + ": the rest still reads");
                Equal(0, dm.Attachments.Count, what + ": the written-over attachment is left out");

                var friend = rows.Single(r => r.Subject == "Deleted Friend");
                Check(!friend.Partial && store.ReadItem(friend.Nid).Contact.Phone(ContactInfo.Business) == "+44 20 7946 0000", what + ": a deleted contact");

                var gone = rows.Single(r => r.Subject == "Deleted long ago");
                Check(gone.Partial && gone.FoundIn.Length == 0, what + ": found without an index entry, place unknown");
                Equal("Only this is left", store.ReadItem(gone.Nid).Body.Text, what + ": its text");

                // Exporting a found item works like any other.
                var eml = EmlWriter.Write(m);
                Equal("Deleted but whole", EmlReader.Read(eml).Subject, what + ": exported");

                // The live email is untouched.
                Equal("RE: Café meeting", store.Items(store.SubFolders(store.Top).First()).Single().Subject, what + ": the kept email");
            }
        }

        private static void TestDeletedUnicode() => CheckDeleted(MakePstWithDeleted(true, PstCryptMethod.High), "Unicode, high encryption");
        private static void TestDeletedAnsi() => CheckDeleted(MakePstWithDeleted(false, PstCryptMethod.Compressible), "ANSI, compressible encryption");
        private static void TestDeletedWithoutFreeMap() => CheckDeleted(MakePstWithDeleted(true, PstCryptMethod.None, freeMap: false), "no map of free space");

        private static void TestDeletedFreeSpaceMap()
        {
            // The quick look reads only what the map lists as free.
            var file = MakePstWithDeleted(true, PstCryptMethod.None);
            using (var store = OpenBytes(file))
            {
                var free = store.File.FreeSpace();
                Check(free != null && free.Count > 0, "free space listed");
                Check(free.Sum(f => f.Length) < file.Length / 2, "only part of the file is free");
            }
        }

        private static void TestDeletedCacheAndIndex()
        {
            var root = Path.Combine(_root, "found-index");
            var path = Path.Combine(_root, "deleted.pst");
            File.WriteAllBytes(path, MakePstWithDeleted(true, PstCryptMethod.Compressible));
            using (var store = PstStore.Open(path))
            {
                var deep = Recovery.Find(store, true);
                var folder = Path.Combine(root, MailIndex.KeyOf(path));
                deep.Save(Path.Combine(folder, "found-deep.bin"));
                var back = Recovery.Load(Path.Combine(folder, "found-deep.bin"));
                Check(back.Deep && back.Items.Count == deep.Items.Count && back.Blocks.Count == deep.Blocks.Count, "saved and read back");
                Check(back.Items.Select(i => (i.Id, i.DataBid, i.SubBid, i.Partial)).SequenceEqual(deep.Items.Select(i => (i.Id, i.DataBid, i.SubBid, i.Partial))), "the same items");
            }
            using (var store = PstStore.Open(path))
            {
                // A new reader of the file uses the saved results, including the blocks no index lists.
                store.UseFound(Recovery.LoadOrFind(store, Path.Combine(root, MailIndex.KeyOf(path))));
                Equal(4, store.Items(store.FoundFolder).Count, "found again from the saved copy");
                Equal(20000, store.ReadItem(store.Items(store.FoundFolder).Single(r => r.Subject == "Deleted but whole").Nid).Attachments.Single().Data().Length, "with its blocks");
            }
            using (var index = new MailIndex(path, root))
            {
                index.BuildNow();
                var hits = index.Search(new SearchQuery { Text = "long ago" });
                Check(hits.Any(h => h.FolderNid == Recovery.FolderNid && h.Subject == "Deleted long ago"), "found items are searchable");
                Check(index.Search(new SearchQuery { Text = "Café" }).All(h => h.FolderNid != Recovery.FolderNid), "kept emails aren't marked deleted");
            }
            // Without the deeper look saved, the index holds the quick look's items; the deeper look's are added after.
            var root2 = Path.Combine(_root, "found-index-2");
            using (var index = new MailIndex(path, root2))
            {
                index.BuildNow();
                Equal(2, index.FoundIndexed, "the quick look's items indexed");
                Check(index.Search(new SearchQuery { Text = "whole" }).Any(h => h.FolderNid == Recovery.FolderNid), "a whole deleted email is searchable");
                Check(!index.Search(new SearchQuery { Text = "long ago" }).Any(), "the deeper look's items aren't there yet");
                Recovery deep;
                using (var store = PstStore.Open(path))
                {
                    var quick = Recovery.LoadOrFind(store, index.Folder);
                    deep = Recovery.Find(store, true, null, default, quick);
                }
                index.AddFoundNow(deep);
                Equal(4, index.FoundIndexed, "all found items indexed");
                Check(index.Search(new SearchQuery { Text = "long ago" }).Any(h => h.FolderNid == Recovery.FolderNid), "added to the index");
                var before = index.Done;
                index.AddFoundNow(deep);
                Equal(before, index.Done, "not added twice");
            }
            using (var again = new MailIndex(path, root2))
            {
                Check(again.Complete && again.FoundIndexed == 4, "remembered");
            }
        }

        private static void TestDeletedInOst2013()
        {
            using (var store = PstStore.Open(Samples.Example2013))
            {
                var quick = Recovery.Find(store, false);
                store.UseFound(quick);
                var rows = store.Items(store.FoundFolder);
                var subjects = string.Join(" | ", rows.Select(r => r.Subject + " (" + r.FoundIn + ")"));
                Check(rows.Count >= 3, "deleted emails in the 2013 OST: " + subjects);
                Check(rows.Any(r => r.Subject == "Test 3" && r.FoundIn == "Inbox" && !r.Partial), "Test 3: " + subjects);
                Check(rows.Any(r => r.Subject == "FW: Test 2" && r.FoundIn == "Inbox"), "FW: Test 2: " + subjects);
                var attachment = rows.Single(r => r.Subject == "Test Attachment");
                Equal("Sampler", attachment.FoundIn, "the folder it was in");
                Check(store.ReadItem(attachment.Nid).VisibleAttachments.Any(a => a.Data().Length > 0), "its attachment");
                var deep = Recovery.Find(store, true, null, default, quick);
                store.UseFound(deep);
                rows = store.Items(store.FoundFolder);
                Equal(deep.Items.Count, rows.Count, "every item found can be shown");
                var test1 = rows.SingleOrDefault(r => r.Subject == "Test1");
                Check(test1 != null && test1.Partial && test1.FoundIn == "Testing", "looking deeper finds a partly overwritten one: " + string.Join(" | ", rows.Select(r => $"{r.Subject} partial={r.Partial} in={r.FoundIn}")));
                Check(rows.All(r => r.Subject.Length > 0), "no empty leftovers");
            }
            using (var store = PstStore.Open(Samples.Enron))
            {
                Equal(0, Recovery.Find(store, true).Items.Count, "nothing is made up where nothing was deleted");
            }
        }
    }
}
