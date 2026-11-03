using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kudupst.Core;
using Kudupst.Core.Mail;
using Kudupst.Core.Search;

namespace Kudupst.Tests
{
    public static partial class Program
    {
        // ================================================================== several files at once

        private static void TestOpenFilesSet()
        {
            var a = Path.Combine(_root, "set", "a.pst");
            var b = Path.Combine(_root, "set", "Mail", "b.ost");
            Check(OpenFiles.Same(a, Path.Combine(_root, "set", "x", "..", "A.PST")), "the same file written two ways");
            Check(!OpenFiles.Same(a, b), "different files");

            var saved = OpenFiles.Save(new[] { a, b, a.ToUpperInvariant(), "", null });
            Equal(2, saved.Split('\n').Length, "each file once");

            var gone = Path.Combine(_root, "set", "gone.pst");
            var (open, missing) = OpenFiles.Restore(saved + "\n" + gone + "\n\n" + b, p => !p.EndsWith("gone.pst"));
            Equal(2, open.Count, "files still there");
            Check(OpenFiles.Same(open[0], a) && OpenFiles.Same(open[1], b), "in the same order");
            Equal(1, missing.Count, "files not there");
            Equal("\"gone.pst\" was open last time but isn't there now, so it was left out.", OpenFiles.MissingNote(missing), "note");
            Check(OpenFiles.MissingNote(new List<string>()) == null, "no note when nothing is missing");
            Check(OpenFiles.MissingNote(new[] { gone, gone + "2" }).StartsWith("2 files"), "note for two");
            Check(OpenFiles.Restore(null, p => true).Open.Count == 0, "nothing saved");
        }

        private static void TestOpenFilesNames()
        {
            var names = OpenFiles.Names(new[] { @"C:\Mail\2019\archive.pst", @"C:\Mail\2020\archive.pst", @"C:\Mail\work.ost" }
                .Select(p => p.Replace('\\', Path.DirectorySeparatorChar).Replace("C:", _root)).ToList());
            Equal("archive.pst (2019)", names[0], "first archive");
            Equal("archive.pst (2020)", names[1], "second archive");
            Equal("work.ost", names[2], "a name used once stays as it is");
            var deeper = OpenFiles.Names(new[] { Path.Combine(_root, "x", "Old", "a.pst"), Path.Combine(_root, "y", "Old", "a.pst") });
            Equal("a.pst (x)", deeper[0], "when the folders have the same name, one further up");
            Equal("a.pst (y)", deeper[1], "and the other");
        }

        private static void TestSearchAcrossFiles()
        {
            var root = Path.Combine(_root, "index-across");
            using (var enron = new MailIndex(Samples.Enron, root))
            using (var ost = new MailIndex(Samples.MountainMan, root))
            {
                enron.BuildNow();
                ost.BuildNow();
                var files = new[] { ("enron.pst", enron), ("mtnman.ost", ost) };

                // A word only in one file finds it there only.
                var hits = OpenFiles.Search(files, new SearchQuery { Text = "Burlington" });
                Check(hits.Count > 0 && hits.All(h => h.File == "enron.pst"), "Burlington only in the Enron file");
                hits = OpenFiles.Search(files, new SearchQuery { Text = "rickroll" });
                Check(hits.Count >= 2 && hits.All(h => h.File == "mtnman.ost"), "rickroll only in the OST");

                // Everything: both files, newest first, and as many as the two searched alone.
                var all = OpenFiles.Search(files, new SearchQuery());
                Equal(enron.Search(new SearchQuery()).Count + ost.Search(new SearchQuery()).Count, all.Count, "every item of both files");
                Check(all.Any(h => h.File == "enron.pst") && all.Any(h => h.File == "mtnman.ost"), "both files in the results");
                for (var i = 1; i < all.Count; i++)
                    Check((all[i - 1].Item.Date ?? DateTime.MinValue) >= (all[i].Item.Date ?? DateTime.MinValue), "newest first");

                // A file without an index (couldn't be made) is left out, not an error.
                Equal(all.Count, OpenFiles.Search(files.Concat(new[] { ("broken.pst", (MailIndex)null) }), new SearchQuery()).Count, "no index");
            }
        }

        private static void TestSearchSingleEmails()
        {
            var msg = MsgReader.Read(Samples.MultiTo);
            Check(OpenFiles.Matches(msg, new SearchQuery { Text = "multiple recipients" }), "words in the subject");
            Check(OpenFiles.Matches(msg, new SearchQuery { Text = "test email body" }), "words in the text");
            Check(OpenFiles.Matches(msg, new SearchQuery { Text = "car" }), "a word's start (carol)");
            Check(OpenFiles.Matches(msg, new SearchQuery { Text = "bob", Field = SearchField.From }), "the sender");
            Check(!OpenFiles.Matches(msg, new SearchQuery { Text = "bob", Field = SearchField.To }), "the sender isn't a recipient");
            Check(OpenFiles.Matches(msg, new SearchQuery { Text = "dave", Field = SearchField.To }), "copies count as recipients");
            Check(!OpenFiles.Matches(msg, new SearchQuery { Text = "multiple zzzqqq" }), "every word must be there");
            Check(!OpenFiles.Matches(msg, new SearchQuery { Text = "", OnlyWithAttachments = true }), "no attachments");
            var withPictures = MsgReader.Read(Samples.UnicodeMsg);
            Check(OpenFiles.Matches(withPictures, new SearchQuery { Text = "", OnlyWithAttachments = true }), "has attachments");
            if (msg.Date.HasValue)
            {
                var day = msg.Date.Value.Date;
                Check(OpenFiles.Matches(msg, new SearchQuery { From = day, To = day.AddDays(1) }), "on its day");
                Check(!OpenFiles.Matches(msg, new SearchQuery { From = day.AddDays(1) }), "not after it");
            }
        }
    }
}
