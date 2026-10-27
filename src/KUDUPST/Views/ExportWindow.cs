using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudupst.Controls;
using Kudupst.Core;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;

namespace Kudupst.Views
{
    public enum ExportScope { Selected, Listed, Folder, FolderAndSubfolders, WholeFile }

    /// <summary>
    /// Export: what to save it as (email files, PDF, attachments only, a CSV list), which emails, and where. Then the
    /// progress, and what happened.
    /// </summary>
    public sealed class ExportWindow : Window
    {
        private readonly MainWindow _main;
        private readonly StackPanel _root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        private ExportFormat _format = ExportFormat.Eml;
        private ExportScope _scope;
        private readonly TextBox _destination = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly CheckBox _keepFolders = new CheckBox { Content = "Keep the folder structure (a folder for each Outlook folder)", IsChecked = true };
        private readonly CheckBox _combine = new CheckBox { Content = "Put all emails in one PDF file (with a bookmark for each)" };
        private readonly CheckBox _inline = new CheckBox { Content = "Also save pictures that are part of the email text (logos and the like)" };
        private readonly TextBlock _error = Ui.Text(string.Empty, 12.5, "BadText", wrap: true);
        private CancellationTokenSource _cancel;

        /// <summary>For the self-test: the export result, and a way to run it without the window.</summary>
        public ExportResult Result { get; private set; }

        public static void Show(MainWindow main, ExportScope? scope = null)
        {
            if (main.Source == null) return;
            new ExportWindow(main, scope).ShowDialog();
        }

        public ExportWindow(MainWindow main, ExportScope? scope = null)
        {
            _main = main;
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Export · KUDUPST", main);
            _scope = scope ?? (main.SelectedRows.Count > 1 ? ExportScope.Selected : main.Source.IsDataFile ? ExportScope.Folder : ExportScope.Selected);
            _destination.Text = Settings.LastExportFolder.Length > 0 && Directory.Exists(Settings.LastExportFolder)
                ? Settings.LastExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Content = _root;
            ShowChoices();
            Closing += (s, e) => _cancel?.Cancel();
        }

        // ================================================================== choices

        private void ShowChoices()
        {
            _root.Children.Clear();
            _root.Children.Add(Ui.Heading("Export", 20).Margin(0, 0, 0, 14));

            _root.Children.Add(Ui.Heading("Save as", 14).Margin(0, 0, 0, 8));
            var formats = new WrapPanel();
            foreach (var (format, text, tip) in new[]
                     {
                         (ExportFormat.Eml, "Email files (.eml)", "One file per email, with its attachments inside. Opens in Outlook, Thunderbird, Windows Mail and other mail programs."),
                         (ExportFormat.Pdf, "PDF", "Each email as a PDF, looking as it does on screen, with a list of its attachments."),
                         (ExportFormat.Attachments, "Attachments only", "The attached files, without the emails."),
                         (ExportFormat.Csv, "List (CSV)", "A list of the emails for Excel: date, from, to, subject, size and attachment names.")
                     })
            {
                var f = format;
                var chip = new RadioButton { Content = text, GroupName = "format", IsChecked = _format == f, ToolTip = tip }.Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    _format = f;
                    UpdateOptions();
                };
                formats.Children.Add(chip);
            }
            _root.Children.Add(formats);
            var formatNote = Ui.Subtle(string.Empty, 12).Margin(0, 6, 0, 0);
            _root.Children.Add(formatNote);

