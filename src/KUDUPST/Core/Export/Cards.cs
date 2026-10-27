using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kudupst.Core.Mail;

namespace Kudupst.Core.Export
{
    /// <summary>Lines for vCard and iCalendar files: escaped values, folded at 75 bytes, ending in CRLF.</summary>
    internal sealed class CardText
    {
        private readonly StringBuilder _sb = new StringBuilder();

        public static string Escape(string value) =>
            Printable(value).Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

        /// <summary>Without control characters (some bodies end in a NUL), which would break the file.</summary>
        public static string Printable(string value)
        {
            value = value ?? string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                if (c >= 0x20 || c == '\n' || c == '\r' || c == '\t') sb.Append(c);
            return sb.ToString();
        }

        public void Add(string name, string value, bool escape = true)
        {
            value = Printable(value).Trim();
            if (escape) value = Escape(value);
            if (value.Length == 0) return;
            Raw(name + ":" + value);
        }

        /// <summary>Adds a line as it is, folded so no line is longer than 75 bytes (RFC 5545 3.1, RFC 2425 5.8.1).</summary>
        public void Raw(string line)
        {
            var bytes = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var size = char.IsHighSurrogate(c) && i + 1 < line.Length ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                if (bytes + size > 75)
                {
                    _sb.Append("\r\n ");
                    bytes = 1;
                }
                _sb.Append(c);
                if (size == 4) _sb.Append(line[++i]);
                bytes += size;
            }
            _sb.Append("\r\n");
        }

