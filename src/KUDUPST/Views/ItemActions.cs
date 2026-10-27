using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Kudupst.Controls;
using Kudupst.Core;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;

namespace Kudupst.Views
{
    /// <summary>What can be done with one email: open its attachments, save it, print it, see its headers.</summary>
    public static class ItemActions
    {
        private static readonly HashSet<string> Risky = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".cpl", ".jar", ".lnk", ".reg", ".inf", ".application", ".appref-ms", ".gadget", ".msc", ".scf", ".url", ".iso", ".img", ".vhd", ".vhdx", ".docm", ".xlsm", ".pptm"
        };

        public static string TempFolder => Path.Combine(Path.GetTempPath(), "KUDUPST");

        /// <summary>Opens an attachment: an attached email in its own window, anything else with its program (after a warning for programs).</summary>
        public static void Open(Window owner, Attachment a)
        {
            try
            {
                if (a.IsEmail)
                {
                    var inner = a.Embedded();
                    if (inner == null) Dialogs.Error(owner, "This attached email couldn't be read.");
                    else new ItemWindow(inner, owner).Show();
                    return;
                }
                var name = Exporter.Clean(a.FileName);
                if (Risky.Contains(Path.GetExtension(name)) &&
                    !Dialogs.Confirm(owner, $"\"{a.FileName}\" can run programs on your computer. Only open it if you trust who sent it.\n\nOpen it anyway?"))
                    return;
                var folder = Path.Combine(TempFolder, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, a.Data());
                File.SetAttributes(path, FileAttributes.ReadOnly);
                AppInfo.OpenFile(path);
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex))
            {
                Dialogs.Error(owner, "Couldn't open the attachment.\n\n" + ex.Message);
            }
        }

        /// <summary>Removes the copies of attachments opened in earlier sessions.</summary>
        public static void CleanTemp()
        {
            try
            {
                if (!Directory.Exists(TempFolder)) return;
                foreach (var dir in Directory.GetDirectories(TempFolder))
                {
                    if (Directory.GetCreationTimeUtc(dir) > DateTime.UtcNow.AddDays(-1)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // In use: next time.
            }
        }

        public static void SaveAttachment(Window owner, Attachment a)
        {
            try
            {
                var name = a.IsEmail ? Exporter.Clean(Path.GetFileNameWithoutExtension(a.FileName)) + ".eml" : Exporter.Clean(a.FileName);
                var path = Dialogs.SaveFile(owner, "Save the attachment", name, "All files|*.*");
                if (path == null) return;
                if (a.IsEmail)
                {
                    var inner = a.Embedded();
                    if (inner != null) File.WriteAllBytes(path, EmlWriter.Write(inner));
                }
                else File.WriteAllBytes(path, a.Data());
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex))
            {
                Dialogs.Error(owner, "Couldn't save the attachment.\n\n" + ex.Message);
            }
        }

        public static void SaveAllAttachments(Window owner, MailItem item)
        {
            var folder = Dialogs.PickFolder(owner, "Choose a folder for the attachments", Settings.LastExportFolder);
            if (folder == null) return;
            Settings.LastExportFolder = folder;
            var result = Exporter.Attachments(new List<ExportItem> { new ExportItem { Load = () => item, Subject = item.Subject } },
                new ExportOptions { Format = ExportFormat.Attachments, Destination = folder, KeepFolders = false }, null, default);
            if (result.Problems.Count > 0) Dialogs.Error(owner, string.Join("\n", result.Problems));
            else AppInfo.OpenFolder(folder, result.FirstFile);
        }

        public static void SaveEml(Window owner, MailItem item)
        {
            var path = Dialogs.SaveFile(owner, "Save as an email file", Exporter.FileName(item.Date, item.Subject, ".eml"), "Email file|*.eml");
            if (path == null) return;
            try { File.WriteAllBytes(path, EmlWriter.Write(item)); }
            catch (Exception ex) when (Exporter.IsReadProblem(ex)) { Dialogs.Error(owner, "Couldn't save the email.\n\n" + ex.Message); }
        }

        public static async Task SavePdf(Window owner, MailItem item)
        {
            if (!PdfMaker.Available)
            {
                Dialogs.Error(owner, "Saving as PDF needs Microsoft Edge WebView2, which isn't installed. It's free from Microsoft.");
                return;
            }
            var path = Dialogs.SaveFile(owner, "Save as PDF", Exporter.FileName(item.Date, item.Subject, ".pdf"), "PDF file|*.pdf");
            if (path == null) return;
            using (var maker = new PdfMaker())
            {
                await maker.Start();
                if (!await maker.Save(item, path)) Dialogs.Error(owner, "Couldn't save the PDF.");
            }
        }

        /// <summary>The internet headers and other details of an email.</summary>
        public static void ShowDetails(Window owner, MailItem item)
        {
            var box = Ui.MonoBox(false);
            box.IsReadOnly = true;
            box.Height = 380;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var details = new List<string>
            {
                "Subject: " + item.Subject,
                "From: " + item.From.Display
            };
            if (!item.Sender.IsEmpty && item.Sender.Display != item.From.Display) details.Add("Sent by: " + item.Sender.Display);
            if (item.Sent.HasValue) details.Add("Sent: " + MailHtml.FormatDate(item.Sent.Value));
            if (item.Received.HasValue) details.Add("Received: " + MailHtml.FormatDate(item.Received.Value));
            if (item.MessageId.Length > 0) details.Add("Message ID: " + item.MessageId);
            details.Add("Type: " + item.MessageClass);
            if (item.Size > 0) details.Add("Size: " + MailHtml.Size(item.Size));
            box.Text = string.Join("\r\n", details) + "\r\n\r\n" + (item.Headers.Length > 0 ? "Internet headers:\r\n\r\n" + item.Headers : "This item has no internet headers (it may have been written in Outlook, or come from Exchange).");
            var content = Ui.Column(Ui.Heading("Details", 18), Ui.Subtle("Where the email came from and how it travelled, as its mail servers recorded it.", 12.5).Margin(0, 4, 0, 10), box);
            var copy = Ui.Button("Copy", "IconCopy");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            var window = Dialogs.Dialog(owner, "Details · KUDUPST", content, 720, copy, close);
            copy.Click += (s, e) => Ui.Copy(box.Text);
            close.Click += (s, e) => window.Close();
            window.ShowDialog();
        }

        /// <summary>A menu of the attachments: open, save, save all.</summary>
        public static void AttachmentsMenu(FrameworkElement at, Window owner, MailItem item)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            var list = item.VisibleAttachments.ToList();
            foreach (var a in list)
            {
                var attachment = a;
                var entry = new MenuItem { Header = a.FileName + (a.Size > 0 && !a.IsEmail ? "  (" + MailHtml.Size(a.Size) + ")" : string.Empty) };
                var open = new MenuItem { Header = "Open" };
                open.Click += (s, e) => Open(owner, attachment);
                var save = new MenuItem { Header = "Save as…" };
                save.Click += (s, e) => SaveAttachment(owner, attachment);
                entry.Items.Add(open);
                entry.Items.Add(save);
                menu.Items.Add(entry);
            }
            if (list.Count == 0) menu.Items.Add(new MenuItem { Header = "No attachments", IsEnabled = false });
            else
            {
                menu.Items.Add(new Separator());
                var all = new MenuItem { Header = "Save all attachments…" };
                all.Click += (s, e) => SaveAllAttachments(owner, item);
                menu.Items.Add(all);
            }
            menu.IsOpen = true;
        }

        /// <summary>The toolbar above an email: save, attachments, print, details.</summary>
        public static WrapPanel Toolbar(Func<MailItem> current, Func<Window> owner, MailView view, Action openInWindow = null)
        {
            var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
            Button Tool(string text, string icon, string tip, Action<Button> click)
            {
                Button b = null;
                b = Ui.Small(Ui.Button(text, icon, "GhostButton", (s, e) => { if (current() != null) click(b); }));
                b.ToolTip = tip;
                b.Margin = new Thickness(0, 0, 2, 0);
                bar.Children.Add(b);
                return b;
            }
            if (openInWindow != null) Tool("Open", "IconExternal", "Open this email in its own window", b => openInWindow());
            Tool("Save as", "IconSave", "Save this email as an email file (.eml) or a PDF", b =>
            {
                var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
                var eml = new MenuItem { Header = "Email file (.eml)…" };
                eml.Click += (s, e) => SaveEml(owner(), current());
                var pdf = new MenuItem { Header = "PDF…" };
                pdf.Click += async (s, e) => await SavePdf(owner(), current());
                menu.Items.Add(eml);
                menu.Items.Add(pdf);
                menu.IsOpen = true;
            });
            Tool("Attachments", "IconAttach", "Open or save this email's attachments", b => AttachmentsMenu(b, owner(), current()));
            Tool("Print", "IconPrint", "Print this email", b => view.Print());
            Tool("Details", "IconInfo", "The internet headers and other details of this email", b => ShowDetails(owner(), current()));
            return bar;
        }
    }

    /// <summary>An email in a window of its own (opened from the list, or an attached email).</summary>
    public sealed class ItemWindow : Window
    {
        private readonly MailView _view = new MailView();

        public ItemWindow(MailItem item, Window owner = null)
        {
            Width = 920;
            Height = 820;
            MinWidth = 520;
            MinHeight = 400;
            Dialogs.Prepare(this, (item.Subject.Length > 0 ? item.Subject : "(no subject)") + " - KUDUPST");
            if (owner != null && owner.IsVisible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = owner.Left + 60;
                Top = owner.Top + 40;
            }
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            var toolbar = ItemActions.Toolbar(() => item, () => this, _view);
            root.Children.Add(new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            root.Children.Add(_view.At(1));
            Content = root;
            _view.AttachmentClicked += i =>
            {
                if (i >= 0 && i < item.Attachments.Count) ItemActions.Open(this, item.Attachments[i]);
            };
            Loaded += async (s, e) => await _view.Show(item);
        }

        public MailView View => _view;
    }
}
