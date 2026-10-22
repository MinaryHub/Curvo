# Curvo — Privacy Policy

**Last updated: August 20, 2026**

Curvo is a Windows desktop utility that warps a video, a document, or a captured
window in real time so that straight lines stay straight when projected onto
curved walls, pillars, and arches. It runs entirely on your own computer.

## 1. Information we collect

minary, the developer of Curvo, does **not** collect, store, transmit, sell, or
share any personal information. Curvo has no user accounts, no sign-in, no
advertising, no analytics, and no telemetry. Nothing you open, capture, or
configure in Curvo is sent to us or to any third party.

## 2. Information Curvo processes on your device

To do its job, Curvo handles the following data locally. All of it stays on your
computer, under your control.

- **Screen and window content.** When you select a window or a display as the
  projection source, Curvo receives its frames through the Windows Graphics
  Capture API. Those frames stay in graphics memory only for as long as it takes
  to warp them and draw them on the projector output window. They are never
  written to a file and never uploaded.
- **Files you open.** Video, image, PDF, and PowerPoint files that you open for
  playback are read from your disk. PDF and PowerPoint files are converted into
  slide images that are cached in your local application data folder so they do
  not have to be converted again.
- **Settings and presets.** Your warp presets, source and output selection, and
  app options (auto-start projection, launch at logon, start minimized, and so
  on) are saved as JSON files under `%AppData%\Curvo` and `%LocalAppData%\Curvo`.

Captured screen content and the documents you open may contain personal
information. Curvo processes them only to render the projected image on your
machine; it does not analyze, retain, or transmit them.

## 3. Network use

The Microsoft Store version of Curvo makes no network connections of its own;
updates are delivered by the Microsoft Store. The one exception is the optional
support link in the app: if you click it, Windows opens that page in your default
browser, and from that point you are on GitHub's website, where GitHub's own
privacy policy applies.

## 4. Permissions

Curvo is packaged as a full-trust desktop application (`runFullTrust`). It uses
that access to capture the window or display you select, enumerate open windows
and monitors, create a Direct3D 11 output window, and read the files you open.
Curvo also declares an optional startup task, which stays disabled until you turn
on "launch at logon" inside the app.

## 5. Data retention and deletion

Because nothing leaves your device, there is no data for us to retain or delete on
our side. To remove everything Curvo has created, uninstall the app and delete the
`%AppData%\Curvo` and `%LocalAppData%\Curvo` folders.

## 6. Children's privacy

Curvo is a general-purpose utility, is not directed at children, and collects no
information from anyone, including children.

## 7. Changes to this policy

If this policy changes, the updated text will be published with the app listing
and in the project repository with a new "Last updated" date.

## 8. Contact

Questions about this policy: open an issue at
<https://github.com/MinaryHub/Curvo/issues>.