            _root.Children.Add(Ui.Heading("Which emails", 14).Margin(0, 16, 0, 6));
            var scopes = new StackPanel();
            var main = _main;
            var selected = main.SelectedRows.Count;
            var listed = main.ListedRows.Count;
            var options = new List<(ExportScope, string)>();
            if (selected > 0) options.Add((ExportScope.Selected, selected == 1 ? "The selected email" : $"The {selected:N0} selected emails"));
            if (main.Source.IsDataFile)
            {
                if (main.IsSearching) options.Add((ExportScope.Listed, $"The search results ({listed:N0})"));
                if (main.CurrentFolder != null)
                {
                    options.Add((ExportScope.Folder, $"The folder \"{main.CurrentFolder.Name}\" ({main.CurrentFolder.ItemCount:N0})"));
                    if (main.CurrentFolder.HasSubfolders) options.Add((ExportScope.FolderAndSubfolders, $"\"{main.CurrentFolder.Name}\" and the folders in it"));
                }
                options.Add((ExportScope.WholeFile, "Everything in the file"));
            }
            else if (options.Count == 0) options.Add((ExportScope.Listed, "This email"));
            if (!options.Any(o => o.Item1 == _scope)) _scope = options[0].Item1;
            foreach (var (scope, text) in options)
            {
                var sc = scope;
                var radio = new RadioButton { Content = text, GroupName = "scope", IsChecked = _scope == sc, Margin = new Thickness(0, 3, 0, 3) };
                radio.Checked += (s, e) => _scope = sc;
                scopes.Children.Add(radio);
            }
            _root.Children.Add(scopes);

            _root.Children.Add(Ui.Heading("Where", 14).Margin(0, 16, 0, 6));
            var where = Ui.Columns(Ui.Star(), Ui.Auto);
            where.Children.Add(_destination.At(0, 0));
            where.Children.Add(Ui.Button("Choose…", "IconFolder", "SecondaryButton", (s, e) => ChooseDestination()).Margin(8, 0, 0, 0).At(0, 1));
            _root.Children.Add(where);

            var optionsPanel = Ui.Column(_keepFolders.Margin(0, 10, 0, 0), _combine.Margin(0, 6, 0, 0), _inline.Margin(0, 6, 0, 0));
            _root.Children.Add(optionsPanel);
            _error.Visibility = Visibility.Collapsed;
            _root.Children.Add(_error.Margin(0, 10, 0, 0));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = Ui.Button("Cancel");
            cancel.IsCancel = true;
            cancel.Click += (s, e) => Close();
            var start = Ui.Button("Export", "IconExport", "PrimaryButton", async (s, e) => await Start());
            start.IsDefault = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(start.Margin(8, 0, 0, 0));
            _root.Children.Add(buttons);

            void Notes()
            {
                switch (_format)
                {
                    case ExportFormat.Eml: formatNote.Text = "One .eml file per email, named by date and subject. Attachments stay inside each email."; break;
                    case ExportFormat.Pdf: formatNote.Text = PdfMaker.Available ? "Pictures from the internet are left out, as in the reading pane." : "Saving as PDF needs Microsoft Edge WebView2, which isn't installed (it's free from Microsoft)."; break;
                    case ExportFormat.Attachments: formatNote.Text = "Files with the same name are numbered: \"report (2).pdf\". Attached emails are saved as .eml files."; break;
                    default: formatNote.Text = "One row per email. Opens in Excel and other spreadsheet programs."; break;
                }
            }
            _updateNotes = Notes;
            UpdateOptions();
        }

        private Action _updateNotes;

        private void UpdateOptions()
        {
            _keepFolders.Visibility = _format == ExportFormat.Csv || _format == ExportFormat.Pdf && _combine.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            _combine.Visibility = _format == ExportFormat.Pdf ? Visibility.Visible : Visibility.Collapsed;
            _inline.Visibility = _format == ExportFormat.Attachments ? Visibility.Visible : Visibility.Collapsed;
            _combine.Click -= CombineClicked;
            _combine.Click += CombineClicked;
            _updateNotes?.Invoke();
        }

        private void CombineClicked(object sender, RoutedEventArgs e) => UpdateOptions();

        private void ChooseDestination()
        {
            var folder = Dialogs.PickFolder(this, "Choose where to save", _destination.Text);
            if (folder != null) _destination.Text = folder;
        }

        // ================================================================== running

        private List<ExportItem> Items()
        {
            var main = _main;
            switch (_scope)
            {
                case ExportScope.Selected: return main.SelectedRows.Select(main.ToExport).ToList();
                case ExportScope.Listed: return main.ListedRows.Select(main.ToExport).ToList();
                case ExportScope.Folder: return main.FolderItems(main.CurrentFolder, false);
                case ExportScope.FolderAndSubfolders: return main.FolderItems(main.CurrentFolder, true);
                default: return main.FolderItems(main.Source.Store.Top, true);
            }
        }

