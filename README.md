<div align="center">

<img src="logo.png" width="130" alt="Citrus logo">

# CITRUS

### A fast, tiny disk-usage explorer & cleanup tool for your terminal.
**One file. Runs on Windows, macOS *and* Linux. Nothing to install.**

![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-2ea44f?style=flat-square)
![Built with](https://img.shields.io/badge/built%20with-C%23%20%2B%20Python-4c9a70?style=flat-square)
![Size](https://img.shields.io/badge/size-~80%20KB-ff9800?style=flat-square)
![Install](https://img.shields.io/badge/install-none-26c6da?style=flat-square)
![License](https://img.shields.io/badge/license-All%20Rights%20Reserved-607d8b?style=flat-square)

</div>

---

Citrus scans a drive, shows you exactly what's eating your space with colored bars and a **treemap**, and helps you clean it up — safely. It's built to be *small* and to run on old machines: the Windows build is a **~80 KB** executable that needs nothing installed. Same single file runs the Python edition on Mac and Linux.

```text
  ██████  ████     ██████  ██  ██████  ██████  ██  ██  ██████
  ██████  ████     ██      ██    ██    ██  ██  ██  ██  ██
  ██████            ██     ██    ██    ████    ██  ██  ██████
  ██████            ██████ ██    ██    ██  ██  ██████      ██
  ██████            ██████ ██    ██    ██  ██  ██████  ██████
   green  teal              disk usage explorer · by Noah
   lime
```

## ⚡ Get it running

Grab **[`Citrus.cmd`](Citrus.cmd)** — that one file *is* the whole app on every OS.

| OS | How to run | Needs |
|----|------------|-------|
| **🪟 Windows** (XP → 11) | Double-click it | Nothing — .NET 4.0 is built into Windows |
| **🍎 macOS** | Right-click → **Open With → Terminal** | Python 3 (Mac offers to install it) |
| **🐧 Linux / BSD** | Double-click → *Run in Terminal*, or `sh Citrus.cmd` | Python 3 (already on most desktops) |

> **The trick:** Windows reads the file as a batch script (builds & runs the fast native version); macOS/Linux read the *same bytes* as a shell script (runs the Python version). Each OS only ever sees its own half. 🤯

## ✨ What it does

- **📊 See what's big** — folders sorted biggest-first, colored bars, live free-space readout, and a full **treemap** map view
- **🔎 Hunt space hogs** — biggest files anywhere · big *old* files · **duplicate finder** (matched by content) · file-type breakdown · whole-drive name search
- **🧹 Clean up** — junk cleaner (temp, caches, Recycle Bin) · Windows deep-clean (`Windows.old`, update cache) · empty-folder finder
- **🗑️ Delete safely** — everything goes to the Recycle Bin / Trash · **system files are protected** · multi-select · undo
- **🛠️ Power tools** — zip a folder · move a folder to another drive · installed-programs list · startup manager · drive health (SMART) · **PC specs sheet** · scan snapshots to compare over time
- **🖱️ Mouse *and* keyboard** — scroll, click, or shortcut keys. Press **`?`** for help, **`T`** for tools.

## 🔨 Build from source (Windows)

No SDK needed — Citrus compiles with the C# compiler that's *already inside Windows*:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The `Citrus.cmd` one-file build is just `src/poly_header.txt` + the C# (between `#<CS>`…`#</CS>`) + the Python (between `#<PY>`…`#</PY>`) stitched into one polyglot.

## 📂 Layout

```text
Citrus/
├── Citrus.cmd            → the one-file, run-anywhere build
├── build.ps1             → builds the Windows exe from src/
└── src/
    ├── StrataCmd.cs       → Windows app  (C#)
    ├── strata.py          → macOS/Linux app  (Python)
    ├── poly_header.txt    → the polyglot launcher
    └── citrus.ico         → logo
```

---

<div align="center">

**Made by Noah** · [github.com/Windows-Ctrl-Shift-B](https://github.com/Windows-Ctrl-Shift-B)

Copyright © 2026 Noah · All rights reserved

</div>
