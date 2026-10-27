using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kudupst.Core;
using Kudupst.Core.Search;
using Kudupst.Views;

namespace Kudupst
{
    public static class Program
    {
        /// <summary>
        /// KUDUPST.exe                       open
        /// KUDUPST.exe file.pst              open a file (.pst, .ost, .msg or .eml)
        /// KUDUPST.exe --register-shell      "Open with KUDUPST" on the right-click menu (used by the installer)
        /// KUDUPST.exe --register-open       open these files with a double-click (used by the installer)
        /// KUDUPST.exe --unregister          remove both (used when uninstalling)
        /// KUDUPST.exe --self-test [--samples folder]   automated checks for the build
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            string Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }

            var app = new App();
            if (Has("--self-test")) return app.RunSelfTest(Value("--samples"));
            if (Has("--register-shell") || Has("--register-open"))
            {
                if (Has("--register-shell"))
                {
                    Settings.ShellMenu = true;
                    ShellIntegration.RegisterMenu();
                }
                if (Has("--register-open"))
                {
                    Settings.OpenByDefault = true;
                    ShellIntegration.RegisterOpen();
                }
                return 0;
            }
            if (Has("--unregister"))
            {
                ShellIntegration.RemoveMenu();
                ShellIntegration.RemoveOpen();
                return 0;
            }
            var file = args.FirstOrDefault(a => !a.StartsWith("--"));
            if (file != null)
            {
                try { file = Path.GetFullPath(file); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            }
            return app.Run(file);
        }
    }

    public sealed class App : Application
    {
        public int Run(string file)
        {
            Startup += (s, e) =>
            {
                Init();
                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                ShellIntegration.Refresh();
                if (!string.IsNullOrEmpty(file)) main.OpenFile(file);
                // Tidy up in the background: old copies of opened attachments, indexes of files that are gone.
                System.Threading.Tasks.Task.Run(() =>
                {
                    ItemActions.CleanTemp();
                    MailIndex.CleanUp();
                });
            };
            return Run();
        }

        public int RunSelfTest(string samples)
        {
            var code = 1;
            Startup += async (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = await SelfTest.Run(samples);
                Shutdown(code);
            };
            Run();
            return code;
        }

        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
