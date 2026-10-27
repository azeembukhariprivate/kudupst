using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Tests
{
    public static partial class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "kudupst-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            try
            {
                var tests = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test"));
                if (args.Length > 0) tests = tests.Where(m => args.Any(a => m.Name.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0));
                foreach (var m in tests)
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ================================================================== helpers

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected [{expected}], got [{actual}]");
        }

        private static PstFolder Child(PstStore store, PstFolder folder, string name)
        {
            var child = store.SubFolders(folder).FirstOrDefault(f => f.Name == name);
            Check(child != null, $"folder \"{name}\" in \"{folder.Name}\" (has: {string.Join(", ", store.SubFolders(folder).Select(f => f.Name))})");
            return child;
        }

        private static PstFolder FolderAt(PstStore store, params string[] names)
        {
            var folder = store.Root;
            foreach (var n in names) folder = Child(store, folder, n);
            return folder;
        }

        // ================================================================== PST: the Enron file (expected values from pst-extractor's tests)

        private static void TestEnronStore()
        {
            using (var store = PstStore.Open(Samples.Enron))
            {
                Equal(PstFormat.Unicode, store.File.Format, "format");
                Equal(PstCryptMethod.Compressible, store.File.Crypt, "encryption");
                Equal("Personal folders", store.DisplayName, "store name");
                Check(!store.HasPassword, "no password");
                var top = store.SubFolders(store.Root);
                Equal(3, top.Count, "root sub-folders");
                Equal("Top of Personal Folders", top[0].Name, "first root folder");
                Equal("Top of Personal Folders", store.Top.Name, "the folders people see");
                var subs = store.SubFolders(top[0]);
                Equal("Deleted Items", subs[0].Name, "first folder");
                Equal("lokay-m", subs[1].Name, "second folder");
                var mlokay = FolderAt(store, "Top of Personal Folders", "lokay-m", "MLOKAY (Non-Privileged)");
                Equal("TW-Commercial Group|Systems|Sent Items|Personal", string.Join("|", store.SubFolders(mlokay).Select(f => f.Name)), "MLOKAY's folders");
                Equal(1, mlokay.ItemCount, "MLOKAY item count");
                Equal(0, mlokay.UnreadCount, "MLOKAY unread");
                Equal("IPF.Note", mlokay.ContainerClass, "container class");
                Check(mlokay.HasSubfolders, "has sub-folders");
            }
        }

        private static void TestEnronMessages()
        {
            using (var store = PstStore.Open(Samples.Enron))
            {
                var group = FolderAt(store, "Top of Personal Folders", "lokay-m", "MLOKAY (Non-Privileged)", "TW-Commercial Group");
                var items = store.Items(group);
                Check(items.Count >= 2, "messages in TW-Commercial Group");

                Equal("New OBA's", items[0].Subject, "first summary subject");
                var first = store.ReadItem(items[0].Nid);
                Equal("IPM.Note", first.MessageClass, "message class");
                Equal("New OBA's", first.Subject, "subject");
                Equal("Dennis.Lee@ENRON.com", first.From.Address, "from address");
                Equal("Lee  Dennis", first.From.Name, "from name");
                Equal("Lindberg  Lorraine; Watson  Kimberly", first.DisplayTo, "display to");
                Check(first.Body.Text.Contains("Mojave (Effective date 12/02/01) and Williams Field Services"), "body text");
                Equal(678, first.Body.Text.Length, "body length");
                Equal(3, first.Recipients.Count, "recipients");
                Equal("Lindberg  Lorraine", first.Recipients[0].Address.Name, "recipient 1 name");
                Equal("Lorraine.Lindberg@ENRON.com", first.Recipients[0].Address.Address, "recipient 1 address");
                Equal("Kimberly.Watson@ENRON.com", first.Recipients[1].Address.Address, "recipient 2 address");
                Equal("Dennis.Lee@ENRON.com", first.Recipients[2].Address.Address, "recipient 3 address");

                var second = store.ReadItem(items[1].Nid);
                Equal("I/B Link Capacity for November and December 2001", second.Subject, "subject 2");
                Equal("Michelle Lokay (E-mail)", second.DisplayTo, "display to 2");
                Equal("Reames Julie", second.From.Name, "from 2");
                Equal("JReames@br-inc.com", second.From.Address, "from address 2");
                Check(second.Body.Text.Contains("Per our earlier conversation, Burlington Resources agrees to purchase 20,000"), "body 2");
                Equal(661, second.Body.Text.Length, "body 2 length");
                Check(second.Headers.Contains("date: Wed, 24 Oct 2001 10:07:37 -0700 (PDT) Wed, 24 Oct 2001 10:07:37 -0500\r\nM"), "headers");
                Equal(new DateTime(2001, 10, 24, 17, 7, 37, DateTimeKind.Utc), second.Sent, "sent");
                Equal(new DateTime(2001, 10, 24, 17, 7, 37, DateTimeKind.Utc), second.Received, "received");
                Equal("<OXDAXN4L22RH32V3FYRFYTV2QE0MXYONB@zlsvr22>", second.MessageId, "message id");
                Check(second.Read, "read");
                Equal(0, second.Attachments.Count, "no attachments");
                Equal("michelle.lokay@enron.com", second.Recipients[0].Address.Address, "recipient smtp");

                // An item whose data is spread over several blocks.
                var personal = FolderAt(store, "Top of Personal Folders", "lokay-m", "MLOKAY (Non-Privileged)", "Personal");
                var p = store.ReadItem(store.Items(personal)[0].Nid);
                Equal("Fwd: Enjoy fall in an Alamo midsize car -- just $169 a week!", p.Subject, "multi-block subject");
                Check(p.Body.PlainText.Length > 1000, "multi-block body");
            }
        }

        /// <summary>Every folder and item in the file, read without errors.</summary>
        private static void TestEnronReadEverything()
        {
            using (var store = PstStore.Open(Samples.Enron))
            {
                var count = 0;
                foreach (var folder in store.AllFolders(store.Root))
                {
                    foreach (var summary in store.Items(folder))
                    {
                        var item = store.ReadItem(summary.Nid);
                        Equal(summary.Subject, item.Subject, "summary and item agree");
                        var text = item.Body.PlainText;
                        foreach (var a in item.Attachments) { if (!a.IsEmail) a.Data(); else Check(a.Embedded() != null, "attached email"); }
                        count++;
                    }
                }
                Equal(71, count, "items read");
            }
        }

        // ================================================================== OST: Outlook 2013+ (4 KB pages, compressed blocks)

        /// <summary>
        /// The whole folder tree, as pst-extractor lists it, and every item it lists. (Its list was made from an older
        /// copy of the file, so this copy has more items: each folder's item count is checked instead.)
        /// </summary>
        private static void TestMountainManTree()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                Equal(PstFormat.Unicode4K, store.File.Format, "format");
                Check(store.File.IsOst, "an OST file");
                Check(!store.HasPassword, "OST files have no password");
                Equal("IPM_SUBTREE", store.Top.Name, "the folders people see");
                Equal("Root - Mailbox", store.Folder(store.Top.ParentNid).Name, "in the mailbox, not public folders");
                var sb = new StringBuilder();
                Dump(store, store.Root, 0, sb);
                var expected = File.ReadAllText(Samples.MountainManTree).Replace("\r\n", "\n").TrimEnd().Split('\n');
                var actual = sb.ToString().TrimEnd().Split('\n');
                var expectedFolders = expected.Where(l => !l.Contains("|- Email: ")).ToList();
                var actualFolders = actual.Where(l => !l.Contains("|- Email: ")).ToList();
                for (var i = 0; i < Math.Max(expectedFolders.Count, actualFolders.Count); i++)
                {
                    var el = i < expectedFolders.Count ? expectedFolders[i] : "(nothing)";
                    var al = i < actualFolders.Count ? actualFolders[i] : "(nothing)";
                    if (el != al) throw new Exception($"folder line {i + 1}: expected [{el}], got [{al}]");
                }
                var mine = new HashSet<string>(actual);
                foreach (var line in expected.Where(l => l.Contains("|- Email: ")))
                    Check(mine.Contains(line), "listed: " + line);
                foreach (var folder in store.AllFolders(store.Root).Where(f => !f.IsSearchFolder))
                    Equal(folder.ItemCount, store.Items(folder).Count, "items in " + folder.Name);
            }
        }

        private static void Dump(PstStore store, PstFolder folder, int depth, StringBuilder sb)
        {
            if (depth > 0) sb.Append(string.Concat(Enumerable.Repeat(" | ", depth - 1))).Append(" |- ").Append(folder.Name).Append('\n');
            foreach (var sub in store.SubFolders(folder)) Dump(store, sub, depth + 1, sb);
            foreach (var item in store.Items(folder))
                sb.Append(string.Concat(Enumerable.Repeat(" | ", depth))).Append(" |- Email: ").Append(item.Nid).Append(" - ").Append(item.Subject).Append('\n');
        }

        private static void TestMountainManAttachments()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var subtree = FolderAt(store, "Root - Mailbox", "IPM_SUBTREE");
                var inbox = Child(store, subtree, "Inbox");
                var items = store.Items(inbox);
                var word = store.ReadItem(items[2].Nid);
                Equal("word attachment", word.Subject, "subject");
                Equal("OBA_2760.doc", word.Attachments[0].FileName, "word file name");
                Equal(54044L, word.Attachments[0].Size, "word attachment size");
                var excel = store.ReadItem(items[3].Nid);
                Equal("RedRockA.xls", excel.Attachments[0].FileName, "excel file name");
                Equal(31016L, excel.Attachments[0].Size, "excel attachment size");
                var jpg = store.ReadItem(items[4].Nid);
                Equal("rickroll.jpg", jpg.Attachments[0].FileName, "jpg name");
                Equal(5020L, jpg.Attachments[0].Size, "jpg attachment size");

                var tasks = Child(store, subtree, "Tasks");
                var task = store.ReadItem(store.Items(tasks)[0].Nid);
                Equal("IPM.Task", task.MessageClass, "task class");
                var a = task.Attachments[0];
                Equal("rickroll.jpg", a.FileName, "task attachment");
                Equal(4796, a.Data().Length, "task attachment contents");
                Check(a.Data().SequenceEqual(File.ReadAllBytes(Samples.Rickroll)), "attachment matches the original picture");

                var contacts = Child(store, subtree, "Contacts");
                var contact = store.ReadItem(store.Items(contacts).First(i => i.MessageClass == "IPM.Contact").Nid);
                Equal("ContactPicture.jpg", contact.Attachments[0].FileName, "contact picture");
                Equal(14055L, contact.Attachments[0].Size, "contact picture size");
                Equal("rickroll.jpg", contact.Attachments[1].FileName, "contact's second attachment");
            }
        }

        private static void TestMountainManReadEverything()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var count = 0;
                foreach (var folder in store.AllFolders(store.Root))
                    foreach (var summary in store.Items(folder))
                    {
                        var item = store.ReadItem(summary.Nid);
                        var text = item.Body.PlainText;
                        foreach (var a in item.Attachments) { if (!a.IsEmail) a.Data(); }
                        count++;
                    }
                Check(count > 40, "items read: " + count);
            }
        }

        private static void TestExample2013()
        {
            using (var store = PstStore.Open(Samples.Example2013))
            {
                Equal(PstFormat.Unicode4K, store.File.Format, "format");
                Equal(2, store.Folder(8578).ItemCount, "inbox items");
                var msg = store.ReadItem(2097284);
                Check(msg.Body.PlainText.Contains("This is an e-mail message sent automatically by Microsoft Outlook while testing the settings for your account."), "body: " + msg.Body.PlainText);
            }
        }

        private static void TestPasswords()
        {
            using (var store = PstStore.Open(Samples.Passworded))
            {
                Check(store.HasPassword, "passworded.pst has a password");
                Check(!store.CheckPassword("wrong password"), "a wrong password is refused");
                // "Forgot the password": the file can still be read.
                Check(store.SubFolders(store.Root).Count > 0, "folders readable");
            }
            using (var store = PstStore.Open(Samples.DistList))
            {
                Check(!store.HasPassword, "dist-list.pst has no password");
                Check(store.CheckPassword(""), "no password needed");
            }
        }

        // ================================================================== MSG files (expected values from msg-extractor's tests)

        private static void TestMsgMultiTo()
        {
            var msg = MsgReader.Read(Samples.MultiTo);
            Check(msg.Subject.StartsWith("Test: multiple To recipients"), "subject: " + msg.Subject);
            Equal("bob@example.com", msg.From.Address, "from");
            Check(msg.Body.PlainText.StartsWith("Test email body."), "body: " + msg.Body.PlainText);
            var to = msg.To.Select(a => a.Address).ToList();
            Check(to.Count >= 2 && to.Contains("alice@example.com") && to.Contains("carol@example.com"), "to: " + string.Join(", ", to));
            Check(msg.Cc.Any(a => a.Address == "dave@example.com"), "cc");
        }

        private static void TestMsgUnicode()
        {
            var msg = MsgReader.Read(Samples.UnicodeMsg);
            Equal(2, msg.Attachments.Count, "attachments");
            foreach (var a in msg.Attachments)
            {
                Equal("image/tiff", a.MimeType, "attachment type");
                Check(a.FileName.Length > 0 && a.Data().Length > 0, "attachment contents");
            }
            var header = MsgReader.Read(Samples.UnicodeHeader);
            Check(header.To.Any(a => a.Address == "alice@example.com") || header.Headers.Contains("alice@example.com"), "alice");
            var strange = MsgReader.Read(Samples.StrangeDate);
            Check(strange.Subject.Length > 0 || strange.Body.PlainText.Length > 0, "strange date message readable");
        }
    }
}
