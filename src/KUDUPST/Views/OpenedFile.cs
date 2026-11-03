using System;
using System.Threading;
using Kudupst.Core;
using Kudupst.Core.Pst;
using Kudupst.Core.Search;

namespace Kudupst.Views
{
    /// <summary>One of the files open in the window, with its search index and its look for deleted items.</summary>
    public sealed class OpenedFile : IDisposable
    {
        public OpenedFile(MailSource source)
        {
            Source = source;
            Name = source.Title;
        }

        public MailSource Source { get; }
        public MailIndex Index { get; set; }

        /// <summary>The name shown: the file name, with its folder when another open file has the same name.</summary>
        public string Name { get; set; }

        public string Path => Source.Path;
        public bool IsDataFile => Source.IsDataFile;
        public PstStore Store => Source.Store;

        /// <summary>Its folders are shown in the folder list.</summary>
        public bool Expanded { get; set; } = true;

        /// <summary>Set while the whole file is being read for deleted items.</summary>
        public CancellationTokenSource RecoveryCancel { get; set; }

        /// <summary>What the look for deleted items says about this file.</summary>
        public string DeletedNote { get; set; } = string.Empty;

        public bool Closed { get; private set; }

        public void Dispose()
        {
            if (Closed) return;
            Closed = true;
            RecoveryCancel?.Cancel();
            Index?.Dispose();
            Source.Dispose();
        }

        public override string ToString() => Name;
    }
}
