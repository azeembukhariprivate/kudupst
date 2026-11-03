using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kudupst.Core.Mail;
using Kudupst.Core.Search;

namespace Kudupst.Core
{
    /// <summary>
    /// Several files open at once: telling files apart, the set kept for next time, and searching all of them together.
    /// </summary>
    public static class OpenFiles
    {
        /// <summary>A path written the same way each time, to tell whether a file is already open.</summary>
        public static string Normal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { path = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                // keep it as it is
            }
            return path.TrimEnd('\\', '/');
        }

        public static bool Same(string a, string b) => string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The set of open files as one piece of text (one path per line).</summary>
        public static string Save(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (var p in paths ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(p) && !list.Any(x => Same(x, p))) list.Add(Normal(p));
            return string.Join("\n", list);
        }

        /// <summary>The saved set: the files that are still there (to open, in the same order) and the ones that aren't.</summary>
        public static (List<string> Open, List<string> Missing) Restore(string saved, Func<string, bool> exists)
        {
            var open = new List<string>();
            var missing = new List<string>();
            foreach (var line in (saved ?? string.Empty).Split('\n'))
            {
                var p = line.Trim();
                if (p.Length == 0 || open.Concat(missing).Any(x => Same(x, p))) continue;
                bool there;
                try { there = exists(p); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { there = false; }
                (there ? open : missing).Add(p);
            }
            return (open, missing);
        }

        /// <summary>In plain words, which files from last time weren't found; null when none.</summary>
        public static string MissingNote(IList<string> missing)
        {
            if (missing == null || missing.Count == 0) return null;
            var names = missing.Select(Path.GetFileName).ToList();
            return missing.Count == 1
                ? $"\"{names[0]}\" was open last time but isn't there now, so it was left out."
                : $"{missing.Count} files open last time aren't there now, so they were left out: " + string.Join(", ", names) + ".";
        }

        /// <summary>
        /// A name for each file: its file name, with the folder it's in added when two open files have the same name
        /// (for example "archive.pst (2019)" and "archive.pst (2020)").
        /// </summary>
        public static List<string> Names(IList<string> paths)
        {
            var names = paths.Select(p => Path.GetFileName(Normal(p))).ToList();
            var result = new List<string>(names);
            for (var i = 0; i < paths.Count; i++)
            {
                var same = Enumerable.Range(0, paths.Count).Where(j => string.Equals(names[j], names[i], StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count < 2) continue;
                // Use the nearest folder name that tells them apart, else the whole folder.
                var dirs = same.ToDictionary(j => j, j => Path.GetDirectoryName(Normal(paths[j])) ?? string.Empty);
                string label = null;
                for (var up = 0; up < 6 && label == null; up++)
                {
                    var mine = Up(dirs[i], up);
                    if (mine.Length > 0 && same.Where(j => j != i).All(j => !string.Equals(Up(dirs[j], up), mine, StringComparison.OrdinalIgnoreCase))) label = mine;
                }
                result[i] = names[i] + " (" + (label ?? dirs[i]) + ")";
            }
            return result;
        }

        private static string Up(string dir, int levels)
        {
            for (var i = 0; i < levels && dir.Length > 0; i++) dir = Path.GetDirectoryName(dir) ?? string.Empty;
            return dir.Length == 0 ? string.Empty : Path.GetFileName(dir.TrimEnd('\\', '/')) is string n && n.Length > 0 ? n : dir;
        }

        /// <summary>
        /// Searches several open files with one question and puts the results together, newest first. Each result
        /// says which file it's from.
        /// </summary>
        public static List<(T File, IndexedItem Item)> Search<T>(IEnumerable<(T File, MailIndex Index)> files, SearchQuery query)
        {
            var all = new List<(T, IndexedItem)>();
            foreach (var (file, index) in files)
            {
                if (index == null) continue;
                foreach (var item in index.Search(query)) all.Add((file, item));
            }
            return all.OrderByDescending(x => x.Item2.Date ?? DateTime.MinValue).ToList();
        }

        /// <summary>
        /// Whether a single email (a .msg or .eml file, which has no search index) matches a search: every word in the
        /// chosen place, and the dates and attachments asked for.
        /// </summary>
        public static bool Matches(MailItem item, SearchQuery query)
        {
            if (item == null) return false;
            if (query.OnlyWithAttachments && !item.VisibleAttachments.Any()) return false;
            if (query.From.HasValue && (!item.Date.HasValue || item.Date.Value < query.From.Value)) return false;
            if (query.To.HasValue && (!item.Date.HasValue || item.Date.Value >= query.To.Value)) return false;
            var words = Words.Of(query.Text ?? string.Empty).ToList();
            if (words.Count == 0) return true;
            string Of(MailAddress a) => a == null ? string.Empty : a.Name + " " + a.Address;
            var to = string.Join(" ", item.To.Concat(item.Cc).Select(Of));
            string text;
            switch (query.Field)
            {
                case SearchField.Subject: text = item.Subject; break;
                case SearchField.From: text = Of(item.From); break;
                case SearchField.To: text = to; break;
                default:
                    text = string.Join(" ", new[] { item.Subject, Of(item.From), to, item.Body.PlainText }
                        .Concat(item.VisibleAttachments.Select(a => a.FileName))
                        .Concat(new[] { item.Contact?.Name, item.Contact?.Company, item.Appointment?.Location }));
                    break;
            }
            var have = new HashSet<string>(Words.Of(text ?? string.Empty));
            // Like the index: each word typed can be the start of a word in the email.
            return words.All(w => have.Any(h => h.StartsWith(w, StringComparison.Ordinal)));
        }
    }
}
