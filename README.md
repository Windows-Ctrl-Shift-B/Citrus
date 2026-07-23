# 🍋 Citrus

**A fast, tiny disk-usage explorer and cleanup tool for the terminal — one file that runs on Windows, macOS and Linux.**

Citrus scans a drive, shows you what's eating your space with colored bars and a treemap, and helps you clean it up — delete safely to the Recycle Bin/Trash, find duplicates, clear junk, and more. It's built to be *small* and to run on old machines: the Windows build is a ~80 KB executable that needs nothing installed.

```
  ██████  ████     CITRUS
  ██████  ████
  ██████          disk usage explorer
  ██████          by Noah
  ██████
```

---

## Download & run

Grab **`Citrus.cmd`** — it's one file that works on all three OSes.

| OS | How to run | Needs |
|----|------------|-------|
| **Windows** (XP SP3 → 11) | Double-click it | Nothing (.NET Framework 4.0, built into Windows) |
| **macOS** | Right-click → **Open With → Terminal** | Python 3 (Mac offers to install it) |
| **Linux** / BSD | Double-click → *Run in Terminal*, or `sh Citrus.cmd` | Python 3 (preinstalled on most desktops) |

> The same file is read by Windows as a batch script (which builds & runs the fast compiled version) and by macOS/Linux as a shell script (which runs the built-in Python version). Each OS only sees its own half.

---

## Features

- 📊 **See what's big** — folders sorted biggest-first with colored bars, a live disk-space readout, and a **treemap** map view
- 🔎 **Find space hogs** — biggest files anywhere, big *old* files, duplicate files (matched by content), file-type breakdown, whole-drive name search
- 🧹 **Clean up** — junk cleaner (temp, caches, Recycle Bin), Windows deep-clean (`Windows.old`, update cache), empty-folder finder
- 🗑️ **Delete safely** — everything goes to the Recycle Bin / Trash, with **system-file protection** so you can't nuke the OS; multi-select; undo
- 🛠️ **Extras** — zip a folder, move a folder to another drive, installed-programs list, startup manager, drive health (SMART), a **PC specs sheet** generator, and scan snapshots you can compare over time
- 🖱️ **Mouse *and* keyboard** — scroll, click, or use the shortcut keys

Press **`?`** in the app for the full key reference, or **`T`** for the tools menu.

---

## Build from source (Windows)

No SDK needed — Citrus compiles with the C# compiler that ships inside Windows:

```powershell
cd src
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize `
  /reference:Microsoft.VisualBasic.dll /reference:System.Management.dll `
  /win32icon:citrus.ico /out:Citrus.exe StrataCmd.cs
```

The `Citrus.cmd` one-file build is just `src/poly_header.txt` + the C# (between `#<CS>`…`#</CS>`) + the Python (between `#<PY>`…`#</PY>`) stitched together.

---

## Project layout

```
Citrus/
├── Citrus.cmd            ← the one-file, run-anywhere build
├── src/
│   ├── StrataCmd.cs      ← Windows app (C#)
│   ├── strata.py         ← macOS/Linux app (Python)
│   ├── poly_header.txt   ← the polyglot launcher
│   └── citrus.ico        ← logo
└── README.md
```

---

## Credits

Created by **Noah** — [github.com/Windows-Ctrl-Shift-B](https://github.com/Windows-Ctrl-Shift-B)

Copyright © 2026 Noah. All rights reserved.
