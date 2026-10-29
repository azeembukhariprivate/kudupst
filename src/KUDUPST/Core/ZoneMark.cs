using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kudupst.Core
{
    /// <summary>
    /// Attachments come from other people's emails, so like Outlook, KUDUPST gives every attachment it writes Windows'
    /// "came from the internet" mark: Windows warns before running it, and Word and Excel open it in Protected View with
    /// macros blocked. Files that can run programs also ask before opening.
    /// </summary>
    public static class ZoneMark
    {
        private static readonly HashSet<string> Risky = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".mst", ".ps1", ".psm1", ".psc1", ".ps1xml", ".vb", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".wsc", ".sct", ".msh", ".hta", ".cpl", ".jar", ".lnk", ".reg", ".inf", ".application", ".appref-ms",
            ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".gadget", ".msc", ".scf", ".url", ".chm", ".diagcab",
            ".settingcontent-ms", ".library-ms", ".search-ms", ".iso", ".img", ".vhd", ".vhdx", ".one", ".onepkg", ".xll", ".xlam", ".ppam",
            ".docm", ".dotm", ".xlsm", ".xltm", ".pptm", ".potm", ".dll", ".sys"
        };

        /// <summary>The file can run a program or change Windows (the app asks before opening it).</summary>
        public static bool IsRisky(string fileName) => Risky.Contains(Path.GetExtension(fileName ?? string.Empty));

        /// <summary>Gives the file the "came from the internet" mark. Returns false where that can't be done (not NTFS, not Windows).</summary>
        public static bool Apply(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            const string text = "[ZoneTransfer]\r\nZoneId=3\r\n";
            using (var h = CreateFile(path + ":Zone.Identifier", 0x40000000, FileShare.Read, IntPtr.Zero, FileMode.Create, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                using (var s = new FileStream(h, FileAccess.Write))
                {
                    var b = Encoding.ASCII.GetBytes(text);
                    s.Write(b, 0, b.Length);
                }
            }
            return true;
        }

        /// <summary>The file has the mark (used by the self-test).</summary>
        public static bool Has(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            using (var h = CreateFile(path + ":Zone.Identifier", 0x80000000, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                using (var r = new StreamReader(new FileStream(h, FileAccess.Read)))
                    return r.ReadToEnd().Contains("ZoneId=3");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    }
}