        private string BaseName()
        {
            switch (_scope)
            {
                case ExportScope.Folder:
                case ExportScope.FolderAndSubfolders: return _main.CurrentFolder?.Name ?? "Emails";
                case ExportScope.WholeFile: return Path.GetFileNameWithoutExtension(_main.Source.Path);
                case ExportScope.Listed: return "Search results";
                default: return _main.SelectedRows.Count == 1 ? _main.SelectedRows[0].Subject : "Emails";
            }
        }

        private async Task Start()
        {
            var destination = _destination.Text.Trim();
            if (destination.Length == 0)
            {
                ShowError("Choose where to save.");
                return;
            }
            if (_format == ExportFormat.Pdf && !PdfMaker.Available)
            {
                ShowError("Saving as PDF needs Microsoft Edge WebView2. It's free from Microsoft: " + Web.DownloadUrl);
                return;
            }
            try
            {
                Directory.CreateDirectory(destination);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                ShowError("That folder can't be used: " + ex.Message);
                return;
            }
            Settings.LastExportFolder = destination;
            List<ExportItem> items;
            try { items = Items(); }
            catch (Exception ex) when (Exporter.IsReadProblem(ex))
            {
                ShowError("The emails couldn't be listed: " + ex.Message);
                return;
            }
            if (items.Count == 0)
            {
                ShowError("There are no emails there to export.");
                return;
            }
            var options = new ExportOptions
            {
                Format = _format,
                Destination = destination,
                KeepFolders = _keepFolders.IsChecked == true && (_scope == ExportScope.FolderAndSubfolders || _scope == ExportScope.WholeFile || _scope == ExportScope.Listed),
                CombinePdf = _combine.IsChecked == true,
                IncludeInlinePictures = _inline.IsChecked == true
            };
            if (_format == ExportFormat.Csv) options.Destination = UniquePath(Path.Combine(destination, Exporter.Clean(BaseName()) + ".csv"));
            Result = await Run(items, options);
            ShowDone(Result, options);
        }

