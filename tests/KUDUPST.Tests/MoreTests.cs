using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Tests
{
    public static partial class Program
    {
        // ================================================================== made-up PST files: ANSI, "high" encryption, big values

        private const uint TopNid = 0x8022, InboxNid = 0x8042, MessageNid = 0x200024;

        /// <summary>A small PST with one folder holding one email (text and HTML body, two recipients, a big attachment, an attached email).</summary>
        private static byte[] MakePst(bool unicode, PstCryptMethod crypt, string password = null, Action<PstWriter> extra = null)
        {
            var w = new PstWriter(unicode, crypt);
            var ansi = Encoding.GetEncoding(1252);
            PstWriter.Prop S(ushort id, string s) => unicode ? PstWriter.Str(id, s) : PstWriter.Str8(id, s, ansi);
            var stringType = unicode ? PropType.Unicode : PropType.String8;

            // The message store, pointing at the top folder.
            var entryId = new byte[24];
            BitConverter.GetBytes(TopNid).CopyTo(entryId, 20);
            var storeProps = new List<PstWriter.Prop> { S(PropId.DisplayName, "Test mailbox"), PstWriter.Bin(PropId.IpmSubtreeEntryId, entryId) };
            if (password != null) storeProps.Add(PstWriter.Int(PropId.PstPassword, (int)Crc32.Standard(ansi.GetBytes(password))));
            w.Node(PstStore.StoreNid, w.DataBlock(w.PropertyContext(storeProps)));

            var folderColumns = new List<(ushort, ushort)> { (PropId.LtpRowId, PropType.Int32), (PropId.DisplayName, stringType) };
            void Folder(uint nid, string name, uint parent, int count, uint[] children)
            {
                w.Node(nid, w.DataBlock(w.PropertyContext(new[] { S(PropId.DisplayName, name), PstWriter.Int(PropId.ContentCount, count), PstWriter.Bool(PropId.Subfolders, children.Length > 0) })), 0, parent);
                w.Node((nid & ~0x1Fu) | 0x0D, w.DataBlock(w.TableContext(folderColumns, children.Select(c => new object[] { c, "child" }).ToList(), ansi)), 0, nid);
            }
            Folder(PstStore.RootFolderNid, string.Empty, PstStore.RootFolderNid, 0, new[] { TopNid });
            Folder(TopNid, "Top of Personal Folders", PstStore.RootFolderNid, 0, new[] { InboxNid });
            Folder(InboxNid, "Inbox", TopNid, 1, new uint[0]);

            var contentColumns = new List<(ushort, ushort)>
            {
                (PropId.LtpRowId, PropType.Int32), (PropId.Subject, stringType), (PropId.SentRepresentingName, stringType),
                (PropId.MessageDeliveryTime, PropType.SysTime), (PropId.MessageFlags, PropType.Int32), (PropId.MessageClass, stringType), (PropId.MessageSize, PropType.Int32)
            };
            var received = new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Utc);
            w.Node((InboxNid & ~0x1Fu) | 0x0E, w.DataBlock(w.TableContext(contentColumns, new List<object[]>
            {
                new object[] { MessageNid, "\u0001\u0004RE: Café meeting", "Zoë Example", received, 0x11, "IPM.Note", 123456 }
            }, ansi)), 0, InboxNid);

            // The message: properties, recipients, attachments (one big, one an attached email).
            var subs = new List<(uint, ulong, ulong)>();
            var html = "<html><head><meta charset=\"windows-1252\"></head><body><p>Hello <b>Zoë</b> – see the café menu.</p><img src=\"cid:logo@x\"></body></html>";
            var msgProps = new List<PstWriter.Prop>
            {
                S(PropId.MessageClass, "IPM.Note"),
                S(PropId.Subject, "\u0001\u0004RE: Café meeting"),
                S(PropId.SentRepresentingName, "Zoë Example"),
                S(PropId.SentRepresentingEmail, "zoe@example.com"),
                S(PropId.SentRepresentingAddrType, "SMTP"),
                S(PropId.DisplayTo, "Bob; Ann"),
                PstWriter.Time(PropId.MessageDeliveryTime, received),
                PstWriter.Time(PropId.ClientSubmitTime, received.AddMinutes(-2)),
                PstWriter.Int(PropId.MessageFlags, 0x11),
                PstWriter.Int(PropId.InternetCodepage, 1252),
                S(PropId.Body, "Hello Zoë – see the café menu." + new string('x', 5000)),
                PstWriter.Bin(PropId.Html, ansi.GetBytes(html)),
                S(PropId.InternetMessageId, "<abc@example.com>")
            };
            var msgData = w.PropertyContext(msgProps, subs);

            var recipientColumns = new List<(ushort, ushort)>
            {
                (PropId.LtpRowId, PropType.Int32), (PropId.DisplayName, stringType), (PropId.EmailAddress, stringType),
                (PropId.AddrType, stringType), (PropId.RecipientType, PropType.Int32)
            };
            subs.Add((0x692, w.DataBlock(w.TableContext(recipientColumns, new List<object[]>
            {
                new object[] { 1, "Bob", "bob@example.com", "SMTP", 1 },
                new object[] { 2, "Ann Ström", "ann@example.com", "SMTP", 2 }
            }, ansi)), 0));

            // Attachment 1: 30 KB of data, stored over several blocks.
            var big = new byte[30000];
            new Random(7).NextBytes(big);
            var att1Subs = new List<(uint, ulong, ulong)>();
            var att1 = w.PropertyContext(new[] { S(PropId.AttachLongFilename, "Zahlen ä.bin"), PstWriter.Int(PropId.AttachMethod, 1), PstWriter.Bin(PropId.AttachDataBinary, big), PstWriter.Int(PropId.AttachSize, 30100) }, att1Subs);
            var att1Nid = 0x8025u;
            subs.Add((att1Nid, w.DataBlock(att1), w.SubNodes(att1Subs)));

            // Attachment 2: a picture used by the HTML body.
            var logo = Encoding.ASCII.GetBytes("GIF89a fake picture");
            var att2Nid = 0x8045u;
            subs.Add((att2Nid, w.DataBlock(w.PropertyContext(new[]
            {
                S(PropId.AttachLongFilename, "logo.gif"), PstWriter.Int(PropId.AttachMethod, 1), PstWriter.Bin(PropId.AttachDataBinary, logo),
                S(PropId.AttachContentId, "logo@x"), S(PropId.AttachMimeTag, "image/gif"), PstWriter.Int(PropId.AttachFlags, 4)
            })), 0));

            // Attachment 3: an attached email.
            var embeddedNid = 0x8061u;
            var embedded = w.PropertyContext(new[] { S(PropId.MessageClass, "IPM.Note"), S(PropId.Subject, "The forwarded one"), S(PropId.Body, "Inside text") });
            var heapRef = new PstWriter.HeapBuilder();
            var att3Subs = new List<(uint, ulong, ulong)> { (embeddedNid, w.DataBlock(embedded), 0) };
            var objectValue = new byte[8];
            BitConverter.GetBytes(embeddedNid).CopyTo(objectValue, 0);
            var att3 = w.PropertyContext(new[]
            {
                S(PropId.AttachLongFilename, "The forwarded one"), PstWriter.Int(PropId.AttachMethod, 5),
                new PstWriter.Prop { Id = PropId.AttachDataBinary, Type = PropType.Object, Value = objectValue }
            });
            var att3Nid = 0x8065u;
            subs.Add((att3Nid, w.DataBlock(att3), w.SubNodes(att3Subs)));

            var attachmentColumns = new List<(ushort, ushort)> { (PropId.LtpRowId, PropType.Int32), (PropId.AttachMethod, PropType.Int32) };
            subs.Add((0x671, w.DataBlock(w.TableContext(attachmentColumns, new List<object[]>
            {
                new object[] { att1Nid, 1 }, new object[] { att2Nid, 1 }, new object[] { att3Nid, 5 }
            }, ansi)), 0));

            w.Node(MessageNid, w.Data(msgData), w.SubNodes(subs), InboxNid);
            extra?.Invoke(w);
            return w.Build();
        }

        private static PstStore OpenBytes(byte[] data) => new PstStore(new PstFile(new MemoryStream(data)));

        private static void CheckMadeUp(byte[] data, bool unicode)
        {
            using (var store = OpenBytes(data))
            {
                Equal(unicode ? PstFormat.Unicode : PstFormat.Ansi, store.File.Format, "format");
                Equal("Test mailbox", store.DisplayName, "store name");
                Equal("Top of Personal Folders", store.Top.Name, "top folder");
                var inbox = store.SubFolders(store.Top).Single();
                Equal("Inbox", inbox.Name, "inbox");
                var rows = store.Items(inbox);
                Equal(1, rows.Count, "rows");
                Equal("RE: Café meeting", rows[0].Subject, "summary subject (marker removed)");
                Check(rows[0].HasAttachments && rows[0].Read, "flags");
                var m = store.ReadItem(rows[0].Nid);
                Equal("RE: Café meeting", m.Subject, "subject");
                Equal("Zoë Example <zoe@example.com>", m.From.Display, "from");
                Equal(new DateTime(2024, 3, 5, 9, 28, 0, DateTimeKind.Utc), m.Sent, "sent");
                Check(m.Body.Text.StartsWith("Hello Zoë – see the café menu.") && m.Body.Text.Length == 5030, "big body text: " + m.Body.Text.Length);
                Check(m.Body.Html.Contains("Hello <b>Zoë</b> – see the café menu."), "html in its code page: " + m.Body.Html);
                Equal(2, m.Recipients.Count, "recipients");
                Equal("Ann Ström", m.Cc.Single().Name, "cc");
                Equal(3, m.Attachments.Count, "attachments");
                var big = new byte[30000];
                new Random(7).NextBytes(big);
                Equal("Zahlen ä.bin", m.Attachments[0].FileName, "attachment name");
                Check(m.Attachments[0].Data().SequenceEqual(big), "attachment over several blocks");
                Equal("logo@x", m.Attachments[1].ContentId, "content id");
                Check(m.Attachments[1].Inline, "picture in the body");
                Check(m.Attachments[2].IsEmail, "attached email");
                var inner = m.Attachments[2].Embedded();
                Equal("The forwarded one", inner.Subject, "attached email subject");
                Equal("Inside text", inner.Body.Text, "attached email body");
                Equal(2, m.VisibleAttachments.Count(), "listed attachments");
            }
        }

        private static void TestMadeUpUnicodeCompressible() => CheckMadeUp(MakePst(true, PstCryptMethod.Compressible), true);
        private static void TestMadeUpUnicodeHigh() => CheckMadeUp(MakePst(true, PstCryptMethod.High), true);
        private static void TestMadeUpUnicodePlain() => CheckMadeUp(MakePst(true, PstCryptMethod.None), true);
        private static void TestMadeUpAnsiCompressible() => CheckMadeUp(MakePst(false, PstCryptMethod.Compressible), false);
        private static void TestMadeUpAnsiHigh() => CheckMadeUp(MakePst(false, PstCryptMethod.High), false);

        private static void TestMadeUpPassword()
        {
            using (var store = OpenBytes(MakePst(true, PstCryptMethod.Compressible, "Secret1")))
            {
                Check(store.HasPassword, "has a password");
                Check(store.CheckPassword("Secret1"), "right password");
                Check(!store.CheckPassword("secret1"), "wrong case refused");
                Check(!store.CheckPassword(""), "empty refused");
            }
        }

        private static void TestNotAPst()
        {
            foreach (var data in new[] { new byte[10], Encoding.ASCII.GetBytes(new string('x', 2000)) })
            {
                try
                {
                    OpenBytes(data);
                    throw new Exception("expected an error");
                }
                catch (PstException)
                {
                }
            }
        }

        private static void TestCryptRoundTrip()
        {
            var data = new byte[1000];
            new Random(3).NextBytes(data);
            var copy = (byte[])data.Clone();
            PstCrypt.EncodeCompressible(copy, 0, copy.Length);
            Check(!copy.SequenceEqual(data), "scrambled");
            PstCrypt.DecodeCompressible(copy, 0, copy.Length);
            Check(copy.SequenceEqual(data), "compressible round trip");
            PstCrypt.EncodeCyclic(copy, 0, copy.Length, 0x12345678);
            PstCrypt.DecodeCyclic(copy, 0, copy.Length, 0x12345678);
            Check(copy.SequenceEqual(data), "cyclic round trip");
        }

        // ================================================================== RTF bodies

        /// <summary>The examples from MS-OXRTFCP section 3.</summary>
        private static void TestCompressedRtf()
        {
            byte[] H(string hex) => hex.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();
            var one = RtfBody.Decompress(H("2d 00 00 00 2b 00 00 00 4c 5a 46 75 f1 c5 c7 a7 03 00 0a 00 72 63 70 67 31 32 35 42 32 0a f3 20 68 65 6c 09 00 20 62 77 05 b0 6c 64 7d 0a 80 0f a0"));
            Equal("{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n", Encoding.ASCII.GetString(one), "example 1");
            var two = RtfBody.Decompress(H("1a 00 00 00 1c 00 00 00 4c 5a 46 75 e2 d4 4b 51 41 00 04 20 57 58 59 5a 0d 6e 7d 01 0e b0"));
            Equal("{\\rtf1 WXYZWXYZWXYZWXYZWXYZ}", Encoding.ASCII.GetString(two), "example 2");
            // Stored without compression ("MELA").
            var raw = Encoding.ASCII.GetBytes("{\\rtf1 plain}");
            var mela = BitConverter.GetBytes(raw.Length + 12).Concat(BitConverter.GetBytes(raw.Length)).Concat(Encoding.ASCII.GetBytes("MELA")).Concat(new byte[4]).Concat(raw).ToArray();
            Equal("{\\rtf1 plain}", Encoding.ASCII.GetString(RtfBody.Decompress(mela)), "uncompressed");
        }

        private static void TestRtfHtml()
        {
            var rtf = "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\deff0{\\fonttbl{\\f0\\fswiss Arial;}{\\f1\\fnil\\fcharset204 Courier;}}" +
                      "{\\*\\htmltag19 <html>}{\\*\\htmltag34 <head>}{\\*\\htmltag161 <title>}Test{\\*\\htmltag169 </title>}{\\*\\htmltag41 </head>}" +
                      "{\\*\\htmltag50 <body>}Hello \\'e9 world\\htmlrtf \\par\\htmlrtf0 {\\*\\htmltag64 <p>}\\f1 \\'cf\\'f0\\'e8\\'e2\\'e5\\'f2\\f0 \\{x\\}\\u8364?{\\*\\htmltag72 </p>}" +
                      "{\\*\\htmltag58 </body>}{\\*\\htmltag27 </html>}}";
            Equal("<html><head><title>Test</title></head><body>Hello é world<p>Привет{x}€</p></body></html>", RtfBody.ExtractHtml(rtf), "html from rtf");
            Check(RtfBody.ToText(rtf).Contains("Hello é world"), "text from html rtf");
            var plain = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}\\f0 First line\\par Second\\tab line\\par{\\*\\generator Riched20;}\\'e9t\\'e9}";
            Equal("First line\r\nSecond\tline\r\nété", RtfBody.ToText(plain), "text from rtf");
        }

        // ================================================================== MIME / EML

        private static string SampleEml =>
            "From: =?utf-8?B?Wm/DqyBFeGFtcGxl?= <zoe@example.com>\r\n" +
            "To: \"Bob, Jr.\" <bob@example.com>, ann@example.com\r\n" +
            "Cc: Team: carl@example.com, dan@example.com;\r\n" +
            "Subject: =?iso-8859-1?Q?Caf=E9?=\r\n =?utf-8?B?IG1lbnUg4oCU?= today\r\n" +
            "Date: Tue, 5 Mar 2024 10:30:00 +0100 (CET)\r\n" +
            "Message-ID: <m1@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
            "\r\n" +
            "preamble\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/related; boundary=inner\r\n" +
            "\r\n" +
            "--inner\r\n" +
            "Content-Type: multipart/alternative; boundary=\"alt\"\r\n" +
            "\r\n" +
            "--alt\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            "Caf=C3=A9 menu =E2=80=94 soft=\r\n" +
            "break\r\n" +
            "--alt\r\n" +
            "Content-Type: text/html; charset=\"iso-8859-1\"\r\n" +
            "\r\n" +
            "<p>Café</p><img src=\"cid:pic1\">\r\n" +
            "--alt--\r\n" +
            "--inner\r\n" +
            "Content-Type: image/png\r\n" +
            "Content-ID: <pic1>\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "iVBORw0KGgo=\r\n" +
            "--inner--\r\n" +
            "--outer\r\n" +
            "Content-Type: application/pdf; name=\"old.pdf\"\r\n" +
            "Content-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9.pdf\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "JVBERi0xLjQK\r\n" +
            "--outer\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "\r\n" +
            "Subject: Inner\r\n" +
            "From: x@example.com\r\n" +
            "\r\n" +
            "Inner body\r\n" +
            "--outer--\r\n";

        private static void TestEmlRead()
        {
            var m = EmlReader.Read(Encoding.GetEncoding(28591).GetBytes(SampleEml));
            Equal("Café menu — today", m.Subject, "subject");
            Equal("Zoë Example <zoe@example.com>", m.From.Display, "from");
            Equal("Bob, Jr.|ann@example.com", string.Join("|", m.To.Select(a => a.Short)), "to");
            Equal("carl@example.com|dan@example.com", string.Join("|", m.Cc.Select(a => a.Address)), "group cc");
            Equal(new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Utc), m.Sent, "date");
            Equal("Café menu — softbreak", m.Body.Text.Trim(), "text body");
            Equal("<p>Café</p><img src=\"cid:pic1\">", m.Body.Html.Trim(), "html body");
            Equal(3, m.Attachments.Count, "attachments");
            Check(m.Attachments[0].Inline && m.Attachments[0].ContentId == "pic1", "inline picture");
            Equal(8, m.Attachments[0].Data().Length, "picture bytes");
            Equal("résumé.pdf", m.Attachments[1].FileName, "RFC 2231 file name");
            Equal("%PDF-1.4\n", Encoding.ASCII.GetString(m.Attachments[1].Data()), "pdf bytes");
            Check(m.Attachments[2].IsEmail, "attached email");
            Equal("Inner", m.Attachments[2].Embedded().Subject, "attached email subject");
            Equal(2, m.VisibleAttachments.Count(), "listed attachments");
        }

        private static void TestDates()
        {
            Equal(new DateTime(2001, 10, 24, 17, 7, 37, DateTimeKind.Utc), Mime.ParseDate("Wed, 24 Oct 2001 10:07:37 -0700 (PDT)"), "with zone");
            Equal(new DateTime(2001, 10, 24, 17, 7, 0, DateTimeKind.Utc), Mime.ParseDate("24 Oct 01 12:07 EST"), "two-digit year, named zone");
            Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), Mime.ParseDate("Thu, 2 Jan 2020 03:04:05 GMT"), "GMT");
            Equal((DateTime?)null, Mime.ParseDate("not a date"), "rubbish");
        }

        private static void TestEmlWriteAndReadBack()
        {
            using (var store = OpenBytes(MakePst(true, PstCryptMethod.Compressible)))
            {
                var original = store.ReadItem(MessageNid);
                var eml = EmlWriter.Write(original);
                var back = EmlReader.Read(eml);
                Equal(original.Subject, back.Subject, "subject");
                Equal(original.From.Display, back.From.Display, "from");
                Equal("Bob <bob@example.com>", back.To.Single().Display, "to");
                Equal("Ann Ström <ann@example.com>", back.Cc.Single().Display, "cc");
                Equal(original.Sent, back.Sent, "date");
                Equal("<abc@example.com>", back.MessageId, "message id");
                Equal(original.Body.Text.Replace("\r\n", "\n"), back.Body.Text.Replace("\r\n", "\n"), "text");
                Equal(original.Body.Html.Replace("\r\n", "\n"), back.Body.Html.Replace("\r\n", "\n"), "html");
                Equal(3, back.Attachments.Count, "attachments");
                var big = back.Attachments.First(a => a.FileName == "Zahlen ä.bin");
                Check(big.Data().SequenceEqual(original.Attachments[0].Data()), "attachment bytes");
                var logo = back.Attachments.First(a => a.ContentId == "logo@x");
                Check(logo.Inline, "picture still inline");
                Equal("The forwarded one", back.Attachments.First(a => a.IsEmail).Embedded().Subject, "attached email");
                foreach (var line in Encoding.UTF8.GetString(eml).Split('\n'))
                    Check(line.Length <= 998, "line length");
            }
            // Real files too.
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                foreach (var folder in store.AllFolders(store.Root))
                    foreach (var s in store.Items(folder))
                    {
                        var item = store.ReadItem(s.Nid);
                        var back = EmlReader.Read(EmlWriter.Write(item));
                        Equal(item.Subject, back.Subject, "real subject");
                        Equal(item.Attachments.Count, back.Attachments.Count, "real attachments of " + item.Subject);
                    }
            }
            var msg = MsgReader.Read(Samples.MultiTo);
            var fromMsg = EmlReader.Read(EmlWriter.Write(msg));
            Check(fromMsg.To.Any(a => a.Address == "alice@example.com") && fromMsg.Cc.Any(a => a.Address == "dave@example.com"), "msg to eml recipients");
        }

        // ================================================================== exports

        private static List<ExportItem> ExportItems(PstStore store)
        {
            var list = new List<ExportItem>();
            foreach (var folder in store.AllFolders(store.Top))
            {
                var path = string.Join("/", store.PathOf(folder.Nid).Skip(1).Select(f => f.Name));
                foreach (var s in store.Items(folder))
                {
                    var nid = s.Nid;
                    list.Add(new ExportItem { Load = () => store.ReadItem(nid), FolderPath = path, Subject = s.Subject, Date = s.Date });
                }
            }
            return list;
        }

        private static void TestExportEmlAndAttachments()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var items = ExportItems(store);
                var dest = Path.Combine(_root, "eml");
                var result = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Eml, Destination = dest, KeepFolders = true }, null, CancellationToken.None);
                Equal(items.Count, result.Exported, "all exported");
                Equal(0, result.Problems.Count, "no problems");
                var files = Directory.GetFiles(dest, "*.eml", SearchOption.AllDirectories);
                Equal(items.Count, files.Length, "one file each");
                Check(Directory.Exists(Path.Combine(dest, "Inbox")), "folder structure kept");
                Check(files.Any(f => Path.GetFileName(f).EndsWith(" - word attachment.eml")), "named by date and subject");

                var att = Path.Combine(_root, "att");
                var r2 = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Attachments, Destination = att, KeepFolders = false }, null, CancellationToken.None);
                var saved = Directory.GetFiles(att).Select(Path.GetFileName).ToList();
                Check(saved.Contains("OBA_2760.doc") && saved.Contains("RedRockA.xls") && saved.Contains("rickroll.jpg") && saved.Contains("rickroll (2).jpg"), "attachments: " + string.Join(", ", saved));
                Check(File.ReadAllBytes(Path.Combine(att, saved.First(n => n.StartsWith("rickroll")))).Length > 0, "attachment bytes");

                var csv = Path.Combine(_root, "list.csv");
                var r3 = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Csv, Destination = csv }, null, CancellationToken.None);
                var lines = File.ReadAllLines(csv, Encoding.UTF8);
                Equal(items.Count + 1, lines.Length, "csv rows");
                Check(lines[0].StartsWith("Folder,Date,From"), "csv header");
                Check(lines.Any(l => l.Contains("word attachment") && l.Contains("OBA_2760.doc")), "csv row");
            }
        }

        private static void TestFileNames()
        {
            Equal("a_b_c_.txt", Exporter.Clean("a/b\\c?.txt"), "bad characters");
            Equal("_CON.eml", Exporter.Clean("CON.eml"), "reserved names");
            Equal("_", Exporter.Clean(".."), "dots");
            Equal("2024-03-05 0930 - Hi there.eml", Exporter.FileName(new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Local), "Hi there", ".eml"), "file name");
            Equal("'=SUM(A1)", Exporter.CsvCell("=SUM(A1)"), "formula cells are made plain text");
            Equal("\"a,\"\"b\"\"\"", Exporter.CsvCell("a,\"b\""), "quoting");
        }

        // ================================================================== search

        private static void TestSearchIndex()
        {
            var root = Path.Combine(_root, "index");
            using (var index = new MailIndex(Samples.Enron, root))
            {
                Check(!index.Complete, "new index");
                index.BuildNow();
                Check(index.Complete, "complete");
                Equal(71, index.Done, "all items");
                var hits = index.Search(new SearchQuery { Text = "Burlington" });
                Check(hits.Any(h => h.Subject == "I/B Link Capacity for November and December 2001"), "body word found");
                hits = index.Search(new SearchQuery { Text = "burl resources" });
                Check(hits.Any(h => h.Subject == "I/B Link Capacity for November and December 2001"), "prefix and AND");
                Check(index.Search(new SearchQuery { Text = "Burlington zzzqqq" }).Count == 0, "AND excludes");
                hits = index.Search(new SearchQuery { Text = "reames", Field = SearchField.From });
                Check(hits.Count > 0 && hits.All(h => h.From.IndexOf("Reames", StringComparison.OrdinalIgnoreCase) >= 0), "from field");
                hits = index.Search(new SearchQuery { Text = "", From = new DateTime(2001, 10, 24, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2001, 10, 25, 0, 0, 0, DateTimeKind.Utc) });
                Check(hits.Count > 0 && hits.All(h => h.Date.Value.Day == 24), "date range");
                var all = index.Search(new SearchQuery());
                Check(all.Count == 71 && all.First().Date >= all.Last().Date, "newest first");
            }
            // Opened again: the index is reused, not rebuilt.
            using (var again = new MailIndex(Samples.Enron, root))
            {
                Check(again.Complete && again.Done == 71, "kept on disk");
                Check(again.Search(new SearchQuery { Text = "Burlington" }).Count > 0, "still searchable");
            }
            // Several pieces merged (the OST has more items than one piece holds? no: force small pieces by indexing twice over).
            using (var ost = new MailIndex(Samples.MountainMan, root))
            {
                ost.BuildNow();
                Check(ost.Search(new SearchQuery { Text = "rickroll" }).Count >= 2, "attachment names searchable");
                Check(ost.Search(new SearchQuery { Text = "", OnlyWithAttachments = true }).All(i => i.HasAttachments), "attachments filter");
                Check(ost.Search(new SearchQuery { Text = "666-9776" }).Any(i => i.Subject == "Ed Pfromer"), "contacts found by phone number");
                Check(ost.Search(new SearchQuery { Text = "klonzo" }).Count(i => i.MessageClass == "IPM.Contact") == 2, "contacts found by company");
                Check(ost.Search(new SearchQuery { Text = "kpfromer2" }).Any(i => i.MessageClass == "IPM.DistList"), "groups found by member");
            }
            MailIndex.CleanUp(root);
            Check(Directory.GetDirectories(root).Length == 2, "indexes of existing files are kept");
        }

        private static void TestWords()
        {
            Equal("hello|world|dennis|lee|enron|com|2024", string.Join("|", Words.Of("Hello, WORLD! Dennis.Lee@ENRON.com 2024")), "words");
            Equal("東|京|tokyo", string.Join("|", Words.Of("東京 Tokyo")), "CJK one by one");
            Equal("café|zoë", string.Join("|", Words.Of("Café ZOË")), "accents kept");
        }

        // ================================================================== the viewer's page

        private static void TestMailPage()
        {
            using (var store = OpenBytes(MakePst(true, PstCryptMethod.Compressible)))
            {
                var m = store.ReadItem(MessageNid);
                var page = System.Net.WebUtility.HtmlDecode(MailHtml.Page(m, new MailHtml.Options()));
                Check(page.Contains("default-src 'none'"), "nothing loaded from the internet");
                Check(!page.Contains("cid:logo@x") && page.Contains(MailHtml.Base + "part/1"), "picture served by the app");
                Check(page.Contains("RE: Café meeting") && page.Contains("Zahlen ä.bin"), "header and attachments");
                Check(!page.Contains("logo.gif (") , "body pictures not listed as attachments");
                Check(page.IndexOf("id=\"kudupst-header\"", StringComparison.Ordinal) > page.IndexOf("<body", StringComparison.Ordinal), "header inside the body");
                Check(!MailHtml.HasRemoteContent(m), "no internet pictures");
                m.Body.Html = "<p>x</p><img src=\"https://tracker.example/p.gif\">";
                Check(MailHtml.HasRemoteContent(m), "internet picture found");
                Check(MailHtml.Page(m, new MailHtml.Options { AllowRemote = true }).Contains("img-src *"), "allowed when asked");
            }
        }
    }
}
