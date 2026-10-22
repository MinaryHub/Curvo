# Microsoft Store listing — English (United States)

Paste-ready copy for Partner Center ▸ Curvo ▸ Store listing.

## Product name

Curvo

## Description

Curvo warps your projected image in real time so that straight lines stay
straight on curved walls, pillars, and arches.

Point a projector at a curved surface and everything bends: text stretches,
edges bow, and grids fall apart. Curvo fixes it before the light leaves the
projector. You drag a handful of control points until the picture lines up with
the wall in front of you, and the corrected image is what gets projected — no
re-authored content, no external scaler, no special hardware.

There are two ways to feed it.

Built-in playback — open a video file, a PDF, a PowerPoint deck, or a folder of
images directly in Curvo. There is no media player or PowerPoint window to keep
alive on screen.

Window capture — capture any app that is already running on your PC and project
that instead.

Either source is corrected in two stages: a four-corner keystone to square the
frame, then a Bezier surface — a 3x3 up to 6x6 grid of control points — to bend
it around the curve. Built-in test patterns (grid, checkerboard, rings, color
bars) make the alignment easy; hide them when you are done.

Everything stays on the GPU. Video is hardware-decoded through Media Foundation,
capture arrives through Windows Graphics Capture, and frames are never copied
down to the CPU, so a full-screen source keeps running smoothly while you go on
using your main monitor.

Save an alignment as a preset and Curvo restores it the next time it starts.
Turn on launch at logon together with automatic projection and the machine comes
up projecting on its own — useful for a lobby display, an exhibit, or a room that
gets used the same way every day.

REQUIREMENTS
- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended
- A Direct3D 11 capable GPU (Curvo falls back to the WARP software renderer)
- Converting PowerPoint files to slides requires PowerPoint on the same PC; PDF
  and image files need nothing extra

GOOD TO KNOW
- DRM-protected video (Netflix, Disney+, Prime Video, and similar) cannot be
  captured or played, and Curvo does not work around that restriction.
- Slide animations and transitions become static images when a deck is imported.
- Curvo runs entirely on your device. There are no accounts, no ads, and no
  telemetry, and nothing you open or capture is ever uploaded.

## Product features

- Real-time warping for curved walls, pillars, and arches
- Two-stage correction: four-corner keystone plus a Bezier surface
- 3x3 to 6x6 control points, with adjustable 16x16 to 128x128 tessellation
- Built-in playback of video, PDF, PowerPoint, and images — no external player
- Hardware-decoded video with audio, looping, seeking, and pause
- Or capture any running window and project that
- Fully GPU-resident Direct3D 11 pipeline — frames never touch the CPU
- Alignment test patterns: grid, checkerboard, rings, color bars, solid fields
- Brightness, contrast, and gamma correction
- Edge blending for multi-projector setups
- Presets saved as JSON, with the last session restored automatically
- Launch at logon and start projecting automatically for unattended displays
- Live editing with undo/redo and 1-pixel arrow-key nudges
- The output window is excluded from capture, so there is no feedback loop
- Works completely offline: no account, no ads, no telemetry

## What's new in this version

(Leave blank — this is the first Store submission for Curvo.)

---

## Restricted capability justification — runFullTrust

Curvo is a Win32 desktop application (.NET 8 / WPF) packaged with the Desktop
Bridge, so the package declares `EntryPoint="Windows.FullTrustApplication"` and
`runFullTrust` is required simply to launch it. It is not used to reach anything
beyond what the app needs to warp and project an image; there is no background
service, no driver, no other process, and no network access from the packaged
build.

Specifically, full trust is needed for:

1. Win32 window and monitor enumeration. The user picks a projection source and
   an output display, so the app calls EnumWindows and EnumDisplayMonitors to
   build those two lists.

2. Windows.Graphics.Capture from a Win32 handle. Capturing the selected window
   or display requires the IGraphicsCaptureItemInterop COM interface
   (CreateForWindow / CreateForMonitor) to turn an HWND or HMONITOR into a
   GraphicsCaptureItem. That interop path is only available to a full-trust
   process. Capture always targets the item the user selected in the app.

3. Direct3D 11 rendering into a borderless full-screen window. The app calls
   D3D11CreateDevice and IDXGIFactory2::CreateSwapChainForHwnd to present the
   warped image on the projector's display with a flip-model swap chain.

4. SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) on its own output window,
   so that projecting onto a display that is itself being captured does not
   create an infinite feedback loop.

5. Media Foundation hardware video decoding. Built-in video playback uses the
   IMFMediaEngine COM interfaces and MFCreateDXGIDeviceManager so decoded frames
   stay on the same D3D11 device as the renderer instead of being copied through
   the CPU.

6. Optional PowerPoint COM automation. When the user imports a .pptx/.ppt file,
   the app uses Type.GetTypeFromProgID("PowerPoint.Application") once to export
   the deck to PNG slides. This runs only on an explicit file import, only if
   PowerPoint is installed, and the app works without it (PDF and image files
   need no automation).

All file access is limited to files the user picks in a file dialog, plus the
app's own settings, presets, and slide cache in the user's AppData folder.

In the packaged build the app detects that it is running in an MSIX package and
disables its own self-updater (updates come from the Microsoft Store) and uses
the manifest's `windows.startupTask` instead of writing to the HKCU Run key.
