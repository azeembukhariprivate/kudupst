using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kudupst.Core
{
    /// <summary>
    /// Windows integration for .pst, .ost, .msg and .eml files, for the current user only (HKEY_CURRENT_USER, no
    /// administrator rights): "Open with KUDUPST" on their right-click menu, and optionally opening them with a
    /// double-click. Windows keeps a choice the user made in "Open with" themselves; KUDUPST never overrides it.
    /// </summary>
    public static class ShellIntegration
    {
        public static readonly string[] Extensions = { ".pst", ".ost", ".msg", ".eml" };

        private static string MenuKey(string ext) => $@"Software\Classes\SystemFileAssociations\{ext}\shell\KUDUPST";
        private static string ProgId(string ext) => "KUDUPST" + ext;

        private static string Describe(string ext)
        {
            switch (ext)
            {
                case ".pst": return "Outlook data file";
                case ".ost": return "Outlook offline data file";
                case ".msg": return "Outlook email";
                default: return "Email message";
            }
        }

        private static string Command => $"\"{AppInfo.ExePath}\" \"%1\"";
        private static string Icon => "\"" + AppInfo.ExePath + "\",0";

        public static void RegisterMenu()
        {
            Safe(() =>
            {
                foreach (var ext in Extensions)
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(MenuKey(ext)))
                    {
                        key.SetValue(string.Empty, "Open with KUDUPST");
                        key.SetValue("Icon", Icon);
                        using (var command = key.CreateSubKey("command")) command.SetValue(string.Empty, Command);
                    }
                }
            });
        }

        public static void RemoveMenu()
        {
            Safe(() =>
            {
                foreach (var ext in Extensions) Registry.CurrentUser.DeleteSubKeyTree(MenuKey(ext), false);
            });
        }

        public static bool IsMenuRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(MenuKey(".pst") + @"\command")) return key != null; }
                catch { return false; }
            }
        }

        public static string MenuCommand()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(MenuKey(".pst") + @"\command")) return key?.GetValue(string.Empty) as string; }
            catch { return null; }
        }

        /// <summary>Makes KUDUPST open these files on a double-click (unless the user chose another program in Windows).</summary>
        public static void RegisterOpen()
        {
            Safe(() =>
            {
                foreach (var ext in Extensions)
                {
                    using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId(ext)}"))
                    {
                        prog.SetValue(string.Empty, Describe(ext));
                        using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, Icon);
                        using (var command = prog.CreateSubKey(@"shell\open\command")) command.SetValue(string.Empty, Command);
                    }
                    using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}"))
                    {
                        using (var with = key.CreateSubKey("OpenWithProgids")) with.SetValue(ProgId(ext), new byte[0], RegistryValueKind.None);
                        key.SetValue(string.Empty, ProgId(ext));
                    }
                }
                using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\KUDUPST.exe"))
                {
                    using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue(string.Empty, Command);
                    using (var types = app.CreateSubKey("SupportedTypes"))
                        foreach (var ext in Extensions) types.SetValue(ext, string.Empty);
                }
            });
            Notify();
        }

        public static void RemoveOpen()
        {
            Safe(() =>
            {
                foreach (var ext in Extensions)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId(ext)}", false);
                    using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", true))
                    {
                        if (key == null) continue;
                        using (var with = key.OpenSubKey("OpenWithProgids", true)) with?.DeleteValue(ProgId(ext), false);
                        if (string.Equals(key.GetValue(string.Empty) as string, ProgId(ext), StringComparison.OrdinalIgnoreCase)) key.DeleteValue(string.Empty, false);
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\KUDUPST.exe", false);
            });
            Notify();
        }

        public static bool IsOpenRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(".pst")}\shell\open\command")) return key != null; }
                catch { return false; }
            }
        }

        /// <summary>Keeps Windows in step with the settings, and pointing at this copy of KUDUPST.</summary>
        public static void Refresh()
        {
            if (Settings.ShellMenu) RegisterMenu();
            else if (IsMenuRegistered) RemoveMenu();
            if (Settings.OpenByDefault) RegisterOpen();
            else if (IsOpenRegistered) RemoveOpen();
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.IO.IOException) { }
        }

        private static void Notify()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
