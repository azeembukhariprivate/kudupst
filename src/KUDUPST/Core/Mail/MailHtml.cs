using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudupst.Core.Mail
{
    /// <summary>
    /// The web page that shows an email (in the reading pane, and when saving as PDF): a header with who, when and
    /// the attachments, then the body. Scripts never run, and nothing is loaded from the internet unless allowed.
    /// </summary>
    public static class MailHtml
    {
        /// <summary>The made-up web address the app serves an email's pictures and attachments from.</summary>
        public const string Host = "mail.kudupst.local";
        public const string Base = "https://" + Host + "/";

        private static readonly Regex Remote = new Regex(@"(src|background|href)\s*=\s*[""']?\s*https?://|url\(\s*[""']?\s*https?://|<link\b[^>]*https?://",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Img = new Regex(@"<img\b[^>]*\bsrc\s*=\s*[""']?\s*https?://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when the body would load pictures (or other things) from the internet.</summary>
        public static bool HasRemoteContent(MailItem item)
        {
            var html = item.Body.Html;
            return !string.IsNullOrEmpty(html) && (Img.IsMatch(html) || Remote.Matches(html).Cast<Match>().Any(m => !m.Value.StartsWith("href", StringComparison.OrdinalIgnoreCase)));
        }

        public sealed class Options
        {
            public bool AllowRemote { get; set; }
            public bool Dark { get; set; }
            public bool ForPrint { get; set; }
            public string TimeZoneNote { get; set; }
        }

        public static string Page(MailItem item, Options options)
        {
            var body = item.Body;
            string html;
            if (body.HasHtml) html = body.Html;
            else if (!string.IsNullOrEmpty(body.Text)) html = HtmlText.FromText(body.Text);
            else if (!string.IsNullOrEmpty(body.Rtf)) html = HtmlText.FromText(RtfBody.ToText(body.Rtf));
            else html = string.Empty;

            html = LinkPictures(item, html);
            var header = Header(item, options);
            var head = HeadExtras(options);

            if (Regex.IsMatch(html, @"<html\b", RegexOptions.IgnoreCase))
            {
                // A whole web page: put our parts into its head and at the top of its body.
                html = Regex.Replace(html, @"<meta[^>]+http-equiv\s*=\s*[""']?(content-security-policy|refresh)[^>]*>", string.Empty, RegexOptions.IgnoreCase);
                html = Regex.IsMatch(html, @"<head\b", RegexOptions.IgnoreCase)
                    ? Regex.Replace(html, @"<head\b[^>]*>", m => m.Value + head, RegexOptions.IgnoreCase)
                    : Regex.Replace(html, @"<html\b[^>]*>", m => m.Value + "<head>" + head + "</head>", RegexOptions.IgnoreCase);
                html = Regex.IsMatch(html, @"<body\b", RegexOptions.IgnoreCase)
                    ? Regex.Replace(html, @"<body\b[^>]*>", m => m.Value + header + "<div id=\"kudupst-body\">", RegexOptions.IgnoreCase) + "</div>"
                    : html + header;
                return html;
            }
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" + head + "</head><body>" + header + "<div id=\"kudupst-body\">" + html + "</div></body></html>";
        }

        private static string HeadExtras(Options o)
        {
            var policy = o.AllowRemote
                ? "default-src 'none'; img-src * data: blob:; style-src * 'unsafe-inline'; font-src * data:; media-src *"
                : $"default-src 'none'; img-src data: blob: {Base}; style-src 'unsafe-inline' {Base}; font-src data:";
            var headerBg = o.Dark && !o.ForPrint ? "#2b2b2b" : "#f6f6f8";
            var headerText = o.Dark && !o.ForPrint ? "#f2f2f2" : "#1b1b1b";
            var subtle = o.Dark && !o.ForPrint ? "#b4b4b4" : "#5c5c5c";
            var border = o.Dark && !o.ForPrint ? "#3a3a3a" : "#e3e3e8";
            var link = o.Dark && !o.ForPrint ? "#a5b4fc" : "#4338ca";
            return "<meta http-equiv=\"Content-Security-Policy\" content=\"" + policy + "\">" +
                   "<meta name=\"referrer\" content=\"no-referrer\">" +
                   "<style>" +
                   "#kudupst-header{all:initial;display:block;font-family:'Segoe UI',system-ui,sans-serif;font-size:13px;line-height:1.45;" +
                   $"background:{headerBg};color:{headerText};border-bottom:1px solid {border};padding:14px 18px 12px;margin:0 0 12px;}}" +
                   "#kudupst-header *{font-family:inherit;}" +
                   $"#kudupst-header h1{{all:initial;display:block;font-family:inherit;font-size:19px;font-weight:600;color:{headerText};margin:0 0 8px;}}" +
                   "#kudupst-header table{border-collapse:collapse;}" +
                   $"#kudupst-header td{{all:initial;font-family:inherit;font-size:13px;color:{headerText};padding:1px 12px 1px 0;vertical-align:top;}}" +
                   $"#kudupst-header td.k{{color:{subtle};white-space:nowrap;}}" +
                   $"#kudupst-header a{{all:initial;font-family:inherit;font-size:13px;color:{link};cursor:pointer;text-decoration:none;margin-right:14px;}}" +
                   "#kudupst-header a:hover{text-decoration:underline;}" +
                   $"#kudupst-header .att{{display:block;margin-top:8px;padding-top:8px;border-top:1px solid {border};}}" +
                   "@media print{#kudupst-header{margin:0 0 14px;}}" +
                   "</style>";
        }

        private static string Header(MailItem item, Options o)
        {
            string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
            var sb = new StringBuilder("<div id=\"kudupst-header\">");
            sb.Append("<h1>").Append(E(item.Subject.Length > 0 ? item.Subject : "(no subject)")).Append("</h1><table>");
            void Row(string key, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                sb.Append("<tr><td class=\"k\">").Append(E(key)).Append("</td><td>").Append(E(value)).Append("</td></tr>");
            }
            var kind = item.Kind;
            Row("From", item.From.Display);
            if (!item.Sender.IsEmpty && !string.Equals(item.Sender.Address, item.From.Address, StringComparison.OrdinalIgnoreCase) && item.Sender.Address.Length > 0)
                Row("Sent by", item.Sender.Display);
            Row(item.Sent.HasValue ? "Sent" : "Date", item.Date.HasValue ? FormatDate(item.Date.Value) + (o.TimeZoneNote ?? string.Empty) : null);
            Row("To", item.ToLine);
            Row("Cc", item.CcLine);
            Row("Bcc", item.BccLine);
            if (kind != ItemKind.Email) Row("Type", ItemKinds.Name(kind));
            if (item.Importance == 2) Row("Importance", "High");
            sb.Append("</table>");
            var attachments = item.VisibleAttachments.ToList();
            if (attachments.Count > 0)
            {
                sb.Append("<div class=\"att\">\U0001F4CE ");
                for (var i = 0; i < item.Attachments.Count; i++)
                {
                    var a = item.Attachments[i];
                    if (!attachments.Contains(a)) continue;
                    var label = E(a.FileName) + (a.Size > 0 && !a.IsEmail ? " (" + Size(a.Size) + ")" : string.Empty);
                    if (o.ForPrint) sb.Append("<a>").Append(label).Append("</a>");
                    else sb.Append("<a href=\"").Append(Base).Append("open/").Append(i).Append("\" title=\"Open\">").Append(label).Append("</a>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>Points cid: pictures at the app's address for that attachment.</summary>
        private static string LinkPictures(MailItem item, string html)
        {
            if (string.IsNullOrEmpty(html) || item.Attachments.Count == 0) return html;
            for (var i = 0; i < item.Attachments.Count; i++)
            {
                var a = item.Attachments[i];
                if (a.ContentId.Length > 0)
                    html = Regex.Replace(html, "cid:" + Regex.Escape(a.ContentId), Base + "part/" + i, RegexOptions.IgnoreCase);
                if (a.ContentLocation.Length > 0 && !a.ContentLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("\"" + a.ContentLocation + "\"", "\"" + Base + "part/" + i + "\"");
            }
            return html;
        }

        public static string FormatDate(DateTime utc) => utc.ToLocalTime().ToString("dddd d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture) + " GB";
        }
    }
}
