using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Tests
{
    // Contacts, contact groups and calendar items (expected values from pst-extractor's and java-libpst's tests).
    public static partial class Program
    {
        private static PstFolder TopFolder(PstStore store, string name) => store.AllFolders(store.Top).First(f => f.Name == name);

        private static MailItem ItemNamed(PstStore store, PstFolder folder, string subject)
        {
            var row = store.Items(folder).FirstOrDefault(i => i.Subject == subject);
            Check(row != null, $"item \"{subject}\" in {folder.Name}");
            return store.ReadItem(row.Nid);
        }

        private static void TestContact()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var folder = TopFolder(store, "Contacts");
                var rows = store.Items(folder);
                var row = rows.First(r => r.Kind == ItemKind.Contact);
                Equal("Ed Pfromer", row.Subject, "listed name");
                Equal("Klonzo, LLC", row.Company, "listed company");
                Equal("epfromer@gmail.com", row.Email, "listed email");
                Equal("Pfromer, Ed", row.FileAs, "listed file as");

                var item = store.ReadItem(row.Nid);
                var c = item.Contact;
                Check(c != null, "contact details read");
                Equal("Ed", c.GivenName, "given name");
                Equal("Pfromer", c.Surname, "surname");
                Equal("Pfromer, Ed", c.FileAs, "file as");
                Equal("Klonzo, LLC", c.Company, "company");
                Equal("President", c.JobTitle, "job title");
                Equal("(720) 666-9776", c.Phone(ContactInfo.Business), "business phone");
                Equal("epfromer@gmail.com", c.Emails[0].Address, "email");
                Equal("Ed Pfromer (epfromer@gmail.com)", c.Emails[0].Name, "email display name");
                var work = c.Address("Work");
                Equal("300 Edison Place", work.Street, "street");
                Equal("Superior", work.City, "city");
                Equal("CO", work.State, "state");
                Equal("80027", work.PostalCode, "postal code");
                Equal("United States of America", work.Country, "country");
                Equal("www.tomoab.com", c.Websites.FirstOrDefault(), "web page");
                Check(!c.Birthday.HasValue, "no birthday");
                Check(item.Body.PlainText.Contains("Never gonna let you down"), "notes");
                Check(c.Photo != null && c.Photo.FileName == "ContactPicture.jpg" && c.Photo.Size == 14055, "contact photo");
                Check(c.Photo.Data().Length > 1000 && c.Photo.Data()[0] == 0xFF, "photo is a JPEG");
                Check(item.VisibleAttachments.All(a => !a.IsContactPhoto), "photo isn't listed as an attachment");
                Check(item.VisibleAttachments.Any(a => a.FileName == "rickroll.jpg"), "other attachments are listed");
                Check(c.SearchText.Contains("(720) 666-9776") && c.SearchText.Contains("Klonzo"), "search text");

                // Groups: the "Staff" list holds the two contacts.
                var group = store.ReadItem(rows.First(r => r.Kind == ItemKind.DistributionList).Nid).Group;
                Equal("Staff", group.Name, "group name");
                Equal(2, group.Members.Count, "group members");
                Check(group.Members.Any(m => m.Address == "kpfromer2@gmail.com"), "group member address");
            }
        }

        private static void TestContactGroupOneOffs()
        {
            using (var store = PstStore.Open(Samples.DistList))
            {
                var item = store.ReadItem(2097188);
                Equal(ItemKind.DistributionList, item.Kind, "kind");
                var members = item.Group.Members;
                Equal(3, members.Count, "members");
                Check(members.Any(m => m.Address == "contact1@rjohnson.id.au"), "the member that's a contact");
                Check(members.Any(m => m.Name == "dist name 1" && m.Address == "dist1@rjohnson.id.au"), "first one-off member");
                Check(members.Any(m => m.Name == "dist name 2" && m.Address == "dist2@rjohnson.id.au"), "second one-off member");
            }
        }

        private static void TestCalendar()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var folder = TopFolder(store, "Calendar");
                var rows = store.Items(folder);
                var lunch = rows.First(r => r.Subject == "get lunch");
                Equal(new DateTime(2018, 3, 4, 19, 0, 0, DateTimeKind.Utc), lunch.Start, "listed start");
                Equal(new DateTime(2018, 3, 4, 20, 0, 0, DateTimeKind.Utc), lunch.End, "listed end");
                Check(!lunch.Recurring && !lunch.AllDay, "a single appointment");

                var workout = ItemNamed(store, folder, "workout");
                var a = workout.Appointment;
                Equal(new DateTime(2018, 3, 5, 17, 0, 0, DateTimeKind.Utc), a.Start, "start");
                Equal(new DateTime(2018, 3, 5, 18, 0, 0, DateTimeKind.Utc), a.End, "end");
                Check(a.Recurring && a.Recurrence != null, "repeats");
                Equal("every day from 10:00 AM to 11:00 AM", a.RecurrenceText, "Outlook's words");
                Equal(2, a.BusyStatus, "busy");
                Equal(2, workout.Importance, "importance");
                Equal(RecurrencePattern.Daily, a.Recurrence.Frequency, "daily");
                Equal(600, a.Recurrence.StartMinutes, "starts at 10:00");
                Equal(420, a.TimeZone.Bias, "Mountain time");
                Equal(new DateTime(2018, 3, 5, 17, 0, 0), a.TimeZone.ToUtc(new DateTime(2018, 3, 5, 10, 0, 0)), "10:00 in March is 17:00 UTC");
                Equal(new DateTime(2018, 3, 12, 16, 0, 0), a.TimeZone.ToUtc(new DateTime(2018, 3, 12, 10, 0, 0)), "after the clocks change it's 16:00 UTC");
            }

            using (var store = PstStore.Open(Samples.ExtractorTest))
            {
                var folder = TopFolder(store, "Calendar");
                var monday = ItemNamed(store, folder, "repeats every monday for 10 weeks").Appointment;
                Equal(new DateTime(2021, 8, 9, 17, 0, 0, DateTimeKind.Utc), monday.Start, "weekly start");
                Equal(new DateTime(2021, 8, 9, 18, 0, 0, DateTimeKind.Utc), monday.End, "weekly end");
                Equal("every Monday from 10:00 AM to 11:00 AM", monday.RecurrenceText, "weekly words");
                Equal(RecurrencePattern.PatternWeek, monday.Recurrence.PatternType, "week pattern");
                Equal(10, monday.Recurrence.OccurrenceCount, "10 times");
                Equal("Every Monday, 10 times", monday.RepeatText, "plain words");
                Equal(15, monday.ReminderMinutes, "reminder");

                var days = ItemNamed(store, folder, "repeats for 3 days").Appointment;
                Equal(new DateTime(2021, 8, 10, 19, 0, 0, DateTimeKind.Utc), days.Start, "daily start");
                Equal(30.0, (days.End.Value - days.Start.Value).TotalMinutes, "30 minutes");
                Equal(RecurrencePattern.PatternDay, days.Recurrence.PatternType, "day pattern");
                Equal(3, days.Recurrence.OccurrenceCount, "3 times");
                Equal("Every day, 3 times", days.RepeatText, "daily plain words");

                var allDay = ItemNamed(store, folder, "all day event for 3 days").Appointment;
                Check(allDay.AllDay, "all day");
                var (first, last) = allDay.Days();
                Equal(new DateTime(2021, 8, 15), first, "first day");
                Equal(new DateTime(2021, 8, 17), last, "last day");
                Equal(1080, allDay.ReminderMinutes, "reminder the evening before");

                var yearly = ItemNamed(store, folder, "Occurs every August 13 effective 8/13/2021 from 10:00 AM to 10:30 AM").Appointment;
                Equal("Every year on 13 August", yearly.RepeatText, "yearly");

                var meeting = ItemNamed(store, folder, "meeting with attachments (jpg, pdf, text)");
                var m = meeting.Appointment;
                Check(m.IsMeeting, "a meeting");
                Equal("home office", m.Location, "location");
                Equal("epfromer@gmail.com", m.Attendees.Single().Address.Address, "attendee");
                Equal(3, m.Attendees.Single().Response, "accepted");

                var invited = ItemNamed(store, folder, "meeting from Ed").Appointment;
                Equal("epfromer@gmail.com", invited.Organizer.Address, "organiser");
            }
        }

        // ================================================================== recurrence details and time zones

        /// <summary>A recurrence blob as Outlook writes it: every 2 weeks on Tuesday and Thursday, 6 times, one moved and one deleted.</summary>
        private static byte[] SampleRecurrence()
        {
            uint Minutes(DateTime d) => (uint)(d - new DateTime(1601, 1, 1)).TotalMinutes;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((ushort)0x3004); w.Write((ushort)0x3004);
            w.Write((ushort)RecurrencePattern.Weekly); w.Write((ushort)RecurrencePattern.PatternWeek); w.Write((ushort)0);
            w.Write(0u); w.Write(2u); w.Write(0u);
            w.Write((uint)(0x04 | 0x10)); // Tuesday, Thursday
            w.Write((uint)RecurrencePattern.EndAfterCount); w.Write(6u); w.Write(0u);
            // Deleted: the moved one (3 Sep) and a removed one (12 Sep). Modified: the moved one.
            w.Write(2u); w.Write(Minutes(new DateTime(2024, 9, 3))); w.Write(Minutes(new DateTime(2024, 9, 12)));
            w.Write(1u); w.Write(Minutes(new DateTime(2024, 9, 4)));
            w.Write(Minutes(new DateTime(2024, 9, 3))); w.Write(Minutes(new DateTime(2024, 10, 3)));
            w.Write(0x3006u); w.Write(0x3009u); w.Write(540u); w.Write(570u);
            w.Write((ushort)1);
            w.Write(Minutes(new DateTime(2024, 9, 4, 14, 0, 0))); w.Write(Minutes(new DateTime(2024, 9, 4, 15, 0, 0)));
            w.Write(Minutes(new DateTime(2024, 9, 3, 9, 0, 0)));
            w.Write((ushort)(0x0001 | 0x0010));
            var subject = Encoding.ASCII.GetBytes("Moved");
            w.Write((ushort)(subject.Length + 1)); w.Write((ushort)subject.Length); w.Write(subject);
            var place = Encoding.ASCII.GetBytes("Room 2");
            w.Write((ushort)(place.Length + 1)); w.Write((ushort)place.Length); w.Write(place);
            w.Write(0u); // reserved block 1
            w.Write(0u); // change highlight
            w.Write(0u); // reserved EE1
            w.Write(Minutes(new DateTime(2024, 9, 4, 14, 0, 0))); w.Write(Minutes(new DateTime(2024, 9, 4, 15, 0, 0))); w.Write(Minutes(new DateTime(2024, 9, 3, 9, 0, 0)));
            w.Write((ushort)"Verschoben ✓".Length); w.Write(Encoding.Unicode.GetBytes("Verschoben ✓"));
            w.Write((ushort)"Raum 2".Length); w.Write(Encoding.Unicode.GetBytes("Raum 2"));
            w.Write(0u); // reserved EE2
            w.Write(0u); // reserved block 2
            return ms.ToArray();
        }

        /// <summary>Central European time as Outlook stores it: UTC+1, summer time from the last Sunday of March to the last of October.</summary>
        private static byte[] CentralEurope()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(-60); w.Write(0); w.Write(-60);
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)10); w.Write((ushort)0); w.Write((ushort)5); w.Write((ushort)3); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)3); w.Write((ushort)0); w.Write((ushort)5); w.Write((ushort)2); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
            return ms.ToArray();
        }

        private static void TestRecurrencePattern()
        {
            var r = RecurrencePattern.Parse(SampleRecurrence());
            Equal(0x14, r.Days, "days");
            Equal(2, r.Period, "every 2 weeks");
            Equal("Every 2 weeks on Tuesday and Thursday, 6 times", r.Describe(), "plain words");
            Equal(540, r.StartMinutes, "9:00");
            Equal(1, r.Exceptions.Count, "one moved");
            var e = r.Exceptions[0];
            Equal("Verschoben ✓", e.Subject, "the Unicode title replaces the ANSI one");
            Equal("Raum 2", e.Location, "the Unicode place");
            Equal(new DateTime(2024, 9, 3, 9, 0, 0), e.OriginalStart, "original time");
            Equal(new DateTime(2024, 9, 12), r.RemovedDates.Single(), "only the deleted one is removed");

            var tz = TimeZoneRule.Parse(CentralEurope());
            Equal(new DateTime(2024, 3, 31, 2, 0, 0), TimeZoneRule.ChangeDay(2024, tz.DaylightDate), "summer time starts");
            Equal(new DateTime(2024, 10, 27, 3, 0, 0), TimeZoneRule.ChangeDay(2024, tz.StandardDate), "summer time ends");
            Equal(new DateTime(2024, 9, 3, 7, 0, 0), tz.ToUtc(new DateTime(2024, 9, 3, 9, 0, 0)), "summer: UTC+2");
            Equal(new DateTime(2024, 12, 3, 8, 0, 0), tz.ToUtc(new DateTime(2024, 12, 3, 9, 0, 0)), "winter: UTC+1");
            Equal(new DateTime(2024, 9, 3, 9, 0, 0), tz.ToLocal(new DateTime(2024, 9, 3, 7, 0, 0)), "back to local");
        }

        private static void TestIcsExport()
        {
            var item = new MailItem
            {
                MessageClass = "IPM.Appointment",
                Subject = "Team, weekly; sync",
                Categories = new[] { "Work" },
                LoadBody = () => new MailBody { Text = "Line one\r\nLine two\0" }
            };
            item.Appointment = new AppointmentInfo
            {
                Start = new DateTime(2024, 9, 3, 7, 0, 0, DateTimeKind.Utc),
                End = new DateTime(2024, 9, 3, 7, 30, 0, DateTimeKind.Utc),
                Location = "Room 1",
                Recurring = true,
                Recurrence = RecurrencePattern.Parse(SampleRecurrence()),
                TimeZone = TimeZoneRule.Parse(CentralEurope()),
                TimeZoneName = "(UTC+01:00) Amsterdam, Berlin",
                ReminderMinutes = 10,
                IsMeeting = true,
                Organizer = new MailAddress("Ann Boss", "ann@example.com")
            };
            item.Appointment.Attendees.Add(new Recipient { Address = new MailAddress("Bob", "bob@example.com"), Response = 3 });
            item.Appointment.Attendees.Add(new Recipient { Address = new MailAddress("Room", "room@example.com"), MeetingType = 3 });

            var allDay = new MailItem { MessageClass = "IPM.Appointment", Subject = "Holiday" };
            allDay.Appointment = new AppointmentInfo
            {
                Start = new DateTime(2024, 12, 24, 23, 0, 0, DateTimeKind.Utc),
                End = new DateTime(2024, 12, 26, 23, 0, 0, DateTimeKind.Utc),
                AllDay = true,
                TimeZone = TimeZoneRule.Parse(CentralEurope())
            };

            var ics = new IcsWriter(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            ics.Add(item);
            ics.Add(allDay);
            var text = ics.ToString();
            var unfolded = text.Replace("\r\n ", string.Empty);
            Check(text.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n") && text.EndsWith("END:VCALENDAR\r\n"), "calendar wrapper");
            Check(text.Split(new[] { "\r\n" }, StringSplitOptions.None).All(l => Encoding.UTF8.GetByteCount(l) <= 75), "lines are folded at 75 bytes");
            Check(!text.Contains("\0"), "no control characters");
            Check(unfolded.Contains("TZID:(UTC+01:00) Amsterdam, Berlin\r\n"), "time zone");
            Check(unfolded.Contains("BEGIN:DAYLIGHT\r\nDTSTART:16010325T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200"), "summer time rule");
            Check(unfolded.Contains("BEGIN:STANDARD\r\nDTSTART:16011028T030000\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100"), "winter time rule");
            Check(unfolded.Contains("DTSTART;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20240903T090000"), "local start");
            Check(unfolded.Contains("DTEND;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20240903T093000"), "local end");
            Check(unfolded.Contains("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;WKST=SU;COUNT=6"), "repeat rule");
            Check(unfolded.Contains("EXDATE;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20240912T090000\r\n"), "deleted time, not the moved one");
            Check(unfolded.Contains("RECURRENCE-ID;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20240903T090000"), "moved time");
            Check(unfolded.Contains("DTSTART;TZID=\"(UTC+01:00) Amsterdam, Berlin\":20240904T140000"), "moved to");
            Check(unfolded.Contains("SUMMARY:Verschoben ✓") && unfolded.Contains("LOCATION:Raum 2"), "moved time's title and place");
            Check(unfolded.Contains("SUMMARY:Team\\, weekly\\; sync"), "escaped title");
            Check(unfolded.Contains("DESCRIPTION:Line one\\nLine two\r\n"), "escaped description");
            Check(unfolded.Contains("ORGANIZER;CN=\"Ann Boss\":mailto:ann@example.com"), "organiser");
            Check(unfolded.Contains("ATTENDEE;CN=\"Bob\";CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:bob@example.com"), "attendee");
            Check(unfolded.Contains("CUTYPE=RESOURCE;ROLE=NON-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:room@example.com"), "room");
            Check(unfolded.Contains("BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Team\\, weekly\\; sync\r\nTRIGGER:-PT10M\r\nEND:VALARM"), "reminder");
            Check(unfolded.Contains("CATEGORIES:Work"), "categories");
            Check(unfolded.Contains("SUMMARY:Holiday") && unfolded.Contains("DTSTART;VALUE=DATE:20241225\r\nDTEND;VALUE=DATE:20241227"), "all-day dates");
            Equal(3, Count(text, "BEGIN:VEVENT"), "events: the series, the moved time, the holiday");
            Equal(1, Count(text, "BEGIN:VTIMEZONE"), "the time zone is written once");
        }

        private static int Count(string text, string what)
        {
            var n = 0;
            for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        private static void TestContactExports()
        {
            using (var store = PstStore.Open(Samples.MountainMan))
            {
                var folder = TopFolder(store, "Contacts");
                var items = store.Items(folder).Select(r => new ExportItem { Load = () => store.ReadItem(r.Nid), Subject = r.Subject }).ToList();

                var vcf = Path.Combine(_root, "contacts.vcf");
                var result = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Vcard, Destination = vcf }, null, default);
                Equal(3, result.Exported, "two contacts and a group");
                var text = File.ReadAllText(vcf);
                var unfolded = text.Replace("\r\n ", string.Empty);
                Check(text.Split(new[] { "\r\n" }, StringSplitOptions.None).All(l => Encoding.UTF8.GetByteCount(l) <= 75), "vCard lines folded");
                Check(unfolded.Contains("N:Pfromer;Ed;;;\r\nFN:Ed Pfromer\r\n"), "name");
                Check(unfolded.Contains("ORG:Klonzo\\, LLC\r\n"), "company");
                Check(unfolded.Contains("TEL;TYPE=WORK,VOICE:(720) 666-9776"), "phone");
                Check(unfolded.Contains("ADR;TYPE=WORK:;;300 Edison Place;Superior;CO;80027;United States of America"), "address");
                Check(unfolded.Contains("EMAIL;TYPE=INTERNET,PREF:epfromer@gmail.com"), "email");
                Check(unfolded.Contains("PHOTO;ENCODING=b;TYPE=JPEG:/9j/"), "photo");
                Check(unfolded.Contains("FN:Staff\r\nKIND:group") && unfolded.Contains("MEMBER:mailto:kpfromer2@gmail.com"), "group");
                Check(!text.Contains("\0"), "no control characters in the vCard");
                Check(File.ReadAllBytes(vcf)[0] == (byte)'B', "no byte order mark");

                var csv = Path.Combine(_root, "contacts.csv");
                Exporter.Run(items, new ExportOptions { Format = ExportFormat.ContactsCsv, Destination = csv }, null, default);
                var lines = ReadCsv(File.ReadAllText(csv));
                Equal(4, lines.Count, "header and three rows");
                var header = lines[0].Split(',');
                Check(header.Contains("First Name") && header.Contains("E-mail Address") && header.Contains("Business Postal Code"), "Outlook import columns");
                var ed = lines.First(l => l.StartsWith(",Ed,"));
                var cells = SplitCsv(ed);
                string Cell(string column) => cells[Array.IndexOf(ContactsCsv.Columns, column)];
                Equal("Pfromer", Cell("Last Name"), "last name");
                Equal("Klonzo, LLC", Cell("Company"), "company cell");
                Equal("80027", Cell("Business Postal Code"), "postal code cell");
                Equal("(720) 666-9776", Cell("Business Phone"), "phone cell");
                Equal("epfromer@gmail.com", Cell("E-mail Address"), "email cell");
                var staff = SplitCsv(lines.First(l => l.StartsWith(",Staff,")));
                Check(staff[Array.IndexOf(ContactsCsv.Columns, "Notes")].Contains("kpfromer2@gmail.com"), "group members in the notes");
            }
        }

        /// <summary>The records of a CSV file (a quoted cell may hold line breaks).</summary>
        private static List<string> ReadCsv(string text)
        {
            var records = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') quoted = !quoted;
                if (!quoted && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    records.Add(sb.ToString());
                    sb.Clear();
                    i++;
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) records.Add(sb.ToString());
            return records;
        }

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static void TestCalendarExportFromFile()
        {
            using (var store = PstStore.Open(Samples.ExtractorTest))
            {
                var items = new List<ExportItem>();
                foreach (var f in store.AllFolders(store.Top))
                    foreach (var r in store.Items(f))
                        items.Add(new ExportItem { Load = () => store.ReadItem(r.Nid), Subject = r.Subject });
                var ics = Path.Combine(_root, "calendar.ics");
                var result = Exporter.Run(items, new ExportOptions { Format = ExportFormat.Ics, Destination = ics }, null, default);
                Check(result.Problems.Count == 0, "no problems: " + string.Join("; ", result.Problems));
                var text = File.ReadAllText(ics).Replace("\r\n ", string.Empty);
                Check(text.Contains("SUMMARY:repeats every monday for 10 weeks"), "weekly item");
                Check(text.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO;WKST=SU;COUNT=10"), "weekly rule");
                Check(text.Contains("DTSTART;TZID=\"(UTC-08:00) Pacific Time (US & Canada)\":20210809T100000"), "weekly start in Pacific time");
                Check(text.Contains("RRULE:FREQ=DAILY;COUNT=3"), "daily rule");
                Check(text.Contains("RRULE:FREQ=YEARLY;BYMONTH=8;BYMONTHDAY=13"), "yearly rule");
                Check(text.Contains("DTSTART;VALUE=DATE:20210815\r\nDTEND;VALUE=DATE:20210818"), "all-day event over 3 days");
                Check(text.Contains("SUMMARY:meeting with attachments (jpg\\, pdf\\, text)"), "meeting invitation");
                Check(!text.Contains("SUMMARY:Accepted:"), "replies aren't events");
                Check(!text.Contains("SUMMARY:Get started with OneDrive"), "emails aren't events");
            }
        }

        private static void TestNameMapFromMsg()
        {
            // An .msg file keeps the same name table in its __nameid storage; an email has no contact or calendar parts.
            var item = MsgReader.Read(Samples.MultiTo);
            Check(item.Contact == null && item.Appointment == null && item.Group == null, "an email stays an email");
        }
    }
}
