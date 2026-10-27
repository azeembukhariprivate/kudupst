using System;
using System.Collections.Generic;
using System.Linq;

namespace Kudupst.Core.Mail
{
    public enum RecipientKind { To = 1, Cc = 2, Bcc = 3 }

    public sealed class MailAddress
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;

        public MailAddress()
        {
        }

        public MailAddress(string name, string address)
        {
            Name = name ?? string.Empty;
            Address = address ?? string.Empty;
        }

        public bool IsEmpty => Name.Length == 0 && Address.Length == 0;

        /// <summary>"Name &lt;address&gt;", or whichever of the two there is.</summary>
        public string Display
        {
            get
            {
                if (Address.Length == 0) return Name;
                if (Name.Length == 0 || string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase)) return Address;
                // Outlook often names contacts "Name (address)": no need to say the address twice.
                if (Name.IndexOf(Address, StringComparison.OrdinalIgnoreCase) >= 0) return Name;
                return $"{Name} <{Address}>";
            }
        }

        /// <summary>The name if there is one, else the address.</summary>
        public string Short => Name.Length > 0 ? Name : Address;

        public override string ToString() => Display;
    }

    public sealed class Recipient
    {
        public MailAddress Address { get; set; } = new MailAddress();
        public RecipientKind Kind { get; set; } = RecipientKind.To;

        /// <summary>For meetings: required (1), optional (2) or a room or equipment (3).</summary>
        public int MeetingType { get; set; } = 1;

        /// <summary>For meetings: the attendee's reply (0 none, 1 organiser, 2 tentative, 3 accepted, 4 declined, 5 no reply).</summary>
        public int Response { get; set; }

        public bool IsOrganizer { get; set; }
    }

    public sealed class Attachment
    {
        public string FileName { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string ContentId { get; set; } = string.Empty;
        public string ContentLocation { get; set; } = string.Empty;
        public long Size { get; set; }

        /// <summary>Shown inside the body (a picture in the HTML), not as a separate attachment.</summary>
        public bool Inline { get; set; }

        /// <summary>Hidden by the sender's mail program (signature logos and the like).</summary>
        public bool Hidden { get; set; }

        /// <summary>A contact's photo.</summary>
        public bool IsContactPhoto { get; set; }

        /// <summary>Reads the attachment's contents. Null for an attached email (see <see cref="Embedded"/>).</summary>
        public Func<byte[]> Load { get; set; }

        /// <summary>An attached email, read when asked for.</summary>
        public Func<MailItem> Embedded { get; set; }

        public bool IsEmail => Embedded != null;

        public byte[] Data() => Load?.Invoke() ?? new byte[0];

        public override string ToString() => FileName;
    }

    /// <summary>
    /// One item (usually an email) from any source: a PST/OST file, an .msg file or an .eml file. The body and the
    /// attachments' contents are only read when asked for.
    /// </summary>
    public sealed class MailItem
    {
        private MailBody _body;
        private List<Attachment> _attachments;

        public string MessageClass { get; set; } = "IPM.Note";
        public string Subject { get; set; } = string.Empty;
        public MailAddress From { get; set; } = new MailAddress();

        /// <summary>Who actually sent it, when that's someone else than From (sending on behalf).</summary>
        public MailAddress Sender { get; set; } = new MailAddress();

        public List<Recipient> Recipients { get; set; } = new List<Recipient>();
        public string DisplayTo { get; set; } = string.Empty;
        public string DisplayCc { get; set; } = string.Empty;
        public string DisplayBcc { get; set; } = string.Empty;

        /// <summary>When it was sent (UTC).</summary>
        public DateTime? Sent { get; set; }

        /// <summary>When it arrived (UTC).</summary>
        public DateTime? Received { get; set; }

        public long Size { get; set; }
        public bool Read { get; set; } = true;
        public int Importance { get; set; } = 1;
        public string MessageId { get; set; } = string.Empty;
        public string InReplyTo { get; set; } = string.Empty;
        public string References { get; set; } = string.Empty;

        /// <summary>The internet headers it arrived with, if they were kept.</summary>
        public string Headers { get; set; } = string.Empty;

        /// <summary>A contact's details, when the item is a contact.</summary>
        public ContactInfo Contact { get; set; }

        /// <summary>A contact group's members, when the item is a contact group.</summary>
        public GroupInfo Group { get; set; }

        /// <summary>When, where and who, when the item is an appointment or a meeting.</summary>
        public AppointmentInfo Appointment { get; set; }

        /// <summary>The categories (coloured labels) given in Outlook.</summary>
        public string[] Categories { get; set; } = new string[0];

        /// <summary>A deleted item found inside the file.</summary>
        public bool Deleted { get; set; }

        /// <summary>A deleted item of which only some parts could be found.</summary>
        public bool Partial { get; set; }

        /// <summary>For a deleted item: the folder it was in, when that's known.</summary>
        public string FoundIn { get; set; } = string.Empty;

        /// <summary>Private (2) or confidential (3); 0 when normal.</summary>
        public int Sensitivity { get; set; }

        public Func<MailBody> LoadBody { get; set; }
        public Func<List<Attachment>> LoadAttachments { get; set; }

        public MailBody Body => _body ?? (_body = LoadBody?.Invoke() ?? new MailBody());

        public List<Attachment> Attachments => _attachments ?? (_attachments = LoadAttachments?.Invoke() ?? new List<Attachment>());

        /// <summary>The attachments to list (not the pictures that are part of the body).</summary>
        public IEnumerable<Attachment> VisibleAttachments => Attachments.Where(a => !a.Inline && !a.Hidden && !a.IsContactPhoto);

        public IEnumerable<MailAddress> To => Recipients.Where(r => r.Kind == RecipientKind.To).Select(r => r.Address);
        public IEnumerable<MailAddress> Cc => Recipients.Where(r => r.Kind == RecipientKind.Cc).Select(r => r.Address);
        public IEnumerable<MailAddress> Bcc => Recipients.Where(r => r.Kind == RecipientKind.Bcc).Select(r => r.Address);

        /// <summary>The date to show: when it was sent, or when it arrived.</summary>
        public DateTime? Date => Sent ?? Received;

        public string ToLine => Recipients.Any(r => r.Kind == RecipientKind.To) ? string.Join("; ", To.Select(a => a.Display)) : DisplayTo;
        public string CcLine => Recipients.Any(r => r.Kind == RecipientKind.Cc) ? string.Join("; ", Cc.Select(a => a.Display)) : DisplayCc;
        public string BccLine => Recipients.Any(r => r.Kind == RecipientKind.Bcc) ? string.Join("; ", Bcc.Select(a => a.Display)) : DisplayBcc;

        public ItemKind Kind => ItemKinds.Of(MessageClass);
    }

    /// <summary>The body in the forms it was stored in.</summary>
    public sealed class MailBody
    {
        public string Text { get; set; }
        public string Html { get; set; }
        public string Rtf { get; set; }

        public bool HasHtml => !string.IsNullOrEmpty(Html);

        /// <summary>The body as plain text, made from the HTML or RTF when there's no text version.</summary>
        public string PlainText
        {
            get
            {
                if (!string.IsNullOrEmpty(Text)) return Text;
                if (HasHtml) return HtmlText.ToText(Html);
                if (!string.IsNullOrEmpty(Rtf)) return RtfBody.ToText(Rtf);
                return string.Empty;
            }
        }
    }

    public enum ItemKind { Email, Meeting, Appointment, Contact, Task, Note, Journal, DistributionList, Report, Other }

    public static class ItemKinds
    {
        public static ItemKind Of(string messageClass)
        {
            var c = (messageClass ?? string.Empty).ToUpperInvariant();
            if (c.StartsWith("IPM.SCHEDULE.MEETING")) return ItemKind.Meeting;
            if (c.StartsWith("IPM.APPOINTMENT")) return ItemKind.Appointment;
            if (c.StartsWith("IPM.CONTACT")) return ItemKind.Contact;
            if (c.StartsWith("IPM.DISTLIST")) return ItemKind.DistributionList;
            if (c.StartsWith("IPM.TASK")) return ItemKind.Task;
            if (c.StartsWith("IPM.STICKYNOTE")) return ItemKind.Note;
            if (c.StartsWith("IPM.ACTIVITY")) return ItemKind.Journal;
            if (c.StartsWith("REPORT.")) return ItemKind.Report;
            if (c.Length == 0 || c.StartsWith("IPM.NOTE") || c == "IPM" || c.StartsWith("IPM.POST") || c.StartsWith("IPM.OUTLOOK.RECALL")) return ItemKind.Email;
            return ItemKind.Other;
        }

        public static string Name(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Meeting: return "Meeting request";
                case ItemKind.Appointment: return "Appointment";
                case ItemKind.Contact: return "Contact";
                case ItemKind.DistributionList: return "Contact group";
                case ItemKind.Task: return "Task";
                case ItemKind.Note: return "Note";
                case ItemKind.Journal: return "Journal entry";
                case ItemKind.Report: return "Delivery report";
                case ItemKind.Email: return "Email";
                default: return "Item";
            }
        }
    }
}
