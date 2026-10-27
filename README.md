<p align="center">
  <img src="docs/logo-512.png" alt="KUDUPST logo" width="128">
</p>

<h1 align="center">KUDUPST</h1>

<p align="center">Open Outlook data files (.pst and .ost) on Windows without Outlook: read your emails, search them all in an instant, and save them as email files, PDF or a list. Free and open source.</p>

<p align="center">
  <a href="https://github.com/azeembukhariprivate/kudupst/releases/latest/download/KUDUPST-setup.exe"><img src="https://img.shields.io/badge/Download-KUDUPST%20for%20Windows-4f46e5?style=for-the-badge" alt="Download KUDUPST for Windows"></a>
  <br>
  <sub>The installer for Windows 10 and 11, always the latest version · <a href="https://github.com/azeembukhariprivate/kudupst/releases/latest">portable zip and what's new</a></sub>
</p>

---

## Features

- **Opens Outlook files without Outlook**:
  - **PST files** from every Outlook version: the old format (Outlook 97–2002, up to 2 GB) and the current one (Outlook 2003 and later).
  - **OST files**, the offline copy of an Exchange or Microsoft 365 mailbox, including the newer kind Outlook 2013 and later makes.
  - **Single emails** saved as **.msg** (dragged out of Outlook) or **.eml** (from other mail programs).
- **Read emails as they were sent**: the folder tree with the number of emails in each, a list of emails (sender, subject, date, attachments), and the email itself with its pictures, formatting and attachments. Open any email in its own window, open or save its attachments, see its internet headers, or print it.
- **Private by design**: pictures from the internet are blocked until you click *Show pictures*, so senders can't tell you've opened their email. Scripts in emails never run. Links open in your browser only after you say yes.
- **Search everything at once**: the first time a file is opened, KUDUPST builds a search index in the background (you can read emails meanwhile). After that, searching a whole mailbox for words in the subject, sender, recipients, text or attachment names is instant, even for files of tens of gigabytes. Search everything or only the subject, sender or recipients, in the whole file or one folder, and filter by date or attachments.
- **Export** the selected emails, a folder (with or without its sub-folders), the search results or the whole file:
  - **Email files (.eml)**, one per email with its attachments inside, keeping the folder structure. They open in Outlook, Thunderbird, Windows Mail and most other mail programs.
  - **PDF**, looking as the email does on screen, one file per email or all in one PDF with a bookmark for each.
  - **Attachments only**, all the attached files in a folder.
  - **A list (CSV)** for Excel: date, from, to, subject, size and attachment names.
- **Password-protected PST files** ask for their password. If you've forgotten the password of your own file, KUDUPST can open it anyway (Outlook's password doesn't lock the emails themselves).
- **Never changes your files**: everything is only read. The search index is kept in your own AppData folder, never next to the file.
- **Open files your way**: drop them on the window, use Open or the recent files list, or (optional, chosen when installing or in Settings) right-click a file → *Open with KUDUPST*, or open it with a double-click.
- Light and dark themes, following Windows.

Coming next: contacts (save as vCard or CSV) and calendars (save as .ics) in version 1.1, then finding deleted emails that are still inside a file in version 1.2.

## Download

**[Download the installer](https://github.com/azeembukhariprivate/kudupst/releases/latest/download/KUDUPST-setup.exe)** (`KUDUPST-setup.exe`, always the latest version), or get any of these from the [Releases page](https://github.com/azeembukhariprivate/kudupst/releases/latest):

- `KUDUPST-x.y.z-setup.exe` installs KUDUPST for your Windows account (no administrator rights needed to install).
- `KUDUPST-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDUPST.exe`.

Requires Windows 10 or 11. Emails are shown with Microsoft Edge WebView2, which is part of Windows 10 and 11 (on older copies of Windows 10 it can be [installed for free from Microsoft](https://go.microsoft.com/fwlink/p/?LinkId=2124703); without it KUDUPST shows the emails' text).

You can also open a file from a command line: `KUDUPST.exe mailbox.pst`.

## Privacy

KUDUPST works on your computer only. It never changes the files it opens and sends nothing anywhere. The only network requests it makes are an optional check for a newer version on GitHub (can be turned off in Settings), and pictures from the internet inside an email, only after you click *Show pictures*.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDUPST.Tests
dotnet build src/KUDUPST/KUDUPST.csproj -c Release -o out/KUDUPST
out\KUDUPST\KUDUPST.exe --self-test --samples tests\KUDUPST.Tests\samples-cache
```

KUDUPST reads Outlook files with its own code, written from Microsoft's published specifications. The tests check it against real Outlook files from other open-source projects (downloaded once and checked by SHA-256, not copied into this repository): an Enron mailbox PST, Outlook 2013 OST files and .msg emails, comparing folder trees, subjects, bodies, recipients and attachments byte for byte with what those projects expect. They also build PST files byte by byte for the formats those files don't cover (the old format, both kinds of encryption, values spread over many blocks, attached emails), and check compressed RTF bodies, HTML kept inside RTF, internet mail (.eml), writing .eml files and reading them back, the search index and every export. `--self-test` opens those files in the real window and uses it: folders, emails in the viewer, search, every export (PDF included), a password-protected file, .msg and .eml files, blocked internet pictures, the File Explorer integration and every window in both themes; it writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDUPST.iss`.

### Shared code

Some files (the look, dialogs, the update check, administrator helpers) are shared by the KUDU apps and come from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common); each starts with a comment saying so, and `.kudu-common`
lists them. You don't need kudu-common to build this app, but change those files there, not here: the build checks that
they match.

## Publishing a new version

1. Change `<Version>` in `src/KUDUPST/KUDUPST.csproj` (and `MyAppVersion` in `installer/KUDUPST.iss`).
2. Commit and push to `main`.
3. GitHub Actions tests and builds the app. When no release exists yet for that version, it creates the `v` tag and publishes the installer and portable zip on the Releases page. Pushes that keep the same version only build.

## License

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

KUDUPST is not affiliated with Microsoft. Outlook, Exchange and Windows are names of Microsoft Corporation.
