using Kudupst.Core;

namespace Kudupst.Tests
{
    public static partial class Program
    {
        /// <summary>Attachments that can run programs or carry macros are spotted, whatever the case of the ending.</summary>
        private static void TestRiskyAttachments()
        {
            foreach (var name in new[] { "invoice.EXE", "notes.one", "addin.xll", "report.docm", "setup.msi", "link.url", "run.ps1", "x.appinstaller", "disk.iso" })
                Check(ZoneMark.IsRisky(name), name + " can run programs");
            foreach (var name in new[] { "photo.jpg", "report.pdf", "letter.docx", "sheet.xlsx", "README", "" })
                Check(!ZoneMark.IsRisky(name), name + " can't run programs");
        }
    }
}
