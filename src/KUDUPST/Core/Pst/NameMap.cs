using System;
using System.Collections.Generic;
using System.Text;

namespace Kudupst.Core.Pst
{
    /// <summary>The groups of named properties KUDUPST reads (MS-OXPROPS), and the IDs in them.</summary>
    public static class PropSet
    {
        public static readonly Guid Common = new Guid("00062008-0000-0000-C000-000000000046");
        public static readonly Guid Address = new Guid("00062004-0000-0000-C000-000000000046");
        public static readonly Guid Appointment = new Guid("00062002-0000-0000-C000-000000000046");
        public static readonly Guid Meeting = new Guid("6ED8DA90-450B-101B-98DA-00AA003F1305");
        public static readonly Guid PublicStrings = new Guid("00020329-0000-0000-C000-000000000046");
        public static readonly Guid Mapi = new Guid("00020328-0000-0000-C000-000000000046");
    }

    /// <summary>Named property IDs (the "long IDs") within their sets.</summary>
    public static class Lid
    {
        // Contacts (PSETID_Address)
        public const int FileUnder = 0x8005;
        public const int HasPicture = 0x8015;
        public const int HomeAddress = 0x801A;
        public const int WorkAddress = 0x801B;
        public const int OtherAddress = 0x801C;
        public const int PostalAddressId = 0x8022;
        public const int Html = 0x802B;
        public const int WorkAddressStreet = 0x8045;
        public const int WorkAddressCity = 0x8046;
        public const int WorkAddressState = 0x8047;
        public const int WorkAddressPostalCode = 0x8048;
        public const int WorkAddressCountry = 0x8049;
        public const int DistributionListName = 0x8053;
        public const int DistributionListOneOffMembers = 0x8054;
        public const int DistributionListMembers = 0x8055;
        public const int InstantMessagingAddress = 0x8062;
        public const int Email1DisplayName = 0x8080;
        public const int Email1AddressType = 0x8082;
        public const int Email1EmailAddress = 0x8083;
        public const int Email2DisplayName = 0x8090;
        public const int Email2AddressType = 0x8092;
        public const int Email2EmailAddress = 0x8093;
        public const int Email3DisplayName = 0x80A0;
        public const int Email3AddressType = 0x80A2;
        public const int Email3EmailAddress = 0x80A3;
        public const int BirthdayLocal = 0x80DE;
        public const int WeddingAnniversaryLocal = 0x80DF;

        // Calendar (PSETID_Appointment)
        public const int BusyStatus = 0x8205;
        public const int Location = 0x8208;
        public const int AppointmentStartWhole = 0x820D;
        public const int AppointmentEndWhole = 0x820E;
        public const int AppointmentDuration = 0x8213;
        public const int AppointmentSubType = 0x8215;
        public const int AppointmentRecur = 0x8216;
        public const int AppointmentStateFlags = 0x8217;
        public const int ResponseStatus = 0x8218;
        public const int Recurring = 0x8223;
        public const int RecurrenceType = 0x8231;
        public const int RecurrencePattern = 0x8232;
        public const int TimeZoneStruct = 0x8233;
        public const int TimeZoneDescription = 0x8234;
        public const int ClipStart = 0x8235;
        public const int ClipEnd = 0x8236;

        // Common (PSETID_Common)
        public const int ReminderDelta = 0x8501;
        public const int ReminderTime = 0x8502;
        public const int ReminderSet = 0x8503;

        // Meetings (PSETID_Meeting)
        public const int GlobalObjectId = 0x0003;
        public const int CleanGlobalObjectId = 0x0023;
    }

    /// <summary>
    /// Outlook keeps contact and calendar fields as "named properties": each file has its own table saying which
    /// property ID (0x8000 and up) stands for which (property set, ID or name). Read from a PST/OST (node 0x61) or an
    /// .msg file (the __nameid storage); the layout is the same (MS-PST 2.4.7, MS-OXMSG 2.2.3).
    /// </summary>
    public sealed class NameMap
    {
        private readonly Dictionary<(Guid, int), ushort> _byLid = new Dictionary<(Guid, int), ushort>();
        private readonly Dictionary<(Guid, string), ushort> _byName = new Dictionary<(Guid, string), ushort>();

        public static readonly NameMap Empty = new NameMap();

        public int Count => _byLid.Count + _byName.Count;

        public static NameMap Parse(byte[] guids, byte[] entries, byte[] strings)
        {
            var map = new NameMap();
            if (entries == null) return map;
            guids = guids ?? new byte[0];
            strings = strings ?? new byte[0];
            for (var at = 0; at + 8 <= entries.Length; at += 8)
            {
                var id = BitConverter.ToUInt32(entries, at);
                var kind = BitConverter.ToUInt16(entries, at + 4);
                var index = BitConverter.ToUInt16(entries, at + 6);
                var isString = (kind & 1) != 0;
                var guidIndex = kind >> 1;
                Guid set;
                if (guidIndex == 1) set = PropSet.Mapi;
                else if (guidIndex == 2) set = PropSet.PublicStrings;
                else if (guidIndex >= 3 && (guidIndex - 3) * 16 + 16 <= guids.Length) set = new Guid(PropValue.Slice(guids, (guidIndex - 3) * 16, 16));
                else continue;
                var propId = (ushort)(0x8000 + index);
                if (isString)
                {
                    if (id + 4 > strings.Length) continue;
                    var length = BitConverter.ToInt32(strings, (int)id);
                    if (length < 0 || id + 4 + length > strings.Length) continue;
                    var name = Encoding.Unicode.GetString(strings, (int)id + 4, length);
                    map._byName[(set, name)] = propId;
                }
                else
                {
                    map._byLid[(set, (int)id)] = propId;
                }
            }
            return map;
        }

        /// <summary>The property ID used in this file for a named property, or 0 when the file has none.</summary>
        public ushort Id(Guid set, int lid) => _byLid.TryGetValue((set, lid), out var id) ? id : (ushort)0;

        public ushort Id(Guid set, string name) => _byName.TryGetValue((set, name), out var id) ? id : (ushort)0;
    }

    /// <summary>An item's properties together with its file's name table, to read named properties by name.</summary>
    public sealed class Props
    {
        public IPropertySource Source { get; }
        public NameMap Names { get; }

        public Props(IPropertySource source, NameMap names)
        {
            Source = source;
            Names = names ?? NameMap.Empty;
        }

        public object Get(Guid set, int lid)
        {
            var id = Names.Id(set, lid);
            return id == 0 ? null : Source.Get(id);
        }

        public string String(Guid set, int lid) => Get(set, lid) as string ?? string.Empty;
        public int Int(Guid set, int lid, int fallback = 0) => Get(set, lid) is int i ? i : Get(set, lid) is bool b ? (b ? 1 : 0) : fallback;
        public bool Bool(Guid set, int lid) => Get(set, lid) is bool b ? b : Get(set, lid) is int i && i != 0;
        public DateTime? Time(Guid set, int lid) => Get(set, lid) as DateTime?;
        public byte[] Bytes(Guid set, int lid) => Get(set, lid) as byte[];

        public string[] Strings(Guid set, string name)
        {
            var id = Names.Id(set, name);
            if (id == 0) return new string[0];
            var v = Source.Get(id);
            return v as string[] ?? (v is string s ? new[] { s } : new string[0]);
        }

        public string String(ushort id) => Source.String(id) ?? string.Empty;
        public DateTime? Time(ushort id) => Source.Time(id);
    }
}
