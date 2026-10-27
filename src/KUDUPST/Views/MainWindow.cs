using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// The main window: a start page to open a file, then the file's folders, the list of emails with search, and the
    /// email itself.
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
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly ColumnDefinition _treeColumn = new ColumnDefinition { Width = new GridLength(250), MinWidth = 150 };
        private readonly ColumnDefinition _treeSplitter = new ColumnDefinition { Width = GridLength.Auto };
        private readonly Grid _fileGrid = new Grid();
        private readonly Grid _searchArea;

        private MailSource _source;
        private MailIndex _index;
        private PstFolder _currentFolder;
        private List<MailRow> _folderItems = new List<MailRow>();
        private bool _searching;
        private int _loadGeneration;

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
            header.Children.Add(Ui.Button("Open…", "IconFolder", "GhostButton", (s, e) => PickAndOpen()).Tip("Open a .pst, .ost, .msg or .eml file (Ctrl+O)").At(0, 3));
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
                if (_folders.SelectedItem is FolderRow row) ShowFolder(row.Folder);
            };
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
            treePanel.Children.Add(_systemFolders.Margin(14, 6, 0, 10).At(2));
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
            foreach (var s in new[] { "Whole file", "This folder" }) _scope.Items.Add(s);
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

            foreach (var s in new[] { "Newest first", "Oldest first", "From", "Subject", "Largest first" }) _sort.Items.Add(s);
            _sort.SelectedIndex = 0;
            _sort.ToolTip = "Sort the list";
            _sort.SelectionChanged += (s, e) => Resort();
            var titleRow = Ui.Columns(Ui.Star(), Ui.Auto);
            _listTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            titleRow.Children.Add(_listTitle.At(0, 0));
            titleRow.Children.Add(_sort.At(0, 1));
            titleRow.Margin = new Thickness(0, 10, 0, 6);

            _list = new ListBox { ItemsSource = _mailRows, ItemTemplate = Templates.Mail, SelectionMode = SelectionMode.Extended }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
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
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenFile(files[0]);
            };
            DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { PickAndOpen(); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && _source != null && _source.IsDataFile) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E && _source != null) { ExportWindow.Show(this); e.Handled = true; }
            };
            Loaded += async (s, e) => await CheckForUpdates();
            Closed += (s, e) => CloseFile();
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
        public MailIndex Index => _index;
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
                    if (_source == null) ShowStart();
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

        /// <summary>Opens a file. Returns false when it couldn't be opened (or the password wasn't given).</summary>
        public bool OpenFile(string path)
        {
            if (Directory.Exists(path))
            {
                Dialogs.Error(this, "That's a folder. Choose a .pst, .ost, .msg or .eml file.");
                return false;
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
            CloseFile();
            _source = source;
            Settings.AddRecent(path);
            Settings.LastFolder = Path.GetDirectoryName(path);
            Title = Path.GetFileName(path) + " - " + AppInfo.Name;
            _fileName.Text = Path.GetFileName(path);
            _fileName.ToolTip = path;
            _export.IsEnabled = true;
            _page.Content = _fileGrid;
            _statusBar.Visibility = Visibility.Visible;
            ClearSearchQuiet();
            _view.Clear();

            if (source.IsDataFile)
            {
                _treeColumn.Width = new GridLength(250);
                _treeColumn.MinWidth = 150;
                _fileGrid.Children[0].Visibility = Visibility.Visible;
                _fileGrid.Children[1].Visibility = Visibility.Visible;
                _searchArea.Visibility = Visibility.Visible;
                FillFolders();
                StartIndex(path);
                var inbox = _folderRows.FirstOrDefault(r => string.Equals(r.Folder.Name, "Inbox", StringComparison.OrdinalIgnoreCase)) ?? _folderRows.FirstOrDefault();
                if (inbox != null) _folders.SelectedItem = inbox;
            }
            else
            {
                _treeColumn.Width = new GridLength(0);
                _treeColumn.MinWidth = 0;
                _fileGrid.Children[0].Visibility = Visibility.Collapsed;
                _fileGrid.Children[1].Visibility = Visibility.Collapsed;
                _searchArea.Visibility = Visibility.Collapsed;
                _indexStatus.Text = string.Empty;
                _folderItems = new List<MailRow> { MailRow.Of(source.Single) };
                _mailRows.Replace(_folderItems);
                _listTitle.Text = source.Kind == SourceKind.Msg ? "Outlook email (.msg)" : "Email (.eml)";
                _status.Text = "1 email";
                _list.SelectedIndex = 0;
            }
            return true;
        }

        public void CloseFile()
        {
            _indexTimer.Stop();
            _index?.Dispose();
            _index = null;
            _source?.Dispose();
            _source = null;
            _currentFolder = null;
            _folderRows.Replace(new FolderRow[0]);
            _mailRows.Replace(new MailRow[0]);
        }

        private void StartIndex(string path)
        {
            try
            {
                _index = new MailIndex(path);
                _index.Start();
                _indexTimer.Start();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _index = null;
                _indexStatus.Text = "Search is limited: the search index couldn't be made (" + ex.Message + ")";
            }
            UpdateIndexStatus();
        }

        private void UpdateIndexStatus()
        {
            if (_index == null) return;
            if (_index.Error != null)
            {
                _indexStatus.Text = "Couldn't finish the search index: " + _index.Error;
                _indexTimer.Stop();
                return;
            }
            _indexStatus.Text = _index.Complete ? $"Search ready · {_index.Done:N0} items" : MailIndex.Describe(_index);
            if (_index.Complete) _indexTimer.Stop();
        }

        // ================================================================== folders

        private void FillFolders()
        {
            if (_source?.Store == null) return;
            var store = _source.Store;
            var start = _systemFolders.IsChecked == true ? store.Root : store.Top;
            var rows = new List<FolderRow>();
            try
            {
                foreach (var f in store.SubFolders(start)) rows.Add(new FolderRow { Folder = f, Depth = 0, CanExpand = f.HasSubfolders });
                // Folders open one level by default, like Outlook.
                var expanded = new List<FolderRow>();
                foreach (var r in rows)
                {
                    expanded.Add(r);
                    if (!r.CanExpand || rows.Count > 30) continue;
                    r.Expanded = true;
                    foreach (var c in store.SubFolders(r.Folder)) expanded.Add(new FolderRow { Folder = c, Depth = 1, CanExpand = c.HasSubfolders });
                }
                rows = expanded;
            }
            catch (PstException ex)
            {
                _status.Text = "Some folders couldn't be read: " + ex.Message;
            }
            if (rows.Count == 0) rows.Add(new FolderRow { Folder = start, Depth = 0 });
            _folderRows.Replace(rows);
        }

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
            }
            else
            {
                List<PstFolder> children;
                try { children = _source.Store.SubFolders(row.Folder); }
                catch (PstException) { children = new List<PstFolder>(); }
                var at = index + 1;
                foreach (var c in children) _folderRows.Insert(at++, new FolderRow { Folder = c, Depth = row.Depth + 1, CanExpand = c.HasSubfolders });
                row.Expanded = true;
            }
            row.Refresh();
        }

        public async void ShowFolder(PstFolder folder)
        {
            if (_source?.Store == null) return;
            _currentFolder = folder;
            if (HasQuery && _scope.SelectedIndex == 1)
            {
                RunSearch();
                return;
            }
            if (HasQuery) return; // searching the whole file: the folder doesn't change the results
            await LoadFolder(folder);
        }

        private async Task LoadFolder(PstFolder folder)
        {
            var generation = ++_loadGeneration;
            _listTitle.Text = folder.Name;
            _status.Text = "Reading the folder…";
            var store = _source.Store;
            List<MailRow> rows;
            try
            {
                rows = await Task.Run(() => store.Items(folder).Select(MailRow.Of).ToList());
            }
            catch (Exception ex) when (ex is PstException || ex is IOException || ex is ObjectDisposedException)
            {
                if (generation != _loadGeneration) return;
                _status.Text = "This folder couldn't be read: " + ex.Message;
                _mailRows.Replace(new MailRow[0]);
                return;
            }
            if (generation != _loadGeneration || _source?.Store != store) return;
            _folderItems = rows;
            _searching = false;
            _mailRows.Replace(Sorted(rows));
            _status.Text = folder.IsSearchFolder
                ? "A search folder: Outlook keeps its results elsewhere, so it has nothing to show here."
                : Count(rows.Count) + " in " + folder.Name;
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else _view.Clear();
        }

        private static string Count(int n) => n == 1 ? "1 item" : $"{n:N0} items";

        // ================================================================== search

        private bool HasQuery => _search.Text.Trim().Length > 0 || _fromDate.SelectedDate.HasValue || _toDate.SelectedDate.HasValue || _withAttachments.IsChecked == true;

        private void RestartSearch()
        {
            if (_source == null || !_source.IsDataFile) return;
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
            if (_currentFolder != null) ShowFolder(_currentFolder);
        }

        /// <summary>Searches with what's in the search box and filters.</summary>
        public async void RunSearch()
        {
            _searchTimer.Stop();
            if (_source?.Store == null) return;
            if (!HasQuery)
            {
                if (_searching && _currentFolder != null) await LoadFolder(_currentFolder);
                return;
            }
            var query = new SearchQuery
            {
                Text = _search.Text.Trim(),
                Field = (SearchField)Math.Max(0, _field.SelectedIndex),
                From = _fromDate.SelectedDate?.Date.ToUniversalTime(),
                To = _toDate.SelectedDate?.Date.AddDays(1).ToUniversalTime(),
                OnlyWithAttachments = _withAttachments.IsChecked == true,
                Folders = _scope.SelectedIndex == 1 && _currentFolder != null ? new HashSet<uint> { _currentFolder.Nid } : null
            };
            var generation = ++_loadGeneration;
            _status.Text = "Searching…";
            var index = _index;
            var store = _source.Store;
            List<MailRow> rows;
            try
            {
                rows = await Task.Run(() =>
                {
                    var found = index != null ? index.Search(query).Select(MailRow.Of).ToList() : new List<MailRow>();
                    var names = new Dictionary<uint, string>();
                    foreach (var r in found)
                    {
                        if (!names.TryGetValue(r.FolderNid, out var name))
                        {
                            try { name = string.Join(" › ", store.PathOf(r.FolderNid).Where(f => f.Nid != store.TopNid && f.Nid != PstStore.RootFolderNid).Select(f => f.Name)); }
                            catch (PstException) { name = string.Empty; }
                            names[r.FolderNid] = name;
                        }
                        r.FolderName = name;
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
            _mailRows.Replace(Sorted(rows));
            _listTitle.Text = "Search results";
            var where = query.Folders != null ? " in " + _currentFolder.Name : string.Empty;
            _status.Text = (rows.Count == 0 ? "Nothing found" : rows.Count == 1 ? "1 item found" : $"{rows.Count:N0} items found") + where +
                           (index != null && !index.Complete ? " so far (the search index is still being built)" : string.Empty);
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else _view.Clear();
        }

        private IEnumerable<MailRow> Sorted(IEnumerable<MailRow> rows)
        {
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
            var store = _source?.Store;
            if (store == null) return null;
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

        public string FolderPathOf(uint folderNid)
        {
            var store = _source?.Store;
            if (store == null) return string.Empty;
            try
            {
                return string.Join("/", store.PathOf(folderNid).Where(f => f.Nid != store.TopNid && f.Nid != PstStore.RootFolderNid).Select(f => f.Name));
            }
            catch (PstException)
            {
                return string.Empty;
            }
        }

        public ExportItem ToExport(MailRow row)
        {
            var store = _source?.Store;
            if (row.Item != null) return new ExportItem { Load = () => row.Item, Subject = row.Subject, Date = row.Date };
            var nid = row.Nid;
            return new ExportItem { Load = () => store.ReadItem(nid), FolderPath = FolderPathOf(row.FolderNid), Subject = row.Subject, Date = row.Date };
        }

        /// <summary>Every item in a folder (and, when asked, its sub-folders).</summary>
        public List<ExportItem> FolderItems(PstFolder folder, bool subFolders)
        {
            var store = _source.Store;
            var list = new List<ExportItem>();
            var folders = subFolders ? store.AllFolders(folder).ToList() : new List<PstFolder> { folder };
            foreach (var f in folders.Where(f => !f.IsSearchFolder))
            {
                var path = FolderPathOf(f.Nid);
                List<ItemSummary> items;
                try { items = store.Items(f); }
                catch (PstException) { continue; }
                foreach (var s in items)
                {
                    var nid = s.Nid;
                    list.Add(new ExportItem { Load = () => store.ReadItem(nid), FolderPath = path, Subject = s.Subject, Date = s.Date });
                }
            }
            return list;
        }

        // ================================================================== for the self-test

        public void SelectFolder(PstFolder folder)
        {
            var row = _folderRows.FirstOrDefault(r => r.Folder.Nid == folder.Nid);
            if (row != null) _folders.SelectedItem = row;
            else ShowFolder(folder);
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
