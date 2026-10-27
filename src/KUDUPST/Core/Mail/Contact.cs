using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kudupst.Core.Pst;

namespace Kudupst.Core.Mail
{
    public sealed class ContactPhone
    {
        public string Kind { get; set; }
        public string Number { get; set; }
    }

    public sealed class ContactAddress
    {
        public string Kind { get; set; }   // Work, Home, Other
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;

        /// <summary>The whole address as Outlook shows it, when the parts are missing.</summary>
        public string Text { get; set; } = string.Empty;

        public bool IsEmpty => Street.Length == 0 && City.Length == 0 && State.Length == 0 && PostalCode.Length == 0 && Country.Length == 0 && Text.Length == 0;

        public string Display
        {
            get
            {
                if (Street.Length == 0 && City.Length == 0 && PostalCode.Length == 0 && Text.Length > 0) return Text;
                var cityLine = string.Join(" ", new[] { City + (State.Length > 0 && City.Length > 0 ? "," : string.Empty), State, PostalCode }.Where(s => s.Length > 0));
                return string.Join("\n", new[] { Street, cityLine, Country }.Where(s => s.Length > 0));
            }
        }
    }

    /// <summary>A contact's details (MS-OXOCNTC).</summary>
    public sealed class ContactInfo
    {
        public string DisplayName { get; set; } = string.Empty;
        public string FileAs { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public string GivenName { get; set; } = string.Empty;
        public string MiddleName { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string Office { get; set; } = string.Empty;
        public string Profession { get; set; } = string.Empty;
        public string Manager { get; set; } = string.Empty;
        public string Assistant { get; set; } = string.Empty;
        public string Spouse { get; set; } = string.Empty;
        public string InstantMessaging { get; set; } = string.Empty;
        public List<MailAddress> Emails { get; } = new List<MailAddress>();
        public List<ContactPhone> Phones { get; } = new List<ContactPhone>();
        public List<ContactAddress> Addresses { get; } = new List<ContactAddress>();
        public List<string> Websites { get; } = new List<string>();
        public DateTime? Birthday { get; set; }
        public DateTime? Anniversary { get; set; }
        public string[] Categories { get; set; } = new string[0];

        /// <summary>The contact's photo, if there is one.</summary>
        public Attachment Photo { get; set; }

        public string Name => DisplayName.Length > 0 ? DisplayName : string.Join(" ", new[] { GivenName, MiddleName, Surname }.Where(s => s.Length > 0));

        public string Phone(string kind) => Phones.FirstOrDefault(p => p.Kind == kind)?.Number ?? string.Empty;
        public ContactAddress Address(string kind) => Addresses.FirstOrDefault(a => a.Kind == kind);

        /// <summary>Everything worth searching for: name, company, emails, phone numbers.</summary>
        public string SearchText => string.Join(" ", new[] { Name, FileAs, Nickname, Company, Department, JobTitle }
            .Concat(Emails.Select(e => e.Address)).Concat(Phones.Select(p => p.Number)));

        // Phone kinds, as shown and as the CSV columns are named.
        public const string Mobile = "Mobile";
        public const string Business = "Business";
        public const string Business2 = "Business 2";
        public const string Home = "Home";
        public const string Home2 = "Home 2";
        public const string BusinessFax = "Business fax";
        public const string HomeFax = "Home fax";
        public const string Pager = "Pager";
        public const string Car = "Car";
        public const string Other = "Other";
        public const string Primary = "Primary";
        public const string CompanyMain = "Company main";
        public const string AssistantPhone = "Assistant";

        private static readonly (ushort Tag, string Kind)[] PhoneTags =
        {
            (0x3A1C, Mobile), (0x3A08, Business), (0x3A1B, Business2), (0x3A09, Home), (0x3A2F, Home2), (0x3A1A, Primary),
            (0x3A57, CompanyMain), (0x3A24, BusinessFax), (0x3A25, HomeFax), (0x3A21, Pager), (0x3A1E, Car), (0x3A2E, AssistantPhone), (0x3A1F, Other)
        };

        /// <summary>Reads a contact from its properties (PST or MSG).</summary>
        public static ContactInfo Read(Props p, IList<Attachment> attachments)
        {
            var c = new ContactInfo
            {
                DisplayName = p.String(PropId.DisplayName),
                FileAs = p.String(PropSet.Address, Lid.FileUnder),
                Prefix = p.String(0x3A45),
                GivenName = p.String(0x3A06),
                MiddleName = p.String(0x3A44),
                Surname = p.String(0x3A11),
                Suffix = p.String(0x3A05),
                Nickname = p.String(0x3A4F),
                Company = p.String(0x3A16),
                JobTitle = p.String(0x3A17),
                Department = p.String(0x3A18),
                Office = p.String(0x3A19),
                Profession = p.String(0x3A46),
                Manager = p.String(0x3A4E),
                Assistant = p.String(0x3A30),
                Spouse = p.String(0x3A48),
                InstantMessaging = p.String(PropSet.Address, Lid.InstantMessagingAddress),
                Categories = p.Strings(PropSet.PublicStrings, "Keywords")
            };
            if (c.DisplayName.Length == 0) c.DisplayName = PstStore.CleanSubject(p.String(PropId.Subject));

            foreach (var (name, type, address) in new[]
                     {
                         (Lid.Email1DisplayName, Lid.Email1AddressType, Lid.Email1EmailAddress),
                         (Lid.Email2DisplayName, Lid.Email2AddressType, Lid.Email2EmailAddress),
                         (Lid.Email3DisplayName, Lid.Email3AddressType, Lid.Email3EmailAddress)
                     })
            {
                var a = p.String(PropSet.Address, address);
                if (a.Length == 0) continue;
                c.Emails.Add(new MailAddress(p.String(PropSet.Address, name), a));
            }

            foreach (var (tag, kind) in PhoneTags)
            {
                var number = p.String(tag);
                if (number.Length > 0) c.Phones.Add(new ContactPhone { Kind = kind, Number = number });
            }

            var work = new ContactAddress
            {
                Kind = "Work",
                Street = First(p.String(PropSet.Address, Lid.WorkAddressStreet), p.String(0x3A29)),
                City = First(p.String(PropSet.Address, Lid.WorkAddressCity), p.String(0x3A27)),
                State = First(p.String(PropSet.Address, Lid.WorkAddressState), p.String(0x3A28)),
                PostalCode = First(p.String(PropSet.Address, Lid.WorkAddressPostalCode), p.String(0x3A2A)),
                Country = First(p.String(PropSet.Address, Lid.WorkAddressCountry), p.String(0x3A26)),
                Text = p.String(PropSet.Address, Lid.WorkAddress)
            };
            var home = new ContactAddress
            {
                Kind = "Home", Street = p.String(0x3A5D), City = p.String(0x3A59), State = p.String(0x3A5C), PostalCode = p.String(0x3A5B), Country = p.String(0x3A5A),
                Text = p.String(PropSet.Address, Lid.HomeAddress)
            };
            var other = new ContactAddress
            {
                Kind = "Other", Street = p.String(0x3A63), City = p.String(0x3A5F), State = p.String(0x3A62), PostalCode = p.String(0x3A61), Country = p.String(0x3A60),
                Text = p.String(PropSet.Address, Lid.OtherAddress)
            };
            foreach (var a in new[] { work, home, other })
                if (!a.IsEmpty) c.Addresses.Add(a);

            foreach (var web in new[] { p.String(PropSet.Address, Lid.Html), p.String(0x3A51), p.String(0x3A50) })
                if (web.Length > 0 && !c.Websites.Contains(web, StringComparer.OrdinalIgnoreCase)) c.Websites.Add(web);

            c.Birthday = DateOnly(p.Time(PropSet.Address, Lid.BirthdayLocal), true) ?? DateOnly(p.Time(0x3A42), false);
            c.Anniversary = DateOnly(p.Time(PropSet.Address, Lid.WeddingAnniversaryLocal), true) ?? DateOnly(p.Time(0x3A41), false);
            c.Photo = attachments?.FirstOrDefault(a => a.IsContactPhoto);
            return c;
        }

        private static string First(string a, string b) => a.Length > 0 ? a : b;

        /// <summary>
        /// Birthdays are stored as a moment in time: midnight where the contact was saved, as UTC (a day early for
        /// anyone east of Greenwich). The local copy is exact; the UTC copy is rounded to the nearest day.
        /// </summary>
        private static DateTime? DateOnly(DateTime? value, bool local)
        {
            if (!value.HasValue || value.Value.Year < 1700 || value.Value.Year > 4000) return null;
            var v = value.Value;
            if (!local) v = v.AddHours(12);
            return new DateTime(v.Year, v.Month, v.Day, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    /// <summary>A contact group (distribution list): its members.</summary>
    public sealed class GroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public List<MailAddress> Members { get; } = new List<MailAddress>();

        private static readonly Guid OneOffProvider = new Guid(new byte[] { 0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02 });

        /// <summary>
        /// Members are kept twice: as entry IDs (which may point at contacts elsewhere in the file) and as "one-off"
        /// entries with each member's name and address, in the same order. The one-off list is read; a member that is
        /// a contact is looked up for its address when the one-off entry has none.
        /// </summary>
        public static GroupInfo Read(Props p, Func<uint, MailAddress> contactByNid)
        {
            var g = new GroupInfo { Name = p.String(PropSet.Address, Lid.DistributionListName) };
            if (g.Name.Length == 0) g.Name = PstStore.CleanSubject(p.String(PropId.Subject));
            var oneOffs = p.Get(PropSet.Address, Lid.DistributionListOneOffMembers) as byte[][];
            var members = p.Get(PropSet.Address, Lid.DistributionListMembers) as byte[][];
            var count = Math.Max(oneOffs?.Length ?? 0, members?.Length ?? 0);
            for (var i = 0; i < count; i++)
            {
                MailAddress address = null;
                if (oneOffs != null && i < oneOffs.Length) address = ParseOneOff(oneOffs[i]);
                if ((address == null || address.Address.Length == 0) && members != null && i < members.Length)
                {
                    var fromEntry = ParseOneOff(members[i]) ?? ContactOf(members[i], contactByNid);
                    if (fromEntry != null) address = fromEntry;
                }
                if (address != null && !address.IsEmpty) g.Members.Add(address);
            }
            return g;
        }

        /// <summary>A one-off entry ID (MS-OXCDATA 2.2.5.1): flags, provider, version, flags, then name, type and address.</summary>
        public static MailAddress ParseOneOff(byte[] entry)
        {
            if (entry == null || entry.Length < 24 || new Guid(PropValue.Slice(entry, 4, 16)) != OneOffProvider) return null;
            var flags = BitConverter.ToUInt16(entry, 22);
            var unicode = (flags & 0x8000) != 0;
            var at = 24;
            string Next()
            {
                var sb = new StringBuilder();
                if (unicode)
                {
                    while (at + 1 < entry.Length)
                    {
                        var ch = (char)BitConverter.ToUInt16(entry, at);
                        at += 2;
                        if (ch == '\0') break;
                        sb.Append(ch);
                    }
                }
                else
                {
                    while (at < entry.Length)
                    {
                        var b = entry[at++];
                        if (b == 0) break;
                        sb.Append((char)b);
                    }
                }
                return sb.ToString();
            }
            var name = Next();
            Next(); // address type
            var address = Next();
            return new MailAddress(name, address);
        }

        /// <summary>A member that's a contact: a wrapped entry ID (MS-OXOCNTC 2.2.2.2.4.1) holding the contact's node ID.</summary>
        private static MailAddress ContactOf(byte[] entry, Func<uint, MailAddress> contactByNid)
        {
            if (entry == null || contactByNid == null || entry.Length < 25) return null;
            // flags (4), provider (16), type (1), then the contact's entry ID: flags (4), provider (16), node ID (4)...
            var inner = 21;
            if (entry.Length >= inner + 24)
            {
                var nid = BitConverter.ToUInt32(entry, inner + 20);
                return contactByNid(nid);
            }
            return null;
        }

        public string MembersText => string.Join("; ", Members.Select(m => m.Display));
    }
}
