using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Kudupst.Core.Mail;

namespace Kudupst.Core.Export
{
    public enum ExportFormat { Eml, Pdf, Attachments, Csv, Vcard, ContactsCsv, Ics }

    /// <summary>An item to export: how to read it, and the folder it's in (for keeping the folder structure).</summary>
    public sealed class ExportItem
    {
        public Func<MailItem> Load { get; set; }
        public string FolderPath { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
    }

    public sealed class ExportOptions
    {
        public ExportFormat Format { get; set; } = ExportFormat.Eml;
        public string Destination { get; set; }
        public bool KeepFolders { get; set; } = true;

        /// <summary>PDF: all items in one file instead of one file each.</summary>
        public bool CombinePdf { get; set; }

        /// <summary>Attachments: also save pictures that are part of the body (logos and the like).</summary>
        public bool IncludeInlinePictures { get; set; }
    }

    public sealed class ExportResult
    {
        public int Exported { get; set; }
        public int Files { get; set; }
        public List<string> Problems { get; } = new List<string>();
        public bool Cancelled { get; set; }
        public string FirstFile { get; set; }
    }

    /// <summary>Saves items as .eml files, saves their attachments, or lists them in a CSV file. (PDF is done by the app's window, which can print web pages.)</summary>
    public static class Exporter
    {
        public static ExportResult Run(IList<ExportItem> items, ExportOptions options, Action<int, string> progress, CancellationToken cancel)
        {
            switch (options.Format)
            {
                case ExportFormat.Csv: return Csv(items, options.Destination, progress, cancel);
                case ExportFormat.Attachments: return Attachments(items, options, progress, cancel);
                case ExportFormat.Eml: return Eml(items, options, progress, cancel);
                case ExportFormat.Vcard:
                case ExportFormat.ContactsCsv:
                case ExportFormat.Ics: return OneFile(items, options.Format, options.Destination, progress, cancel);
                default: throw new NotSupportedException("PDF is saved by the app window.");
            }
        }

        public static ExportResult Eml(IList<ExportItem> items, ExportOptions options, Action<int, string> progress, CancellationToken cancel)
        {
            var result = new ExportResult();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Count; i++)
            {
                if (cancel.IsCancellationRequested) { result.Cancelled = true; break; }
                var e = items[i];
                progress?.Invoke(i, e.Subject);
                try
                {
                    var item = e.Load();
                    var folder = FolderFor(options, e);
                    Directory.CreateDirectory(folder);
                    var path = Unique(Path.Combine(folder, FileName(item.Date ?? e.Date, item.Subject, ".eml")), used);
                    File.WriteAllBytes(path, EmlWriter.Write(item));
                    result.Exported++;
                    result.Files++;
                    result.FirstFile = result.FirstFile ?? path;
                }
                catch (Exception ex) when (IsReadProblem(ex))
                {
                    result.Problems.Add($"{Describe(e)}: {ex.Message}");
                }
            }
            progress?.Invoke(items.Count, null);
            return result;
        }

        public static ExportResult Attachments(IList<ExportItem> items, ExportOptions options, Action<int, string> progress, CancellationToken cancel)
        {
            var result = new ExportResult();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Count; i++)
            {
                if (cancel.IsCancellationRequested) { result.Cancelled = true; break; }
                var e = items[i];
                progress?.Invoke(i, e.Subject);
                try
                {
                    var item = e.Load();
                    var any = false;
                    foreach (var a in item.Attachments)
                    {
                        if (!options.IncludeInlinePictures && (a.Inline || a.Hidden)) continue;
                        var folder = FolderFor(options, e);
                        Directory.CreateDirectory(folder);
                        var name = a.IsEmail ? Clean(Path.GetFileNameWithoutExtension(a.FileName)) + ".eml" : Clean(a.FileName);
                        var path = Unique(Path.Combine(folder, name), used);
                        if (a.IsEmail)
                        {
                            var embedded = a.Embedded();
                            if (embedded == null) continue;
                            File.WriteAllBytes(path, EmlWriter.Write(embedded));
                        }
                        else
                        {
                            File.WriteAllBytes(path, a.Data());
                        }
                        ZoneMark.Apply(path); // from someone else's email: Windows and Office treat it as from the internet
                        result.Files++;
                        result.FirstFile = result.FirstFile ?? path;
                        any = true;
                    }
                    if (any) result.Exported++;
                }
                catch (Exception ex) when (IsReadProblem(ex))
                {
                    result.Problems.Add($"{Describe(e)}: {ex.Message}");
                }
            }
            progress?.Invoke(items.Count, null);
            return result;
        }

        public static readonly string[] CsvColumns =
        {
            "Folder", "Date", "From", "From address", "To", "Cc", "Subject", "Size (KB)", "Attachments", "Read", "Type"
        };

