using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudupst.Core.Mail
{
    /// <summary>
    /// Writes an email as a standard .eml file (RFC 5322 / MIME) that Outlook, Thunderbird, Windows Mail and other mail
    /// programs open: text and HTML bodies, pictures in the body, attachments, and attached emails.
    /// </summary>
    public static class EmlWriter
    {
        private static readonly string[] AddressHeaders = { "From", "To", "Cc", "Bcc", "Sender", "Reply-To" };
        private static readonly string[] MimeHeaders = { "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID", "MIME-Version", "Content-Description", "Content-Language" };

        public static byte[] Write(MailItem item)
        {
            var sb = new StringBuilder();
            Write(item, sb, 0);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static void Save(MailItem item, string path) => File.WriteAllBytes(path, Write(item));

        private static void Write(MailItem item, StringBuilder sb, int depth)
        {
            WriteHeaders(item, sb);
            var body = item.Body;
            var html = body.HasHtml ? body.Html : null;
            var text = !string.IsNullOrEmpty(body.Text) ? body.Text : html != null ? HtmlText.ToText(html) : RtfBody.ToText(body.Rtf);
            var related = html == null ? new List<Attachment>() : item.Attachments.Where(a => !a.IsEmail && a.ContentId.Length > 0 && html.IndexOf("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var attachments = item.Attachments.Where(a => !related.Contains(a)).ToList();

            Action<StringBuilder> bodyPart;
            if (html == null) bodyPart = b => TextPart(b, "text/plain", text);
            else
            {
                Action<StringBuilder> alternative = b => Multipart(b, "multipart/alternative", new Action<StringBuilder>[]
                {
                    p => TextPart(p, "text/plain", text),
                    p => TextPart(p, "text/html", html)
                });
                if (related.Count == 0) bodyPart = alternative;
                else
                {
                    var parts = new List<Action<StringBuilder>> { alternative };
                    parts.AddRange(related.Select(a => (Action<StringBuilder>)(p => AttachmentPart(p, a, true, depth))));
                    bodyPart = b => Multipart(b, "multipart/related", parts, "; type=\"multipart/alternative\"");
                }
            }

            if (attachments.Count == 0) bodyPart(sb);
            else
            {
                var parts = new List<Action<StringBuilder>> { bodyPart };
                parts.AddRange(attachments.Select(a => (Action<StringBuilder>)(p => AttachmentPart(p, a, false, depth))));
                Multipart(sb, "multipart/mixed", parts);
            }
        }

        private static void WriteHeaders(MailItem item, StringBuilder sb)
        {
            // Keep the headers it arrived with (the Received: trail and so on), except the ones about its MIME layout,
            // which is written anew.
            var kept = Mime.ParseHeaders(item.Headers).Entries
                .Where(e => !MimeHeaders.Any(m => string.Equals(m, e.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            bool Has(string name) => kept.Any(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase));
            foreach (var e in kept)
            {
                var value = e.Value;
                if (!value.All(c => c < 128) && AddressHeaders.Any(h => string.Equals(h, e.Key, StringComparison.OrdinalIgnoreCase)))
                    value = string.Join(",\r\n ", Mime.ParseAddresses(value).Select(Address));
                Header(sb, e.Key, Ascii(value));
            }

            if (!Has("From") && !item.From.IsEmpty) Header(sb, "From", Address(item.From));
            if (!Has("Sender") && !item.Sender.IsEmpty && !string.Equals(item.Sender.Address, item.From.Address, StringComparison.OrdinalIgnoreCase))
                Header(sb, "Sender", Address(item.Sender));
            foreach (var (name, kind, display) in new[] { ("To", RecipientKind.To, item.DisplayTo), ("Cc", RecipientKind.Cc, item.DisplayCc), ("Bcc", RecipientKind.Bcc, item.DisplayBcc) })
            {
                if (Has(name)) continue;
                var list = item.Recipients.Where(r => r.Kind == kind).Select(r => r.Address).ToList();
                if (list.Count > 0) Header(sb, name, string.Join(",\r\n ", list.Select(Address)));
                else if (!string.IsNullOrWhiteSpace(display)) Header(sb, name, EncodeText(display));
            }
            if (!Has("Subject")) Header(sb, "Subject", EncodeText(item.Subject));
            if (!Has("Date") && item.Date.HasValue) Header(sb, "Date", item.Date.Value.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
            if (!Has("Message-ID") && item.MessageId.Length > 0) Header(sb, "Message-ID", Ascii(item.MessageId));
            if (!Has("In-Reply-To") && item.InReplyTo.Length > 0) Header(sb, "In-Reply-To", Ascii(item.InReplyTo));
            if (!Has("References") && item.References.Length > 0) Header(sb, "References", Ascii(item.References));
            if (!Has("Importance") && item.Importance != 1) Header(sb, "Importance", item.Importance > 1 ? "high" : "low");
            Header(sb, "MIME-Version", "1.0");
        }

        private static void Header(StringBuilder sb, string name, string value) => sb.Append(name).Append(": ").Append(value).Append("\r\n");

        /// <summary>Header text that isn't plain ASCII becomes encoded words.</summary>
        private static string Ascii(string value) => value.All(c => c < 128) || value.Contains("=?") ? value : EncodeText(value);

        public static string EncodeText(string text)
        {
            text = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " ");
            if (text.All(c => c >= 32 && c < 127)) return text;
            // Encoded words of at most 45 bytes each (60 base64 characters), split between characters.
            var words = new List<string>();
            var chunk = new StringBuilder();
            foreach (var c in StringInfoElements(text))
            {
                if (Encoding.UTF8.GetByteCount(chunk + c) > 45)
                {
                    words.Add(Word(chunk.ToString()));
                    chunk.Clear();
                }
                chunk.Append(c);
            }
            if (chunk.Length > 0) words.Add(Word(chunk.ToString()));
            return string.Join("\r\n ", words);
        }

        private static IEnumerable<string> StringInfoElements(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) yield return (string)e.Current;
        }

        private static string Word(string s) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

        private static string Address(MailAddress a)
        {
            if (a.Address.Length == 0) return EncodeText(a.Name);
            if (a.Name.Length == 0 || a.Name == a.Address) return "<" + a.Address + ">";
            var name = a.Name.All(c => c >= 32 && c < 127)
                ? "\"" + a.Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : EncodeText(a.Name);
            return name + " <" + Ascii(a.Address) + ">";
        }

        private static void Multipart(StringBuilder sb, string type, IList<Action<StringBuilder>> parts, string extra = "")
        {
            var boundary = "----=_KUDUPST_" + Guid.NewGuid().ToString("N");
            sb.Append("Content-Type: ").Append(type).Append(";\r\n boundary=\"").Append(boundary).Append('"').Append(extra).Append("\r\n\r\n");
            sb.Append("This is a message in MIME format.\r\n");
            foreach (var part in parts)
            {
                sb.Append("\r\n--").Append(boundary).Append("\r\n");
                part(sb);
            }
            sb.Append("\r\n--").Append(boundary).Append("--\r\n");
        }

        private static void TextPart(StringBuilder sb, string type, string text)
        {
            sb.Append("Content-Type: ").Append(type).Append("; charset=\"utf-8\"\r\n");
            sb.Append("Content-Transfer-Encoding: quoted-printable\r\n\r\n");
            sb.Append(QuotedPrintable(Encoding.UTF8.GetBytes((text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n"))));
        }

        private static void AttachmentPart(StringBuilder sb, Attachment a, bool inline, int depth)
        {
            if (a.IsEmail && depth < 16)
            {
                var embedded = a.Embedded();
                if (embedded != null)
                {
                    sb.Append("Content-Type: message/rfc822").Append(NameParam("name", a.FileName)).Append("\r\n");
                    sb.Append("Content-Disposition: attachment").Append(NameParam("filename", EmailName(a.FileName))).Append("\r\n\r\n");
                    Write(embedded, sb, depth + 1);
                    sb.Append("\r\n");
                    return;
                }
            }
            var type = a.MimeType.Length > 0 && a.MimeType.Contains("/") ? a.MimeType : Extensions.MediaType(a.FileName);
            sb.Append("Content-Type: ").Append(type).Append(NameParam("name", a.FileName)).Append("\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n");
            sb.Append("Content-Disposition: ").Append(inline ? "inline" : "attachment").Append(NameParam("filename", a.FileName)).Append("\r\n");
            if (a.ContentId.Length > 0) sb.Append("Content-ID: <").Append(a.ContentId).Append(">\r\n");
            sb.Append("\r\n");
            var data = a.Data();
            var b64 = Convert.ToBase64String(data);
            for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        }

        private static string EmailName(string name) => name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? name : name + ".eml";

        /// <summary>A file name parameter: quoted ASCII, or RFC 2231 encoded (plus an encoded-word copy older programs read).</summary>
        private static string NameParam(string param, string value)
        {
            value = (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " ");
            if (value.All(c => c >= 32 && c < 127)) return ";\r\n " + param + "=\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var encoded = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '.' || b == '-' || b == '_') encoded.Append((char)b);
                else encoded.Append('%').Append(b.ToString("X2"));
            }
            return ";\r\n " + param + "*=utf-8''" + encoded;
        }

        public static string QuotedPrintable(byte[] data)
        {
            var sb = new StringBuilder();
            var line = 0;
            for (var i = 0; i < data.Length; i++)
            {
                var b = data[i];
                if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                {
                    // A space or tab at a line end must be encoded.
                    if (sb.Length > 0 && (sb[sb.Length - 1] == ' ' || sb[sb.Length - 1] == '\t'))
                    {
                        var last = sb[sb.Length - 1];
                        sb.Length--;
                        sb.Append('=').Append(((int)last).ToString("X2"));
                    }
                    sb.Append("\r\n");
                    i++;
                    line = 0;
                    continue;
                }
                string piece;
                if ((b >= 33 && b <= 126 && b != '=') || b == ' ' || b == '\t') piece = ((char)b).ToString();
                else piece = "=" + b.ToString("X2");
                if (line + piece.Length > 75)
                {
                    sb.Append("=\r\n");
                    line = 0;
                }
                sb.Append(piece);
                line += piece.Length;
            }
            if (sb.Length > 0 && (sb[sb.Length - 1] == ' ' || sb[sb.Length - 1] == '\t'))
            {
                var last = sb[sb.Length - 1];
                sb.Length--;
                sb.Append('=').Append(((int)last).ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
