# KUDUPST

View, search and export Outlook data files (PST, OST) and single emails (.msg, .eml) on Windows, without Outlook: emails,
contacts and contact groups (export .vcf and import CSV), the calendar (export .ics), deleted items still inside a file,
and saving any of it as a new PST file.
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDUPST.Tests` (a .NET 8 console program).
  The tests download real Outlook files from other open-source projects (pinned commits, SHA-256 checked) into
  `samples-cache`; never commit those files.
- `KUDUPST.exe --self-test --samples <samples-cache>` drives the real app on Windows (GitHub Actions runs it after the
  tests, keeping screenshots of every window as a build download). Add a self-test step for new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDUPST`. The Inno Setup installer (`installer/KUDUPST.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDUPST.Tests -c Release
dotnet build src/KUDUPST/KUDUPST.csproj -c Release -o out/KUDUPST
out\KUDUPST\KUDUPST.exe --self-test --samples tests\KUDUPST.Tests\samples-cache        (Windows only)
```

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kudupst` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDUPST/KUDUPST.csproj` (and `MyAppVersion` in `installer/KUDUPST.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDUPST-setup.exe` that the README's download button links to (`releases/latest/download/KUDUPST-setup.exe`).
   Pushes that keep the same version only build.

## Notes

- The PST/OST reader (`Core/Pst`) is the app's own code from Microsoft's published MS-PST specification: node database
  (`PstFile`: ANSI, Unicode, Outlook 2013+ 4K pages with zlib blocks, both encryptions), heaps, property and table contexts
  (`Ltp`), and the mail layer (`PstStore`). `.msg` is `Core/Mail/Msg.cs` (compound files), `.eml` is `Core/Mail/Mime.cs`.
- Files are only ever read. Never write to a user's PST/OST/MSG/EML file (saving as PST makes a new file).
- Emails are shown with WebView2 (`Views/MailView.cs`): scripts off, every request served by the app, internet content
  blocked until the user clicks "Show pictures". Keep it that way.
- The search index (`Core/Search/MailIndex.cs`) lives in `%LocalAppData%\KUDUPST\Index`, never next to the user's file.
- Password-protected PST files ask for the password; "Forgot the password?" opens the user's own file after a warning.
- Contacts and calendar fields are Outlook "named properties": `Core/Pst/NameMap.cs` reads each file's name table (PST node
  0x61, the .msg `__nameid` storage). Readers: `Core/Mail/Contact.cs`, `Core/Mail/Appointment.cs` (repeat patterns, time zones);
  writers: `Core/Export/Cards.cs` (vCard 3.0, Outlook/Google contacts CSV, iCalendar). Tasks and notes stay plain items.
- Deleted items (`Core/Pst/Recovery.cs`): each open does a quick look through the free space (the file's map of used space;
  the whole file for 4 KB-page OSTs) for old node-index pages and blocks, checked by their signature and checksum, and keeps
  complete items; "Look deeper" reads the whole file and also keeps partial items and property blocks with no index entry.
  Old copies of items that are still in a folder never count as deleted. Results are saved next to the search index
  (`found-quick.bin`, `found-deep.bin`) and shown in a made-up "Found deleted items" folder (`Recovery.FolderNid`).
  Tests build files with deleted items via `tests/KUDUPST.Tests/PstWriter.cs` (`Forget`, `Damage`, `DeletedNode`).
- Several files at once (1.3): `Views/OpenedFile.cs` is one open file (its `MailSource`, search index, look for deleted
  items). `MainWindow` keeps `_files`; the folder list has a row per data file (`FolderRowKind.File`, its folders below at
  depth 1+) and a "Single emails" row for .msg/.eml files; every `FolderRow` and `MailRow` knows its `File`, so reading,
  exporting and folder names use the right file. `_file` is the file shown. Search scopes are All open files / This file /
  This folder; `Core/OpenFiles.cs` merges the index results (`Search`), checks single emails (`Matches`), names files
  with the same name (`Names`) and keeps the set for next time (`Settings.OpenFiles`, `Save`/`Restore`). A window started
  by a double-click on one .msg/.eml doesn't restore or change the saved set (`DontRemember`).
- Save as a new PST (1.4): `Core/Pst/PstBuilder.cs` writes the file's lowest layer (header, maps of used space, blocks,
  lists of blocks, sub-node lists, the two indexes), `PstLtpWriter.cs` heaps, property lists and tables, and
  `PstStoreWriter.cs` the folders and items: the nodes every Outlook file has (laid out like the Outlook-made
  `dist-list.pst` sample), a new table of named properties (`NameMapWriter`), items from a PST/OST copied property by
  property (`AddCopy`: old one-byte text becomes Unicode, named properties get the new file's IDs, attached emails are
  copied the same way) and single .msg/.eml emails written from their `MailItem` (`Add`). `Exporter.Pst` writes to
  "name.pst.unfinished" and renames when done; `ExportItem.Store`/`Nid` say where an item can be copied from.
  Microsoft's document can't be reached from the build container and Outlook can't be run in tests, so the checks are:
  our own reader, plus `readpst` and `pffexport` by hand. The map pages for files over about 32 MB (and 2 GB) are placed
  as the specification was remembered; no Outlook-made file that big was at hand to compare.
