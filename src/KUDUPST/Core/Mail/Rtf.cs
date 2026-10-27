using System;
using System.Collections.Generic;
using System.Text;

namespace Kudupst.Core.Mail
{
    /// <summary>
    /// Outlook's compressed RTF bodies (MS-OXRTFCP), and getting HTML or text out of RTF: emails that were HTML carry
    /// the original HTML inside the RTF (MS-OXRTFEX), which is put back together exactly.
    /// </summary>
    public static class RtfBody
    {
        private const string Prebuf =
            "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier" +
            "{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

        private const uint Compressed = 0x75465A4C; // "LZFu"
        private const uint Uncompressed = 0x414C454D; // "MELA"

        /// <summary>Unpacks a compressed RTF body. Returns null when the data isn't one.</summary>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length < 16) return null;
            var compSize = BitConverter.ToUInt32(data, 0);
            var rawSize = BitConverter.ToUInt32(data, 4);
            var type = BitConverter.ToUInt32(data, 8);
            if (type == Uncompressed)
            {
                var size = (int)Math.Min(rawSize, (uint)(data.Length - 16));
                var raw = new byte[size];
                Buffer.BlockCopy(data, 16, raw, 0, size);
                return raw;
            }
            if (type != Compressed || rawSize > 512 * 1024 * 1024) return null;

            var dict = new byte[4096];
            var pre = Encoding.ASCII.GetBytes(Prebuf);
            Buffer.BlockCopy(pre, 0, dict, 0, pre.Length);
            var write = pre.Length;
            var output = new List<byte>((int)Math.Min(rawSize, 16 * 1024 * 1024));
            var end = (int)Math.Min((long)data.Length, compSize + 4L);
            var at = 16;
            while (at < end && output.Count < rawSize)
            {
                int control = data[at++];
                for (var bit = 0; bit < 8 && at < end && output.Count < rawSize; bit++)
                {
                    if ((control & (1 << bit)) == 0)
                    {
                        var b = data[at++];
                        output.Add(b);
                        dict[write] = b;
                        write = (write + 1) & 0xFFF;
                        continue;
                    }
                    if (at + 1 >= end) return output.ToArray();
                    var reference = (data[at] << 8) | data[at + 1];
                    at += 2;
                    var offset = reference >> 4;
                    var length = (reference & 0xF) + 2;
                    if (offset == write) return output.ToArray(); // the end mark
                    for (var i = 0; i < length; i++)
                    {
                        var b = dict[(offset + i) & 0xFFF];
                        output.Add(b);
                        dict[write] = b;
                        write = (write + 1) & 0xFFF;
                    }
                }
            }
            return output.ToArray();
        }

