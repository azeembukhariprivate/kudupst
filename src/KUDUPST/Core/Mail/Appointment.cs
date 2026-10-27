using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Kudupst.Core.Pst;

namespace Kudupst.Core.Mail
{
    /// <summary>An appointment or meeting: when, where and who (MS-OXOCAL).</summary>
    public sealed class AppointmentInfo
    {
        /// <summary>Start and end (UTC). For a repeating item, its first time.</summary>
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public bool AllDay { get; set; }
        public string Location { get; set; } = string.Empty;

        /// <summary>0 free, 1 tentative, 2 busy, 3 away, 4 working elsewhere.</summary>
        public int BusyStatus { get; set; } = 2;

        public bool IsMeeting { get; set; }
        public MailAddress Organizer { get; set; } = new MailAddress();
        public List<Recipient> Attendees { get; } = new List<Recipient>();

        /// <summary>Minutes before the start to remind, or null for no reminder.</summary>
        public int? ReminderMinutes { get; set; }

        public bool Recurring { get; set; }
        public RecurrencePattern Recurrence { get; set; }

        /// <summary>Outlook's own words for how it repeats ("every Monday from 10:00 AM to 11:00 AM").</summary>
        public string RecurrenceText { get; set; } = string.Empty;

        /// <summary>The time zone the repeating times are in.</summary>
        public TimeZoneRule TimeZone { get; set; }
        public string TimeZoneName { get; set; } = string.Empty;

        /// <summary>The meeting's ID, the same in every copy of it (from the organiser's calendar or an invitation).</summary>
        public byte[] GlobalId { get; set; }

        public string BusyText
        {
            get
            {
                switch (BusyStatus)
                {
                    case 0: return "Free";
                    case 1: return "Tentative";
                    case 3: return "Away";
                    case 4: return "Working elsewhere";
                    default: return "Busy";
                }
            }
        }

        /// <summary>How it repeats, in plain words.</summary>
        public string RepeatText => Recurrence?.Describe() ?? (Recurring && RecurrenceText.Length > 0 ? Capitalise(RecurrenceText) : string.Empty);

        private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static AppointmentInfo Read(Props p, MailItem item)
        {
            var a = new AppointmentInfo
            {
                Start = p.Time(PropSet.Appointment, Lid.AppointmentStartWhole),
                End = p.Time(PropSet.Appointment, Lid.AppointmentEndWhole),
                AllDay = p.Bool(PropSet.Appointment, Lid.AppointmentSubType),
                Location = p.String(PropSet.Appointment, Lid.Location),
                BusyStatus = p.Int(PropSet.Appointment, Lid.BusyStatus, 2),
                IsMeeting = (p.Int(PropSet.Appointment, Lid.AppointmentStateFlags) & 1) != 0,
                Recurring = p.Bool(PropSet.Appointment, Lid.Recurring),
                RecurrenceText = p.String(PropSet.Appointment, Lid.RecurrencePattern),
                TimeZoneName = p.String(PropSet.Appointment, Lid.TimeZoneDescription),
                GlobalId = p.Bytes(PropSet.Meeting, Lid.GlobalObjectId)
            };
            if (!a.Start.HasValue) a.Start = p.Time(0x0060); // PidTagStartDate
            if (!a.End.HasValue) a.End = p.Time(0x0061);     // PidTagEndDate
            if (!a.End.HasValue && a.Start.HasValue)
            {
                var minutes = p.Int(PropSet.Appointment, Lid.AppointmentDuration);
                a.End = a.Start.Value.AddMinutes(minutes);
            }
            if (p.Bool(PropSet.Common, Lid.ReminderSet)) a.ReminderMinutes = Math.Max(0, p.Int(PropSet.Common, Lid.ReminderDelta, 15));

            var tz = p.Bytes(PropSet.Appointment, Lid.TimeZoneStruct);
            if (tz != null) a.TimeZone = TimeZoneRule.Parse(tz);
            var recur = p.Bytes(PropSet.Appointment, Lid.AppointmentRecur);
            if (recur != null)
            {
                try { a.Recurrence = RecurrencePattern.Parse(recur); }
                catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException) { a.Recurrence = null; }
                if (a.Recurrence != null) a.Recurring = true;
            }