        public static ExportResult Csv(IList<ExportItem> items, string path, Action<int, string> progress, CancellationToken cancel)
        {
            var result = new ExportResult();
            var sb = new StringBuilder();
            sb.Append(string.Join(",", CsvColumns.Select(CsvCell))).Append("\r\n");
            for (var i = 0; i < items.Count; i++)
            {
                if (cancel.IsCancellationRequested) { result.Cancelled = true; break; }
                var e = items[i];
                progress?.Invoke(i, e.Subject);
                try
                {
                    var item = e.Load();
                    var cells = new[]
                    {
                        e.FolderPath,
                        item.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                        item.From.Name,
                        item.From.Address,
                        item.ToLine,
                        item.CcLine,
                        item.Subject,
                        Math.Ceiling(item.Size / 1024.0).ToString(CultureInfo.InvariantCulture),
                        string.Join("; ", item.VisibleAttachments.Select(a => a.FileName)),
                        item.Read ? "Yes" : "No",
                        ItemKinds.Name(item.Kind)
                    };
                    sb.Append(string.Join(",", cells.Select(CsvCell))).Append("\r\n");
                    result.Exported++;
                }
                catch (Exception ex) when (IsReadProblem(ex))
                {
                    result.Problems.Add($"{Describe(e)}: {ex.Message}");
                }
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // UTF-8 with a byte order mark, so Excel shows accents and other scripts correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            result.Files = 1;
            result.FirstFile = path;
            progress?.Invoke(items.Count, null);
            return result;
        }

        /// <summary>Whether an item can go into a contacts or calendar file.</summary>
        public static bool Fits(ExportFormat format, MailItem item)
        {
            switch (format)
            {
                case ExportFormat.Vcard:
                case ExportFormat.ContactsCsv: return item.Contact != null || item.Group != null;
                case ExportFormat.Ics: return IcsWriter.IsEvent(item);
                default: return true;
            }
        }

        /// <summary>Contacts into one .vcf or .csv file, or calendar items into one .ics file. Other items are skipped.</summary>
        public static ExportResult OneFile(IList<ExportItem> items, ExportFormat format, string path, Action<int, string> progress, CancellationToken cancel)
        {
            var result = new ExportResult();
            var sb = new StringBuilder();
            var ics = new IcsWriter();
            if (format == ExportFormat.ContactsCsv) sb.Append(ContactsCsv.Header);
            for (var i = 0; i < items.Count; i++)
            {
                if (cancel.IsCancellationRequested) { result.Cancelled = true; break; }
                var e = items[i];
                progress?.Invoke(i, e.Subject);
                try
                {
                    var item = e.Load();
                    if (!Fits(format, item)) continue;
                    if (format == ExportFormat.Vcard) sb.Append(VcardWriter.Write(item));
                    else if (format == ExportFormat.ContactsCsv) sb.Append(ContactsCsv.Row(item));
                    else ics.Add(item);
                    result.Exported++;
                }
                catch (Exception ex) when (IsReadProblem(ex))
                {
                    result.Problems.Add($"{Describe(e)}: {ex.Message}");
                }
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = format == ExportFormat.Ics ? ics.ToString() : sb.ToString();
            // CSV gets a byte order mark so Excel shows accents correctly; vCard and iCalendar readers expect none.
            File.WriteAllText(path, text, new UTF8Encoding(format == ExportFormat.ContactsCsv));
            result.Files = 1;
            result.FirstFile = path;
            progress?.Invoke(items.Count, null);
            return result;
        }

        public static string CsvCell(string value)
        {
            value = value ?? string.Empty;
            // A cell starting with = + - @ would be run as a formula by spreadsheet programs.
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) value = "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        public static bool IsReadProblem(Exception ex) =>
            ex is Pst.PstException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException ||
            ex is ArgumentException || ex is IndexOutOfRangeException || ex is NotSupportedException;

        private static string Describe(ExportItem e) => string.IsNullOrEmpty(e.Subject) ? "(no subject)" : e.Subject;

        public static string FolderFor(ExportOptions options, ExportItem e)
        {
            if (!options.KeepFolders || string.IsNullOrEmpty(e.FolderPath)) return options.Destination;
            var parts = e.FolderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Clean);
            return Path.Combine(new[] { options.Destination }.Concat(parts).ToArray());
        }

        /// <summary>"2024-05-01 0930 - Subject.eml", safe for Windows.</summary>
        public static string FileName(DateTime? date, string subject, string extension)
        {
            var stamp = date.HasValue ? date.Value.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + " - " : string.Empty;
            var name = Clean(string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject);
            if (name.Length > 120) name = name.Substring(0, 120).TrimEnd(' ', '.');
            return stamp + name + extension;
        }

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>A name with the characters Windows doesn't allow replaced, and never a path.</summary>
        public static string Clean(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? string.Empty)
            {
                if (c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            var s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length == 0 || s == "." || s == "..") s = "_";
            var stem = s.Split('.')[0];
            if (Reserved.Contains(stem)) s = "_" + s;
            if (s.Length > 150)
            {
                var ext = Path.GetExtension(s);
                if (ext.Length > 20) ext = string.Empty;
                s = s.Substring(0, 150 - ext.Length).TrimEnd(' ', '.') + ext;
            }
            return s;
        }

        /// <summary>A path that doesn't exist yet: "name (2).eml" and so on.</summary>
        public static string Unique(string path, HashSet<string> used)
        {
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var candidate = path;
            for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate) || used.Contains(candidate); n++)
                candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            used.Add(candidate);
            return candidate;
        }
    }
}
