# AI Palm Web desktop client v0.1.7

This is the current WebView2-based AI Palm desktop client. The classic Windows Forms client remains available in `host-win/`.

## Architecture

- HTML, CSS, and JavaScript render Explore, Chat, Share, and Settings.
- Microsoft Edge WebView2 hosts the local interface inside the desktop window.
- The existing native host core handles the system tray, GPU detection, local-engine detection, protected settings, and inference jobs.
- Web content communicates with the native layer through WebView2 messages. Local API keys are never sent to the AI Palm platform.
- The official AI Palm coordinator URL is compiled into the native core and is not exposed as an editable interface field.
- The chat supports streamed reasoning and answers, automatic RTL/LTR direction, fenced code rendering and copying, token usage, and a persistent remaining-context meter.

## Build

From the repository root:

```powershell
.\build_webview_exe.ps1
```

The standalone executable is written to `dist-webview-v0.1.7/AIPalmWeb.exe`. The classic application can still be built independently with `build_exe.ps1`.

## Runtime requirement

Microsoft Edge WebView2 Runtime is required. It is already present on most supported Windows installations.