            var organizer = item.Recipients.FirstOrDefault(r => r.IsOrganizer);
            a.Organizer = organizer?.Address ?? item.From;
            foreach (var r in item.Recipients)
                if (!r.IsOrganizer) a.Attendees.Add(r);
            if (a.Attendees.Count > 0) a.IsMeeting = true;
            return a;
        }

        /// <summary>A repeating item's local time (in its own time zone) as UTC.</summary>
        public DateTime ToUtc(DateTime local) => TimeZone != null ? TimeZone.ToUtc(local) : DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

        public DateTime ToLocalOfItem(DateTime utc) => TimeZone != null ? TimeZone.ToLocal(utc) : utc.ToLocalTime();

        /// <summary>
        /// The day an all-day item falls on. It's stored as midnight where it was made, as UTC, so it's turned back into
        /// that place's time when the place is known; otherwise the computer's time zone is used.
        /// </summary>
        public DateTime DayOf(DateTime utc)
        {
            if (TimeZone != null) return TimeZone.ToLocal(utc).Date;
            var zone = SystemZone(TimeZoneName);
            if (zone != null) return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).AddHours(12).Date;
            return DayOfAllDay(utc);
        }

        /// <summary>
        /// The day of an all-day item when where it was made isn't known: the computer's day when that's at midnight
        /// (made in the same time zone), else the nearest midnight in UTC (right for anywhere from UTC-12 to UTC+11).
        /// </summary>
        public static DateTime DayOfAllDay(DateTime utc)
        {
            var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            if (local.TimeOfDay == TimeSpan.Zero) return local.Date;
            return utc.AddHours(12).Date;
        }

        /// <summary>The first and last day of an all-day item (the end is stored as the next midnight).</summary>
        public (DateTime First, DateTime Last) Days()
        {
            if (!Start.HasValue) return (DateTime.MinValue, DateTime.MinValue);
            var first = DayOf(Start.Value);
            var last = End.HasValue ? DayOf(End.Value).AddDays(-1) : first;
            return (first, last < first ? first : last);
        }

        private static TimeZoneInfo SystemZone(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { return TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z => string.Equals(z.DisplayName, name, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is InvalidTimeZoneException) { return null; }
        }

        public static string ResponseText(int response)
        {
            switch (response)
            {
                case 1: return "Organiser";
                case 2: return "Tentative";
                case 3: return "Accepted";
                case 4: return "Declined";
                case 5: return "No reply";
                default: return string.Empty;
            }
        }

        public static string AttendeeKind(int meetingType) => meetingType == 2 ? "Optional" : meetingType == 3 ? "Room or equipment" : "Required";
    }

    /// <summary>A change to one time of a repeating item: moved, or with a different title or place.</summary>
    public sealed class RecurrenceException
    {
        /// <summary>Local times, in the item's time zone.</summary>
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateTime OriginalStart { get; set; }
        public int OverrideFlags { get; set; }
        public string Subject { get; set; }
        public string Location { get; set; }
        public int? BusyStatus { get; set; }
        public int? ReminderMinutes { get; set; }
        public bool? ReminderSet { get; set; }
        public bool? AllDay { get; set; }
    }

    /// <summary>How an item repeats (MS-OXOCAL 2.2.1.44, the AppointmentRecurrencePattern structure).</summary>
    public sealed class RecurrencePattern
    {
        public const int Daily = 0x200A, Weekly = 0x200B, Monthly = 0x200C, Yearly = 0x200D;
        public const int PatternDay = 0, PatternWeek = 1, PatternMonth = 2, PatternMonthNth = 3, PatternMonthEnd = 4;
        public const int EndByDate = 0x2021, EndAfterCount = 0x2022, EndNever = 0x2023;

        public int Frequency { get; set; }
        public int PatternType { get; set; }
        public int CalendarType { get; set; }

        /// <summary>Minutes for a daily pattern, weeks for a weekly one, months otherwise.</summary>
        public int Period { get; set; }

        /// <summary>Days of the week: Sunday 1, Monday 2, ... Saturday 0x40.</summary>
        public int Days { get; set; }

        public int DayOfMonth { get; set; }

        /// <summary>Which week of the month: 1 to 4, or 5 for the last.</summary>
        public int Nth { get; set; }

        public int EndType { get; set; }
        public int OccurrenceCount { get; set; }
        public int FirstDayOfWeek { get; set; }

        /// <summary>Dates (local midnight) of the times that were deleted or changed, and of the changed ones.</summary>
        public List<DateTime> DeletedDates { get; } = new List<DateTime>();
        public List<DateTime> ModifiedDates { get; } = new List<DateTime>();

        /// <summary>The first and last day it repeats on (local).</summary>
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        /// <summary>Minutes after midnight that each time starts and ends.</summary>
        public int StartMinutes { get; set; }
        public int EndMinutes { get; set; }

        public List<RecurrenceException> Exceptions { get; } = new List<RecurrenceException>();

        public bool NeverEnds => EndType != EndByDate && EndType != EndAfterCount;

        /// <summary>The dates whose time was deleted, not moved.</summary>
        public IEnumerable<DateTime> RemovedDates => DeletedDates.Where(d => !Exceptions.Any(e => e.OriginalStart.Date == d.Date));

        public static DateTime FromMinutes(uint minutes) => new DateTime(1601, 1, 1).AddMinutes(minutes);

        public static RecurrencePattern Parse(byte[] data)
        {
            var r = new Reader(data);
            var p = new RecurrencePattern();
            r.U16(); // reader version
            r.U16(); // writer version
            p.Frequency = r.U16();
            p.PatternType = r.U16();
            p.CalendarType = r.U16();
            r.U32(); // first date-time
            p.Period = (int)r.U32();
            r.U32(); // sliding flag (tasks only)
            switch (p.PatternType)
            {
                case PatternWeek:
                    p.Days = (int)r.U32();
                    break;
                case PatternMonth:
                case PatternMonthEnd:
                case 0x0A:
                case 0x0C:
                    p.DayOfMonth = (int)r.U32();
                    break;
                case PatternMonthNth:
                case 0x0B:
                    p.Days = (int)r.U32();
                    p.Nth = (int)r.U32();
                    break;
            }
            if (p.PatternType == 0x0A) p.PatternType = PatternMonth;
            if (p.PatternType == 0x0B) p.PatternType = PatternMonthNth;
            if (p.PatternType == 0x0C) p.PatternType = PatternMonthEnd;
            p.EndType = (int)r.U32();
            p.OccurrenceCount = (int)r.U32();
            p.FirstDayOfWeek = (int)r.U32();
            var deleted = r.U32();
            for (var i = 0; i < deleted && i < 100000; i++) p.DeletedDates.Add(FromMinutes(r.U32()));
            var modified = r.U32();
            for (var i = 0; i < modified && i < 100000; i++) p.ModifiedDates.Add(FromMinutes(r.U32()));
            p.StartDate = FromMinutes(r.U32());
            p.EndDate = FromMinutes(r.U32());

            // The rest is only in the appointment form of the structure.
            if (r.Left < 16) return p;
            r.U32(); // reader version 2
            var writer2 = r.U32();
            p.StartMinutes = (int)r.U32();
            p.EndMinutes = (int)r.U32();
            var count = r.U16();
            for (var i = 0; i < count; i++)
            {
                var e = new RecurrenceException
                {
                    Start = FromMinutes(r.U32()),
                    End = FromMinutes(r.U32()),
                    OriginalStart = FromMinutes(r.U32()),
                    OverrideFlags = r.U16()
                };
                if ((e.OverrideFlags & 0x0001) != 0) e.Subject = r.AnsiWithLengths();
                if ((e.OverrideFlags & 0x0002) != 0) r.U32(); // meeting type
                if ((e.OverrideFlags & 0x0004) != 0) e.ReminderMinutes = (int)r.U32();
                if ((e.OverrideFlags & 0x0008) != 0) e.ReminderSet = r.U32() != 0;
                if ((e.OverrideFlags & 0x0010) != 0) e.Location = r.AnsiWithLengths();
                if ((e.OverrideFlags & 0x0020) != 0) e.BusyStatus = (int)r.U32();
                if ((e.OverrideFlags & 0x0040) != 0) r.U32(); // attachment
                if ((e.OverrideFlags & 0x0080) != 0) e.AllDay = r.U32() != 0;
                if ((e.OverrideFlags & 0x0100) != 0) r.U32(); // colour
                p.Exceptions.Add(e);
            }

            // Newer files repeat changed titles and places in Unicode (the extended exceptions).
            try
            {
                r.Skip((int)r.U32()); // reserved block 1
                foreach (var e in p.Exceptions)
                {
                    if (writer2 >= 0x3009) r.Skip((int)r.U32()); // change highlight
                    r.Skip((int)r.U32()); // reserved block EE1
                    if ((e.OverrideFlags & 0x0011) != 0)
                    {
                        r.Skip(12); // start, end and original start again
                        if ((e.OverrideFlags & 0x0001) != 0) e.Subject = r.Unicode(r.U16());
                        if ((e.OverrideFlags & 0x0010) != 0) e.Location = r.Unicode(r.U16());
                        r.Skip((int)r.U32()); // reserved block EE2
                    }
                }
            }
            catch (ArgumentException)
            {
                // Keep the ANSI copies.
            }
            return p;
        }

        private static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        private static readonly string[] NthNames = { string.Empty, "first", "second", "third", "fourth", "last" };

        public IEnumerable<DayOfWeek> DaysOfWeek
        {
            get
            {
                for (var i = 0; i < 7; i++)
                    if ((Days & (1 << i)) != 0) yield return (DayOfWeek)i;
            }
        }

        public bool IsWeekdays => Days == 0x3E;

        private string DayList()
        {
            if (IsWeekdays) return "weekday";
            var names = DaysOfWeek.Select(d => DayNames[(int)d]).ToList();
            if (names.Count == 0) return "day";
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        private static string Every(int n, string one, string many) => n <= 1 ? "Every " + one : $"Every {n} {many}";

        /// <summary>How it repeats, in plain words: "Every Monday, 10 times".</summary>
        public string Describe()
        {
            string what;
            var nth = Nth >= 1 && Nth <= 5 ? NthNames[Nth] : "first";
            var monthName = StartDate.ToString("MMMM", CultureInfo.InvariantCulture);
            switch (PatternType)
            {
                case PatternDay:
                    what = Every(Math.Max(1, Period / 1440), "day", "days");
                    break;
                case PatternWeek:
                    if (Frequency == Daily || (Period <= 1 && IsWeekdays)) what = IsWeekdays ? "Every weekday" : "Every " + DayList();
                    else what = Period <= 1 ? "Every " + DayList() : $"Every {Period} weeks on {DayList()}";
                    break;
                case PatternMonth:
                    what = Frequency == Yearly
                        ? Every(Math.Max(1, Period / 12), "year", "years") + $" on {DayOfMonth} {monthName}"
                        : Every(Period, "month", "months") + $" on day {DayOfMonth}";
                    break;
                case PatternMonthNth:
                    what = Frequency == Yearly
                        ? Every(Math.Max(1, Period / 12), "year", "years") + $" on the {nth} {DayList()} of {monthName}"
                        : Every(Period, "month", "months") + $" on the {nth} {DayList()}";
                    break;
                case PatternMonthEnd:
                    what = Frequency == Yearly
                        ? Every(Math.Max(1, Period / 12), "year", "years") + $" on the last day of {monthName}"
                        : Every(Period, "month", "months") + " on the last day";
                    break;
                default:
                    what = "Repeats";
                    break;
            }
            if (EndType == EndAfterCount) what += OccurrenceCount == 1 ? ", once" : $", {OccurrenceCount} times";
            else if (EndType == EndByDate) what += ", until " + EndDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            return what;
        }

        private sealed class Reader
        {
            private readonly byte[] _d;
            private int _at;

            public Reader(byte[] d) { _d = d; }

            public int Left => _d.Length - _at;

            private void Need(int n)
            {
                if (n < 0 || _at + n > _d.Length) throw new ArgumentException("The repeat details are damaged.");
            }

            public int U16() { Need(2); var v = BitConverter.ToUInt16(_d, _at); _at += 2; return v; }
            public uint U32() { Need(4); var v = BitConverter.ToUInt32(_d, _at); _at += 4; return v; }
            public void Skip(int n) { Need(n); _at += n; }

            public string AnsiWithLengths()
            {
                U16(); // length + 1
                var length = U16();
                Need(length);
                var s = PropValue.DefaultAnsi.GetString(_d, _at, length);
                _at += length;
                return s;
            }

            public string Unicode(int chars)
            {
                Need(chars * 2);
                var s = Encoding.Unicode.GetString(_d, _at, chars * 2);
                _at += chars * 2;
                return s;
            }
        }
    }

    /// <summary>A time zone as Outlook keeps it with a repeating item (TZSTRUCT: offsets and the daylight saving dates).</summary>
    public sealed class TimeZoneRule
    {
        /// <summary>Minutes: UTC = local + Bias (+ StandardBias or DaylightBias).</summary>
        public int Bias { get; set; }
        public int StandardBias { get; set; }
        public int DaylightBias { get; set; }
        public SystemTime StandardDate { get; set; }
        public SystemTime DaylightDate { get; set; }

        public bool HasDaylight => StandardDate.Month != 0 && DaylightDate.Month != 0;

        public struct SystemTime
        {
            public int Year, Month, DayOfWeek, Day, Hour, Minute;
        }

        public static TimeZoneRule Parse(byte[] d)
        {
            if (d == null || d.Length < 48) return null;
            SystemTime St(int at) => new SystemTime
            {
                Year = BitConverter.ToUInt16(d, at),
                Month = BitConverter.ToUInt16(d, at + 2),
                DayOfWeek = BitConverter.ToUInt16(d, at + 4),
                Day = BitConverter.ToUInt16(d, at + 6),
                Hour = BitConverter.ToUInt16(d, at + 8),
                Minute = BitConverter.ToUInt16(d, at + 10)
            };
            return new TimeZoneRule
            {
                Bias = BitConverter.ToInt32(d, 0),
                StandardBias = BitConverter.ToInt32(d, 4),
                DaylightBias = BitConverter.ToInt32(d, 8),
                StandardDate = St(14),
                DaylightDate = St(32)
            };
        }

        /// <summary>The day a change happens in a year: the Nth (5 = last) given weekday of the month.</summary>
        public static DateTime ChangeDay(int year, SystemTime t)
        {
            if (t.Year != 0) return new DateTime(year, t.Month, Math.Min(t.Day, DateTime.DaysInMonth(year, t.Month)), t.Hour, t.Minute, 0);
            var first = new DateTime(year, t.Month, 1);
            var day = first.AddDays(((t.DayOfWeek - (int)first.DayOfWeek) + 7) % 7);
            day = day.AddDays(7 * (Math.Max(1, t.Day) - 1));
            while (day.Month != t.Month) day = day.AddDays(-7);
            return day.AddHours(t.Hour).AddMinutes(t.Minute);
        }

        public bool IsDaylight(DateTime local)
        {
            if (!HasDaylight) return false;
            var start = ChangeDay(local.Year, DaylightDate);
            var end = ChangeDay(local.Year, StandardDate);
            return start < end ? local >= start && local < end : local >= start || local < end;
        }

        /// <summary>Minutes ahead of UTC at a local time.</summary>
        public int OffsetAt(DateTime local) => -(Bias + (IsDaylight(local) ? DaylightBias : StandardBias));

        public int StandardOffset => -(Bias + StandardBias);
        public int DaylightOffset => -(Bias + DaylightBias);

        public DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local.AddMinutes(-OffsetAt(local)), DateTimeKind.Utc);

        public DateTime ToLocal(DateTime utc)
        {
            var guess = utc.AddMinutes(StandardOffset);
            return DateTime.SpecifyKind(utc.AddMinutes(OffsetAt(guess)), DateTimeKind.Unspecified);
        }
    }
}
