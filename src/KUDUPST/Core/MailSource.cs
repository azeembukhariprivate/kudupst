using System;
using System.Collections.Generic;
using System.IO;
using Kudupst.Core.Mail;
using Kudupst.Core.Pst;

namespace Kudupst.Core
{
    public enum SourceKind { DataFile, Msg, Eml }

    /// <summary>An opened file: a PST/OST data file with folders, or a single .msg / .eml email.</summary>
    public sealed class MailSource : IDisposable
    {
        public string Path { get; private set; }
        public SourceKind Kind { get; private set; }
        public PstStore Store { get; private set; }
        public MailItem Single { get; private set; }

        public string Title => System.IO.Path.GetFileName(Path);
        public bool IsDataFile => Kind == SourceKind.DataFile;

        /// <summary>What a file is, from its first bytes (the extension can be wrong).</summary>
        public static SourceKind Detect(string path)
        {
            var head = new byte[8];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                fs.Read(head, 0, head.Length);
            if (head[0] == '!' && head[1] == 'B' && head[2] == 'D' && head[3] == 'N') return SourceKind.DataFile;
            if (head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0) return SourceKind.Msg;
            return SourceKind.Eml;
        }

        public static MailSource Open(string path)
        {
            var source = new MailSource { Path = path, Kind = Detect(path) };
            switch (source.Kind)
            {
                case SourceKind.DataFile:
                    source.Store = PstStore.Open(path);
                    break;
                case SourceKind.Msg:
                    source.Single = MsgReader.Read(path);
                    break;
                default:
                    var info = new FileInfo(path);
                    if (info.Length > 300L * 1024 * 1024) throw new IOException("This email file is too big to open (over 300 MB).");
                    source.Single = EmlReader.Read(path);
                    break;
            }
            return source;
        }

        public void Dispose() => Store?.Dispose();
    }
}
