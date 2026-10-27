using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Views
{
    /// <summary>A list that can be refilled in one go (one "reset" instead of a change per row), for big folders.</summary>
    public sealed class RowList<T> : ObservableCollection<T>
    {
        public void Replace(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public abstract class RowBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        public static Geometry Res(string key) => Application.Current?.TryFindResource(key) as Geometry;
    }

    /// <summary>A folder in the folder tree.</summary>
    public sealed class FolderRow : RowBase
    {
        public PstFolder Folder { get; set; }
        public int Depth { get; set; }
        public bool Expanded { get; set; }
        public bool CanExpand { get; set; }

        public string Name => Folder.Name.Length > 0 ? Folder.Name : "(no name)";
        public Thickness Indent => new Thickness(4 + Depth * 16, 0, 0, 0);
        public string Expander => CanExpand ? (Expanded ? "▾" : "▸") : string.Empty;
        public Geometry Icon => Res(IconKey);

        private string IconKey
        {
            get
            {
                var c = Folder.ContainerClass.ToUpperInvariant();
                if (c.StartsWith("IPF.APPOINTMENT")) return "IconCalendar";
                if (c.StartsWith("IPF.CONTACT")) return "IconUser";
                if (c.StartsWith("IPF.TASK")) return "IconTask";
                if (c.StartsWith("IPF.STICKYNOTE") || c.StartsWith("IPF.JOURNAL")) return "IconNote";
                return "IconFolder";
            }
        }

        public string CountText => Folder.UnreadCount > 0 ? Folder.UnreadCount.ToString("N0", CultureInfo.CurrentCulture) : Folder.ItemCount > 0 ? Folder.ItemCount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        public FontWeight CountWeight => Folder.UnreadCount > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        public string Tip => Folder.UnreadCount > 0 ? $"{Folder.ItemCount:N0} items, {Folder.UnreadCount:N0} not read" : $"{Folder.ItemCount:N0} items";
    }

    /// <summary>An item in the mail list.</summary>
    public sealed class MailRow : RowBase
    {
        public uint Nid { get; set; }
        public uint FolderNid { get; set; }
        public string FolderName { get; set; } = string.Empty;
        public string MessageClass { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public long Size { get; set; }
        public bool Read { get; set; } = true;
        public bool HasAttachments { get; set; }

        /// <summary>For a single .msg or .eml file.</summary>
        public MailItem Item { get; set; }

        public static MailRow Of(ItemSummary s) => new MailRow
        {
            Nid = s.Nid, FolderNid = s.FolderNid, MessageClass = s.MessageClass, Subject = s.Subject, From = s.From,
            Date = s.Date, Size = s.Size, Read = s.Read, HasAttachments = s.HasAttachments
        };

        public static MailRow Of(IndexedItem i) => new MailRow
        {
            Nid = i.Nid, FolderNid = i.FolderNid, MessageClass = i.MessageClass, Subject = i.Subject, From = i.From,
            Date = i.Date, Size = i.Size, Read = i.Read, HasAttachments = i.HasAttachments
        };

        public static MailRow Of(MailItem m) => new MailRow
        {
            Item = m, MessageClass = m.MessageClass, Subject = m.Subject, From = m.From.Short, Date = m.Date, Size = m.Size, Read = true,
            HasAttachments = m.VisibleAttachments.GetEnumerator().MoveNext()
        };

        public ItemKind Kind => ItemKinds.Of(MessageClass);
        public string FromText => From.Length > 0 ? From : "(no sender)";
        public string SubjectText => Subject.Length > 0 ? Subject : "(no subject)";
        public FontWeight Weight => Read ? FontWeights.Normal : FontWeights.SemiBold;
        public Visibility UnreadBar => Read ? Visibility.Hidden : Visibility.Visible;
        public Visibility AttachmentVisible => HasAttachments ? Visibility.Visible : Visibility.Collapsed;
        public Geometry Icon => Res(KindIcon);
        public Geometry AttachIcon => Res("IconAttach");

        private string KindIcon
        {
            get
            {
            switch (Kind)
            {
                case ItemKind.Appointment:
                case ItemKind.Meeting: return "IconCalendar";
                case ItemKind.Contact:
                case ItemKind.DistributionList: return "IconUser";
                case ItemKind.Task: return "IconTask";
                case ItemKind.Note:
                case ItemKind.Journal: return "IconNote";
                default: return "IconMail";
            }
            }
        }

        public string DateText
        {
            get
            {
                if (!Date.HasValue) return string.Empty;
                var local = Date.Value.ToLocalTime();
                var now = DateTime.Now;
                if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
                if (local.Year == now.Year) return local.ToString("d MMM", CultureInfo.CurrentCulture);
                return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            }
        }

        public string Tip
        {
            get
            {
                var parts = new List<string> { SubjectText, "From: " + FromText };
                if (Date.HasValue) parts.Add(MailHtml.FormatDate(Date.Value));
                if (Size > 0) parts.Add(MailHtml.Size(Size));
                if (FolderName.Length > 0) parts.Add("In: " + FolderName);
                if (Kind != ItemKind.Email) parts.Add(ItemKinds.Name(Kind));
                return string.Join("\n", parts);
            }
        }

        public string FolderLine => FolderName;
        public Visibility FolderVisible => FolderName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Row templates, written in XAML and loaded at startup, so long lists only build the rows on screen.</summary>
    public static class Templates
    {
        private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        private static string Icon(string binding, string brush, double size = 15) =>
            $"<Viewbox Width=\"{size}\" Height=\"{size}\" VerticalAlignment=\"Center\"><Canvas Width=\"24\" Height=\"24\"><Path Data=\"{{Binding {binding}}}\" Stroke=\"{{DynamicResource {brush}}}\" StrokeThickness=\"1.8\" StrokeLineJoin=\"Round\" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\"/></Canvas></Viewbox>";

        public static DataTemplate Folder => Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"28\" ToolTip=\"{Binding Tip}\" Background=\"Transparent\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>" +
            "<StackPanel Orientation=\"Horizontal\" Margin=\"{Binding Indent}\">" +
            "<TextBlock x:Name=\"Exp\" Text=\"{Binding Expander}\" Width=\"16\" FontSize=\"12\" Foreground=\"{DynamicResource SubtleText}\" VerticalAlignment=\"Center\" TextAlignment=\"Center\" Cursor=\"Hand\" Background=\"Transparent\"/>" +
            Icon("Icon", "SubtleText") +
            "<TextBlock Text=\"{Binding Name}\" Margin=\"8,0,0,0\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/></StackPanel>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding CountText}\" FontWeight=\"{Binding CountWeight}\" FontSize=\"12\" Margin=\"6,0,10,0\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "</Grid></DataTemplate>");

        public static DataTemplate Mail => Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"54\" ToolTip=\"{Binding Tip}\" Background=\"Transparent\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"4\"/><ColumnDefinition Width=\"30\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>" +
            "<Border Background=\"{DynamicResource Accent}\" Visibility=\"{Binding UnreadBar}\" Margin=\"0,6,0,6\" CornerRadius=\"1\"/>" +
            "<Grid Grid.Column=\"1\" VerticalAlignment=\"Top\" Margin=\"0,10,0,0\" HorizontalAlignment=\"Center\">" + Icon("Icon", "SubtleText") + "</Grid>" +
            "<StackPanel Grid.Column=\"2\" VerticalAlignment=\"Center\" Margin=\"0,0,8,0\">" +
            "<TextBlock Text=\"{Binding FromText}\" FontWeight=\"{Binding Weight}\" TextTrimming=\"CharacterEllipsis\"/>" +
            "<TextBlock Text=\"{Binding SubjectText}\" FontSize=\"12.5\" Foreground=\"{DynamicResource SubtleText}\" TextTrimming=\"CharacterEllipsis\" Margin=\"0,1,0,0\"/>" +
            "<TextBlock Text=\"{Binding FolderLine}\" Visibility=\"{Binding FolderVisible}\" FontSize=\"11\" Foreground=\"{DynamicResource SubtleText}\" TextTrimming=\"CharacterEllipsis\" Opacity=\"0.8\"/>" +
            "</StackPanel>" +
            "<StackPanel Grid.Column=\"3\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\">" +
            "<TextBlock Text=\"{Binding DateText}\" FontSize=\"12\" HorizontalAlignment=\"Right\" FontWeight=\"{Binding Weight}\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "<Grid HorizontalAlignment=\"Right\" Margin=\"0,4,0,0\" Visibility=\"{Binding AttachmentVisible}\">" + Icon("AttachIcon", "SubtleText", 14) + "</Grid>" +
            "</StackPanel>" +
            "</Grid></DataTemplate>");

        private static DataTemplate Parse(string xaml) => (DataTemplate)XamlReader.Parse(xaml);
    }
}
