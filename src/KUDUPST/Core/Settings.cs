using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Kudupst.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\KUDUPST";

        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>"Open with KUDUPST" on the right-click menu of .pst, .ost, .msg and .eml files.</summary>
        public static bool ShellMenu
        {
            get => GetBool("ShellMenu", false);
            set => SetBool("ShellMenu", value);
        }

        /// <summary>Double-clicking .pst, .ost, .msg and .eml files opens them in KUDUPST.</summary>
        public static bool OpenByDefault
        {
            get => GetBool("OpenByDefault", false);
            set => SetBool("OpenByDefault", value);
        }

        /// <summary>Show pictures from the internet in every email without asking.</summary>
        public static bool AlwaysShowPictures
        {
            get => GetBool("AlwaysShowPictures", false);
            set => SetBool("AlwaysShowPictures", value);
        }

        public static string LastFolder
        {
            get => Get("LastFolder", string.Empty);
            set => Set("LastFolder", value);
        }

        public static string LastExportFolder
        {
            get => Get("LastExportFolder", string.Empty);
            set => Set("LastExportFolder", value);
        }

        /// <summary>Files opened recently, newest first.</summary>
        public static List<string> Recent => Get("Recent", string.Empty).Split('\n').Where(p => p.Length > 0).ToList();

        public static void AddRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var list = Recent.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, path);
            Set("Recent", string.Join("\n", list.Take(10)));
        }

        public static void ClearRecent() => Set("Recent", string.Empty);
    }
}
