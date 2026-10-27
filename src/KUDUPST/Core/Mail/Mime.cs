using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudupst.Core.Mail
{
    /// <summary>Email headers, in order, with case-insensitive lookup.</summary>
    public sealed class HeaderList
    {
        public List<KeyValuePair<string, string>> Entries { get; } = new List<KeyValuePair<string, string>>();

        public string Get(string name)
        {
            foreach (var e in Entries)
                if (string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)) return e.Value;
            return null;
        }

        public IEnumerable<string> GetAll(string name) =>
            Entries.Where(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value);
    }

    /// <summary>A part of an internet email (RFC 2045–2049): headers, a decoded body, and sub-parts.</summary>
    public sealed class MimePart
    {
        public HeaderList Headers { get; set; } = new HeaderList();
        public string MediaType { get; set; } = "text/plain";
        public Dictionary<string, string> TypeParams { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Disposition { get; set; } = string.Empty;
        public Dictionary<string, string> DispositionParams { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];
        public List<MimePart> Parts { get; } = new List<MimePart>();

        public bool IsMultipart => MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
        public string Charset => TypeParams.TryGetValue("charset", out var c) ? c : null;

        public string FileName
        {
            get
            {
                if (DispositionParams.TryGetValue("filename", out var f) && f.Length > 0) return f;
                if (TypeParams.TryGetValue("name", out var n) && n.Length > 0) return n;
                return string.Empty;
            }
        }

        public string ContentId => (Headers.Get("Content-ID") ?? string.Empty).Trim().Trim('<', '>');

        public string Text => Mime.DecodeText(Body, Charset);
    }

    /// <summary>Reading internet email (.eml): headers, encoded words, addresses, dates and MIME parts.</summary>
    public static class Mime
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        // ================================================================== whole messages

        public static MimePart Parse(byte[] data) => ParsePart(data, 0, data.Length, 0);

        private static MimePart ParsePart(byte[] data, int start, int end, int depth)
        {
            var part = new MimePart();
            var bodyStart = HeaderEnd(data, start, end);
            var headerText = Latin1.GetString(data, start, Math.Max(0, bodyStart - start));
            part.Headers = ParseHeaders(headerText);
            var contentType = part.Headers.Get("Content-Type");
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var (value, ps) = ParseParameterized(contentType);
                part.MediaType = value.Length > 0 ? value.ToLowerInvariant() : "text/plain";
                part.TypeParams = ps;
            }
            var disposition = part.Headers.Get("Content-Disposition");
            if (!string.IsNullOrWhiteSpace(disposition))
            {
                var (value, ps) = ParseParameterized(disposition);
                part.Disposition = value.ToLowerInvariant();
                part.DispositionParams = ps;
            }

            if (part.IsMultipart && depth < 32 && part.TypeParams.TryGetValue("boundary", out var boundary) && boundary.Length > 0)
            {
                foreach (var (s, e) in SplitParts(data, bodyStart, end, boundary))
                    part.Parts.Add(ParsePart(data, s, e, depth + 1));
                return part;
            }

            var raw = new byte[Math.Max(0, end - bodyStart)];
            Buffer.BlockCopy(data, bodyStart, raw, 0, raw.Length);
            var encoding = (part.Headers.Get("Content-Transfer-Encoding") ?? string.Empty).Trim().ToLowerInvariant();
            part.Body = encoding == "base64" ? FromBase64(raw) : encoding == "quoted-printable" ? FromQuotedPrintable(raw) : raw;

            if (part.MediaType == "message/rfc822" && depth < 32)
                part.Parts.Add(ParsePart(part.Body, 0, part.Body.Length, depth + 1));
            return part;
        }

        /// <summary>Where the headers end: after the first empty line.</summary>
        private static int HeaderEnd(byte[] data, int start, int end)
        {
            // A part with no headers starts with an empty line straight away.
            if (start < end && (data[start] == '\n' || (data[start] == '\r' && start + 1 < end && data[start + 1] == '\n')))
                return data[start] == '\n' ? start + 1 : start + 2;
            for (var i = start; i < end - 1; i++)
            {
                if (data[i] != '\n') continue;
                if (data[i + 1] == '\n') return i + 2;
                if (data[i + 1] == '\r' && i + 2 < end && data[i + 2] == '\n') return i + 3;
            }
            return end;
        }

        private static IEnumerable<(int Start, int End)> SplitParts(byte[] data, int start, int end, string boundary)
        {
            var marker = Latin1.GetBytes("--" + boundary);
            var positions = new List<(int LineStart, int After, bool Final)>();
            var i = start;
            while (i < end)
            {
                // Boundaries are at the start of a line.
                var lineStart = i;
                var lineEnd = i;
                while (lineEnd < end && data[lineEnd] != '\n') lineEnd++;
                if (lineEnd - lineStart >= marker.Length && Matches(data, lineStart, marker))
                {
                    var final = lineStart + marker.Length + 1 < end && data[lineStart + marker.Length] == '-' && data[lineStart + marker.Length + 1] == '-';
                    positions.Add((lineStart, Math.Min(end, lineEnd + 1), final));
                    if (final) break;
                }
                i = lineEnd + 1;
            }
            for (var k = 0; k < positions.Count; k++)
            {
                if (positions[k].Final) yield break;
                var partStart = positions[k].After;
                var partEnd = k + 1 < positions.Count ? positions[k + 1].LineStart : end;
                // The line break before a boundary belongs to the boundary.
                if (partEnd > partStart && data[partEnd - 1] == '\n') partEnd--;
                if (partEnd > partStart && data[partEnd - 1] == '\r') partEnd--;
                if (partEnd >= partStart) yield return (partStart, partEnd);
            }
        }

        private static bool Matches(byte[] data, int at, byte[] marker)
        {
            for (var i = 0; i < marker.Length; i++)
                if (data[at + i] != marker[i]) return false;
            return true;
        }

        // ================================================================== headers

        public static HeaderList ParseHeaders(string text)
        {
            var list = new HeaderList();
            if (string.IsNullOrEmpty(text)) return list;
            string name = null;
            var value = new StringBuilder();
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (rawLine.Length == 0) continue;
                if ((rawLine[0] == ' ' || rawLine[0] == '\t') && name != null)
                {
                    value.Append(' ').Append(rawLine.Trim());
                    continue;
                }
                if (name != null) list.Entries.Add(new KeyValuePair<string, string>(name, value.ToString().Trim()));
                var colon = rawLine.IndexOf(':');
                if (colon <= 0)
                {
                    name = null;
                    continue;
                }
                name = rawLine.Substring(0, colon).Trim();
                value.Clear().Append(rawLine.Substring(colon + 1));
            }
            if (name != null) list.Entries.Add(new KeyValuePair<string, string>(name, value.ToString().Trim()));
            return list;
        }

        /// <summary>"text/plain; charset=utf-8; name*=..." → the value and its parameters (RFC 2231 included).</summary>
        public static (string Value, Dictionary<string, string> Params) ParseParameterized(string header)
        {
            var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pieces = SplitOutsideQuotes(header, ';');
            var value = pieces.Count > 0 ? pieces[0].Trim() : string.Empty;
            var continued = new SortedDictionary<string, SortedDictionary<int, (string Text, bool Encoded)>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < pieces.Count; i++)
            {
                var eq = pieces[i].IndexOf('=');
                if (eq <= 0) continue;
                var key = pieces[i].Substring(0, eq).Trim();
                var v = Unquote(pieces[i].Substring(eq + 1).Trim());
                var m = Regex.Match(key, @"^(.+?)\*(\d+)?(\*)?$");
                if (m.Success)
                {
                    var baseName = m.Groups[1].Value;
                    var index = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                    var encoded = m.Groups[3].Success || (!m.Groups[2].Success && key.EndsWith("*"));
                    if (!continued.TryGetValue(baseName, out var pieceList)) continued[baseName] = pieceList = new SortedDictionary<int, (string, bool)>();
                    pieceList[index] = (v, encoded);
                }
                else
                {
                    ps[key] = DecodeWords(v);
                }
            }
            foreach (var kv in continued)
            {
                string charset = null;
                var bytes = new List<byte>();
                var sb = new StringBuilder();
                foreach (var piece in kv.Value)
                {
                    var text = piece.Value.Text;
                    if (piece.Value.Encoded)
                    {
                        if (piece.Key == 0)
                        {
                            var parts = text.Split(new[] { '\'' }, 3);
                            if (parts.Length == 3)
                            {
                                charset = parts[0];
                                text = parts[2];
                            }
                        }
                        bytes.AddRange(PercentDecode(text));
                    }
                    else
                    {
                        if (bytes.Count > 0)
                        {
                            sb.Append(DecodeText(bytes.ToArray(), charset));
                            bytes.Clear();
                        }
                        sb.Append(text);
                    }
                }
                if (bytes.Count > 0) sb.Append(DecodeText(bytes.ToArray(), charset));
                ps[kv.Key] = sb.ToString();
            }
            return (value, ps);
        }

        private static List<byte> PercentDecode(string s)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && byte.TryParse(s.Substring(i + 1, 2), NumberStyles.HexNumber, null, out var b))
                {
                    bytes.Add(b);
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
                }
            }
            return bytes;
        }

        private static List<string> SplitOutsideQuotes(string s, char separator)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') quoted = !quoted;
                if (c == '\\' && quoted && i + 1 < s.Length)
                {
                    sb.Append(c).Append(s[++i]);
                    continue;
                }
                if (c == separator && !quoted)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return Regex.Replace(s.Substring(1, s.Length - 2), @"\\(.)", "$1");
            return s;
        }

        private static readonly Regex EncodedWord = new Regex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=", RegexOptions.Compiled);

        /// <summary>Decodes RFC 2047 encoded words ("=?utf-8?B?...?="). Spaces between two encoded words are dropped.</summary>
        public static string DecodeWords(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("=?", StringComparison.Ordinal) < 0) return text ?? string.Empty;
            text = Regex.Replace(text, @"(\?=)\s+(=\?)", "$1$2");
            return EncodedWord.Replace(text, m =>
            {
                var charset = m.Groups[1].Value;
                var star = charset.IndexOf('*'); // RFC 2231 language
                if (star >= 0) charset = charset.Substring(0, star);
                byte[] bytes;
                if (m.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = FromBase64(Latin1.GetBytes(m.Groups[3].Value));
                }
                else
                {
                    bytes = FromQuotedPrintable(Latin1.GetBytes(m.Groups[3].Value.Replace('_', ' ')));
                }
                return DecodeText(bytes, charset);
            });
        }

        public static string DecodeText(byte[] bytes, string charset)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var enc = GetEncoding(charset);
            if (enc == null)
            {
                // No (or unknown) charset: UTF-8 if it's valid, else Windows-1252.
                try { return new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { enc = Pst.PropValue.DefaultAnsi; }
            }
            var text = enc.GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        }

        public static Encoding GetEncoding(string charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return null;
            charset = charset.Trim().Trim('"');
            if (charset.Equals("utf8", StringComparison.OrdinalIgnoreCase)) charset = "utf-8";
            if (charset.Equals("ks_c_5601-1987", StringComparison.OrdinalIgnoreCase)) charset = "ks_c_5601-1987";
            try { return Encoding.GetEncoding(charset); }
            catch (ArgumentException) { return null; }
        }

        // ================================================================== addresses

        /// <summary>"Name &lt;a@b&gt;, "Last, First" &lt;c@d&gt;, e@f" → addresses (groups flattened).</summary>
        public static List<MailAddress> ParseAddresses(string header)
        {
            var list = new List<MailAddress>();
            if (string.IsNullOrWhiteSpace(header)) return list;
            header = Regex.Replace(header, @"^[^""<>@,]*:(.*);\s*$", "$1"); // a group: "Team: a@b, c@d;"
            foreach (var piece in SplitOutsideQuotes(header, ','))
            {
                var p = piece.Trim();
                if (p.Length == 0) continue;
                var lt = p.LastIndexOf('<');
                var gt = p.LastIndexOf('>');
                string name, address;
                if (lt >= 0 && gt > lt)
                {
                    address = p.Substring(lt + 1, gt - lt - 1).Trim();
                    name = p.Substring(0, lt).Trim();
                }
                else
                {
                    var m = Regex.Match(p, @"^(\S+@\S+)\s*\((.*)\)$");
                    if (m.Success)
                    {
                        address = m.Groups[1].Value;
                        name = m.Groups[2].Value;
                    }
                    else
                    {
                        address = p;
                        name = string.Empty;
                    }
                }
                name = DecodeWords(Unquote(name.Trim()));
                if (address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = address.Substring(7);
                list.Add(new MailAddress(name, DecodeWords(address)));
            }
            return list;
        }

        // ================================================================== dates

        private static readonly Dictionary<string, int> Zones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
            ["EST"] = -500, ["EDT"] = -400, ["CST"] = -600, ["CDT"] = -500,
            ["MST"] = -700, ["MDT"] = -600, ["PST"] = -800, ["PDT"] = -700
        };

        private static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>An RFC 5322 date ("Wed, 24 Oct 2001 10:07:37 -0700 (PDT)") as UTC, or null.</summary>
        public static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = Regex.Replace(text, @"\([^)]*\)", " ").Trim();
            var m = Regex.Match(s, @"(\d{1,2})\s+([A-Za-z]{3})[a-z]*\.?\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?\s*([+-]\d{4}|[A-Za-z]{1,5})?");
            if (m.Success)
            {
                var month = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant()) + 1;
                if (month <= 0) return null;
                var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (year < 50) year += 2000;
                else if (year < 1000) year += 1900;
                try
                {
                    var local = new DateTime(year, month, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                        m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0, DateTimeKind.Unspecified);
                    var zone = m.Groups[7].Success ? m.Groups[7].Value : "+0000";
                    int hhmm;
                    if (zone[0] == '+' || zone[0] == '-') hhmm = int.Parse(zone, CultureInfo.InvariantCulture);
                    else if (!Zones.TryGetValue(zone, out hhmm)) hhmm = 0;
                    var offset = TimeSpan.FromMinutes((hhmm / 100) * 60 + (hhmm % 100));
                    return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto.UtcDateTime;
            return null;
        }

        // ================================================================== transfer encodings

        public static byte[] FromBase64(byte[] data)
        {
            var clean = new StringBuilder(data.Length);
            foreach (var b in data)
            {
                var c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/') clean.Append(c);
                else if (c == '-') clean.Append('+'); // base64url
                else if (c == '_') clean.Append('/');
            }
            var len = clean.Length - clean.Length % 4;
            var rest = clean.Length % 4;
            var s = clean.ToString(0, len);
            if (rest == 2) s += clean.ToString(len, 2) + "==";
            else if (rest == 3) s += clean.ToString(len, 3) + "=";
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return new byte[0]; }
        }

        public static byte[] FromQuotedPrintable(byte[] data)
        {
            var output = new MemoryStream(data.Length);
            for (var i = 0; i < data.Length; i++)
            {
                var b = data[i];
                if (b != '=')
                {
                    output.WriteByte(b);
                    continue;
                }
                if (i + 1 < data.Length && data[i + 1] == '\n')
                {
                    i += 1;
                    continue;
                }
                if (i + 2 < data.Length && data[i + 1] == '\r' && data[i + 2] == '\n')
                {
                    i += 2;
                    continue;
                }
                if (i + 2 < data.Length && IsHex(data[i + 1]) && IsHex(data[i + 2]))
                {
                    output.WriteByte((byte)(Hex(data[i + 1]) * 16 + Hex(data[i + 2])));
                    i += 2;
                    continue;
                }
                output.WriteByte(b);
            }
            return output.ToArray();
        }

        private static bool IsHex(byte b) => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');

        private static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
    }

    /// <summary>Turns a parsed .eml file into a <see cref="MailItem"/>.</summary>
    public static class EmlReader
    {
        public static MailItem Read(string path) => Read(File.ReadAllBytes(path));

        public static MailItem Read(byte[] data) => FromPart(Mime.Parse(data), data.Length);

        public static MailItem FromPart(MimePart root, long size)
        {
            var h = root.Headers;
            var headerText = string.Join("\r\n", h.Entries.Select(e => e.Key + ": " + e.Value));
            var from = Mime.ParseAddresses(h.Get("From")).FirstOrDefault() ?? new MailAddress();
            var sender = Mime.ParseAddresses(h.Get("Sender")).FirstOrDefault() ?? new MailAddress();
            var item = new MailItem
            {
                MessageClass = "IPM.Note",
                Subject = Mime.DecodeWords(h.Get("Subject") ?? string.Empty),
                From = from,
                Sender = sender.IsEmpty ? from : sender,
                Sent = Mime.ParseDate(h.Get("Date")),
                Received = ReceivedDate(h),
                Size = size,
                MessageId = (h.Get("Message-ID") ?? string.Empty).Trim(),
                InReplyTo = (h.Get("In-Reply-To") ?? string.Empty).Trim(),
                References = (h.Get("References") ?? string.Empty).Trim(),
                Headers = headerText,
                Importance = ImportanceOf(h)
            };
            foreach (var (header, kind) in new[] { ("To", RecipientKind.To), ("Cc", RecipientKind.Cc), ("Bcc", RecipientKind.Bcc) })
                foreach (var a in Mime.ParseAddresses(string.Join(", ", h.GetAll(header))))
                    item.Recipients.Add(new Recipient { Address = a, Kind = kind });
            item.DisplayTo = string.Join("; ", item.To.Select(a => a.Short));
            item.DisplayCc = string.Join("; ", item.Cc.Select(a => a.Short));

            var body = new MailBody();
            var attachments = new List<Attachment>();
            Walk(root, body, attachments, true);
            // Pictures used by the HTML (cid:) are part of the body.
            if (body.HasHtml)
                foreach (var a in attachments)
                    if (a.ContentId.Length > 0 && body.Html.IndexOf("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase) >= 0) a.Inline = true;
            item.LoadBody = () => body;
            item.LoadAttachments = () => attachments;
            return item;
        }

        private static DateTime? ReceivedDate(HeaderList h)
        {
            var received = h.Get("Received");
            if (received == null) return null;
            var semi = received.LastIndexOf(';');
            return semi >= 0 ? Mime.ParseDate(received.Substring(semi + 1)) : null;
        }

        private static int ImportanceOf(HeaderList h)
        {
            var importance = (h.Get("Importance") ?? string.Empty).Trim().ToLowerInvariant();
            if (importance == "high") return 2;
            if (importance == "low") return 0;
            var priority = (h.Get("X-Priority") ?? string.Empty).Trim();
            if (priority.StartsWith("1") || priority.StartsWith("2")) return 2;
            if (priority.StartsWith("4") || priority.StartsWith("5")) return 0;
            return 1;
        }

        private static void Walk(MimePart part, MailBody body, List<Attachment> attachments, bool isRoot)
        {
            if (part.IsMultipart)
            {
                foreach (var child in part.Parts) Walk(child, body, attachments, false);
                return;
            }
            var isAttachment = part.Disposition == "attachment" || (part.FileName.Length > 0 && !part.MediaType.StartsWith("text/"));
            if (!isAttachment && part.MediaType == "text/html" && body.Html == null)
            {
                body.Html = part.Text;
                return;
            }
            if (!isAttachment && part.MediaType == "text/plain" && body.Text == null)
            {
                body.Text = part.Text;
                return;
            }
            if (!isAttachment && part.MediaType == "text/rtf" && body.Rtf == null)
            {
                body.Rtf = Encoding.GetEncoding(28591).GetString(part.Body);
                return;
            }
            if (isRoot && part.MediaType.StartsWith("text/") && body.Text == null)
            {
                body.Text = part.Text;
                return;
            }
            var name = part.FileName;
            var p = part;
            var a = new Attachment
            {
                FileName = name,
                MimeType = part.MediaType,
                ContentId = part.ContentId,
                ContentLocation = part.Headers.Get("Content-Location") ?? string.Empty,
                Size = part.Body.Length,
                Inline = part.Disposition == "inline" && part.MediaType.StartsWith("image/") && part.ContentId.Length > 0
            };
            if (part.MediaType == "message/rfc822" && part.Parts.Count > 0)
            {
                a.Embedded = () => FromPart(p.Parts[0], p.Body.Length);
                if (a.FileName.Length == 0) a.FileName = (Mime.DecodeWords(p.Parts[0].Headers.Get("Subject") ?? "Attached email")) + ".eml";
            }
            else
            {
                a.Load = () => p.Body;
                if (a.FileName.Length == 0) a.FileName = "attachment" + Extensions.For(part.MediaType);
            }
            attachments.Add(a);
        }
    }

    /// <summary>File name extensions for common types (for attachments without a name).</summary>
    public static class Extensions
    {
        public static string For(string mediaType)
        {
            switch ((mediaType ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/bmp": return ".bmp";
                case "application/pdf": return ".pdf";
                case "text/plain": return ".txt";
                case "text/html": return ".html";
                case "text/calendar": return ".ics";
                case "text/vcard":
                case "text/x-vcard": return ".vcf";
                case "message/rfc822": return ".eml";
                case "application/zip": return ".zip";
                default: return ".bin";
            }
        }

        public static string MediaType(string fileName)
        {
            switch ((Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".svg": return "image/svg+xml";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain";
                case ".htm":
                case ".html": return "text/html";
                case ".ics": return "text/calendar";
                case ".vcf": return "text/vcard";
                case ".eml": return "message/rfc822";
                case ".zip": return "application/zip";
                case ".doc": return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt": return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "application/octet-stream";
            }
        }
    }
}
