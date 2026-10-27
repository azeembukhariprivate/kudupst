using System;
using System.Collections.Generic;
using System.Text;

namespace Kudupst.Core.Pst
{
    /// <summary>MAPI property types (MS-OXCDATA 2.11.1).</summary>
    public static class PropType
    {
        public const ushort Int16 = 0x0002;
        public const ushort Int32 = 0x0003;
        public const ushort Float = 0x0004;
        public const ushort Double = 0x0005;
        public const ushort Currency = 0x0006;
        public const ushort AppTime = 0x0007;
        public const ushort Error = 0x000A;
        public const ushort Boolean = 0x000B;
        public const ushort Object = 0x000D;
        public const ushort Int64 = 0x0014;
        public const ushort String8 = 0x001E;
        public const ushort Unicode = 0x001F;
        public const ushort SysTime = 0x0040;
        public const ushort Guid = 0x0048;
        public const ushort Binary = 0x0102;
        public const ushort MultiFlag = 0x1000;

        public static bool IsFixed(ushort type) => FixedSize(type) > 0;

        public static int FixedSize(ushort type)
        {
            switch (type)
            {
                case Boolean: return 1;
                case Int16: return 2;
                case Int32:
                case Float:
                case Error: return 4;
                case Double:
                case Currency:
                case AppTime:
                case Int64:
                case SysTime: return 8;
                case Guid: return 16;
                default: return 0;
            }
        }
    }

    /// <summary>The property IDs KUDUPST reads (the upper 16 bits of a property tag).</summary>
    public static class PropId
    {
        public const ushort Importance = 0x0017;
        public const ushort MessageClass = 0x001A;
        public const ushort Priority = 0x0026;
        public const ushort Sensitivity = 0x0036;
        public const ushort Subject = 0x0037;
        public const ushort ClientSubmitTime = 0x0039;
        public const ushort SubjectPrefix = 0x003D;
        public const ushort ReceivedByName = 0x0040;
        public const ushort SentRepresentingName = 0x0042;
        public const ushort SentRepresentingAddrType = 0x0064;
        public const ushort SentRepresentingEmail = 0x0065;
        public const ushort ConversationTopic = 0x0070;
        public const ushort TransportHeaders = 0x007D;
        public const ushort RecipientType = 0x0C15;
        public const ushort SenderName = 0x0C1A;
        public const ushort SenderAddrType = 0x0C1E;
        public const ushort SenderEmail = 0x0C1F;
        public const ushort DisplayBcc = 0x0E02;
        public const ushort DisplayCc = 0x0E03;
        public const ushort DisplayTo = 0x0E04;
        public const ushort MessageDeliveryTime = 0x0E06;
        public const ushort MessageFlags = 0x0E07;
        public const ushort MessageSize = 0x0E08;
        public const ushort NormalizedSubject = 0x0E1D;
        public const ushort AttachSize = 0x0E20;
        public const ushort Body = 0x1000;
        public const ushort RtfCompressed = 0x1009;
        public const ushort Html = 0x1013;
        public const ushort InternetMessageId = 0x1035;
        public const ushort References = 0x1039;
        public const ushort InReplyTo = 0x1042;
        public const ushort DisplayName = 0x3001;
        public const ushort AddrType = 0x3002;
        public const ushort EmailAddress = 0x3003;
        public const ushort Comment = 0x3004;
        public const ushort CreationTime = 0x3007;
        public const ushort LastModificationTime = 0x3008;
        public const ushort ContentCount = 0x3602;
        public const ushort ContentUnreadCount = 0x3603;
        public const ushort Subfolders = 0x360A;
        public const ushort ContainerClass = 0x3613;
        public const ushort IpmSubtreeEntryId = 0x35E0;
        public const ushort IpmWastebasketEntryId = 0x35E3;
        public const ushort AttachDataBinary = 0x3701;
        public const ushort AttachExtension = 0x3703;
        public const ushort AttachFilename = 0x3704;
        public const ushort AttachMethod = 0x3705;
        public const ushort AttachLongFilename = 0x3707;
        public const ushort RenderingPosition = 0x370B;
        public const ushort AttachMimeTag = 0x370E;
        public const ushort AttachContentId = 0x3712;
        public const ushort AttachContentLocation = 0x3713;
        public const ushort AttachFlags = 0x3714;
        public const ushort SmtpAddress = 0x39FE;
        public const ushort InternetCodepage = 0x3FDE;
        public const ushort MessageCodepage = 0x3FFD;
        public const ushort SenderSmtpAddress = 0x5D01;
        public const ushort SentRepresentingSmtpAddress = 0x5D02;
        public const ushort LtpRowId = 0x67F2;
        public const ushort PstPassword = 0x67FF;
        public const ushort AttachmentHidden = 0x7FFE;
        public const ushort AttachmentContactPhoto = 0x7FFF;
        public const ushort RecipientFlags = 0x5FFD;
        public const ushort RecipientTrackStatus = 0x5FFF;
        public const ushort NameIdGuids = 0x0002;
        public const ushort NameIdEntries = 0x0003;
        public const ushort NameIdStrings = 0x0004;
    }

