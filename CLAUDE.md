# KUDUPST

View, search and export Outlook data files (PST, OST) and single emails (.msg, .eml) on Windows, without Outlook.
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
- Files are only ever read. Never write to a user's PST/OST/MSG/EML file.
- Emails are shown with WebView2 (`Views/MailView.cs`): scripts off, every request served by the app, internet content
  blocked until the user clicks "Show pictures". Keep it that way.
- The search index (`Core/Search/MailIndex.cs`) lives in `%LocalAppData%\KUDUPST\Index`, never next to the user's file.
- Password-protected PST files ask for the password; "Forgot the password?" opens the user's own file after a warning.
- Plan agreed for later versions: 1.1 contacts (vCard/CSV) and calendar (.ics); 1.2 finding deleted emails inside a file.