        private static string UniquePath(string path) => Exporter.Unique(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        private void ShowError(string text)
        {
            _error.Text = text;
            _error.Visibility = Visibility.Visible;
        }

        /// <summary>Runs an export with a progress bar in this window.</summary>
        public async Task<ExportResult> Run(List<ExportItem> items, ExportOptions options)
        {
            _cancel = new CancellationTokenSource();
            var bar = new ProgressBar { Height = 6, Maximum = items.Count, Margin = new Thickness(0, 14, 0, 6) };
            var line = Ui.Subtle(string.Empty, 12.5, false);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            var cancel = Ui.Button("Stop");
            cancel.HorizontalAlignment = HorizontalAlignment.Right;
            cancel.Click += (s, e) => _cancel.Cancel();
            _root.Children.Clear();
            _root.Children.Add(Ui.Heading("Exporting…", 20));
            _root.Children.Add(Ui.Subtle($"{items.Count:N0} items to {options.Destination}", 12.5).Margin(0, 4, 0, 0));
            _root.Children.Add(bar);
            _root.Children.Add(line);
            _root.Children.Add(cancel.Margin(0, 16, 0, 0));
            var token = _cancel.Token;
            void Progress(int done, string subject)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    bar.Value = done;
                    line.Text = subject == null ? string.Empty : $"{done + 1:N0} of {items.Count:N0}: {subject}";
                }));
            }
            if (options.Format == ExportFormat.Pdf) return await Pdf(items, options, Progress, token);
            return await Task.Run(() => Exporter.Run(items, options, Progress, token));
        }

        /// <summary>PDF needs the Edge engine, which lives on this window's thread: the emails are read in the background, printed here.</summary>
        private static async Task<ExportResult> Pdf(List<ExportItem> items, ExportOptions options, Action<int, string> progress, CancellationToken cancel)
        {
            var result = new ExportResult();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temp = Path.Combine(Path.GetTempPath(), "KUDUPST", "pdf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var parts = new List<(string File, string Title)>();
            using (var maker = new PdfMaker())
            {
                await maker.Start();
                for (var i = 0; i < items.Count; i++)
                {
                    if (cancel.IsCancellationRequested) { result.Cancelled = true; break; }
                    var e = items[i];
                    progress(i, e.Subject);
                    try
                    {
                        var item = await Task.Run(() =>
                        {
                            var m = e.Load();
                            var b = m.Body;
                            var a = m.Attachments;
                            return m;
                        });
                        string path;
                        if (options.CombinePdf)
                        {
                            Directory.CreateDirectory(temp);
                            path = Path.Combine(temp, i.ToString("D6") + ".pdf");
                        }
                        else
                        {
                            var folder = Exporter.FolderFor(options, e);
                            Directory.CreateDirectory(folder);
                            path = Exporter.Unique(Path.Combine(folder, Exporter.FileName(item.Date ?? e.Date, item.Subject, ".pdf")), used);
                        }
                        if (await maker.Save(item, path))
                        {
                            result.Exported++;
                            if (options.CombinePdf) parts.Add((path, Title(item)));
                            else
                            {
                                result.Files++;
                                result.FirstFile = result.FirstFile ?? path;
                            }
                        }
                        else result.Problems.Add($"{e.Subject}: the PDF couldn't be made.");
                    }
                    catch (Exception ex) when (Exporter.IsReadProblem(ex))
                    {
                        result.Problems.Add($"{e.Subject}: {ex.Message}");
                    }
                }
            }
            if (options.CombinePdf && parts.Count > 0 && !result.Cancelled)
            {
                var path = Exporter.Unique(Path.Combine(options.Destination, "Emails.pdf"), used);
                try
                {
                    await Task.Run(() => PdfMaker.Join(parts, path));
                    result.Files = 1;
                    result.FirstFile = path;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    result.Problems.Add("The PDF files couldn't be joined: " + ex.Message);
                }
            }
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            catch (IOException) { }
            progress(items.Count, null);
            return result;
        }

        private static string Title(MailItem item)
        {
            var date = item.Date.HasValue ? item.Date.Value.ToLocalTime().ToString("yyyy-MM-dd") + " " : string.Empty;
            return date + (item.Subject.Length > 0 ? item.Subject : "(no subject)");
        }

        private void ShowDone(ExportResult result, ExportOptions options)
        {
            _root.Children.Clear();
            var title = result.Cancelled ? "Export stopped" : result.Problems.Count == 0 ? "Export finished" : "Export finished, with problems";
            _root.Children.Add(Ui.Heading(title, 20));
            string what;
            switch (options.Format)
            {
                case ExportFormat.Attachments: what = $"{result.Files:N0} attachments saved from {result.Exported:N0} emails."; break;
                case ExportFormat.Csv: what = $"{result.Exported:N0} emails listed in {Path.GetFileName(options.Destination)}."; break;
                case ExportFormat.Pdf when options.CombinePdf: what = $"{result.Exported:N0} emails saved in one PDF."; break;
                default: what = $"{result.Exported:N0} emails saved."; break;
            }
            _root.Children.Add(Ui.Text(what, 13.5, wrap: true).Margin(0, 8, 0, 0));
            if (result.Problems.Count > 0)
            {
                var box = Ui.MonoBox(true);
                box.IsReadOnly = true;
                box.Height = 140;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                box.Text = string.Join("\r\n", result.Problems);
                _root.Children.Add(Ui.Subtle($"{result.Problems.Count:N0} couldn't be saved:", 12.5).Margin(0, 12, 0, 4));
                _root.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            if (result.FirstFile != null)
            {
                var folder = options.Format == ExportFormat.Csv ? Path.GetDirectoryName(options.Destination) : options.Destination;
                buttons.Children.Add(Ui.Button("Show in File Explorer", "IconFolder", "SecondaryButton", (s, e) => AppInfo.OpenFolder(folder, result.FirstFile)));
            }
            var close = Ui.Button("Close", null, "PrimaryButton", (s, e) => Close());
            close.IsDefault = true;
            close.IsCancel = true;
            buttons.Children.Add(close.Margin(8, 0, 0, 0));
            _root.Children.Add(buttons);
        }
    }
}