    /// <summary>Anything that has MAPI properties: a PST item, or an item read from an .msg file.</summary>
    public interface IPropertySource
    {
        bool Has(ushort id);
        object Get(ushort id);
        byte[] Raw(ushort id);
    }

    public static class PropValue
    {
        public static Encoding DefaultAnsi
        {
            get
            {
                try { return Encoding.GetEncoding(1252); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { return Encoding.GetEncoding(28591); }
            }
        }

        /// <summary>Turns raw property bytes into a .NET value: int, long, double, bool, DateTime (UTC), string, byte[], Guid, or an array.</summary>
        public static object Decode(ushort type, byte[] data, Encoding ansi)
        {
            if (data == null) return null;
            switch (type)
            {
                case PropType.Int16: return data.Length >= 2 ? (int)BitConverter.ToInt16(data, 0) : 0;
                case PropType.Int32:
                case PropType.Error: return data.Length >= 4 ? BitConverter.ToInt32(data, 0) : 0;
                case PropType.Float: return data.Length >= 4 ? (double)BitConverter.ToSingle(data, 0) : 0.0;
                case PropType.Double:
                case PropType.AppTime: return data.Length >= 8 ? BitConverter.ToDouble(data, 0) : 0.0;
                case PropType.Currency:
                case PropType.Int64: return data.Length >= 8 ? BitConverter.ToInt64(data, 0) : 0L;
                case PropType.Boolean: return data.Length >= 1 && data[0] != 0;
                case PropType.SysTime: return data.Length >= 8 ? FileTime(BitConverter.ToInt64(data, 0)) : (object)null;
                case PropType.Guid: return data.Length >= 16 ? new Guid(Slice(data, 0, 16)) : (object)null;
                case PropType.Unicode: return TrimNul(Encoding.Unicode.GetString(data, 0, data.Length & ~1));
                case PropType.String8: return TrimNul((ansi ?? DefaultAnsi).GetString(data));
                case PropType.Binary: return data;
                case PropType.MultiFlag | PropType.Unicode:
                case PropType.MultiFlag | PropType.String8:
                case PropType.MultiFlag | PropType.Binary:
                {
                    var items = Variable(data);
                    if (type == (PropType.MultiFlag | PropType.Binary)) return items.ToArray();
                    var strings = new string[items.Count];
                    for (var i = 0; i < items.Count; i++)
                        strings[i] = (string)Decode((ushort)(type & ~PropType.MultiFlag), items[i], ansi);
                    return strings;
                }
                default:
                    if ((type & PropType.MultiFlag) != 0)
                    {
                        var single = (ushort)(type & ~PropType.MultiFlag);
                        var size = PropType.FixedSize(single);
                        if (size == 0) return data;
                        var values = new object[data.Length / size];
                        for (var i = 0; i < values.Length; i++) values[i] = Decode(single, Slice(data, i * size, size), ansi);
                        return values;
                    }
                    return data;
            }
        }

        /// <summary>Several variable-size values: a count, offsets, then the values (MS-PST 2.3.3.4.2).</summary>
        private static List<byte[]> Variable(byte[] data)
        {
            var list = new List<byte[]>();
            if (data.Length < 4) return list;
            var count = BitConverter.ToInt32(data, 0);
            if (count < 0 || 4 + count * 4L > data.Length) return list;
            for (var i = 0; i < count; i++)
            {
                var start = BitConverter.ToInt32(data, 4 + i * 4);
                var end = i + 1 < count ? BitConverter.ToInt32(data, 8 + i * 4) : data.Length;
                if (start < 0 || end > data.Length || end < start) break;
                list.Add(Slice(data, start, end - start));
            }
            return list;
        }

        public static DateTime? FileTime(long value)
        {
            if (value <= 0 || value >= DateTime.MaxValue.ToFileTimeUtc()) return null;
            try { return DateTime.FromFileTimeUtc(value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static byte[] Slice(byte[] data, int start, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(data, start, slice, 0, count);
            return slice;
        }

        private static string TrimNul(string s)
        {
            var end = s.Length;
            while (end > 0 && s[end - 1] == '\0') end--;
            return end == s.Length ? s : s.Substring(0, end);
        }
    }

    public static class PropertySourceExtensions
    {
        public static string String(this IPropertySource p, ushort id) => p.Get(id) as string;

        public static int Int(this IPropertySource p, ushort id, int fallback = 0)
        {
            switch (p.Get(id))
            {
                case int i: return i;
                case long l: return (int)l;
                case bool b: return b ? 1 : 0;
                default: return fallback;
            }
        }

        public static long Long(this IPropertySource p, ushort id) => p.Get(id) is long l ? l : p.Get(id) is int i ? i : 0;

        public static bool Bool(this IPropertySource p, ushort id) => p.Get(id) is bool b ? b : p.Get(id) is int i && i != 0;

        public static DateTime? Time(this IPropertySource p, ushort id) => p.Get(id) as DateTime?;

        public static byte[] Bytes(this IPropertySource p, ushort id) => p.Get(id) as byte[] ?? (p.Get(id) is string ? p.Raw(id) : null);
    }
}
