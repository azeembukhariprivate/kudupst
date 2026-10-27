using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kudupst.Core;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Views
{
    /// <summary>
    /// KUDUPST.exe --self-test --samples folder: opens real Outlook files (the ones the unit tests download) in the
    /// real window and uses it: folders, emails in the Edge viewer, search with the index, every export (email files,
    /// PDF one by one and joined, attachments, CSV), a password-protected file, an OST, .msg and .eml files, blocked
    /// internet pictures, the File Explorer integration, and every window in both themes. Writes self-test.log next to
    /// the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static string _samples;

        private static string Sample(string name)
        {
            var path = Path.Combine(_samples ?? string.Empty, name);
            if (!File.Exists(path)) throw new Exception($"the sample file {name} is missing (run the unit tests first, then --self-test --samples <their samples-cache folder>)");
            return path;
        }

        public static async Task<int> Run(string samples)
        {
            Running = true;
            _log = new StringBuilder();
            _samples = samples;
            _temp = Path.Combine(Path.GetTempPath(), "kudupst-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            var savedTheme = Settings.Theme;
            var savedPictures = Settings.AlwaysShowPictures;
            Settings.AlwaysShowPictures = false;

            await Step("Load themes and styles", () => { ThemeManager.Init(ThemeMode.Light); return Task.CompletedTask; });
            await Step("Theme keys and row templates", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "SurfaceAltBg", "Text", "SubtleText", "Accent", "BorderBrush", "InputBorder", "SelectionBg", "BannerBg", "BadText",
                                            "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "ChipRadio", "ChipToggle", "MonoBox", "TableList",
                                            "IconFolder", "IconMail", "IconAttach", "IconPicture", "IconPrint", "IconLock", "IconFilter", "IconCalendar", "IconUser",
                                            "IconTask", "IconNote", "IconSave", "IconExport", "IconHistory", "IconSettings", "IconInfo", "IconShield", "IconExternal", "IconTrash" })
                    Application.Current.FindResource(key);
                Expect(Templates.Folder != null && Templates.Mail != null, "row templates load");
                return Task.CompletedTask;
            });
            await Step("Edge engine (WebView2) is there", () =>
            {
                var version = Web.RuntimeVersion;
                Note("WebView2 " + version);
                Expect(version != null, "WebView2 runtime");
                return Task.CompletedTask;
            });

            MainWindow main = null;
            await Step("Start page", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                main.Show();
                Pump();
                return Task.CompletedTask;
            });
            if (main != null)
            {
                await PstRound(main);
                await OstRound(main);
                await PasswordRound(main);
                await SingleFilesRound(main);
                await Step("Dark theme", async () =>
                {
                    main.OpenFile(Sample("enron.pst"));
                    main.ExpandAll();
                    var group = main.FolderRows.Select(r => r.Folder).First(f => f.Name == "TW-Commercial Group");
                    main.SelectFolder(group);
                    await Wait(() => main.CurrentFolder?.Nid == group.Nid && main.Rows.Count > 0, 30, "the folder");
                    ThemeManager.Apply(ThemeMode.Dark);
                    await ShowRow(main, "New OBA's");
                    await Capture(main.View, "Dark theme email");
                    Screenshots.All("Dark theme");
                    ShowAndClose(new SettingsWindow(main));
                    ShowAndClose(About.Build(main));
                    ShowAndClose(new ExportWindow(main));
                    ThemeManager.Apply(ThemeMode.Light);
                });
                await Step("Settings window", () => { ShowAndClose(new SettingsWindow(main)); return Task.CompletedTask; });
                await Step("About window", () => { ShowAndClose(About.Build(main)); return Task.CompletedTask; });
                await Step("Close", () =>
                {
                    main.Close();
                    return Task.CompletedTask;
                });
            }
            await Step("File Explorer integration", () =>
            {
                ShellIntegration.RegisterMenu();
                Expect(ShellIntegration.IsMenuRegistered, "the right-click menu is added");
                Expect((ShellIntegration.MenuCommand() ?? string.Empty).EndsWith("\"%1\""), "menu command");
                ShellIntegration.RemoveMenu();
                Expect(!ShellIntegration.IsMenuRegistered, "the right-click menu is removed");
                ShellIntegration.RegisterOpen();
                Expect(ShellIntegration.IsOpenRegistered, "double-click is set up");
                ShellIntegration.RemoveOpen();
                Expect(!ShellIntegration.IsOpenRegistered, "double-click is removed");
                return Task.CompletedTask;
            });

            Settings.Theme = savedTheme;
            Settings.AlwaysShowPictures = savedPictures;
            PasswordWindow.AutoAnswer = null;
            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== a PST file

        private static async Task PstRound(MainWindow main)
        {
            PstFolder group = null;
            await Step("Open a PST file", async () =>
            {
                Expect(main.OpenFile(Sample("enron.pst")), "opened");
                Expect(main.Source.IsDataFile && main.FolderRows.Count > 0, "folders listed");
                Expect(main.FolderRows.Any(r => r.Folder.Name == "lokay-m"), "lokay-m listed");
                await Wait(() => main.Index != null, 5, "the search index starts");
            });
            await Step("Go into a folder", async () =>
            {
                main.ExpandAll();
                group = main.FolderRows.Select(r => r.Folder).First(f => f.Name == "TW-Commercial Group");
                main.SelectFolder(group);
                await Wait(() => main.CurrentFolder?.Nid == group.Nid && main.Rows.Count > 0, 30, "the folder's emails");
                Expect(main.Rows.Any(r => r.Subject == "New OBA's"), "an email listed");
            });
            await Step("Read an email", async () =>
            {
                await ShowRow(main, "I/B Link Capacity for November and December 2001");
                var text = await main.View.PageText();
                Expect(text.Contains("Burlington Resources agrees to purchase"), "its text is on the page");
                Expect(text.Contains("Reames Julie"), "the sender is on the page");
                await Capture(main.View, "Reading an email");
            });
            await Step("Search", async () =>
            {
                await Wait(() => main.Index.Complete, 180, "the search index");
                Note(MailIndexText(main));
                main.SetSearch("Burlington");
                await Wait(() => main.IsSearching && main.Rows.Count > 0, 30, "search results");
                Expect(main.Rows.Any(r => r.Subject == "I/B Link Capacity for November and December 2001"), "found by a word in the text");
                Expect(main.Rows.All(r => r.FolderName.Length > 0), "results say which folder");
                main.SetSearch("reames", 0, 2);
                await Wait(() => main.Rows.Count > 0 && main.Rows.All(r => r.From.IndexOf("Reames", StringComparison.OrdinalIgnoreCase) >= 0), 30, "search by sender");
                main.ShowFilters(true);
                main.SetSearch("zzzzqqqq");
                await Wait(() => main.IsSearching && main.Rows.Count == 0, 30, "nothing found");
                main.ClearSearch();
                main.ShowFilters(false);
                await Wait(() => !main.IsSearching && main.Rows.Count > 0, 30, "back to the folder");
            });
            await Step("Email in its own window", async () =>
            {
                main.SelectRow(0);
                await ShownNow(main.View);
                var w = new ItemWindow(main.View.Item, main);
                OffScreen(w);
                var shown = Shown(w.View);
                w.Show();
                await shown;
                Pump();
                Screenshots.One(w, w.Title);
                w.Close();
            });
            await Step("Export as email files", async () =>
            {
                var dir = Path.Combine(_temp, "eml");
                var items = main.FolderItems(main.Source.Store.Top, true);
                var result = await Export(main, items, new ExportOptions { Format = ExportFormat.Eml, Destination = dir, KeepFolders = true });
                Expect(result.Exported == items.Count && result.Problems.Count == 0, $"all {items.Count} saved ({result.Exported}, problems: {string.Join("; ", result.Problems)})");
                var files = Directory.GetFiles(dir, "*.eml", SearchOption.AllDirectories);
                Expect(files.Length == items.Count, "one file each");
                Expect(Directory.Exists(Path.Combine(dir, "lokay-m", "MLOKAY (Non-Privileged)", "TW-Commercial Group")), "folders kept");
                var back = EmlReader.Read(files.First(f => f.Contains("I_B Link Capacity")));
                Expect(back.From.Address == "JReames@br-inc.com", "an exported email reads back");
            });
            await Step("Export as PDF", async () =>
            {
                main.SelectFolder(group);
                await Wait(() => main.CurrentFolder?.Nid == group.Nid && main.Rows.Count > 0, 30, "the folder");
                var items = main.FolderItems(group, false);
                var dir = Path.Combine(_temp, "pdf");
                var result = await Export(main, items, new ExportOptions { Format = ExportFormat.Pdf, Destination = dir, KeepFolders = false });
                Expect(result.Exported == items.Count && result.Problems.Count == 0, "all saved: " + string.Join("; ", result.Problems));
                var files = Directory.GetFiles(dir, "*.pdf");
                Expect(files.Length == items.Count, "one PDF each");
                foreach (var f in files) Expect(Encoding.ASCII.GetString(File.ReadAllBytes(f), 0, 5) == "%PDF-", "a PDF: " + Path.GetFileName(f));
                var joined = Path.Combine(_temp, "pdf-one");
                var r2 = await Export(main, items, new ExportOptions { Format = ExportFormat.Pdf, Destination = joined, CombinePdf = true, KeepFolders = false });
                Expect(r2.Files == 1 && File.Exists(r2.FirstFile), "one joined PDF");
                using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(r2.FirstFile, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    Expect(doc.PageCount >= items.Count, $"pages: {doc.PageCount} for {items.Count} emails");
                Note($"{items.Count} emails as PDF");
            });
            await Step("Export attachments and a CSV list", async () =>
            {
                var items = main.FolderItems(main.Source.Store.Top, true);
                var csv = Path.Combine(_temp, "list.csv");
                var r = await Export(main, items, new ExportOptions { Format = ExportFormat.Csv, Destination = csv });
                Expect(File.ReadAllLines(csv).Length == items.Count + 1, "a row per email");
                var att = await Export(main, items, new ExportOptions { Format = ExportFormat.Attachments, Destination = Path.Combine(_temp, "att"), KeepFolders = false });
                Expect(att.Problems.Count == 0, "attachments: " + string.Join("; ", att.Problems));
            });
            await Step("Export window", () =>
            {
                ShowAndClose(new ExportWindow(main));
                return Task.CompletedTask;
            });
        }

        private static string MailIndexText(MainWindow main) => $"indexed {main.Index.Done} items";

        // ================================================================== an OST file

        private static async Task OstRound(MainWindow main)
        {
            await Step("Open an OST file (Outlook 2013 and later)", async () =>
            {
                Expect(main.OpenFile(Sample("mtnman1965@outlook.com.ost")), "opened without asking for a password");
                Expect(main.Source.Store.Top.Name == "IPM_SUBTREE", "the mailbox's folders");
                var inbox = main.FolderRows.First(r => r.Folder.Name == "Inbox").Folder;
                main.SelectFolder(inbox);
                await Wait(() => main.CurrentFolder?.Nid == inbox.Nid && main.Rows.Count == 5, 30, "the inbox");
                await ShowRow(main, "word attachment");
                Expect(main.View.Item.VisibleAttachments.Any(a => a.FileName == "OBA_2760.doc"), "the attachment");
                var text = await main.View.PageText();
                Expect(text.Contains("OBA_2760.doc"), "attachments listed on the page");
                await Capture(main.View, "An email with an attachment");
                await ShowRow(main, "never gonna give you up");
                Expect(!main.View.Item.VisibleAttachments.Any(), "a picture in the text isn't listed as an attachment");
                await Capture(main.View, "A picture in the text");
                var calendar = main.FolderRows.First(r => r.Folder.Name == "Calendar").Folder;
                main.SelectFolder(calendar);
                await Wait(() => main.CurrentFolder?.Nid == calendar.Nid && main.Rows.Count > 0, 30, "the calendar");
                main.SelectRow(0);
                await ShownNow(main.View);
                main.ShowSystemFolders(true);
                Expect(main.FolderRows.Any(r => r.Folder.Name == "Root - Mailbox"), "system folders shown");
                main.ShowSystemFolders(false);
            });
        }

        // ================================================================== a file with a password

        private static async Task PasswordRound(MainWindow main)
        {
            await Step("A file with a password", async () =>
            {
                PasswordWindow.AutoAnswer = () => "not the password";
                Expect(!main.OpenFile(Sample("passworded.pst")), "a wrong password doesn't open it");
                PasswordWindow.AutoAnswer = () => null;
                Expect(!main.OpenFile(Sample("passworded.pst")), "cancelled");
                PasswordWindow.AutoAnswer = () => string.Empty;
                Expect(main.OpenFile(Sample("passworded.pst")), "\"forgot the password\" opens it");
                Expect(main.FolderRows.Count > 0, "its folders");
                PasswordWindow.AutoAnswer = null;
                await Task.Delay(100);
            });
        }

        // ================================================================== single emails

        private static async Task SingleFilesRound(MainWindow main)
        {
            await Step("Open an .msg email", async () =>
            {
                Expect(main.OpenFile(Sample("multi-to.msg")), "opened");
                Expect(!main.Source.IsDataFile && main.Rows.Count == 1, "one email");
                await ShownNow(main.View);
                var text = await main.View.PageText();
                Expect(text.Contains("alice@example.com"), "its recipients");
                await Capture(main.View, "An msg file");
            });
            await Step("Open an .eml email, internet pictures blocked", async () =>
            {
                var path = Path.Combine(_temp, "remote.eml");
                File.WriteAllText(path,
                    "From: Shop <news@shop.example>\r\nTo: you@example.com\r\nSubject: Our sale\r\nDate: Tue, 5 Mar 2024 10:30:00 +0000\r\n" +
                    "MIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Big sale!</p><img src=\"https://shop.example/track.gif\" width=\"1\" height=\"1\">\r\n");
                Expect(main.OpenFile(path), "opened");
                await ShownNow(main.View);
                Expect(main.View.PicturesBlocked, "internet pictures blocked");
                var text = await main.View.PageText();
                Expect(text.Contains("Big sale!"), "the text is shown");
                Screenshots.All("Internet pictures blocked");
                await Capture(main.View, "Internet pictures blocked");
                var shown = Shown(main.View);
                main.View.ShowPictures();
                await shown;
                Expect(!main.View.PicturesBlocked && main.View.AllowRemote, "shown when asked");
            });
        }

        // ================================================================== helpers

        private static Task<ExportResult> Export(MainWindow main, List<ExportItem> items, ExportOptions options)
        {
            var w = new ExportWindow(main);
            OffScreen(w);
            w.Show();
            var task = w.Run(items, options);
            return task.ContinueWith(t =>
            {
                w.Close();
                return t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Selects the email with this subject and waits until the page shows it.</summary>
        private static async Task ShowRow(MainWindow main, string subject)
        {
            var index = main.Rows.ToList().FindIndex(r => r.Subject == subject);
            Expect(index >= 0, "\"" + subject + "\" in the list");
            main.SelectRow(index);
            await Wait(() => main.View.ShownItem != null && main.View.ShownItem.Subject == subject, 30, "\"" + subject + "\" to show");
        }

        /// <summary>Waits until the page on screen shows the email the view has.</summary>
        private static Task ShownNow(MailView view) =>
            Wait(() => view.Item != null && ReferenceEquals(view.ShownItem, view.Item), 30, "the email to show");

        private static Task Shown(MailView view)
        {
            var done = new TaskCompletionSource<bool>();
            void Handler()
            {
                view.Shown -= Handler;
                done.TrySetResult(true);
            }
            view.Shown += Handler;
            return Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30))).ContinueWith(t =>
            {
                if (t.Result != done.Task) throw new Exception("timed out waiting for the email to show");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static async Task Capture(MailView view, string name)
        {
            try
            {
                Directory.CreateDirectory(Screenshots.Folder);
                var path = Path.Combine(Screenshots.Folder, $"web {name}.png");
                using (var fs = File.Create(path)) await view.Capture(fs);
            }
            catch (Exception ex)
            {
                Note("couldn't capture the page: " + ex.Message);
            }
        }

        private static async Task Step(string name, Func<Task> action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                await action();
                Pump();
                Screenshots.All(name);
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static async Task Wait(Func<bool> condition, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for " + what);
                await Task.Delay(50);
            }
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            Screenshots.One(w, w.Title);
            w.Close();
        }
    }
}
