using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudupst.Core.Mail
{
    /// <summary>Readable plain text from an HTML email body (for searching, the CSV list and text exports).</summary>
    public static class HtmlText
    {
        private static readonly Regex Hidden = new Regex(@"<(script|style|head|title|xml)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Comments = new Regex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Breaks = new Regex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/blockquote|/table|hr)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Cells = new Regex(@"<\s*/t[dh]\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Items = new Regex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Tags = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"[ \t ]+", RegexOptions.Compiled);
        private static readonly Regex BlankLines = new Regex(@"(\r?\n[ \t]*){3,}", RegexOptions.Compiled);

        public static string ToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            var s = Comments.Replace(html, string.Empty);
            s = Hidden.Replace(s, string.Empty);
            s = s.Replace("\r", string.Empty).Replace("\n", " ");
            s = Breaks.Replace(s, "\n");
            s = Cells.Replace(s, "\t");
            s = Items.Replace(s, "\n• ");
            s = Tags.Replace(s, string.Empty);
            s = WebUtility.HtmlDecode(s);
            s = Spaces.Replace(s, " ");
            var sb = new StringBuilder();
            foreach (var line in s.Split('\n')) sb.Append(line.Trim()).Append("\r\n");
            return BlankLines.Replace(sb.ToString(), "\r\n\r\n").Trim();
        }

        /// <summary>Plain text as a simple HTML page body (for showing and printing text-only emails).</summary>
        public static string FromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var encoded = WebUtility.HtmlEncode(text.Replace("\r\n", "\n").Replace('\r', '\n'));
            // Make web and email addresses clickable.
            encoded = Regex.Replace(encoded, @"\b(https?://[^\s<>&]+)", "<a href=\"$1\">$1</a>");
            return "<div style=\"white-space:pre-wrap;word-wrap:break-word;font-family:'Segoe UI',Calibri,sans-serif;font-size:14px\">" + encoded + "</div>";
        }
    }
}
