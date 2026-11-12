using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Kudupst.Controls;
using Kudupst.Core;
using Kudupst.Core.Export;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Views
{
    /// <summary>
    /// The main window: a start page to open a file, then the open files with their folders (several at once, like
    /// Outlook), the list of emails with search across all of them, and the email itself.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly TextBlock _fileName = Ui.Text(string.Empty, 13, "SubtleText");
        private readonly Button _export;
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12.5, false);
        private readonly TextBlock _indexStatus = Ui.Subtle(string.Empty, 12.5, false);
        private readonly Border _statusBar;

        // The file page.
        private readonly RowList<FolderRow> _folderRows = new RowList<FolderRow>();
        private readonly RowList<MailRow> _mailRows = new RowList<MailRow>();
        private readonly ListBox _folders;
        private readonly ListBox _list;
        private readonly MailView _view = new MailView();
        private readonly TextBox _search = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _searchHint = Ui.Subtle("Search emails", 13, false);
        private readonly ComboBox _scope = new ComboBox { Width = 120, Margin = new Thickness(6, 0, 0, 0) };
        private readonly ComboBox _field = new ComboBox { Width = 110, Margin = new Thickness(6, 0, 0, 0) };
        private readonly ComboBox _sort = new ComboBox { Width = 130 };
        private readonly ToggleButton _filterToggle = new ToggleButton { Margin = new Thickness(6, 0, 0, 0) };
        private readonly Grid _filters = new Grid { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly DatePicker _fromDate = new DatePicker { Width = 130 };
        private readonly DatePicker _toDate = new DatePicker { Width = 130 };
        private readonly CheckBox _withAttachments = new CheckBox { Content = "With attachments", VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _listTitle = Ui.Text(string.Empty, 14, bold: true);
        private readonly CheckBox _systemFolders = new CheckBox { Content = "Show system folders", FontSize = 12 };
        private readonly Button _lookDeeper;
        private readonly TextBlock _deletedNote = Ui.Subtle(string.Empty, 12);
        private const string LookDeeperText = "Look deeper for deleted items";
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly ColumnDefinition _treeColumn = new ColumnDefinition { Width = new GridLength(250), MinWidth = 150 };
        private readonly ColumnDefinition _treeSplitter = new ColumnDefinition { Width = GridLength.Auto };
        private readonly Grid _fileGrid = new Grid();
        private readonly Grid _searchArea;

        private readonly List<OpenedFile> _files = new List<OpenedFile>();
        private readonly FrameworkElement _closeLinks;
        private readonly Button _closeAll;

        /// <summary>The file whose folder is shown (null while the single emails are shown).</summary>
        private OpenedFile _file;
        private bool _singlesShown;
        private bool _remember = true;

        /// <summary>The file searched when looking in "This file".</summary>
        private OpenedFile _searchedFile;

        /// <summary>The file shown: the folder's, or the single email selected.</summary>
        private MailSource _source => _file?.Source ?? (_singlesShown ? ((_list.SelectedItem as MailRow)?.File ?? _files.FirstOrDefault(f => !f.IsDataFile))?.Source : null);
        private PstFolder _currentFolder;
        private List<MailRow> _folderItems = new List<MailRow>();
        private bool _searching;
        private int _loadGeneration;

        /// <summary>What the list shows: emails, contacts (sorted by name) or calendar items (by date, under month headings).</summary>
        public enum ListKind { Mail, Contacts, Calendar }

        public ListKind Kind { get; private set; } = ListKind.Mail;
        private bool _settingKind;

        public MainWindow()
        {
            Width = 1360;
            Height = 860;
            MinWidth = 900;
            MinHeight = 560;
            Dialogs.Prepare(this, AppInfo.Name);
            AllowDrop = true;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header: the name, the open file, a new version, and the main actions.
            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            var brand = Ui.Row(LogoTile.Create(26), Ui.Text(AppInfo.Name, 15, bold: true).Margin(10, 0, 0, 0), _fileName.Margin(14, 0, 0, 0));
            _fileName.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(brand.At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 2));
            header.Children.Add(Ui.Button("Open…", "IconFolder", "GhostButton", (s, e) => PickAndOpen()).Tip("Open a .pst, .ost, .msg or .eml file, as well as the ones open now (Ctrl+O)").At(0, 3));
            Button recent = null;
            recent = Ui.Button("Recent", "IconHistory", "GhostButton", (s, e) => RecentMenu(recent));
            header.Children.Add(recent.At(0, 4));
            _export = Ui.Button("Export", "IconExport", "GhostButton", (s, e) => ExportWindow.Show(this));
            _export.ToolTip = "Save emails as email files, PDF, attachments only, or a list for Excel";
            _export.IsEnabled = false;
            header.Children.Add(_export.At(0, 5));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).At(0, 6));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 7));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            root.Children.Add(_page.At(1));

            var status = Ui.Columns(Ui.Star(), Ui.Auto);
            status.Children.Add(_status.At(0, 0));
            status.Children.Add(_indexStatus.At(0, 1));
            _statusBar = new Border { Child = status, Padding = new Thickness(16, 7, 16, 7), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush");
            root.Children.Add(_statusBar.At(2));
            Content = root;

            // ------------------------------------------------------------------ the file page
            _folders = new ListBox { ItemsSource = _folderRows, ItemTemplate = Templates.Folder, SelectionMode = SelectionMode.Single }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(_folders, true);
            _folders.ItemContainerStyle = NoLines();
            _folders.SelectionChanged += (s, e) =>
            {
                if (!_selectingQuietly && _folders.SelectedItem is FolderRow row) ShowRow(row);
            };
            _folders.ContextMenuOpening += (s, e) => _folders.ContextMenu = FolderMenu();
            _folders.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // A click on ▸ / ▾ opens or closes the folder.
                if (e.OriginalSource is TextBlock t && t.Name == "Exp" && t.DataContext is FolderRow row && row.CanExpand)
                {
                    Toggle(row);
                    e.Handled = true;
                }
            };
            _folders.KeyDown += (s, e) =>
            {
                if (!(_folders.SelectedItem is FolderRow row) || !row.CanExpand) return;
                if ((e.Key == Key.Right && !row.Expanded) || (e.Key == Key.Left && row.Expanded))
                {
                    Toggle(row);
                    e.Handled = true;
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(_folders, "Folders");
            _systemFolders.Click += (s, e) => FillFolders();
            var treePanel = new Grid();
            treePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            treePanel.RowDefinitions.Add(new RowDefinition());
            treePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            treePanel.Children.Add(Ui.Text("Folders", 12.5, "SubtleText", bold: true).Margin(14, 12, 0, 6).At(0));
            treePanel.Children.Add(_folders.At(1));
            _lookDeeper = Ui.Link(LookDeeperText, async (s, e) => await LookDeeper());
            _lookDeeper.ToolTip = "Reads the whole file for what's left of deleted items. Takes longer on big files; some may be only partly there.";
            _lookDeeper.HorizontalAlignment = HorizontalAlignment.Left;
            _deletedNote.Visibility = Visibility.Collapsed;
            var closeThis = Ui.Link("Close this file", (s, e) => CloseShown());
            closeThis.ToolTip = "Close the file shown (the others stay open)";
            _closeAll = Ui.Link("Close all", (s, e) => CloseAll());
            _closeAll.ToolTip = "Close every open file";
            _closeLinks = Ui.Row(closeThis, _closeAll.Margin(14, 0, 0, 0));
            treePanel.Children.Add(Ui.Column(_systemFolders, _lookDeeper.Margin(0, 6, 0, 0), _deletedNote.Margin(0, 4, 8, 0), _closeLinks.Margin(0, 8, 0, 0)).Margin(14, 6, 0, 10).At(2));
            treePanel.Res(BackgroundProperty, "SurfaceAltBg");

            // The search box with its options.
            _search.ToolTip = "Words to look for in the emails: subject, sender, recipients, text and attachment names";
            System.Windows.Automation.AutomationProperties.SetName(_search, "Search");
            _search.TextChanged += (s, e) =>
            {
                _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                RestartSearch();
            };
            _search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) RunSearch();
                else if (e.Key == Key.Escape) ClearSearch();
            };
            _searchHint.IsHitTestVisible = false;
            _searchHint.Margin = new Thickness(10, 0, 0, 0);
            var searchBox = new Grid();
            searchBox.Children.Add(_search);
            searchBox.Children.Add(_searchHint);
            foreach (var s in new[] { "All open files", "This file", "This folder" }) _scope.Items.Add(s);
            _scope.SelectedIndex = 0;
            _scope.ToolTip = "Where to look";
            _scope.SelectionChanged += (s, e) => RestartSearch();
            foreach (var s in new[] { "Everything", "Subject", "From", "To" }) _field.Items.Add(s);
            _field.SelectedIndex = 0;
            _field.ToolTip = "What to look in";
            _field.SelectionChanged += (s, e) => RestartSearch();
            _filterToggle.Content = Ui.Label("IconFilter", null);
            _filterToggle.Styled("ChipToggle");
            _filterToggle.ToolTip = "Dates and attachments";
            System.Windows.Automation.AutomationProperties.SetName(_filterToggle, "Filters");
            _filterToggle.Click += (s, e) => _filters.Visibility = _filterToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            var searchRow = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            searchRow.Children.Add(searchBox.At(0, 0));
            searchRow.Children.Add(_field.At(0, 1));
            searchRow.Children.Add(_scope.At(0, 2));
            searchRow.Children.Add(_filterToggle.At(0, 3));
            _fromDate.SelectedDateChanged += (s, e) => RestartSearch();
            _toDate.SelectedDateChanged += (s, e) => RestartSearch();
            _withAttachments.Click += (s, e) => RestartSearch();
            var filterRow = new WrapPanel();
            filterRow.Children.Add(Ui.Subtle("From", 12.5, false).Margin(0, 0, 6, 0));
            filterRow.Children.Add(_fromDate);
            filterRow.Children.Add(Ui.Subtle("to", 12.5, false).Margin(8, 0, 6, 0));
            filterRow.Children.Add(_toDate);
            filterRow.Children.Add(_withAttachments.Margin(14, 0, 0, 0));
            filterRow.Children.Add(Ui.Link("Clear", (s, e) => ClearSearch()).Margin(14, 0, 0, 0));
            foreach (FrameworkElement c in filterRow.Children) c.VerticalAlignment = VerticalAlignment.Center;
            _filters.Children.Add(filterRow);
            _searchArea = WrapInGrid(Ui.Column(searchRow, _filters));

            foreach (var s in MailSorts) _sort.Items.Add(s);
            _sort.SelectedIndex = 0;
            _sort.ToolTip = "Sort the list";
            _sort.SelectionChanged += (s, e) =>
            {
                if (!_settingKind) Resort();
            };
            var titleRow = Ui.Columns(Ui.Star(), Ui.Auto);
            _listTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            titleRow.Children.Add(_listTitle.At(0, 0));
            titleRow.Children.Add(_sort.At(0, 1));
            titleRow.Margin = new Thickness(0, 10, 0, 6);

            _list = new ListBox { ItemsSource = _mailRows, ItemTemplate = Templates.Mail, SelectionMode = SelectionMode.Extended }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(_list, true);
            _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = Templates.MonthHeading });
            System.Windows.Automation.AutomationProperties.SetName(_list, "Emails");
            _list.SelectionChanged += async (s, e) => await ShowSelected();
            _list.MouseDoubleClick += (s, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && fe.DataContext is MailRow) OpenInWindow();
            };
            _list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OpenInWindow();
            };
            _list.ContextMenuOpening += (s, e) => _list.ContextMenu = ListMenu();

            var listPanel = new Grid { Margin = new Thickness(12, 10, 0, 0) };
            listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listPanel.RowDefinitions.Add(new RowDefinition());
            listPanel.Children.Add(_searchArea.Margin(0, 0, 12, 0).At(0));
            listPanel.Children.Add(titleRow.Margin(0, 10, 12, 6).At(1));
            listPanel.Children.Add(_list.At(2));

            var readPanel = new Grid();
            readPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            readPanel.RowDefinitions.Add(new RowDefinition());
            var toolbar = ItemActions.Toolbar(() => _view.Item, () => this, _view, OpenInWindow);
            readPanel.Children.Add(new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            readPanel.Children.Add(_view.At(1));
            _view.AttachmentClicked += i =>
            {
                var item = _view.Item;
                if (item != null && i >= 0 && i < item.Attachments.Count) ItemActions.Open(this, item.Attachments[i]);
            };

            _fileGrid.ColumnDefinitions.Add(_treeColumn);
            _fileGrid.ColumnDefinitions.Add(_treeSplitter);
            _fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420), MinWidth = 300 });
            _fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            _fileGrid.Children.Add(treePanel.At(0, 0));
            _fileGrid.Children.Add(Splitter().At(0, 1));
            _fileGrid.Children.Add(listPanel.At(0, 2));
            _fileGrid.Children.Add(Splitter().At(0, 3));
            _fileGrid.Children.Add(new Border { Child = readPanel, BorderThickness = new Thickness(1, 0, 0, 0) }.Res(Border.BorderBrushProperty, "BorderBrush").At(0, 4));

            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                RunSearch();
            };
            _indexTimer.Tick += (s, e) => UpdateIndexStatus();

            Drop += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
                // Every file dropped is added; the last one is shown.
                for (var i = 0; i < files.Length; i++) OpenFile(files[i], i == files.Length - 1);
            };
            DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { PickAndOpen(); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && _files.Count > 0) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E && _source != null) { ExportWindow.Show(this); e.Handled = true; }
            };
            Loaded += async (s, e) => await CheckForUpdates();
            Closed += (s, e) => DisposeAll();
            ShowStart();
        }

        private static Grid WrapInGrid(UIElement element)
        {
            var g = new Grid();
            g.Children.Add(element);
            return g;
        }

        private static GridSplitter Splitter()
        {
            var s = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
            s.Res(BackgroundProperty, "SurfaceBg");
            return s;
        }

        /// <summary>Folder rows without the line under each row.</summary>
        private static Style NoLines()
        {
            var style = new Style(typeof(ListBoxItem), Application.Current.TryFindResource(typeof(ListBoxItem)) as Style);
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            return style;
        }

        public MailSource Source => _source;
        public MailIndex Index => _file?.Index;
        public OpenedFile Shown => _file;
        public PstFolder CurrentFolder => _currentFolder;
        public MailView View => _view;
        public IList<MailRow> Rows => _mailRows;
        public IList<FolderRow> FolderRows => _folderRows;
        public bool IsSearching => _searching;

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"{AppInfo.Name} {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: try again next time.
            }
        }

        // ================================================================== start page

        public void ShowStart()
        {
            var content = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            var tile = LogoTile.Create(72);
            tile.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(tile);
            var title = Ui.Heading("Open an Outlook data file", 24).Margin(0, 18, 0, 6);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(title);
            var sub = Ui.Subtle("PST and OST files, and single emails saved as .msg or .eml. Outlook isn't needed, and the file is only read, never changed.", 13.5);
            sub.TextAlignment = TextAlignment.Center;
            content.Children.Add(sub);
            var open = Ui.Button("Open a file…", "IconFolder", "PrimaryButton", (s, e) => PickAndOpen());
            open.HorizontalAlignment = HorizontalAlignment.Center;
            open.Margin = new Thickness(0, 22, 0, 6);
            content.Children.Add(open);
            var drop = Ui.Subtle("or drop it on this window", 12.5, false);
            drop.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(drop);

            var recent = Settings.Recent.Where(File.Exists).Take(6).ToList();
            if (recent.Count > 0)
            {
                var heading = Ui.Subtle("Opened recently", 12, false).Margin(0, 30, 0, 6);
                heading.HorizontalAlignment = HorizontalAlignment.Center;
                content.Children.Add(heading);
                foreach (var path in recent)
                {
                    var p = path;
                    var link = Ui.Link(Path.GetFileName(path), (s, e) => OpenFile(p));
                    link.ToolTip = path;
                    link.HorizontalAlignment = HorizontalAlignment.Center;
                    link.Margin = new Thickness(0, 2, 0, 2);
                    content.Children.Add(link);
                }
            }
            var card = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(40),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false }
            }.Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "InputBorder");
            _page.Content = card;
            _statusBar.Visibility = Visibility.Collapsed;
            _export.IsEnabled = false;
            _fileName.Text = string.Empty;
            Title = AppInfo.Name;
        }

        private void RecentMenu(FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            var list = Settings.Recent;
            foreach (var path in list)
            {
                var p = path;
                var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, IsEnabled = File.Exists(path) };
                item.Click += (s, e) => OpenFile(p);
                menu.Items.Add(item);
            }
            if (list.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing opened yet", IsEnabled = false });
            else
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Clear this list" };
                clear.Click += (s, e) =>
                {
                    Settings.ClearRecent();
                    if (_files.Count == 0) ShowStart();
                };
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
        }

        // ================================================================== opening files

        public void PickAndOpen()
        {
            var path = Dialogs.PickFile(this, "Open an Outlook data file or an email",
                "Outlook files and emails|*.pst;*.ost;*.msg;*.eml|Outlook data files (*.pst, *.ost)|*.pst;*.ost|Emails (*.msg, *.eml)|*.msg;*.eml|All files|*.*");
            if (path != null) OpenFile(path);
        }

        /// <summary>The files open now, in the order they were opened.</summary>
        public IReadOnlyList<OpenedFile> Files => _files;

        /// <summary>
        /// Opens a file and adds it to the ones already open (a file that's open already is just shown). Returns false
        /// when it couldn't be opened (or the password wasn't given).
        /// </summary>
        public bool OpenFile(string path) => OpenFile(path, true);

        private bool OpenFile(string path, bool show)
        {
            if (Directory.Exists(path))
            {
                Dialogs.Error(this, "That's a folder. Choose a .pst, .ost, .msg or .eml file.");
                return false;
            }
            var already = _files.FirstOrDefault(f => OpenFiles.Same(f.Path, path));
            if (already != null)
            {
                if (show) GoTo(already);
                return true;
            }
            MailSource source;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                source = MailSource.Open(path);
            }
            catch (Exception ex) when (ex is PstException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is NotSupportedException)
            {
                Mouse.OverrideCursor = null;
                Dialogs.Error(this, $"Couldn't open \"{Path.GetFileName(path)}\".\n\n{ex.Message}");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (source.IsDataFile && source.Store.HasPassword && !PasswordWindow.Ask(this, source.Store, Path.GetFileName(path)))
            {
                source.Dispose();
                return false;
            }
            var file = new OpenedFile(source);
            _files.Add(file);
            NameFiles();
            Settings.AddRecent(path);
            Settings.LastFolder = Path.GetDirectoryName(path);
            SaveSession();
            ShowFilePage();

            if (file.IsDataFile)
            {
                StartIndex(file);
                InsertFileRows(file);
                FindDeleted(file);
            }
            else
            {
                UpdateSinglesRow();
            }
            if (show || _file == null && !_singlesShown) GoTo(file);
            UpdateTitle();
            return true;
        }

        /// <summary>Shows a file that's open: a data file's Inbox (or first folder), or a single email among the others.</summary>
        public void GoTo(OpenedFile file)
        {
            if (file.IsDataFile)
            {
                var rows = RowsOf(file).Where(r => !r.IsFile).ToList();
                var inbox = rows.FirstOrDefault(r => r.Depth == 1 && string.Equals(r.Folder.Name, "Inbox", StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault()
                            ?? _folderRows.FirstOrDefault(r => r.IsFile && r.File == file);
                ClearSearchQuiet();
                if (inbox != null)
                {
                    if (_folders.SelectedItem == inbox) ShowRow(inbox);
                    else _folders.SelectedItem = inbox;
                    _folders.ScrollIntoView(inbox);
                }
            }
            else
            {
                var singles = _folderRows.FirstOrDefault(r => r.IsSingles);
                ClearSearchQuiet();
                if (singles == null) return;
                if (_folders.SelectedItem == singles) ShowRow(singles);
                else _folders.SelectedItem = singles;
                var row = _mailRows.FirstOrDefault(r => r.File == file);
                if (row != null)
                {
                    _list.SelectedItem = row;
                    _list.ScrollIntoView(row);
                }
            }
        }

        /// <summary>Names the open files; two with the same file name get their folder added.</summary>
        private void NameFiles()
        {
            var names = OpenFiles.Names(_files.Select(f => f.Path).ToList());
            for (var i = 0; i < _files.Count; i++) _files[i].Name = names[i];
            foreach (var r in _folderRows.Where(r => r.IsFile)) r.Refresh();
        }

        private void ShowFilePage()
        {
            _page.Content = _fileGrid;
            _statusBar.Visibility = Visibility.Visible;
            _export.IsEnabled = true;
            _closeLinks.Visibility = Visibility.Visible;
        }

        private void UpdateTitle()
        {
            var name = _files.Count == 1 ? _files[0].Name : _files.Count + " files";
            Title = _files.Count == 0 ? AppInfo.Name : name + " - " + AppInfo.Name;
            _fileName.Text = _files.Count == 0 ? string.Empty : _files.Count == 1 ? _files[0].Name : $"{_files.Count} files open";
            _fileName.ToolTip = _files.Count == 0 ? null : string.Join("\n", _files.Select(f => f.Path));
            _closeAll.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ the set of open files, kept for next time

        /// <summary>Keeps the set of open files for next time (not in a window opened for a single email).</summary>
        private void SaveSession()
        {
            if (_remember && !SelfTest.Running) Settings.OpenFiles = OpenFiles.Save(_files.Select(f => f.Path));
        }

        /// <summary>
        /// Opens again the files that were open last time. Files with a password ask for it again; files that aren't
        /// there any more are left out, with a note. Returns that note (or null).
        /// </summary>
        public string RestoreSession(string saved = null)
        {
            var (open, missing) = OpenFiles.Restore(saved ?? Settings.OpenFiles, File.Exists);
            foreach (var path in open) OpenFile(path, false);
            if (_files.Count > 0 && _file == null && !_singlesShown) GoTo(_files[0]);
            SaveSession();
            var note = OpenFiles.MissingNote(missing);
            if (note != null)
            {
                if (_files.Count > 0) _status.Text = note;
                else Dialogs.Info(this, note);
            }
            return note;
        }

        /// <summary>A window started to show one email (a double-click on a .msg or .eml) leaves the saved set alone.</summary>
        public void DontRemember() => _remember = false;

        // ------------------------------------------------------------------ closing

        /// <summary>Closes one file; the others stay open.</summary>
        public void CloseFile(OpenedFile file)
        {
            if (file == null || !_files.Contains(file)) return;
            var wasShown = _file == file || (_singlesShown && !file.IsDataFile);
            var listed = _mailRows.Any(r => r.File == file);
            if (_view.Item != null && (_list.SelectedItem as MailRow)?.File == file) _view.Clear();
            _files.Remove(file);
            file.Dispose();
            if (file.IsDataFile) RemoveFileRows(file);
            else UpdateSinglesRow();
            NameFiles();
            SaveSession();
            UpdateTitle();
            if (_files.Count == 0)
            {
                ResetLists();
                ShowStart();
                return;
            }
            if (wasShown && (file.IsDataFile || !_files.Any(f => !f.IsDataFile)))
            {
                _file = null;
                _singlesShown = false;
                GoTo(_files[0]);
            }
            else if (listed)
            {
                if (_searching) RunSearch();
                else if (_singlesShown) ShowSingles();
            }
            UpdateIndexStatus();
        }

        /// <summary>Closes every open file and goes back to the start page.</summary>
        public void CloseAll()
        {
            foreach (var f in _files.ToList()) f.Dispose();
            _files.Clear();
            ResetLists();
            SaveSession();
            UpdateTitle();
            ShowStart();
        }

        /// <summary>When the window closes: the files are let go, and the saved set stays as it is.</summary>
        private void DisposeAll()
        {
            foreach (var f in _files) f.Dispose();
            _files.Clear();
            ResetLists();
        }

        private void ResetLists()
        {
            _indexTimer.Stop();
            _file = null;
            _singlesShown = false;
            _currentFolder = null;
            _searching = false;
            _folderRows.Replace(new FolderRow[0]);
            _mailRows.Replace(new MailRow[0]);
            _folderItems = new List<MailRow>();
            _view.Clear();
        }

        /// <summary>The menu of the folder list: close the file a row belongs to, or all of them.</summary>
        private ContextMenu FolderMenu()
        {
            var menu = new ContextMenu();
            var row = _folders.SelectedItem as FolderRow;
            if (row?.File != null)
            {
                var file = row.File;
                var close = new MenuItem { Header = $"Close \"{file.Name}\"" };
                close.Click += (s, e) => CloseFile(file);
                menu.Items.Add(close);
            }
            else if (row != null && row.IsSingles)
            {
                var close = new MenuItem { Header = "Close the single emails" };
                close.Click += (s, e) =>
                {
                    foreach (var f in _files.Where(f => !f.IsDataFile).ToList()) CloseFile(f);
                };
                menu.Items.Add(close);
            }
            var all = new MenuItem { Header = "Close all files", IsEnabled = _files.Count > 0 };
            all.Click += (s, e) => CloseAll();
            menu.Items.Add(all);
            return menu;
        }

        /// <summary>Closes the file of the folder shown (or, for single emails, the email shown).</summary>
        public void CloseShown()
        {
            if (_file != null) CloseFile(_file);
            else if (_singlesShown) CloseFile((_list.SelectedItem as MailRow)?.File ?? _files.FirstOrDefault(f => !f.IsDataFile));
        }

        // ------------------------------------------------------------------ the search index

        private void StartIndex(OpenedFile file)
        {
            try
            {
                file.Index = new MailIndex(file.Path);
                file.Index.Start();
                _indexTimer.Start();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                file.Index = null;
                _indexStatus.Text = "Search is limited: the search index of " + file.Name + " couldn't be made (" + ex.Message + ")";
            }
            UpdateIndexStatus();
        }

        private void UpdateIndexStatus()
        {
            var building = _files.Where(f => f.Index != null && !f.Index.Complete && f.Index.Error == null).ToList();
            var index = _file?.Index;
            if (index != null)
            {
                if (index.Error != null) _indexStatus.Text = "Couldn't finish the search index: " + index.Error;
                else if (!index.Complete) _indexStatus.Text = MailIndex.Describe(index);
                else if (building.Count > 0) _indexStatus.Text = $"Search ready · {index.Done:N0} items · still making the index of " + string.Join(", ", building.Select(f => f.Name));
                else _indexStatus.Text = _files.Count(f => f.IsDataFile) > 1
                    ? $"Search ready · {index.Done:N0} items here, {_files.Where(f => f.Index != null).Sum(f => f.Index.Done):N0} in all open files"
                    : $"Search ready · {index.Done:N0} items";
            }
            else _indexStatus.Text = building.Count > 0 ? "Making the search index of " + string.Join(", ", building.Select(f => f.Name)) + "…" : string.Empty;
            if (building.Count == 0) _indexTimer.Stop();
        }

        // ================================================================== deleted items found in the file

        private static string FoundCacheFolder(OpenedFile file) => file.Index?.Folder ?? Path.Combine(MailIndex.IndexRoot, MailIndex.KeyOf(file.Path));

        /// <summary>The quick look for deleted items, in the background, each time a file is opened.</summary>
        private async void FindDeleted(OpenedFile file)
        {
            var store = file.Store;
            var folder = FoundCacheFolder(file);
            Recovery found;
            try
            {
                found = await Task.Run(() => Recovery.LoadOrFind(store, folder));
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex) || ex is ObjectDisposedException)
            {
                return;
            }
            if (file.Closed) return;
            ShowFound(file, found);
        }

        public bool LookingDeeper => _file?.RecoveryCancel != null;

        /// <summary>Reads the whole file shown for deleted items (or stops doing so).</summary>
        public async Task LookDeeper()
        {
            var file = _file;
            if (file == null || !file.IsDataFile) return;
            if (file.RecoveryCancel != null)
            {
                file.RecoveryCancel.Cancel();
                return;
            }
            var store = file.Store;
            var folder = FoundCacheFolder(file);
            var cancel = new CancellationTokenSource();
            file.RecoveryCancel = cancel;
            file.DeletedNote = "Looking through the whole file…";
            RefreshDeletedPanel();
            IProgress<double> progress = new Progress<double>(p =>
            {
                if (file.RecoveryCancel != cancel) return;
                file.DeletedNote = $"Looking through the whole file… {p:P0}";
                if (_file == file) _deletedNote.Text = file.DeletedNote;
            });
            try
            {
                var earlier = store.Found;
                var deep = await Task.Run(() => Recovery.Find(store, true, progress.Report, cancel.Token, earlier));
                try { deep.Save(Path.Combine(folder, "found-deep.bin")); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Looked through again next time.
                }
                if (file.Closed) return;
                if (file.RecoveryCancel == cancel) file.RecoveryCancel = null;
                ShowFound(file, deep);
                if (file.Index != null)
                {
                    file.Index.AddFound(deep);
                    _indexTimer.Start();
                }
            }
            catch (OperationCanceledException)
            {
                file.DeletedNote = "Stopped looking.";
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex) || ex is ObjectDisposedException)
            {
                file.DeletedNote = "Couldn't look through the whole file: " + ex.Message;
            }
            finally
            {
                if (file.RecoveryCancel == cancel) file.RecoveryCancel = null;
                if (!file.Closed) RefreshDeletedPanel();
            }
        }

        /// <summary>The "Look deeper" link and its note, for the file shown.</summary>
        private void RefreshDeletedPanel()
        {
            var file = _file;
            if (file == null || !file.IsDataFile || file.Closed)
            {
                _lookDeeper.Visibility = Visibility.Collapsed;
                _deletedNote.Visibility = Visibility.Collapsed;
                return;
            }
            var deep = file.Store.Found?.Deep ?? false;
            _lookDeeper.Content = file.RecoveryCancel != null ? "Stop looking" : LookDeeperText;
            _lookDeeper.Visibility = deep && file.RecoveryCancel == null ? Visibility.Collapsed : Visibility.Visible;
            _deletedNote.Text = file.DeletedNote;
            _deletedNote.Visibility = string.IsNullOrEmpty(file.DeletedNote) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Shows a file's "Found deleted items" folder (at the end of its top folders), or updates it.</summary>
        private void ShowFound(OpenedFile file, Recovery found)
        {
            var store = file.Store;
            store.UseFound(found);
            var partial = found.Items.Count(i => i.Partial);
            if (found.Deep)
            {
                file.DeletedNote = found.Items.Count == 0
                    ? "Looked through the whole file: no deleted items left in it."
                    : $"Looked through the whole file: {Plural(found.Items.Count, "deleted item")} found" + (partial > 0 ? $" ({partial:N0} only partly)." : ".");
            }
            if (store.FoundFolder != null)
            {
                var row = _folderRows.FirstOrDefault(r => r.File == file && !r.IsFile && r.Folder.Nid == Recovery.FolderNid);
                if (row != null)
                {
                    row.Folder = store.FoundFolder;
                    row.Refresh();
                }
                else if (file.Expanded)
                {
                    var at = EndOf(file);
                    if (at >= 0) _folderRows.Insert(at, new FolderRow { Folder = store.FoundFolder, Depth = 1, File = file });
                }
                if (_file == file && _currentFolder?.Nid == Recovery.FolderNid && !_searching) ShowFolder(store.FoundFolder);
            }
            if (_file == file) RefreshDeletedPanel();
        }

        private static string Plural(int n, string word) => n == 1 ? "1 " + word : $"{n:N0} {word}s";

        // ================================================================== folders

        /// <summary>The rows of one open file: its own row, then its folders when it's open.</summary>
        private IEnumerable<FolderRow> RowsOf(OpenedFile file) => _folderRows.Where(r => r.File == file);

        /// <summary>Where the rows of a file end (the index just after its last row), or -1.</summary>
        private int EndOf(OpenedFile file)
        {
            var start = _folderRows.ToList().FindIndex(r => r.IsFile && r.File == file);
            if (start < 0) return -1;
            var end = start + 1;
            while (end < _folderRows.Count && _folderRows[end].Depth > 0) end++;
            return end;
        }

        /// <summary>A file's row and, when it's open, its top folders (one level open by default, like Outlook).</summary>
        private List<FolderRow> FileRows(OpenedFile file)
        {
            var store = file.Store;
            var start = _systemFolders.IsChecked == true ? store.Root : store.Top;
            var rows = new List<FolderRow> { new FolderRow { Type = FolderRowKind.File, File = file, Folder = start, Depth = 0, CanExpand = true, Expanded = file.Expanded } };
            if (file.Expanded) rows.AddRange(TopFolderRows(file));
            return rows;
        }

        private List<FolderRow> TopFolderRows(OpenedFile file)
        {
            var store = file.Store;
            var start = _systemFolders.IsChecked == true ? store.Root : store.Top;
            var rows = new List<FolderRow>();
            try
            {
                foreach (var f in store.SubFolders(start)) rows.Add(new FolderRow { Folder = f, Depth = 1, CanExpand = f.HasSubfolders, File = file });
                var expanded = new List<FolderRow>();
                foreach (var r in rows)
                {
                    expanded.Add(r);
                    if (!r.CanExpand || rows.Count > 30) continue;
                    r.Expanded = true;
                    foreach (var c in store.SubFolders(r.Folder)) expanded.Add(new FolderRow { Folder = c, Depth = 2, CanExpand = c.HasSubfolders, File = file });
                }
                rows = expanded;
            }
            catch (PstException ex)
            {
                _status.Text = "Some folders of " + file.Name + " couldn't be read: " + ex.Message;
            }
            if (rows.Count == 0) rows.Add(new FolderRow { Folder = start, Depth = 1, File = file });
            if (store.FoundFolder != null && rows.All(r => r.Folder.Nid != Recovery.FolderNid))
                rows.Add(new FolderRow { Folder = store.FoundFolder, Depth = 1, File = file });
            return rows;
        }

        /// <summary>Adds a newly opened data file's rows, after the other files and before the single emails.</summary>
        private void InsertFileRows(OpenedFile file)
        {
            var at = _folderRows.ToList().FindIndex(r => r.IsSingles);
            if (at < 0) at = _folderRows.Count;
            foreach (var r in FileRows(file)) _folderRows.Insert(at++, r);
        }

        private void RemoveFileRows(OpenedFile file)
        {
            for (var i = _folderRows.Count - 1; i >= 0; i--)
                if (_folderRows[i].File == file) _folderRows.RemoveAt(i);
        }

        /// <summary>The "Single emails" row at the end of the folder list (only while some are open).</summary>
        private void UpdateSinglesRow()
        {
            var count = _files.Count(f => !f.IsDataFile);
            var row = _folderRows.FirstOrDefault(r => r.IsSingles);
            if (count == 0)
            {
                if (row != null) _folderRows.Remove(row);
                return;
            }
            if (row == null)
            {
                row = new FolderRow { Type = FolderRowKind.Singles, Folder = new PstFolder { Name = "Single emails" }, Depth = 0 };
                _folderRows.Add(row);
            }
            row.Count = count;
            row.Refresh();
            if (_singlesShown && !_searching) ShowSingles();
        }

        /// <summary>Builds the whole folder list again (when system folders are shown or hidden), keeping what's selected.</summary>
        private void FillFolders()
        {
            var selected = _folders.SelectedItem as FolderRow;
            var rows = new List<FolderRow>();
            foreach (var f in _files.Where(f => f.IsDataFile)) rows.AddRange(FileRows(f));
            var count = _files.Count(f => !f.IsDataFile);
            if (count > 0) rows.Add(new FolderRow { Type = FolderRowKind.Singles, Folder = new PstFolder { Name = "Single emails" }, Depth = 0, Count = count });
            _selectingQuietly = true;
            try
            {
                _folderRows.Replace(rows);
                if (selected != null)
                    _folders.SelectedItem = rows.FirstOrDefault(r => r.File == selected.File && r.Type == selected.Type && (r.IsFile || r.IsSingles || r.Folder.Nid == selected.Folder.Nid));
            }
            finally
            {
                _selectingQuietly = false;
            }
        }

        private bool _selectingQuietly;

        private void Toggle(FolderRow row)
        {
            var index = _folderRows.IndexOf(row);
            if (index < 0) return;
            if (row.Expanded)
            {
                var end = index + 1;
                while (end < _folderRows.Count && _folderRows[end].Depth > row.Depth) end++;
                for (var i = end - 1; i > index; i--) _folderRows.RemoveAt(i);
                row.Expanded = false;
                if (row.IsFile) row.File.Expanded = false;
            }
            else
            {
                List<FolderRow> children;
                if (row.IsFile)
                {
                    row.File.Expanded = true;
                    children = TopFolderRows(row.File);
                }
                else
                {
                    List<PstFolder> folders;
                    try { folders = row.File.Store.SubFolders(row.Folder); }
                    catch (PstException) { folders = new List<PstFolder>(); }
                    children = folders.Select(c => new FolderRow { Folder = c, Depth = row.Depth + 1, CanExpand = c.HasSubfolders, File = row.File }).ToList();
                }
                var at = index + 1;
                foreach (var c in children) _folderRows.Insert(at++, c);
                row.Expanded = true;
            }
            row.Refresh();
        }

        /// <summary>Shows what a row of the folder list holds.</summary>
        private void ShowRow(FolderRow row)
        {
            if (row.IsSingles)
            {
                _file = null;
                _singlesShown = true;
                _currentFolder = null;
                RefreshDeletedPanel();
                UpdateIndexStatus();
                if (HasQuery && _scope.SelectedIndex == 0) return; // searching all files: the results stay
                if (HasQuery) RunSearch();
                else ShowSingles();
                return;
            }
            var changedFile = _file != row.File;
            _file = row.File;
            _singlesShown = false;
            if (changedFile)
            {
                RefreshDeletedPanel();
                UpdateIndexStatus();
            }
            ShowFolder(row.Folder);
        }

        /// <summary>Lists the single emails (.msg and .eml files) that are open.</summary>
        private void ShowSingles()
        {
            ++_loadGeneration;
            var rows = _files.Where(f => !f.IsDataFile).Select(f =>
            {
                var r = MailRow.Of(f.Source.Single);
                r.File = f;
                r.FolderName = f.Name;
                return r;
            }).ToList();
            _folderItems = rows;
            _searching = false;
            SetKind(ListKind.Mail);
            _mailRows.Replace(Sorted(rows));
            _listTitle.Text = "Single emails";
            _status.Text = rows.Count == 1 ? "1 email file" : $"{rows.Count:N0} email files";
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else _view.Clear();
        }

        public async void ShowFolder(PstFolder folder)
        {
            if (_file?.Store == null) return;
            _currentFolder = folder;
            if (HasQuery && _scope.SelectedIndex == 2)
            {
                RunSearch();
                return;
            }
            if (HasQuery && _scope.SelectedIndex == 1 && _searchedFile != _file)
            {
                RunSearch();
                return;
            }
            if (HasQuery) return; // searching a whole file, or all of them: the folder doesn't change the results
            await LoadFolder(folder);
        }

        private async Task LoadFolder(PstFolder folder)
        {
            var generation = ++_loadGeneration;
            var file = _file;
            _listTitle.Text = folder.Nid == file.Store.Top.Nid || folder.Nid == file.Store.Root.Nid ? file.Name : folder.Name;
            _status.Text = "Reading the folder…";
            var store = file.Store;
            List<MailRow> rows;
            try
            {
                rows = await Task.Run(() => store.Items(folder).Select(s =>
                {
                    var r = MailRow.Of(s);
                    r.File = file;
                    return r;
                }).ToList());
            }
            catch (Exception ex) when (ex is PstException || ex is IOException || ex is ObjectDisposedException)
            {
                if (generation != _loadGeneration) return;
                _status.Text = "This folder couldn't be read: " + ex.Message;
                _mailRows.Replace(new MailRow[0]);
                return;
            }
            if (generation != _loadGeneration || _file != file || file.Closed) return;
            _folderItems = rows;
            _searching = false;
            SetKind(KindOf(folder, rows));
            _mailRows.Replace(Sorted(rows));
            _status.Text = folder.IsSearchFolder
                ? "A search folder: Outlook keeps its results elsewhere, so it has nothing to show here."
                : Count(rows.Count) + " in " + _listTitle.Text + (_files.Count > 1 ? " (" + file.Name + ")" : string.Empty);
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else _view.Clear();
        }

        private static string Count(int n) => n == 1 ? "1 item" : $"{n:N0} items";

        // ================================================================== search

        private bool HasQuery => _search.Text.Trim().Length > 0 || _fromDate.SelectedDate.HasValue || _toDate.SelectedDate.HasValue || _withAttachments.IsChecked == true;

        private void RestartSearch()
        {
            if (_files.Count == 0) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void ClearSearchQuiet()
        {
            _search.Text = string.Empty;
            _fromDate.SelectedDate = null;
            _toDate.SelectedDate = null;
            _withAttachments.IsChecked = false;
            _searchTimer.Stop();
            _searching = false;
        }

        public void ClearSearch()
        {
            ClearSearchQuiet();
            if (_singlesShown) ShowSingles();
            else if (_currentFolder != null) ShowFolder(_currentFolder);
        }

        /// <summary>
        /// Searches with what's in the search box and filters: in all open files (each result says which file it's
        /// from), the file shown, or the folder shown.
        /// </summary>
        public async void RunSearch()
        {
            _searchTimer.Stop();
            if (_files.Count == 0) return;
            if (!HasQuery)
            {
                if (_searching)
                {
                    if (_singlesShown) ShowSingles();
                    else if (_currentFolder != null && _file != null) await LoadFolder(_currentFolder);
                }
                return;
            }
            var scope = _scope.SelectedIndex;
            // Searching "this file" or "this folder" while single emails are shown: those emails.
            var inFolder = scope == 2 && _file != null && _currentFolder != null;
            var query = new SearchQuery
            {
                Text = _search.Text.Trim(),
                Field = (SearchField)Math.Max(0, _field.SelectedIndex),
                From = _fromDate.SelectedDate?.Date.ToUniversalTime(),
                To = _toDate.SelectedDate?.Date.AddDays(1).ToUniversalTime(),
                OnlyWithAttachments = _withAttachments.IsChecked == true,
                Folders = inFolder ? new HashSet<uint> { _currentFolder.Nid } : null
            };
            List<OpenedFile> files;
            if (scope == 0) files = _files.ToList();
            else if (_file != null) files = new List<OpenedFile> { _file };
            else files = _files.Where(f => !f.IsDataFile).ToList();
            _searchedFile = scope == 1 ? _file : null;
            var many = _files.Count > 1;
            var generation = ++_loadGeneration;
            _status.Text = "Searching…";
            List<MailRow> rows;
            try
            {
                rows = await Task.Run(() =>
                {
                    var found = new List<MailRow>();
                    var data = files.Where(f => f.IsDataFile && !f.Closed).ToList();
                    foreach (var (file, item) in OpenFiles.Search(data.Select(f => (f, f.Index)), query))
                    {
                        var r = MailRow.Of(item);
                        r.File = file;
                        found.Add(r);
                    }
                    // Folder names, once per folder of each file.
                    var names = new Dictionary<(OpenedFile, uint), string>();
                    foreach (var r in found)
                    {
                        if (!names.TryGetValue((r.File, r.FolderNid), out var name))
                        {
                            var store = r.File.Store;
                            try { name = string.Join(" › ", store.PathOf(r.FolderNid).Where(f => f.Nid != store.TopNid && f.Nid != PstStore.RootFolderNid).Select(f => f.Name)); }
                            catch (Exception ex) when (ex is PstException || ex is ObjectDisposedException) { name = string.Empty; }
                            if (many) name = name.Length > 0 ? r.File.Name + " › " + name : r.File.Name;
                            names[(r.File, r.FolderNid)] = name;
                        }
                        r.FolderName = name;
                    }
                    // Single emails have no index: each is checked as it is.
                    foreach (var single in files.Where(f => !f.IsDataFile && !f.Closed))
                    {
                        if (!OpenFiles.Matches(single.Source.Single, query)) continue;
                        var r = MailRow.Of(single.Source.Single);
                        r.File = single;
                        r.FolderName = many ? "Single emails › " + single.Name : single.Name;
                        found.Add(r);
                    }
                    return found;
                });
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException)
            {
                _status.Text = "The search didn't work: " + ex.Message;
                return;
            }
            if (generation != _loadGeneration) return;
            _searching = true;
            _folderItems = rows;
            SetKind(ListKind.Mail);
            _mailRows.Replace(Sorted(rows));
            _listTitle.Text = "Search results";
            string where;
            if (inFolder) where = " in " + _currentFolder.Name;
            else if (scope == 0) where = _files.Count > 1 ? $" in all {_files.Count} open files" : string.Empty;
            else where = _file != null ? (_files.Count > 1 ? " in " + _file.Name : string.Empty) : " in the single emails";
            var building = files.Any(f => f.Index != null && !f.Index.Complete);
            _status.Text = (rows.Count == 0 ? "Nothing found" : rows.Count == 1 ? "1 item found" : $"{rows.Count:N0} items found") + where +
                           (building ? " so far (the search index is still being made)" : string.Empty);
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else _view.Clear();
        }

        private static readonly string[] MailSorts = { "Newest first", "Oldest first", "From", "Subject", "Largest first" };
        private static readonly string[] ContactSorts = { "Name", "Company" };
        private static readonly string[] CalendarSorts = { "Oldest first", "Newest first" };

        /// <summary>Contacts and calendar folders are listed their own way; any other folder as emails.</summary>
        public static ListKind KindOf(PstFolder folder, IList<MailRow> rows)
        {
            var c = folder.ContainerClass.ToUpperInvariant();
            if (c.StartsWith("IPF.APPOINTMENT")) return ListKind.Calendar;
            if (c.StartsWith("IPF.CONTACT")) return ListKind.Contacts;
            if (c.Length == 0 && rows.Count > 0)
            {
                if (rows.All(r => r.IsCalendar)) return ListKind.Calendar;
                if (rows.All(r => r.IsContact)) return ListKind.Contacts;
            }
            return ListKind.Mail;
        }

        private void SetKind(ListKind kind)
        {
            if (kind == Kind) return;
            var view = CollectionViewSource.GetDefaultView(_mailRows);
            Kind = kind;
            _settingKind = true;
            try
            {
                _sort.Items.Clear();
                foreach (var s in kind == ListKind.Contacts ? ContactSorts : kind == ListKind.Calendar ? CalendarSorts : MailSorts) _sort.Items.Add(s);
                _sort.SelectedIndex = 0;
            }
            finally
            {
                _settingKind = false;
            }
            view.GroupDescriptions.Clear();
            if (kind == ListKind.Calendar) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MailRow.MonthText)));
            System.Windows.Automation.AutomationProperties.SetName(_list, kind == ListKind.Contacts ? "Contacts" : kind == ListKind.Calendar ? "Calendar" : "Emails");
        }

        private IEnumerable<MailRow> Sorted(IEnumerable<MailRow> rows)
        {
            if (Kind == ListKind.Contacts)
            {
                return _sort.SelectedIndex == 1
                    ? rows.OrderBy(r => r.Company.Length == 0).ThenBy(r => r.Company, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.SortName, StringComparer.CurrentCultureIgnoreCase)
                    : rows.OrderBy(r => r.SortName, StringComparer.CurrentCultureIgnoreCase);
            }
            if (Kind == ListKind.Calendar)
            {
                return _sort.SelectedIndex == 1
                    ? rows.OrderByDescending(r => r.When ?? DateTime.MinValue)
                    : rows.OrderBy(r => r.When ?? DateTime.MaxValue);
            }
            switch (_sort.SelectedIndex)
            {
                case 1: return rows.OrderBy(r => r.Date ?? DateTime.MaxValue);
                case 2: return rows.OrderBy(r => r.From, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Date);
                case 3: return rows.OrderBy(r => Subject(r.Subject), StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Date);
                case 4: return rows.OrderByDescending(r => r.Size);
                default: return rows.OrderByDescending(r => r.Date ?? DateTime.MinValue);
            }
        }

        /// <summary>A subject without "RE:" and "FW:" in front, for sorting conversations together.</summary>
        private static string Subject(string s)
        {
            var t = s ?? string.Empty;
            while (true)
            {
                var m = System.Text.RegularExpressions.Regex.Match(t, @"^\s*(re|fw|fwd|aw|wg|sv|vs|tr)\s*:\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return t;
                t = t.Substring(m.Length);
            }
        }

        private void Resort()
        {
            var selected = _list.SelectedItem;
            _mailRows.Replace(Sorted(_folderItems));
            if (selected != null) _list.SelectedItem = selected;
        }

        // ================================================================== reading

        public async Task<MailItem> Load(MailRow row)
        {
            if (row.Item != null) return row.Item;
            var store = (row.File ?? _file)?.Store;
            if (store == null || (row.File?.Closed ?? false)) return null;
            return await Task.Run(() =>
            {
                var item = store.ReadItem(row.Nid);
                // Read the body and attachment list now, away from the window's thread.
                var body = item.Body;
                var attachments = item.Attachments;
                return item;
            });
        }

        private int _showGeneration;

        private async Task ShowSelected()
        {
            var row = _list.SelectedItems.Count == 1 ? _list.SelectedItem as MailRow : null;
            var generation = ++_showGeneration;
            if (row == null)
            {
                if (_list.SelectedItems.Count > 1) _status.Text = $"{_list.SelectedItems.Count:N0} selected";
                return;
            }
            MailItem item;
            try
            {
                item = await Load(row);
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex) || ex is ObjectDisposedException)
            {
                if (generation == _showGeneration) _status.Text = "This item couldn't be read: " + ex.Message;
                return;
            }
            if (generation != _showGeneration || item == null) return;
            await _view.Show(item);
        }

        private async void OpenInWindow()
        {
            if (!(_list.SelectedItem is MailRow row)) return;
            try
            {
                var item = await Load(row);
                if (item != null) new ItemWindow(item, this).Show();
            }
            catch (Exception ex) when (Exporter.IsReadProblem(ex))
            {
                Dialogs.Error(this, "This item couldn't be read.\n\n" + ex.Message);
            }
        }

        private ContextMenu ListMenu()
        {
            var menu = new ContextMenu();
            var count = _list.SelectedItems.Count;
            var open = new MenuItem { Header = "Open in its own window", IsEnabled = count == 1 };
            open.Click += (s, e) => OpenInWindow();
            menu.Items.Add(open);
            var export = new MenuItem { Header = count > 1 ? $"Export the {count:N0} selected…" : "Export…", IsEnabled = count > 0 };
            export.Click += (s, e) => ExportWindow.Show(this, ExportScope.Selected);
            menu.Items.Add(export);
            return menu;
        }

        // ================================================================== export support

        /// <summary>The selected rows.</summary>
        public List<MailRow> SelectedRows => _list.SelectedItems.OfType<MailRow>().ToList();

        /// <summary>The rows the list shows now (a folder or search results).</summary>
        public List<MailRow> ListedRows => _mailRows.ToList();

        public string FolderPathOf(uint folderNid) => FolderPathOf(_file, folderNid);

        /// <summary>Where a folder is in a file, e.g. "Inbox/Projects" (with the file's name first when several are open).</summary>
        public string FolderPathOf(OpenedFile file, uint folderNid)
        {
            var store = file?.Store;
            if (store == null) return string.Empty;
            string path;
            try
            {
                path = string.Join("/", store.PathOf(folderNid).Where(f => f.Nid != store.TopNid && f.Nid != PstStore.RootFolderNid).Select(f => f.Name));
            }
            catch (PstException)
            {
                path = string.Empty;
            }
            return _files.Count > 1 ? (path.Length > 0 ? file.Name + "/" + path : file.Name) : path;
        }

        public ExportItem ToExport(MailRow row)
        {
            if (row.Item != null) return new ExportItem { Load = () => row.Item, Subject = row.Subject, Date = row.Date };
            var file = row.File ?? _file;
            var store = file.Store;
            var nid = row.Nid;
            return new ExportItem { Load = () => store.ReadItem(nid), FolderPath = FolderPathOf(file, row.FolderNid), Subject = row.Subject, Date = row.Date, Store = store, Nid = nid };
        }

        /// <summary>Every item in a folder of the file shown (and, when asked, its sub-folders).</summary>
        public List<ExportItem> FolderItems(PstFolder folder, bool subFolders) => FolderItems(_file, folder, subFolders);

        public List<ExportItem> FolderItems(OpenedFile file, PstFolder folder, bool subFolders)
        {
            var store = file.Store;
            var list = new List<ExportItem>();
            var folders = subFolders ? store.AllFolders(folder).ToList() : new List<PstFolder> { folder };
            foreach (var f in folders.Where(f => !f.IsSearchFolder))
            {
                var path = FolderPathOf(file, f.Nid);
                List<ItemSummary> items;
                try { items = store.Items(f); }
                catch (PstException) { continue; }
                foreach (var s in items)
                {
                    var nid = s.Nid;
                    list.Add(new ExportItem { Load = () => store.ReadItem(nid), FolderPath = path, Subject = s.Subject, Date = s.Date, Store = store, Nid = nid });
                }
            }
            return list;
        }

        /// <summary>Everything in every open file: each data file's folders under its name, and the single emails.</summary>
        public List<ExportItem> AllFilesItems()
        {
            var list = new List<ExportItem>();
            foreach (var f in _files)
            {
                if (f.IsDataFile) list.AddRange(FolderItems(f, f.Store.Top, true));
                else
                {
                    var item = f.Source.Single;
                    list.Add(new ExportItem { Load = () => item, FolderPath = "Single emails", Subject = item.Subject, Date = item.Date });
                }
            }
            return list;
        }

        // ================================================================== for the self-test

        /// <summary>Shows a folder: the row with that very folder, else the one with its number in the file shown.</summary>
        public void SelectFolder(PstFolder folder)
        {
            var row = _folderRows.FirstOrDefault(r => !r.IsFile && ReferenceEquals(r.Folder, folder))
                      ?? _folderRows.FirstOrDefault(r => !r.IsFile && !r.IsSingles && r.File == _file && r.Folder.Nid == folder.Nid)
                      ?? _folderRows.FirstOrDefault(r => !r.IsFile && !r.IsSingles && r.Folder.Nid == folder.Nid);
            if (row != null)
            {
                if (_folders.SelectedItem == row) ShowRow(row);
                else _folders.SelectedItem = row;
            }
            else ShowFolder(folder);
        }

        /// <summary>Shows the single emails.</summary>
        public void SelectSingles()
        {
            var row = _folderRows.FirstOrDefault(r => r.IsSingles);
            if (row != null) _folders.SelectedItem = row;
        }

        public void ExpandAll()
        {
            for (var i = 0; i < _folderRows.Count; i++)
                if (_folderRows[i].CanExpand && !_folderRows[i].Expanded) Toggle(_folderRows[i]);
        }

        public void SetSearch(string text, int scope = 0, int field = 0, bool attachments = false)
        {
            _scope.SelectedIndex = scope;
            _field.SelectedIndex = field;
            _withAttachments.IsChecked = attachments;
            _search.Text = text;
            RunSearch();
        }

        public void SelectRow(int index)
        {
            _list.SelectedIndex = -1;
            _list.SelectedIndex = index;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        public void SelectAllRows() => _list.SelectAll();

        public void ShowFilters(bool show)
        {
            _filterToggle.IsChecked = show;
            _filters.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowSystemFolders(bool show)
        {
            _systemFolders.IsChecked = show;
            FillFolders();
        }
    }
}