        public override string ToString() => _sb.ToString();
    }

    /// <summary>Contacts as a vCard 3.0 file (RFC 2426), which phones, Google and most address books read.</summary>
    public static class VcardWriter
    {
        public static string Write(MailItem item)
        {
            var t = new CardText();
            t.Raw("BEGIN:VCARD");
            t.Raw("VERSION:3.0");
            if (item.Group != null) Group(t, item);
            else Contact(t, item);
            t.Raw("END:VCARD");
            return t.ToString();
        }

        private static void Contact(CardText t, MailItem item)
        {
            var c = item.Contact ?? new ContactInfo { DisplayName = item.Subject };
            t.Raw("N:" + string.Join(";", new[] { c.Surname, c.GivenName, c.MiddleName, c.Prefix, c.Suffix }.Select(CardText.Escape)));
            t.Raw("FN:" + CardText.Escape(c.Name.Length > 0 ? c.Name : c.FileAs));
            t.Add("NICKNAME", c.Nickname);
            if (c.Company.Length > 0 || c.Department.Length > 0) t.Raw("ORG:" + CardText.Escape(c.Company) + (c.Department.Length > 0 ? ";" + CardText.Escape(c.Department) : string.Empty));
            t.Add("TITLE", c.JobTitle);
            t.Add("ROLE", c.Profession);
            for (var i = 0; i < c.Emails.Count; i++)
                t.Raw("EMAIL;TYPE=INTERNET" + (i == 0 ? ",PREF" : string.Empty) + ":" + CardText.Escape(c.Emails[i].Address));
            foreach (var phone in c.Phones)
                t.Raw("TEL;TYPE=" + PhoneType(phone.Kind) + ":" + CardText.Escape(phone.Number));
            foreach (var a in c.Addresses)
            {
                var street = a.Street.Length == 0 && a.City.Length == 0 && a.PostalCode.Length == 0 ? a.Text : a.Street;
                var type = a.Kind == "Work" ? "WORK" : a.Kind == "Home" ? "HOME" : "POSTAL";
                t.Raw($"ADR;TYPE={type}:;;" + string.Join(";", new[] { street, a.City, a.State, a.PostalCode, a.Country }.Select(CardText.Escape)));
            }
            foreach (var web in c.Websites) t.Add("URL", web, false);
            t.Add("X-MS-IMADDRESS", c.InstantMessaging);
            if (c.Birthday.HasValue) t.Raw("BDAY:" + c.Birthday.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (c.Anniversary.HasValue) t.Raw("X-ANNIVERSARY:" + c.Anniversary.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            t.Add("X-SPOUSE", c.Spouse);
            t.Add("X-MANAGER", c.Manager);
            t.Add("X-ASSISTANT", c.Assistant);
            t.Add("NOTE", item.Body.PlainText.Trim());
            if (item.Categories.Length > 0) t.Raw("CATEGORIES:" + string.Join(",", item.Categories.Select(CardText.Escape)));
            var photo = c.Photo?.Data();
            if (photo != null && photo.Length > 0)
                t.Raw("PHOTO;ENCODING=b;TYPE=" + PhotoType(photo) + ":" + Convert.ToBase64String(photo));
        }

        /// <summary>
        /// vCard 3.0 has no contact groups; the ways other programs mark them are used (KIND/MEMBER from vCard 4, and the
        /// X-ADDRESSBOOKSERVER ones), with the members also listed in the note so nothing is lost.
        /// </summary>
        private static void Group(CardText t, MailItem item)
        {
            var g = item.Group;
            t.Raw("N:" + CardText.Escape(g.Name) + ";;;;");
            t.Raw("FN:" + CardText.Escape(g.Name));
            t.Raw("KIND:group");
            t.Raw("X-ADDRESSBOOKSERVER-KIND:group");
            foreach (var m in g.Members.Where(m => m.Address.Length > 0))
            {
                t.Raw("MEMBER:mailto:" + m.Address);
                t.Raw("X-ADDRESSBOOKSERVER-MEMBER:mailto:" + m.Address);
            }
            var note = "Contact group members:\n" + string.Join("\n", g.Members.Select(m => m.Display));
            var body = item.Body.PlainText.Trim();
            t.Add("NOTE", body.Length > 0 ? body + "\n\n" + note : note);
            if (item.Categories.Length > 0) t.Raw("CATEGORIES:" + string.Join(",", item.Categories.Select(CardText.Escape)));
        }

        private static string PhoneType(string kind)
        {
            switch (kind)
            {
                case ContactInfo.Mobile: return "CELL,VOICE";
                case ContactInfo.Business:
                case ContactInfo.Business2:
                case ContactInfo.CompanyMain: return "WORK,VOICE";
                case ContactInfo.Home:
                case ContactInfo.Home2: return "HOME,VOICE";
                case ContactInfo.BusinessFax: return "WORK,FAX";
                case ContactInfo.HomeFax: return "HOME,FAX";
                case ContactInfo.Pager: return "PAGER";
                case ContactInfo.Car: return "CAR,VOICE";
                case ContactInfo.Primary: return "PREF,VOICE";
                default: return "VOICE";
            }
        }

        private static string PhotoType(byte[] data)
        {
            if (data.Length > 3 && data[0] == 0x89 && data[1] == 0x50) return "PNG";
            if (data.Length > 3 && data[0] == 0x47 && data[1] == 0x49) return "GIF";
            if (data.Length > 3 && data[0] == 0x42 && data[1] == 0x4D) return "BMP";
            return "JPEG";
        }
    }

    /// <summary>Contacts as a CSV file with the columns Outlook and Google Contacts expect when importing.</summary>
    public static class ContactsCsv
    {
        public static readonly string[] Columns =
        {
            "Title", "First Name", "Middle Name", "Last Name", "Suffix", "Nickname",
            "Company", "Department", "Job Title", "Office Location", "Manager's Name", "Assistant's Name",
            "Business Street", "Business City", "Business State", "Business Postal Code", "Business Country/Region",
            "Home Street", "Home City", "Home State", "Home Postal Code", "Home Country/Region",
            "Other Street", "Other City", "Other State", "Other Postal Code", "Other Country/Region",
            "Assistant's Phone", "Business Fax", "Business Phone", "Business Phone 2", "Car Phone", "Company Main Phone",
            "Home Fax", "Home Phone", "Home Phone 2", "Mobile Phone", "Other Phone", "Pager", "Primary Phone",
            "E-mail Address", "E-mail Display Name", "E-mail 2 Address", "E-mail 2 Display Name", "E-mail 3 Address", "E-mail 3 Display Name",
            "Anniversary", "Birthday", "Spouse", "Web Page", "Personal Web Page", "Categories", "Notes"
        };

        public static string Header => string.Join(",", Columns.Select(Exporter.CsvCell)) + "\r\n";

        public static string Row(MailItem item)
        {
            var cells = new Dictionary<string, string>();
            if (item.Group != null)
            {
                cells["First Name"] = item.Group.Name;
                var note = "Contact group members: " + item.Group.MembersText;
                var body = item.Body.PlainText.Trim();
                cells["Notes"] = body.Length > 0 ? body + "\r\n\r\n" + note : note;
            }
            else
            {
                var c = item.Contact ?? new ContactInfo { DisplayName = item.Subject };
                cells["Title"] = c.Prefix;
                cells["First Name"] = c.GivenName.Length > 0 || c.Surname.Length > 0 ? c.GivenName : c.Name;
                cells["Middle Name"] = c.MiddleName;
                cells["Last Name"] = c.Surname;
                cells["Suffix"] = c.Suffix;
                cells["Nickname"] = c.Nickname;
                cells["Company"] = c.Company;
                cells["Department"] = c.Department;
                cells["Job Title"] = c.JobTitle;
                cells["Office Location"] = c.Office;
                cells["Manager's Name"] = c.Manager;
                cells["Assistant's Name"] = c.Assistant;
                foreach (var (kind, prefix) in new[] { ("Work", "Business"), ("Home", "Home"), ("Other", "Other") })
                {
                    var a = c.Address(kind);
                    if (a == null) continue;
                    cells[prefix + " Street"] = a.Street.Length == 0 && a.City.Length == 0 && a.PostalCode.Length == 0 ? a.Text : a.Street;
                    cells[prefix + " City"] = a.City;
                    cells[prefix + " State"] = a.State;
                    cells[prefix + " Postal Code"] = a.PostalCode;
                    cells[prefix + " Country/Region"] = a.Country;
                }
                cells["Assistant's Phone"] = c.Phone(ContactInfo.AssistantPhone);
                cells["Business Fax"] = c.Phone(ContactInfo.BusinessFax);
                cells["Business Phone"] = c.Phone(ContactInfo.Business);
                cells["Business Phone 2"] = c.Phone(ContactInfo.Business2);
                cells["Car Phone"] = c.Phone(ContactInfo.Car);
                cells["Company Main Phone"] = c.Phone(ContactInfo.CompanyMain);
                cells["Home Fax"] = c.Phone(ContactInfo.HomeFax);
                cells["Home Phone"] = c.Phone(ContactInfo.Home);
                cells["Home Phone 2"] = c.Phone(ContactInfo.Home2);
                cells["Mobile Phone"] = c.Phone(ContactInfo.Mobile);
                cells["Other Phone"] = c.Phone(ContactInfo.Other);
                cells["Pager"] = c.Phone(ContactInfo.Pager);
                cells["Primary Phone"] = c.Phone(ContactInfo.Primary);
                for (var i = 0; i < c.Emails.Count && i < 3; i++)
                {
                    var n = i == 0 ? string.Empty : " " + (i + 1);
                    cells[$"E-mail{n} Address"] = c.Emails[i].Address;
                    cells[$"E-mail{n} Display Name"] = c.Emails[i].Name;
                }
                cells["Anniversary"] = c.Anniversary?.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
                cells["Birthday"] = c.Birthday?.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
                cells["Spouse"] = c.Spouse;
                cells["Web Page"] = c.Websites.FirstOrDefault();
                cells["Personal Web Page"] = c.Websites.Skip(1).FirstOrDefault();
                cells["Notes"] = item.Body.PlainText.Trim();
            }
            cells["Categories"] = string.Join(";", item.Categories);
            return string.Join(",", Columns.Select(col => Exporter.CsvCell(cells.TryGetValue(col, out var v) ? v : string.Empty))) + "\r\n";
        }
    }

    /// <summary>
    /// Calendar items as an iCalendar file (RFC 5545), which Google Calendar, Apple Calendar, Thunderbird and Outlook
    /// import. Repeating items keep their pattern, their time zone, and the times that were skipped or moved.
    /// </summary>
    public sealed class IcsWriter
    {
        private readonly CardText _events = new CardText();
        private readonly CardText _zones = new CardText();
        private readonly Dictionary<string, string> _zoneIds = new Dictionary<string, string>();
        private readonly DateTime _stamp;

        public int Count { get; private set; }

        public IcsWriter(DateTime? stamp = null)
        {
            _stamp = stamp ?? DateTime.UtcNow;
        }

        /// <summary>Appointments and meeting invitations (not replies or cancellations, which aren't events).</summary>
        public static bool IsEvent(MailItem item) =>
            item.Appointment != null &&
            (item.Kind == ItemKind.Appointment || item.MessageClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase));

        public void Add(MailItem item)
        {
            var a = item.Appointment;
            if (a == null || !a.Start.HasValue) return;
            var t = _events;
            var uid = Uid(item);
            var r = a.Recurring ? a.Recurrence : null;
            var zone = r != null && a.TimeZone != null && !a.AllDay ? Zone(a) : null;

            t.Raw("BEGIN:VEVENT");
            t.Add("UID", uid);
            t.Raw("DTSTAMP:" + Utc(_stamp));
            if (r != null)
            {
                var start = r.StartDate.AddMinutes(r.StartMinutes);
                var end = r.StartDate.AddMinutes(Math.Max(r.EndMinutes, r.StartMinutes));
                if (a.AllDay)
                {
                    t.Raw("DTSTART;VALUE=DATE:" + Date(r.StartDate));
                    t.Raw("DTEND;VALUE=DATE:" + Date(r.StartDate.AddDays(Math.Max(1, (int)Math.Round((r.EndMinutes - r.StartMinutes) / 1440.0)))));
                }
                else
                {
                    t.Raw("DTSTART" + Local(zone, start));
                    t.Raw("DTEND" + Local(zone, end));
                }
                t.Raw("RRULE:" + Rule(r, a, zone));
                var removed = r.RemovedDates.ToList();
                if (removed.Count > 0)
                {
                    t.Raw(a.AllDay
                        ? "EXDATE;VALUE=DATE:" + string.Join(",", removed.Select(d => Date(d)))
                        : "EXDATE" + (zone != null ? ";TZID=\"" + zone + "\"" : string.Empty) + ":" + string.Join(",", removed.Select(d => Stamp(d.Date.AddMinutes(r.StartMinutes)))));
                }
            }
            else if (a.AllDay)
            {
                var (first, last) = a.Days();
                t.Raw("DTSTART;VALUE=DATE:" + Date(first));
                t.Raw("DTEND;VALUE=DATE:" + Date(last.AddDays(1)));
            }
            else
            {
                t.Raw("DTSTART:" + Utc(a.Start.Value));
                t.Raw("DTEND:" + Utc(a.End ?? a.Start.Value));
            }
            Common(t, item, item.Subject, a.Location, a.BusyStatus, a.ReminderMinutes, true);
            t.Raw("END:VEVENT");

            // Times that were moved or changed: their own event, tied to the repeating one by UID and RECURRENCE-ID.
            if (r != null)
            {
                foreach (var e in r.Exceptions)
                {
                    t.Raw("BEGIN:VEVENT");
                    t.Add("UID", uid);
                    t.Raw("DTSTAMP:" + Utc(_stamp));
                    var allDay = e.AllDay ?? a.AllDay;
                    if (a.AllDay) t.Raw("RECURRENCE-ID;VALUE=DATE:" + Date(e.OriginalStart));
                    else t.Raw("RECURRENCE-ID" + Local(zone, e.OriginalStart));
                    if (allDay)
                    {
                        t.Raw("DTSTART;VALUE=DATE:" + Date(e.Start));
                        var endDay = e.End.TimeOfDay == TimeSpan.Zero ? e.End.Date : e.End.Date.AddDays(1);
                        t.Raw("DTEND;VALUE=DATE:" + Date(endDay > e.Start.Date ? endDay : e.Start.Date.AddDays(1)));
                    }
                    else
                    {
                        t.Raw("DTSTART" + Local(zone, e.Start));
                        t.Raw("DTEND" + Local(zone, e.End));
                    }
                    var reminder = e.ReminderSet == false ? null : e.ReminderMinutes ?? a.ReminderMinutes;
                    Common(t, item, e.Subject ?? item.Subject, e.Location ?? a.Location, e.BusyStatus ?? a.BusyStatus, reminder, false);
                    t.Raw("END:VEVENT");
                }
            }
            Count++;
        }

        private static void Common(CardText t, MailItem item, string subject, string location, int busy, int? reminder, bool withDescription)
        {
            var a = item.Appointment;
            t.Add("SUMMARY", subject);
            t.Add("LOCATION", location);
            if (withDescription) t.Add("DESCRIPTION", item.Body.PlainText.Trim());
            t.Raw("TRANSP:" + (busy == 0 ? "TRANSPARENT" : "OPAQUE"));
            t.Raw("X-MICROSOFT-CDO-BUSYSTATUS:" + (busy == 0 ? "FREE" : busy == 1 ? "TENTATIVE" : busy == 3 ? "OOF" : busy == 4 ? "WORKINGELSEWHERE" : "BUSY"));
            t.Raw("CLASS:" + (item.Sensitivity == 2 ? "PRIVATE" : item.Sensitivity == 3 ? "CONFIDENTIAL" : "PUBLIC"));
            if (item.Importance == 2) t.Raw("PRIORITY:1");
            else if (item.Importance == 0) t.Raw("PRIORITY:9");
            if (item.Categories.Length > 0) t.Raw("CATEGORIES:" + string.Join(",", item.Categories.Select(CardText.Escape)));
            if (a.IsMeeting)
            {
                if (!a.Organizer.IsEmpty) t.Raw("ORGANIZER" + Cn(a.Organizer) + ":" + Mailto(a.Organizer));
                foreach (var r in a.Attendees)
                {
                    var role = r.MeetingType == 2 ? "OPT-PARTICIPANT" : r.MeetingType == 3 ? "NON-PARTICIPANT" : "REQ-PARTICIPANT";
                    var status = r.Response == 3 ? "ACCEPTED" : r.Response == 4 ? "DECLINED" : r.Response == 2 ? "TENTATIVE" : "NEEDS-ACTION";
                    var kind = r.MeetingType == 3 ? ";CUTYPE=RESOURCE" : ";CUTYPE=INDIVIDUAL";
                    t.Raw("ATTENDEE" + Cn(r.Address) + kind + ";ROLE=" + role + ";PARTSTAT=" + status + ":" + Mailto(r.Address));
                }
            }
            if (reminder.HasValue)
            {
                t.Raw("BEGIN:VALARM");
                t.Raw("ACTION:DISPLAY");
                t.Add("DESCRIPTION", string.IsNullOrEmpty(subject) ? "Reminder" : subject);
                t.Raw("TRIGGER:-PT" + reminder.Value.ToString(CultureInfo.InvariantCulture) + "M");
                t.Raw("END:VALARM");
            }
        }

        private static string Cn(MailAddress a)
        {
            if (a.Name.Length == 0) return string.Empty;
            var name = a.Name.Replace("\"", "'");
            return ";CN=\"" + name + "\"";
        }

        private static string Mailto(MailAddress a) => "mailto:" + (a.Address.Length > 0 ? a.Address : a.Name.Replace(" ", string.Empty) + "@invalid");

        /// <summary>The meeting's own ID (the same in every attendee's copy), else one made from the item.</summary>
        private static string Uid(MailItem item)
        {
            var id = item.Appointment.GlobalId;
            if (id != null && id.Length >= 20)
            {
                // The ID of the whole series: without the date of a single time (bytes 16-19).
                var copy = (byte[])id.Clone();
                copy[16] = copy[17] = copy[18] = copy[19] = 0;
                return BitConverter.ToString(copy).Replace("-", string.Empty);
            }
            using (var sha = SHA1.Create())
            {
                var seed = item.Subject + "|" + Utc(item.Appointment.Start ?? DateTime.MinValue) + "|" + item.Appointment.Location;
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed))).Replace("-", string.Empty).ToLowerInvariant() + "@kudupst";
            }
        }

        private static string Rule(RecurrencePattern r, AppointmentInfo a, string zone)
        {
            var parts = new List<string>();
            string Days() => string.Join(",", r.DaysOfWeek.Select(d => DayCode(d)));
            var nth = r.Nth >= 5 ? "-1" : Math.Max(1, r.Nth).ToString(CultureInfo.InvariantCulture);
            var yearly = r.Frequency == RecurrencePattern.Yearly;
            var interval = 1;
            switch (r.PatternType)
            {
                case RecurrencePattern.PatternDay:
                    parts.Add("FREQ=DAILY");
                    interval = Math.Max(1, r.Period / 1440);
                    break;
                case RecurrencePattern.PatternWeek:
                    parts.Add("FREQ=WEEKLY");
                    interval = r.Frequency == RecurrencePattern.Daily ? 1 : Math.Max(1, r.Period);
                    parts.Add("BYDAY=" + Days());
                    parts.Add("WKST=" + DayCode((DayOfWeek)(r.FirstDayOfWeek % 7)));
                    break;
                case RecurrencePattern.PatternMonth:
                    parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                    interval = yearly ? Math.Max(1, r.Period / 12) : Math.Max(1, r.Period);
                    if (yearly) parts.Add("BYMONTH=" + r.StartDate.Month.ToString(CultureInfo.InvariantCulture));
                    // Outlook's "day 31" means the last day in shorter months.
                    parts.Add("BYMONTHDAY=" + (r.DayOfMonth >= 31 ? "-1" : r.DayOfMonth.ToString(CultureInfo.InvariantCulture)));
                    break;
                case RecurrencePattern.PatternMonthNth:
                    parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                    interval = yearly ? Math.Max(1, r.Period / 12) : Math.Max(1, r.Period);
                    if (yearly) parts.Add("BYMONTH=" + r.StartDate.Month.ToString(CultureInfo.InvariantCulture));
                    parts.Add("BYDAY=" + Days());
                    parts.Add("BYSETPOS=" + nth);
                    break;
                case RecurrencePattern.PatternMonthEnd:
                    parts.Add(yearly ? "FREQ=YEARLY" : "FREQ=MONTHLY");
                    interval = yearly ? Math.Max(1, r.Period / 12) : Math.Max(1, r.Period);
                    if (yearly) parts.Add("BYMONTH=" + r.StartDate.Month.ToString(CultureInfo.InvariantCulture));
                    parts.Add("BYMONTHDAY=-1");
                    break;
            }
            if (interval > 1) parts.Insert(1, "INTERVAL=" + interval.ToString(CultureInfo.InvariantCulture));
            if (r.EndType == RecurrencePattern.EndAfterCount) parts.Add("COUNT=" + r.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
            else if (r.EndType == RecurrencePattern.EndByDate)
            {
                if (a.AllDay) parts.Add("UNTIL=" + Date(r.EndDate));
                else
                {
                    var lastLocal = r.EndDate.Date.AddMinutes(r.StartMinutes);
                    parts.Add("UNTIL=" + Utc(zone != null ? a.TimeZone.ToUtc(lastLocal) : DateTime.SpecifyKind(lastLocal, DateTimeKind.Local).ToUniversalTime()));
                }
            }
            return string.Join(";", parts);
        }

        private static string DayCode(DayOfWeek d) => new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }[(int)d];

        /// <summary>The TZID for an item's time zone, adding its VTIMEZONE the first time it's used.</summary>
        private string Zone(AppointmentInfo a)
        {
            var z = a.TimeZone;
            var key = $"{z.Bias}|{z.StandardBias}|{z.DaylightBias}|{Sig(z.StandardDate)}|{Sig(z.DaylightDate)}";
            if (_zoneIds.TryGetValue(key, out var id)) return id;
            var name = a.TimeZoneName.Length > 0 ? a.TimeZoneName : "Time zone";
            name = CardText.Printable(name).Replace("\"", "'");
            var unique = name;
            for (var n = 2; _zoneIds.ContainsValue(unique); n++) unique = $"{name} {n}";
            _zoneIds[key] = unique;

            var t = _zones;
            t.Raw("BEGIN:VTIMEZONE");
            t.Raw("TZID:" + unique);
            if (z.HasDaylight)
            {
                Part(t, "STANDARD", z.StandardDate, z.DaylightOffset, z.StandardOffset);
                Part(t, "DAYLIGHT", z.DaylightDate, z.StandardOffset, z.DaylightOffset);
            }
            else
            {
                t.Raw("BEGIN:STANDARD");
                t.Raw("DTSTART:16010101T000000");
                t.Raw("TZOFFSETFROM:" + Offset(z.StandardOffset));
                t.Raw("TZOFFSETTO:" + Offset(z.StandardOffset));
                t.Raw("END:STANDARD");
            }
            t.Raw("END:VTIMEZONE");
            return unique;
        }

        private static string Sig(TimeZoneRule.SystemTime s) => $"{s.Year}.{s.Month}.{s.DayOfWeek}.{s.Day}.{s.Hour}.{s.Minute}";

        private static void Part(CardText t, string kind, TimeZoneRule.SystemTime when, int from, int to)
        {
            t.Raw("BEGIN:" + kind);
            if (when.Year != 0)
            {
                t.Raw("DTSTART:" + Stamp(TimeZoneRule.ChangeDay(when.Year, when)));
            }
            else
            {
                t.Raw("DTSTART:" + Stamp(TimeZoneRule.ChangeDay(1601, when)));
                var week = when.Day >= 5 ? "-1" : Math.Max(1, when.Day).ToString(CultureInfo.InvariantCulture);
                t.Raw($"RRULE:FREQ=YEARLY;BYMONTH={when.Month};BYDAY={week}{DayCode((DayOfWeek)(when.DayOfWeek % 7))}");
            }
            t.Raw("TZOFFSETFROM:" + Offset(from));
            t.Raw("TZOFFSETTO:" + Offset(to));
            t.Raw("END:" + kind);
        }

        private static string Offset(int minutes)
        {
            var sign = minutes < 0 ? "-" : "+";
            minutes = Math.Abs(minutes);
            return sign + (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Local(string zone, DateTime local) => (zone != null ? ";TZID=\"" + zone + "\"" : string.Empty) + ":" + Stamp(local);
        private static string Stamp(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        private static string Utc(DateTime d) => Stamp(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d) + "Z";
        private static string Date(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        public override string ToString()
        {
            var t = new CardText();
            t.Raw("BEGIN:VCALENDAR");
            t.Raw("VERSION:2.0");
            t.Raw("PRODID:-//KUDUPST//KUDUPST//EN");
            t.Raw("CALSCALE:GREGORIAN");
            t.Raw("METHOD:PUBLISH");
            return t + _zones.ToString() + _events + "END:VCALENDAR\r\n";
        }
    }
}
