using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudupst.Controls;
using Kudupst.Core;
using Kudupst.Core.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Kudupst.Views
{
    /// <summary>
    /// The shared Edge engine (WebView2) set-up: one profile in the user's AppData for all KUDUPST windows. Scripts are
    /// off, and every request is answered by KUDUPST itself: the email page and its pictures from memory, anything from
    /// the internet only when allowed.
    /// </summary>
    public static class Web
    {
        private static Task<CoreWebView2Environment> _environment;

        public static string DataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUPST", "WebView2");

        private static bool _loaderSet;

        /// <summary>Points the Edge engine at its loader for this computer's processor (x64, x86 or ARM).</summary>
        private static void EnsureLoader()
        {
            if (_loaderSet) return;
            _loaderSet = true;
            try
            {
                string arch;
                switch (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
                {
                    case System.Runtime.InteropServices.Architecture.X86: arch = "x86"; break;
                    case System.Runtime.InteropServices.Architecture.Arm64: arch = "arm64"; break;
                    default: arch = "x64"; break;
                }
                var folder = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "runtimes", "win-" + arch, "native");
                if (Directory.Exists(folder)) CoreWebView2Environment.SetLoaderDllFolderPath(folder);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is IOException)
            {
                // Already set, or not needed.
            }
        }

        public static Task<CoreWebView2Environment> Environment2()
        {
            EnsureLoader();
            if (_environment == null)
                _environment = CoreWebView2Environment.CreateAsync(null, DataFolder, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
            return _environment;
        }

        /// <summary>The installed Edge engine's version, or null when it's missing.</summary>
        public static string RuntimeVersion
        {
            get
            {
                EnsureLoader();
                try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex is DllNotFoundException || ex is BadImageFormatException) { return null; }
            }
        }

        public const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>Sets up a WebView2 for showing emails.</summary>
        public static async Task<CoreWebView2> Prepare(WebView2 view)
        {
            var env = await Environment2();
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            var s = core.Settings;
            s.IsScriptEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsWebMessageEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsStatusBarEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            return core;
        }

        public static CoreWebView2WebResourceResponse Response(CoreWebView2Environment env, byte[] data, string type, int status = 200)
        {
            var stream = data == null ? null : new MemoryStream(data);
            var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Blocked";
            return env.CreateWebResourceResponse(stream, status, reason, "Content-Type: " + type + "\r\nCache-Control: no-store");
        }

        /// <summary>Opens a web or email link from an email in the user's own browser or mail program.</summary>
        public static void OpenLink(Window owner, string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeMailto) return;
            if (u.Scheme != Uri.UriSchemeMailto && !Dialogs.Confirm(owner, "Open this link in your web browser?\n\n" + (uri.Length > 300 ? uri.Substring(0, 300) + "…" : uri))) return;
            AppInfo.OpenUrl(uri);
        }
    }

    /// <summary>The reading pane: shows one email.</summary>
    public sealed class MailView : Grid
    {
        private readonly WebView2 _web = new WebView2();
        private readonly TextBox _fallback = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(16) };
        private readonly Border _banner;
        private readonly TextBlock _bannerText = Ui.Text(string.Empty, 12.5, wrap: true);
        private readonly Button _showPictures;
        private readonly Grid _empty;
        private CoreWebView2 _core;
        private Task _ready;
        private MailItem _item;
        private bool _allowRemote;
        private string _page;
        private int _generation;

        /// <summary>Raised when an attachment's name is clicked in the header (its index in the item's attachments).</summary>
        public event Action<int> AttachmentClicked;

        /// <summary>Raised when a page has finished showing (for the self-test).</summary>
        public event Action Shown;

        public MailItem Item => _item;
        public bool PicturesBlocked { get; private set; }
        public bool UsingFallback { get; private set; }
        public bool AllowRemote => _allowRemote;

        public MailView()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());

            _showPictures = Ui.Small(Ui.Button("Show pictures", "IconPicture", "SecondaryButton", (s, e) => ShowPictures()));
            var bannerGrid = Ui.Columns(Ui.Star(), Ui.Auto);
            bannerGrid.Children.Add(_bannerText.At(0, 0));
            bannerGrid.Children.Add(_showPictures.Margin(12, 0, 0, 0).At(0, 1));
            _banner = Ui.Banner("IconShield", bannerGrid);
            _banner.CornerRadius = new CornerRadius(0);
            _banner.Visibility = Visibility.Collapsed;
            Children.Add(_banner.At(0));

            _web.Visibility = Visibility.Collapsed;
            _web.DefaultBackgroundColor = System.Drawing.Color.White;
            Children.Add(_web.At(1));
            _fallback.Res(TextBox.BackgroundProperty, "SurfaceBg").Res(TextBox.ForegroundProperty, "Text");
            _fallback.Visibility = Visibility.Collapsed;
            Children.Add(_fallback.At(1));

            _empty = new Grid();
            var hint = Ui.Column(Ui.Icon("IconMail", 36).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText"),
                Ui.Subtle("Choose an email to read it here.", 13.5, false).Margin(0, 10, 0, 0));
            foreach (FrameworkElement c in hint.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Center;
            _empty.Children.Add(hint);
            Children.Add(_empty.At(1));
            this.Res(BackgroundProperty, "SurfaceBg");
            Unloaded += (s, e) => { };
        }

        private Task Ready()
        {
            if (_ready != null) return _ready;
            _ready = Init();
            return _ready;
        }

        private async Task Init()
        {
            try
            {
                _core = await Web.Prepare(_web);
                var env = await Web.Environment2();
                _core.WebResourceRequested += (s, e) => Serve(env, e);
                _core.NavigationStarting += (s, e) =>
                {
                    if (e.Uri.StartsWith(MailHtml.Base + "page/", StringComparison.OrdinalIgnoreCase)) return;
                    e.Cancel = true;
                    if (e.Uri.StartsWith(MailHtml.Base + "open/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(e.Uri.Substring((MailHtml.Base + "open/").Length), out var index)) AttachmentClicked?.Invoke(index);
                        return;
                    }
                    if (e.IsUserInitiated) Web.OpenLink(Window.GetWindow(this), e.Uri);
                };
                _core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    Web.OpenLink(Window.GetWindow(this), e.Uri);
                };
                _core.NavigationCompleted += (s, e) => Shown?.Invoke();
                _core.ContextMenuRequested += (s, e) =>
                {
                    // Keep copying and selecting; leave out "back", "reload", "save as", "inspect" and the like.
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "copy", "copyLinkLocation", "selectAll", "copyImage", "print" };
                    for (var i = e.MenuItems.Count - 1; i >= 0; i--)
                        if (!keep.Contains(e.MenuItems[i].Name)) e.MenuItems.RemoveAt(i);
                };
                UsingFallback = false;
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex is DllNotFoundException || ex is BadImageFormatException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
            {
                UsingFallback = true;
            }
        }

        private void Serve(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri = e.Request.Uri;
            if (uri.StartsWith(MailHtml.Base, StringComparison.OrdinalIgnoreCase))
            {
                var rest = uri.Substring(MailHtml.Base.Length);
                if (rest.StartsWith("page/"))
                {
                    e.Response = Web.Response(env, Encoding.UTF8.GetBytes(_page ?? string.Empty), "text/html; charset=utf-8");
                    return;
                }
                if (rest.StartsWith("part/") && _item != null && int.TryParse(rest.Substring(5), out var index) && index >= 0 && index < _item.Attachments.Count)
                {
                    var a = _item.Attachments[index];
                    try
                    {
                        e.Response = Web.Response(env, a.Data(), a.MimeType.Length > 0 ? a.MimeType : Extensions.MediaType(a.FileName));
                    }
                    catch (Exception ex) when (Core.Export.Exporter.IsReadProblem(ex))
                    {
                        e.Response = Web.Response(env, null, "text/plain", 404);
                    }
                    return;
                }
                e.Response = Web.Response(env, null, "text/plain", 404);
                return;
            }
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            if (!_allowRemote) e.Response = Web.Response(env, null, "text/plain", 403);
        }

        public void Clear()
        {
            _item = null;
            _generation++;
            _banner.Visibility = Visibility.Collapsed;
            _web.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
        }

        public async Task Show(MailItem item)
        {
            _item = item;
            _allowRemote = Settings.AlwaysShowPictures;
            var generation = ++_generation;
            await Ready();
            if (generation != _generation) return;
            Render();
        }

        public void ShowPictures()
        {
            _allowRemote = true;
            Render();
        }

        private void Render()
        {
            var item = _item;
            if (item == null) return;
            _empty.Visibility = Visibility.Collapsed;
            var remote = MailHtml.HasRemoteContent(item);
            PicturesBlocked = remote && !_allowRemote;
            if (UsingFallback)
            {
                _bannerText.Text = "Install Microsoft Edge WebView2 (free, from Microsoft) to see emails as they were designed. Showing the text only.";
                _showPictures.Visibility = Visibility.Collapsed;
                _banner.Visibility = Visibility.Visible;
                _fallback.Text = Header(item) + "\r\n\r\n" + item.Body.PlainText;
                _fallback.Visibility = Visibility.Visible;
                Shown?.Invoke();
                return;
            }
            _bannerText.Text = "Pictures from the internet are blocked, so the sender can't tell you've opened this email.";
            _showPictures.Visibility = Visibility.Visible;
            _banner.Visibility = PicturesBlocked ? Visibility.Visible : Visibility.Collapsed;
            _page = MailHtml.Page(item, new MailHtml.Options { AllowRemote = _allowRemote, Dark = ThemeManager.IsDark });
            _web.Visibility = Visibility.Visible;
            _core.Navigate(MailHtml.Base + "page/" + _generation + (_allowRemote ? "r" : string.Empty));
        }

        private static string Header(MailItem item)
        {
            var sb = new StringBuilder();
            sb.Append(item.Subject).Append("\r\n");
            sb.Append("From: ").Append(item.From.Display).Append("\r\n");
            if (item.Date.HasValue) sb.Append("Date: ").Append(MailHtml.FormatDate(item.Date.Value)).Append("\r\n");
            if (item.ToLine.Length > 0) sb.Append("To: ").Append(item.ToLine).Append("\r\n");
            if (item.CcLine.Length > 0) sb.Append("Cc: ").Append(item.CcLine).Append("\r\n");
            foreach (var a in item.VisibleAttachments) sb.Append("Attachment: ").Append(a.FileName).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>The text on the page (for the self-test). Scripts in emails never run; this is the app asking.</summary>
        public async Task<string> PageText()
        {
            if (_core == null) return _fallback.Text;
            var json = await _core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            return json;
        }

        /// <summary>A picture of the page (for the self-test's screenshots).</summary>
        public async Task Capture(Stream stream)
        {
            if (_core != null) await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }

        public void Print()
        {
            if (_core != null && _item != null) _core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }

        public void Zoom(double factor) => _web.ZoomFactor = Math.Max(0.25, Math.Min(5, factor));
    }

    /// <summary>
    /// Saves emails as PDF files by printing their page with the Edge engine in a hidden window, and joins several into
    /// one PDF (with a bookmark per email) when asked.
    /// </summary>
    public sealed class PdfMaker : IDisposable
    {
        private readonly Window _window;
        private readonly WebView2 _web = new WebView2();
        private CoreWebView2 _core;
        private CoreWebView2Environment _env;
        private MailItem _item;
        private string _page;
        private TaskCompletionSource<bool> _loaded;

        public PdfMaker()
        {
            _window = new Window
            {
                Width = 900,
                Height = 1200,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "KUDUPST PDF",
                Content = _web
            };
        }

        public static bool Available => Web.RuntimeVersion != null;

        public async Task Start()
        {
            _window.Show();
            _core = await Web.Prepare(_web);
            _env = await Web.Environment2();
            _core.WebResourceRequested += (s, e) =>
            {
                var uri = e.Request.Uri;
                if (uri.StartsWith(MailHtml.Base + "page/", StringComparison.OrdinalIgnoreCase))
                    e.Response = Web.Response(_env, Encoding.UTF8.GetBytes(_page ?? string.Empty), "text/html; charset=utf-8");
                else if (uri.StartsWith(MailHtml.Base + "part/", StringComparison.OrdinalIgnoreCase) && _item != null &&
                         int.TryParse(uri.Substring((MailHtml.Base + "part/").Length), out var index) && index >= 0 && index < _item.Attachments.Count)
                {
                    try { e.Response = Web.Response(_env, _item.Attachments[index].Data(), _item.Attachments[index].MimeType.Length > 0 ? _item.Attachments[index].MimeType : "application/octet-stream"); }
                    catch (Exception ex) when (Core.Export.Exporter.IsReadProblem(ex)) { e.Response = Web.Response(_env, null, "text/plain", 404); }
                }
                else if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    e.Response = Web.Response(_env, null, "text/plain", 403);
            };
            _core.NavigationCompleted += (s, e) => _loaded?.TrySetResult(e.IsSuccess);
        }

        private int _count;

        /// <summary>Prints one email to a PDF file.</summary>
        public async Task<bool> Save(MailItem item, string path)
        {
            _item = item;
            _page = MailHtml.Page(item, new MailHtml.Options { ForPrint = true });
            _loaded = new TaskCompletionSource<bool>();
            _core.Navigate(MailHtml.Base + "page/" + (++_count));
            var done = await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (done != _loaded.Task) return false;
            var settings = _env.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = settings.MarginBottom = 0.5;
            settings.MarginLeft = settings.MarginRight = 0.5;
            return await _core.PrintToPdfAsync(path, settings);
        }

        /// <summary>Joins PDF files into one, with a bookmark for each.</summary>
        public static void Join(IList<(string File, string Title)> parts, string path)
        {
            using (var output = new PdfSharp.Pdf.PdfDocument())
            {
                output.Info.Creator = "KUDUPST";
                foreach (var (file, title) in parts)
                {
                    using (var input = PdfSharp.Pdf.IO.PdfReader.Open(file, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    {
                        PdfSharp.Pdf.PdfPage first = null;
                        foreach (var page in input.Pages)
                        {
                            var added = output.AddPage(page);
                            first = first ?? added;
                        }
                        if (first != null) output.Outlines.Add(string.IsNullOrEmpty(title) ? "(no subject)" : title, first);
                    }
                }
                output.Save(path);
            }
        }

        public void Dispose()
        {
            try { _web.Dispose(); }
            catch (InvalidOperationException) { }
            _window.Close();
        }
    }
}
