using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kudupst.Controls;
using Kudupst.Core;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Views
{
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window owner)
        {
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUPST", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("Privacy", 14).Margin(0, 16, 0, 4));
            var pictures = new CheckBox { Content = "Always show pictures from the internet in emails", IsChecked = Settings.AlwaysShowPictures };
            pictures.Click += (s, e) => Settings.AlwaysShowPictures = pictures.IsChecked == true;
            root.Children.Add(pictures);
            root.Children.Add(Ui.Subtle("Off: they're blocked until you click \"Show pictures\", so senders can't tell you opened their email.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("File Explorer", 14).Margin(0, 16, 0, 4));
            var menu = new CheckBox { Content = "Add \"Open with KUDUPST\" to the right-click menu of .pst, .ost, .msg and .eml files", IsChecked = Settings.ShellMenu };
            menu.Click += (s, e) =>
            {
                Settings.ShellMenu = menu.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(menu);
            var open = new CheckBox { Content = "Open these files in KUDUPST when they're double-clicked", IsChecked = Settings.OpenByDefault };
            open.Click += (s, e) =>
            {
                Settings.OpenByDefault = open.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(open.Margin(0, 6, 0, 0));
            root.Children.Add(Ui.Subtle("For your Windows account only. If you chose another program in Windows' \"Open with\", Windows keeps that choice. On Windows 11 the menu is under \"Show more options\".", 12)
                .Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("Search", 14).Margin(0, 16, 0, 4));
            var size = Ui.Subtle(string.Empty, 12.5, false);
            void UpdateSize() => size.Text = "Search indexes use " + MailHtml(MailIndex.SizeOnDisk()) + " in your AppData folder. They're never stored next to your files.";
            UpdateSize();
            root.Children.Add(size);
            var clear = Ui.Small(Ui.Button("Delete all search indexes", "IconTrash", "SecondaryButton"));
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Margin = new Thickness(0, 8, 0, 0);
            clear.Click += (s, e) =>
            {
                if (!Dialogs.Confirm(this, "Delete the search indexes? They're made again when you next open a file.")) return;
                try
                {
                    foreach (Window w in Application.Current.Windows)
                        if (w is MainWindow m) m.CloseFile();
                    if (Directory.Exists(MailIndex.IndexRoot)) Directory.Delete(MailIndex.IndexRoot, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Dialogs.Error(this, "Some indexes are in use and weren't deleted.\n\n" + ex.Message);
                }
                UpdateSize();
                foreach (Window w in Application.Current.Windows)
                    if (w is MainWindow m && m.Source == null) m.ShowStart();
            };
            root.Children.Add(clear);

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUPST version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUPST", (s, e) => About.Show(this)).At(0, 0));
            var closeButton = Ui.Button("Close", null, "PrimaryButton");
            closeButton.IsCancel = true;
            closeButton.Click += (s, e) => Close();
            bottom.Children.Add(closeButton.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }

        private static string MailHtml(long bytes) => Core.Mail.MailHtml.Size(bytes);
    }

    /// <summary>Asks for the password of a PST file that has one; or, for the user's own file, opens it anyway.</summary>
    public static class PasswordWindow
    {
        /// <summary>For the self-test: the answer to give instead of showing the window ("" = forgot it, null = cancel).</summary>
        public static Func<string> AutoAnswer { get; set; }

        public static bool Ask(Window owner, PstStore store, string fileName)
        {
            if (AutoAnswer != null)
            {
                var answer = AutoAnswer();
                return answer != null && (answer.Length == 0 || store.CheckPassword(answer));
            }
            var box = new PasswordBox { MinHeight = 34, Margin = new Thickness(0, 8, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, "Password");
            var error = Ui.Text("That's not the password.", 12.5, "BadText");
            error.Visibility = Visibility.Collapsed;
            var opened = false;
            Window window = null;
            var forgot = Ui.Link("Forgot the password?", (s, e) =>
            {
                var sure = Dialogs.Confirm(window,
                    "Outlook's password only stops Outlook from opening the file; the emails in it aren't locked with it, so KUDUPST can read them anyway.\n\n" +
                    "Only do this with your own files, or files you're allowed to read.\n\nOpen it without the password?");
                if (!sure) return;
                opened = true;
                window.DialogResult = true;
            });
            var content = Ui.Column(
                Ui.Row(Ui.Icon("IconLock", 20), Ui.Heading("This file has a password", 17).Margin(10, 0, 0, 0)),
                Ui.Subtle($"\"{fileName}\" was given a password in Outlook. Type it to open the file.", 12.5).Margin(0, 8, 0, 0),
                box, error.Margin(0, 6, 0, 0), forgot.Margin(0, 12, 0, 0));
            var cancel = Ui.Button("Cancel");
            var ok = Ui.Button("Open", null, "PrimaryButton");
            cancel.IsCancel = true;
            ok.IsDefault = true;
            window = Dialogs.Dialog(owner, "Password · KUDUPST", content, 460, cancel, ok);
            ok.Click += (s, e) =>
            {
                if (store.CheckPassword(box.Password))
                {
                    opened = true;
                    window.DialogResult = true;
                }
                else
                {
                    error.Visibility = Visibility.Visible;
                    box.SelectAll();
                    box.Focus();
                }
            };
            window.Loaded += (s, e) => box.Focus();
            window.ShowDialog();
            return opened;
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUPST", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Read, search and export Outlook files", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(120), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Reading files", Ui.Text("Its own reader for Outlook PST and OST files, .msg and .eml emails", 12.5, wrap: true));
            var web = Web.RuntimeVersion;
            Row("Showing emails", Ui.Text(web != null ? "Microsoft Edge WebView2 " + web : "Text only (Microsoft Edge WebView2 isn't installed)", 12.5, wrap: true));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUPST only reads your files; it never changes them. Search indexes are kept in your AppData folder. Nothing is sent anywhere.", 11.5)
                    .Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUPST", content, 580, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUPST {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }
}
