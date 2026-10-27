using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Core.Search
{
    /// <summary>An item as the index keeps it: enough to list and filter it without opening the file.</summary>
    public sealed class IndexedItem
    {
        public int Ordinal { get; set; }
        public uint Nid { get; set; }
        public uint FolderNid { get; set; }
        public DateTime? Date { get; set; }
        public long Size { get; set; }
        public bool Read { get; set; } = true;
        public bool HasAttachments { get; set; }
        public string MessageClass { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }

    public enum SearchField { Everything, Subject, From, To }

    public sealed class SearchQuery
    {
        public string Text { get; set; } = string.Empty;
        public SearchField Field { get; set; } = SearchField.Everything;
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public bool OnlyWithAttachments { get; set; }

        /// <summary>Only items in these folders (null: everywhere).</summary>
        public HashSet<uint> Folders { get; set; }
    }

    /// <summary>Splits text into words for the index: lower case letters and digits; Chinese, Japanese and Korean characters one by one.</summary>
    public static class Words
    {
        public const int MaxLength = 40;

        public static IEnumerable<string> Of(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var sb = new StringBuilder();
            foreach (var raw in text)
            {
                var c = char.ToLowerInvariant(raw);
                if (IsCjk(c))
                {
                    if (sb.Length > 0) { if (sb.Length <= MaxLength) yield return sb.ToString(); sb.Clear(); }
                    yield return c.ToString();
                }
                else if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0)
                {
                    if (sb.Length <= MaxLength) yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length > 0 && sb.Length <= MaxLength) yield return sb.ToString();
        }

        private static bool IsCjk(char c) =>
            (c >= 0x3040 && c <= 0x30FF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0xF900 && c <= 0xFAFF);
    }

    /// <summary>
    /// One piece of the index on disk: the items it covers, a sorted word list, and for each word the items it's in.
    /// Only every 32nd word is kept in memory; the rest is read from disk when searching.
    /// </summary>
    internal sealed class Segment : IDisposable
    {
        private const string Magic = "KUDUPST-INDEX-1";
        private const int SampleEvery = 32;

        private readonly FileStream _file;
        private readonly BinaryReader _reader;
        private readonly List<(string Word, long At)> _samples = new List<(string, long)>();
        private readonly long _dictionaryEnd;
        private readonly object _sync = new object();

        public string Path { get; }
        public List<IndexedItem> Items { get; } = new List<IndexedItem>();
        public int WordCount { get; }

        public Segment(string path)
        {
            Path = path;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            _reader = new BinaryReader(_file, Encoding.UTF8);
            if (_reader.ReadString() != Magic) throw new InvalidDataException("Not an index file.");
            var count = _reader.ReadInt32();
            for (var i = 0; i < count; i++) Items.Add(ReadItem(_reader));
            WordCount = _reader.ReadInt32();
            var dictionaryAt = _reader.ReadInt64();
            _file.Position = dictionaryAt;
            for (var i = 0; i < WordCount; i++)
            {
                var at = _file.Position;
                var word = _reader.ReadString();
                _reader.ReadInt64();
                if (i % SampleEvery == 0) _samples.Add((word, at));
            }
            _dictionaryEnd = _file.Position;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _file.Dispose();
        }

        /// <summary>The words starting with a prefix (or equal to it), with where their item lists are.</summary>
        public IEnumerable<(string Word, long Postings)> Words(string prefix, bool exact)
        {
            var results = new List<(string, long)>();
            lock (_sync)
            {
                // The last sample before the prefix.
                int lo = 0, hi = _samples.Count - 1, start = 0;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    if (string.CompareOrdinal(_samples[mid].Word, prefix) < 0)
                    {
                        start = mid;
                        lo = mid + 1;
                    }
                    else hi = mid - 1;
                }
                if (_samples.Count == 0) return results;
                _file.Position = _samples[start].At;
                while (_file.Position < _dictionaryEnd)
                {
                    var word = _reader.ReadString();
                    var postings = _reader.ReadInt64();
                    var cmp = string.CompareOrdinal(word, prefix);
                    if (cmp < 0) continue;
                    if (exact ? cmp != 0 : !word.StartsWith(prefix, StringComparison.Ordinal)) break;
                    results.Add((word, postings));
                    if (exact || results.Count > 5000) break;
                }
            }
            return results;
        }

        public List<int> Postings(long at)
        {
            lock (_sync)
            {
                _file.Position = at;
                var count = ReadVarInt(_reader);
                var list = new List<int>(count);
                var last = 0;
                for (var i = 0; i < count; i++)
                {
                    last += ReadVarInt(_reader);
                    list.Add(last);
                }
                return list;
            }
        }

        // ------------------------------------------------------------------ writing

        public static void Write(string path, IList<IndexedItem> items, SortedDictionary<string, List<int>> words)
        {
            var temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(file, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(items.Count);
                foreach (var item in items) WriteItem(w, item);
                w.Write(words.Count);
                var dictionaryAtPosition = file.Position;
                w.Write(0L);
                var offsets = new List<long>(words.Count);
                foreach (var kv in words)
                {
                    offsets.Add(file.Position);
                    WriteVarInt(w, kv.Value.Count);
                    var last = 0;
                    foreach (var ordinal in kv.Value)
                    {
                        WriteVarInt(w, ordinal - last);
                        last = ordinal;
                    }
                }
                var dictionaryAt = file.Position;
                var i = 0;
                foreach (var kv in words)
                {
                    w.Write(kv.Key);
                    w.Write(offsets[i++]);
                }
                file.Position = dictionaryAtPosition;
                w.Write(dictionaryAt);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Joins segments (in ordinal order) into one.</summary>
        public static void Merge(IList<Segment> segments, string path)
        {
            var items = segments.SelectMany(s => s.Items).ToList();
            var words = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var s in segments)
            {
                lock (s._sync)
                {
                    s._file.Position = s._samples.Count > 0 ? s._samples[0].At : s._dictionaryEnd;
                    var entries = new List<(string, long)>();
                    while (s._file.Position < s._dictionaryEnd) entries.Add((s._reader.ReadString(), s._reader.ReadInt64()));
                    foreach (var (word, at) in entries)
                    {
                        s._file.Position = at;
                        var count = ReadVarInt(s._reader);
                        if (!words.TryGetValue(word, out var list)) words[word] = list = new List<int>(count);
                        var last = 0;
                        for (var i = 0; i < count; i++)
                        {
                            last += ReadVarInt(s._reader);
                            list.Add(last);
                        }
                    }
                }
            }
            Write(path, items, words);
        }

        private static void WriteItem(BinaryWriter w, IndexedItem item)
        {
            w.Write(item.Ordinal);
            w.Write(item.Nid);
            w.Write(item.FolderNid);
            w.Write(item.Date?.Ticks ?? 0L);
            w.Write(item.Size);
            w.Write((byte)((item.Read ? 1 : 0) | (item.HasAttachments ? 2 : 0)));
            w.Write(item.MessageClass ?? string.Empty);
            w.Write(item.Subject ?? string.Empty);
            w.Write(item.From ?? string.Empty);
            w.Write(item.To ?? string.Empty);
        }

        private static IndexedItem ReadItem(BinaryReader r)
        {
            var item = new IndexedItem { Ordinal = r.ReadInt32(), Nid = r.ReadUInt32(), FolderNid = r.ReadUInt32() };
            var ticks = r.ReadInt64();
            item.Date = ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            item.Size = r.ReadInt64();
            var flags = r.ReadByte();
            item.Read = (flags & 1) != 0;
            item.HasAttachments = (flags & 2) != 0;
            item.MessageClass = r.ReadString();
            item.Subject = r.ReadString();
            item.From = r.ReadString();
            item.To = r.ReadString();
            return item;
        }

        private static void WriteVarInt(BinaryWriter w, int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                w.Write((byte)(v | 0x80));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        private static int ReadVarInt(BinaryReader r)
        {
            var result = 0;
            var shift = 0;
            while (true)
            {
                var b = r.ReadByte();
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 28) throw new InvalidDataException("The search index is damaged.");
            }
        }
    }

    /// <summary>
    /// The search index of one PST/OST file, kept in the user's AppData (never next to the file). It's built in the
    /// background the first time a file is opened, in pieces, so a big file can be searched while it's still being
    /// indexed, and an interrupted build carries on where it stopped. A changed file gets a new index.
    /// </summary>
    public sealed class MailIndex : IDisposable
    {
        private const int ItemsPerSegment = 2000;
        private const int MaxBodyChars = 200000;
        private const int MergeAbove = 8;

        private readonly string _sourcePath;
        private readonly List<Segment> _segments = new List<Segment>();
        private readonly object _sync = new object();
        private Thread _thread;
        private volatile bool _stop;

        public string Folder { get; }
        public bool Complete { get; private set; }
        public int Done { get; private set; }
        public int Total { get; private set; }
        public string Error { get; private set; }
        public bool Running => _thread != null && _thread.IsAlive;

        /// <summary>Raised (on the indexing thread) when more items can be searched.</summary>
        public event Action Progress;

        public static string IndexRoot =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUPST", "Index");

        public MailIndex(string sourcePath, string root = null)
        {
            _sourcePath = sourcePath;
            Folder = System.IO.Path.Combine(root ?? IndexRoot, KeyOf(sourcePath));
            Directory.CreateDirectory(Folder);
            File.WriteAllText(System.IO.Path.Combine(Folder, "source.txt"), sourcePath);
            Load();
        }

        /// <summary>A folder name for a file: its path, size and date, so a changed file gets a new index.</summary>
        public static string KeyOf(string path)
        {
            var info = new FileInfo(path);
            var text = path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 20).ToLowerInvariant();
        }

        private string StatePath => System.IO.Path.Combine(Folder, "state.txt");

        private void Load()
        {
            var state = File.Exists(StatePath) ? File.ReadAllLines(StatePath) : new string[0];
            Complete = state.Contains("complete");
            var total = state.FirstOrDefault(l => l.StartsWith("total="));
            if (total != null && int.TryParse(total.Substring(6), out var t)) Total = t;
            foreach (var path in Directory.GetFiles(Folder, "seg-*.bin").OrderBy(p => p, StringComparer.Ordinal))
            {
                try { _segments.Add(new Segment(path)); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is EndOfStreamException)
                {
                    // A damaged piece (e.g. the computer was switched off while writing): start over.
                    Reset();
                    return;
                }
            }
            Done = _segments.Sum(s => s.Items.Count);
        }

        private void Reset()
        {
            foreach (var s in _segments) s.Dispose();
            _segments.Clear();
            foreach (var f in Directory.GetFiles(Folder, "seg-*")) File.Delete(f);
            if (File.Exists(StatePath)) File.Delete(StatePath);
            Complete = false;
            Done = 0;
        }

        /// <summary>Starts (or carries on) building the index in the background.</summary>
        public void Start()
        {
            if (Complete || Running) return;
            _stop = false;
            _thread = new Thread(Build) { IsBackground = true, Name = "KUDUPST index", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _thread?.Join(5000);
        }

        public void Dispose()
        {
            Stop();
            lock (_sync)
            {
                foreach (var s in _segments) s.Dispose();
                _segments.Clear();
            }
        }

        /// <summary>Builds the index right away on this thread (used by tests).</summary>
        public void BuildNow() => Build();

        private void Build()
        {
            try
            {
                using (var store = PstStore.Open(_sourcePath))
                {
                    var folders = store.AllFolders(store.Root).Where(f => !f.IsSearchFolder).ToList();
                    Total = folders.Sum(f => Math.Max(0, f.ItemCount));
                    var ordinal = 0;
                    var skip = Done;
                    var items = new List<IndexedItem>();
                    var words = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
                    foreach (var folder in folders)
                    {
                        List<ItemSummary> summaries;
                        try { summaries = store.Items(folder); }
                        catch (PstException) { continue; }
                        foreach (var s in summaries)
                        {
                            if (_stop) return;
                            if (ordinal < skip)
                            {
                                ordinal++;
                                continue;
                            }
                            var entry = new IndexedItem
                            {
                                Ordinal = ordinal, Nid = s.Nid, FolderNid = folder.Nid, Date = s.Date, Size = s.Size, Read = s.Read,
                                HasAttachments = s.HasAttachments, MessageClass = s.MessageClass, Subject = s.Subject, From = s.From, To = s.To
                            };
                            var text = new StringBuilder();
                            text.Append(s.Subject).Append(' ').Append(s.From).Append(' ').Append(s.To).Append(' ');
                            try
                            {
                                var item = store.ReadItem(s.Nid);
                                text.Append(item.From.Name).Append(' ').Append(item.From.Address).Append(' ');
                                foreach (var r in item.Recipients) text.Append(r.Address.Name).Append(' ').Append(r.Address.Address).Append(' ');
                                var body = item.Body.PlainText;
                                text.Append(body.Length > MaxBodyChars ? body.Substring(0, MaxBodyChars) : body).Append(' ');
                                foreach (var a in item.Attachments) text.Append(a.FileName).Append(' ');
                                if (entry.From.Length == 0) entry.From = item.From.Short;
                                if (!entry.Date.HasValue) entry.Date = item.Date;
                            }
                            catch (Exception ex) when (ex is PstException || ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
                            {
                                // An item that can't be read is still listed by its subject.
                            }
                            foreach (var word in new HashSet<string>(Words.Of(text.ToString())))
                            {
                                if (!words.TryGetValue(word, out var list)) words[word] = list = new List<int>();
                                list.Add(ordinal);
                            }
                            items.Add(entry);
                            ordinal++;
                            if (items.Count >= ItemsPerSegment)
                            {
                                Flush(items, words);
                                items = new List<IndexedItem>();
                                words = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
                            }
                        }
                    }
                    if (items.Count > 0) Flush(items, words);
                    MergeAll();
                    Total = Done;
                    Complete = true;
                    File.WriteAllLines(StatePath, new[] { "complete", "total=" + Done });
                    Progress?.Invoke();
                }
            }
            catch (Exception ex) when (ex is PstException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Error = ex.Message;
                Progress?.Invoke();
            }
        }

        private void Flush(List<IndexedItem> items, SortedDictionary<string, List<int>> words)
        {
            var path = System.IO.Path.Combine(Folder, $"seg-{items[0].Ordinal:D9}.bin");
            Segment.Write(path, items, words);
            lock (_sync)
            {
                _segments.Add(new Segment(path));
                Done += items.Count;
                File.WriteAllLines(StatePath, new[] { "total=" + Total });
                if (_segments.Count > MergeAbove) MergeAll();
            }
            Progress?.Invoke();
        }

        private void MergeAll()
        {
            lock (_sync)
            {
                if (_segments.Count <= 1) return;
                var first = _segments[0].Items.Count > 0 ? _segments[0].Items[0].Ordinal : 0;
                var path = System.IO.Path.Combine(Folder, $"seg-{first:D9}.merge");
                Segment.Merge(_segments, path);
                foreach (var s in _segments)
                {
                    s.Dispose();
                    File.Delete(s.Path);
                }
                _segments.Clear();
                var final = System.IO.Path.Combine(Folder, $"seg-{first:D9}.bin");
                File.Move(path, final);
                _segments.Add(new Segment(final));
            }
        }

        // ================================================================== searching

        /// <summary>Every item indexed so far.</summary>
        public List<IndexedItem> AllItems()
        {
            lock (_sync) return _segments.SelectMany(s => s.Items).ToList();
        }

        /// <summary>The items that match, newest first.</summary>
        public List<IndexedItem> Search(SearchQuery query)
        {
            var terms = Words.Of(query.Text).Distinct().ToList();
            List<IndexedItem> candidates;
            lock (_sync)
            {
                if (terms.Count == 0 || query.Field != SearchField.Everything)
                {
                    candidates = _segments.SelectMany(s => s.Items).ToList();
                    if (terms.Count > 0) candidates = candidates.Where(i => FieldMatches(i, query.Field, terms)).ToList();
                }
                else
                {
                    candidates = new List<IndexedItem>();
                    foreach (var segment in _segments)
                    {
                        HashSet<int> hits = null;
                        foreach (var term in terms)
                        {
                            var found = new HashSet<int>();
                            foreach (var (_, at) in segment.Words(term, term.Length < 3))
                                found.UnionWith(segment.Postings(at));
                            if (hits == null) hits = found;
                            else hits.IntersectWith(found);
                            if (hits.Count == 0) break;
                        }
                        if (hits == null || hits.Count == 0) continue;
                        candidates.AddRange(segment.Items.Where(i => hits.Contains(i.Ordinal)));
                    }
                }
            }
            return candidates.Where(i => Filter(i, query)).OrderByDescending(i => i.Date ?? DateTime.MinValue).ToList();
        }

        private static bool FieldMatches(IndexedItem item, SearchField field, List<string> terms)
        {
            var text = field == SearchField.Subject ? item.Subject : field == SearchField.From ? item.From : item.To;
            var words = new HashSet<string>(Words.Of(text));
            return terms.All(t => words.Any(w => t.Length < 3 ? w == t : w.StartsWith(t, StringComparison.Ordinal)));
        }

        public static bool Filter(IndexedItem item, SearchQuery query)
        {
            if (query.Folders != null && !query.Folders.Contains(item.FolderNid)) return false;
            if (query.OnlyWithAttachments && !item.HasAttachments) return false;
            if (query.From.HasValue && (!item.Date.HasValue || item.Date.Value < query.From.Value)) return false;
            if (query.To.HasValue && (!item.Date.HasValue || item.Date.Value >= query.To.Value)) return false;
            return true;
        }

        /// <summary>Deletes indexes of files that no longer exist, or that were last used long ago.</summary>
        public static void CleanUp(string root = null)
        {
            root = root ?? IndexRoot;
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var source = System.IO.Path.Combine(dir, "source.txt");
                    var path = File.Exists(source) ? File.ReadAllText(source) : null;
                    var stale = path == null || !File.Exists(path) || KeyOf(path) != System.IO.Path.GetFileName(dir) ||
                                Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-180);
                    if (stale) Directory.Delete(dir, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // In use by another KUDUPST window: leave it.
                }
            }
        }

        /// <summary>How much space all indexes take.</summary>
        public static long SizeOnDisk(string root = null)
        {
            root = root ?? IndexRoot;
            if (!Directory.Exists(root)) return 0;
            return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }

        public static string Describe(MailIndex index) =>
            index.Complete ? string.Format(CultureInfo.InvariantCulture, "{0:N0} items indexed", index.Done)
                : index.Total > 0 ? string.Format(CultureInfo.InvariantCulture, "Indexing for search… {0:P0}", Math.Min(1.0, (double)index.Done / index.Total))
                : "Indexing for search…";
    }
}