        /// <summary>The RTF text of a compressed RTF body (bytes kept one-to-one, so escapes decode later).</summary>
        public static string FromCompressed(byte[] data)
        {
            var raw = Decompress(data);
            return raw == null ? null : Latin1.GetString(raw);
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static bool IsFromHtml(string rtf) => rtf != null && rtf.IndexOf("\\fromhtml", StringComparison.Ordinal) >= 0;

        /// <summary>The HTML an email had, when its RTF body was made from HTML; otherwise null.</summary>
        public static string ExtractHtml(string rtf) => IsFromHtml(rtf) ? new Parser(rtf, true).Run() : null;

        /// <summary>The body as plain text.</summary>
        public static string ToText(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return string.Empty;
            if (IsFromHtml(rtf)) return HtmlText.ToText(ExtractHtml(rtf));
            return new Parser(rtf, false).Run().Trim('\r', '\n');
        }

        /// <summary>
        /// A small RTF reader. In HTML mode it keeps the text of \*\htmltag groups and the text outside \htmlrtf
        /// sections (MS-OXRTFEX 2.1.3); in text mode it keeps the visible text.
        /// </summary>
        private sealed class Parser
        {
            private readonly string _rtf;
            private readonly bool _html;
            private readonly StringBuilder _out = new StringBuilder();
            private readonly List<byte> _pending = new List<byte>();
            private readonly Dictionary<int, int> _fontCodePages = new Dictionary<int, int>();
            private int _ansiCodePage = 1252;
            private int _pos;

            private sealed class State
            {
                public bool Skip;          // a destination whose text isn't shown
                public bool HtmlTag;       // inside {\*\htmltag ...}
                public bool HtmlRtf;       // inside \htmlrtf ... \htmlrtf0 (RTF-only parts in HTML mode)
                public bool FontTable;
                public int CodePage = -1;
                public int SkipAfterUnicode = 1;
                public int FontNumber = -1;
                public int FontCharset = -1;

                public State Copy() => (State)MemberwiseClone();
            }

            private State _state = new State();
            private readonly Stack<State> _stack = new Stack<State>();

            public Parser(string rtf, bool html)
            {
                _rtf = rtf;
                _html = html;
            }

            private bool Visible => !_state.Skip && (!_html || _state.HtmlTag || !_state.HtmlRtf);

            public string Run()
            {
                var skipChars = 0;
                while (_pos < _rtf.Length)
                {
                    var c = _rtf[_pos];
                    if (c == '{')
                    {
                        Flush();
                        _stack.Push(_state);
                        _state = _state.Copy();
                        _state.FontNumber = -1;
                        _state.FontCharset = -1;
                        _pos++;
                        // "{\*\dest" groups are skipped unless they're HTML tags.
                        if (_pos + 1 < _rtf.Length && _rtf[_pos] == '\\' && _rtf[_pos + 1] == '*')
                        {
                            _pos += 2;
                            while (_pos < _rtf.Length && char.IsWhiteSpace(_rtf[_pos])) _pos++;
                            var word = PeekWord();
                            if (word == "htmltag" && _html)
                            {
                                _state.HtmlTag = true;
                                _state.Skip = false;
                            }
                            else
                            {
                                _state.Skip = true;
                            }
                        }
                        continue;
                    }
                    if (c == '}')
                    {
                        Flush();
                        if (_state.FontTable && _state.FontNumber >= 0 && _state.FontCharset >= 0)
                            _fontCodePages[_state.FontNumber] = CharsetToCodePage(_state.FontCharset);
                        _state = _stack.Count > 0 ? _stack.Pop() : new State();
                        _pos++;
                        continue;
                    }
                    if (c == '\\')
                    {
                        _pos++;
                        if (_pos >= _rtf.Length) break;
                        var next = _rtf[_pos];
                        if (next == '\\' || next == '{' || next == '}')
                        {
                            _pos++;
                            if (skipChars > 0) { skipChars--; continue; }
                            Emit(next.ToString());
                            continue;
                        }
                        if (next == '\'')
                        {
                            _pos++;
                            if (_pos + 2 <= _rtf.Length && TryHex(_rtf.Substring(_pos, 2), out var b))
                            {
                                _pos += 2;
                                if (skipChars > 0) { skipChars--; continue; }
                                if (Visible) _pending.Add(b);
                            }
                            continue;
                        }
                        if (!char.IsLetter(next))
                        {
                            _pos++;
                            if (skipChars > 0) { skipChars--; continue; }
                            if (next == '~') Emit(" ");
                            else if (next == '_') Emit("-");
                            else if (next == '\r' || next == '\n') Emit(_html ? string.Empty : "\r\n");
                            continue;
                        }
                        var word = ReadWord(out var hasParam, out var param);
                        skipChars = Control(word, hasParam, param, skipChars);
                        continue;
                    }
                    if (c == '\r' || c == '\n')
                    {
                        _pos++;
                        continue;
                    }
                    _pos++;
                    if (skipChars > 0) { skipChars--; continue; }
                    if (_state.FontTable)
                    {
                        // Font table entries end with ';'.
                        if (c == ';' && _state.FontNumber >= 0 && _state.FontCharset >= 0)
                            _fontCodePages[_state.FontNumber] = CharsetToCodePage(_state.FontCharset);
                        continue;
                    }
                    if (Visible) _pending.Add((byte)c);
                }
                Flush();
                return _out.ToString();
            }

            private string PeekWord()
            {
                if (_pos >= _rtf.Length || _rtf[_pos] != '\\') return string.Empty;
                var start = _pos + 1;
                var end = start;
                while (end < _rtf.Length && char.IsLetter(_rtf[end])) end++;
                return _rtf.Substring(start, end - start);
            }

            private string ReadWord(out bool hasParam, out int param)
            {
                var start = _pos;
                while (_pos < _rtf.Length && char.IsLetter(_rtf[_pos])) _pos++;
                var word = _rtf.Substring(start, _pos - start);
                hasParam = false;
                param = 0;
                var numStart = _pos;
                if (_pos < _rtf.Length && (_rtf[_pos] == '-' || char.IsDigit(_rtf[_pos])))
                {
                    _pos++;
                    while (_pos < _rtf.Length && char.IsDigit(_rtf[_pos])) _pos++;
                    hasParam = int.TryParse(_rtf.Substring(numStart, _pos - numStart), out param);
                }
                if (_pos < _rtf.Length && _rtf[_pos] == ' ') _pos++; // the delimiting space belongs to the word
                return word;
            }

            private int Control(string word, bool hasParam, int param, int skipChars)
            {
                switch (word)
                {
                    case "ansicpg":
                        if (hasParam && param > 0) _ansiCodePage = param;
                        return skipChars;
                    case "fonttbl":
                        _state.FontTable = true;
                        _state.Skip = true;
                        return skipChars;
                    case "f":
                        Flush();
                        if (_state.FontTable) _state.FontNumber = param;
                        else _state.CodePage = _fontCodePages.TryGetValue(param, out var cp) ? cp : -1;
                        return skipChars;
                    case "fcharset":
                        if (_state.FontTable) _state.FontCharset = param;
                        return skipChars;
                    case "colortbl":
                    case "stylesheet":
                    case "info":
                    case "pict":
                    case "object":
                    case "header":
                    case "footer":
                    case "headerl":
                    case "headerr":
                    case "footerl":
                    case "footerr":
                    case "listtable":
                    case "listoverridetable":
                    case "rsidtbl":
                    case "generator":
                    case "xmlnstbl":
                    case "themedata":
                    case "colorschememapping":
                    case "latentstyles":
                    case "datastore":
                    case "fldinst":
                        _state.Skip = true;
                        return skipChars;
                    case "htmlrtf":
                        Flush();
                        _state.HtmlRtf = !hasParam || param != 0;
                        return skipChars;
                    case "uc":
                        _state.SkipAfterUnicode = hasParam ? Math.Max(0, param) : 1;
                        return skipChars;
                    case "u":
                        if (!hasParam) return skipChars;
                        if (Visible) Emit(((char)(param < 0 ? param + 65536 : param)).ToString());
                        return _state.SkipAfterUnicode;
                    case "par":
                    case "line":
                        Emit("\r\n");
                        return skipChars;
                    case "tab":
                        Emit("\t");
                        return skipChars;
                    case "emdash": Emit("—"); return skipChars;
                    case "endash": Emit("–"); return skipChars;
                    case "bullet": Emit("•"); return skipChars;
                    case "lquote": Emit("‘"); return skipChars;
                    case "rquote": Emit("’"); return skipChars;
                    case "ldblquote": Emit("“"); return skipChars;
                    case "rdblquote": Emit("”"); return skipChars;
                    default:
                        return skipChars;
                }
            }

            private void Emit(string text)
            {
                if (!Visible) return;
                Flush();
                _out.Append(text);
            }

            private void Flush()
            {
                if (_pending.Count == 0) return;
                var codePage = _state.CodePage > 0 ? _state.CodePage : _ansiCodePage;
                _out.Append(Decode(_pending.ToArray(), codePage));
                _pending.Clear();
            }

            private static string Decode(byte[] bytes, int codePage)
            {
                try { return Encoding.GetEncoding(codePage).GetString(bytes); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { return Latin1.GetString(bytes); }
            }

            private static bool TryHex(string s, out byte b) => byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out b);

            private int CharsetToCodePage(int charset)
            {
                switch (charset)
                {
                    case 77: return 10000;
                    case 128: return 932;
                    case 129: return 949;
                    case 130: return 1361;
                    case 134: return 936;
                    case 136: return 950;
                    case 161: return 1253;
                    case 162: return 1254;
                    case 163: return 1258;
                    case 177: return 1255;
                    case 178: return 1256;
                    case 186: return 1257;
                    case 204: return 1251;
                    case 222: return 874;
                    case 238: return 1250;
                    default: return _ansiCodePage;
                }
            }
        }
    }
}
