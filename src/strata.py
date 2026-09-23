#!/usr/bin/env python3
"""
Citrus — a disk usage explorer for the terminal (macOS / Linux / Windows).

Keyboard-driven, no install beyond Python 3. Navigate, filter, sort, find
the biggest files, find duplicates, a junk cleaner, a help screen,
multi-select, and delete to the trash — nothing is off-limits.

Run:  python3 strata.py     (or double-click Citrus.command on a Mac)
"""

import os
import sys
import shutil
import hashlib
import heapq
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime

try:
    from send2trash import send2trash
    HAS_TRASH = True
except ImportError:
    HAS_TRASH = False

IS_WIN = os.name == "nt"

# Only the OS's own core folders are blocked from deletion; everything else
# (including Program Files / ProgramData) is fair game. Drive root blocked too.
PROTECTED = {
    "windows", "winsxs", "system32", "syswow64", "boot", "efi",
    "usr", "bin", "sbin", "etc", "lib", "lib64", "var", "sys", "proc",
    "dev", "run", "system", "library", "private",
}

# ----------------------------------------------------------------- terminal --

ANSI = True


def enable_ansi():
    global ANSI
    if not IS_WIN:
        return
    try:
        import ctypes
        k = ctypes.windll.kernel32
        h = k.GetStdHandle(-11)
        mode = ctypes.c_uint32()
        if k.GetConsoleMode(h, ctypes.byref(mode)):
            k.SetConsoleMode(h, mode.value | 0x0004)
    except Exception:
        ANSI = False


def c(code, text):
    return "\x1b[" + code + "m" + text + "\x1b[0m" if ANSI else text


def clear():
    sys.stdout.write("\x1b[H\x1b[2J\x1b[3J" if ANSI else "\n" * 40)


def home():
    sys.stdout.write("\x1b[H")


def hide_cursor(h):
    if ANSI:
        sys.stdout.write("\x1b[?25l" if h else "\x1b[?25h")
        sys.stdout.flush()


# -------------------------------------------------------------------- input --

if IS_WIN:
    import msvcrt

    _SPECIAL = {"H": "up", "P": "down", "K": "left", "M": "right",
                "I": "pgup", "Q": "pgdn", "S": "delete", "G": "home", "O": "end"}

    def read_key():
        ch = msvcrt.getwch()
        if ch in ("\x00", "\xe0"):
            return _SPECIAL.get(msvcrt.getwch(), "")
        if ch == "\r":
            return "enter"
        if ch == "\x08":
            return "backspace"
        if ch == "\x1b":
            return "esc"
        if ch == "\x03":
            raise KeyboardInterrupt
        if ch == " ":
            return "space"
        return ch

    def esc_pressed():
        hit = False
        while msvcrt.kbhit():
            if msvcrt.getwch() == "\x1b":
                hit = True
        return hit
else:
    import termios
    import tty
    import select

    def read_key():
        fd = sys.stdin.fileno()
        old = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            ch = sys.stdin.read(1)
            if ch == "\x1b":
                if select.select([sys.stdin], [], [], 0.05)[0]:
                    seq = sys.stdin.read(2)
                    return {"[A": "up", "[B": "down", "[D": "left", "[C": "right",
                            "[5": "pgup", "[6": "pgdn", "[3": "delete",
                            "[H": "home", "[F": "end"}.get(seq, "esc")
                return "esc"
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old)
        if ch in ("\r", "\n"):
            return "enter"
        if ch == "\x7f":
            return "backspace"
        if ch == "\x03":
            raise KeyboardInterrupt
        if ch == " ":
            return "space"
        return ch

    def esc_pressed():
        hit = False
        while select.select([sys.stdin], [], [], 0)[0]:
            if sys.stdin.read(1) == "\x1b":
                hit = True
        return hit


# ------------------------------------------------------------------ helpers --

def human(n):
    v = float(n)
    for u in ("B", "KB", "MB", "GB", "TB"):
        if v < 1024 or u == "TB":
            return (str(int(n)) + " B") if u == "B" else ("%.1f %s" % (v, u))
        v /= 1024


def is_drive_root(p):
    parent = os.path.dirname(p.rstrip(os.sep)) or p
    return os.path.abspath(parent) == os.path.abspath(p)


def is_protected(p):
    # Protection removed by request: Citrus will delete anything, including
    # system files. Deletes still go to the trash where possible, so most
    # things can be restored — but use with care.
    return False


def fit(s, w):
    if len(s) > w:
        return (s[:w - 1] + "…") if w > 1 else s[:w]
    return s.ljust(w)


def date_str(ts):
    try:
        return datetime.fromtimestamp(ts).strftime("%Y-%m-%d")
    except Exception:
        return ""


def cols_rows():
    sz = shutil.get_terminal_size((90, 30))
    return max(64, sz.columns), max(16, sz.lines)


# ------------------------------------------------------------------ scanner --

def dir_size(path, cancel):
    total = 0
    stack = [path]
    while stack:
        if cancel.is_set():
            return total
        d = stack.pop()
        try:
            with os.scandir(d) as it:
                for e in it:
                    try:
                        if e.is_symlink():
                            continue
                        if e.is_dir(follow_symlinks=False):
                            stack.append(e.path)
                        else:
                            total += e.stat(follow_symlinks=False).st_size
                    except OSError:
                        continue
        except OSError:
            continue
    return total


class App:
    def __init__(self):
        self.path = None
        self.entries = []
        self.view = []
        self.filter = ""
        self.sort = 0        # 0 size, 1 name, 2 newest
        self.sel = 0
        self.offset = 0
        self.status = ""
        self.cache = {}
        self.cancel = threading.Event()

    # ----- scanning -----

    def scan(self, force=False):
        clear()
        sys.stdout.write(c("1;96", " " + self.path) + "\n\n" + c("90", " Measuring...") + "\n")
        sys.stdout.flush()
        self.cancel.clear()
        results, todo = [], []
        try:
            with os.scandir(self.path) as it:
                for e in it:
                    try:
                        st = e.stat(follow_symlinks=False)
                        is_dir = e.is_dir(follow_symlinks=False)
                        row = {"name": e.name, "is_dir": is_dir, "size": 0,
                               "mtime": st.st_mtime, "mark": False}
                        results.append(row)
                        if e.is_symlink():
                            pass
                        elif is_dir:
                            full = os.path.join(self.path, e.name)
                            if not force and full in self.cache:
                                row["size"] = self.cache[full]
                            else:
                                todo.append((row, full))
                        else:
                            row["size"] = st.st_size
                    except OSError:
                        continue
        except OSError as err:
            self.entries = []
            self.status = "Can't open this folder: " + str(err)
            self.rebuild()
            return

        if todo:
            workers = min(32, max(8, (os.cpu_count() or 4) * 4))
            done = 0
            with ThreadPoolExecutor(max_workers=workers) as pool:
                futs = {pool.submit(dir_size, full, self.cancel): (row, full) for row, full in todo}
                for fut in as_completed(futs):
                    row, full = futs[fut]
                    row["size"] = fut.result()
                    if not self.cancel.is_set():
                        self.cache[full] = row["size"]
                    done += 1
                    if done % 20 == 0:
                        sys.stdout.write("\r" + c("30;103", " Measuring... %d/%d " % (done, len(todo))) + "\x1b[K")
                        sys.stdout.flush()
                        if esc_pressed():
                            self.cancel.set()

        self.entries = results
        self.status = ""
        self.filter = ""
        self.sel = 0
        self.offset = 0
        self.rebuild()

    def rebuild(self):
        self.view = [e for e in self.entries
                     if not self.filter or self.filter.lower() in e["name"].lower()]
        if self.sort == 0:
            self.view.sort(key=lambda e: e["size"], reverse=True)
        elif self.sort == 1:
            self.view.sort(key=lambda e: e["name"].lower())
        else:
            self.view.sort(key=lambda e: e["mtime"], reverse=True)
        if self.sel >= len(self.view):
            self.sel = max(0, len(self.view) - 1)

    def forget(self, path):
        path = os.path.abspath(path)
        for k in [k for k in self.cache if k == path or k.startswith(path + os.sep)]:
            del self.cache[k]

    # ----- drawing -----

    def draw(self):
        cols, rows = cols_rows()
        list_rows = max(1, rows - 5)
        out = []
        try:
            du = shutil.disk_usage(self.path)
            disk = human(du.free) + " free of " + human(du.total)
        except OSError:
            disk = ""
        title = " CITRUS - disk usage explorer"
        gap = max(1, cols - 1 - len(title) - len(disk) - 1)
        out.append(c("1;97;44", (title + " " * gap + disk + " ")[:cols - 1].ljust(cols - 1)))

        sortname = ("size", "name", "newest")[self.sort]
        if self.filter:
            out.append(c("1;97;46", fit(" Filter: " + self.filter + "   (Enter keep - Esc clear) - " + str(len(self.view)) + " match", cols - 1)))
        else:
            out.append(c("90", fit(" up/down move - Enter open - Space tick - / find - S sort - B big - U dupes - J junk - Del remove - ? help", cols - 1)))
        out.append(c("1;96", fit(" " + self.path, cols - 1)))
        out.append(c("90", "─" * (cols - 1)))

        bar_w = max(8, min(20, cols // 6))
        size_w, date_w, pct_w = 10, 10, 4
        name_w = max(8, cols - bar_w - size_w - date_w - pct_w - 12)
        total_all = sum(e["size"] for e in self.entries) or 1

        if self.sel >= self.offset + list_rows:
            self.offset = self.sel - list_rows + 1
        if self.sel < self.offset:
            self.offset = self.sel
        max_size = max((e["size"] for e in self.view), default=1) or 1

        for i in range(list_rows):
            idx = self.offset + i
            if idx >= len(self.view):
                out.append("")
                continue
            e = self.view[idx]
            frac = e["size"] / max_size
            filled = int(frac * bar_w)
            num = ("%d. " % (i + 1)) if i < 9 else "   "
            date = date_str(e["mtime"]).ljust(date_w)
            pct = ("%d%%" % int(e["size"] * 100 / total_all)).rjust(pct_w)
            mark = "•" if e["mark"] else " "
            icon = "▸ " if e["is_dir"] else "  "
            name = fit(e["name"], name_w)
            if idx == self.sel:
                bar = "█" * filled + "░" * (bar_w - filled)
                line = mark + num + icon + name + " " + date + " " + human(e["size"]).rjust(size_w) + " " + pct + " " + bar
                out.append(c("7", fit(line, cols - 1)))
            else:
                bar_col = "91" if frac >= 0.66 else "93" if frac >= 0.33 else "92"
                bar = c(bar_col, "█" * filled) + c("90", "░" * (bar_w - filled))
                namecol = "93" if e["mark"] else ("1;97" if e["is_dir"] else "37")
                line = (c("92", mark) + c("90", num) + c("93", icon)
                        + c(namecol, name) + " " + c("90", date) + " "
                        + c("1;96", human(e["size"]).rjust(size_w)) + " "
                        + c("90", pct) + " " + bar)
                out.append(line)

        out.append(c("90", "─" * (cols - 1)))
        marked = sum(1 for e in self.view if e["mark"])
        total = human(sum(e["size"] for e in self.view))
        extra = (" - " + str(marked) + " ticked") if marked else ""
        left = " " + str(len(self.view)) + " items - " + total + " - sort:" + sortname + extra
        keys = "B big  U dupes  J junk  ? help  Q quit "
        pad = max(1, cols - 1 - len(left) - len(keys))
        out.append(c("1;92", left) + " " * pad + c("90", keys))

        home()
        sys.stdout.write("\n".join(out))
        if self.status:
            col = "1;92" if self.status.startswith("✓") else "1;91"
            sys.stdout.write("\n" + c(col, " " + self.status))
        sys.stdout.write("\x1b[J" if ANSI else "")
        sys.stdout.flush()

    # ----- actions -----

    def open_sel(self):
        if not self.view:
            return
        e = self.view[self.sel]
        full = os.path.join(self.path, e["name"])
        if e["is_dir"]:
            self.path = full
            self.scan()
        else:
            self.open_external(full)

    def open_external(self, full):
        try:
            if IS_WIN:
                os.startfile(full)  # noqa
            elif sys.platform == "darwin":
                os.system('open "' + full + '" >/dev/null 2>&1 &')
            else:
                os.system('xdg-open "' + full + '" >/dev/null 2>&1 &')
        except Exception:
            pass

    def go_up(self):
        parent = os.path.dirname(self.path.rstrip(os.sep))
        if parent and parent != self.path:
            child = os.path.basename(self.path.rstrip(os.sep))
            self.path = parent
            self.scan()
            for i, e in enumerate(self.view):
                if e["is_dir"] and e["name"] == child:
                    self.sel = i
                    break

    def confirm(self, prompt):
        cols, rows = cols_rows()
        sys.stdout.write(("\x1b[%d;1H" % rows) if ANSI else "\n")
        sys.stdout.write(c("1;30;103", fit(" " + prompt + "   Enter = yes, Esc = no ", cols - 1)))
        sys.stdout.flush()
        return read_key() in ("enter", "y", "Y")

    def message(self, msg):
        cols, rows = cols_rows()
        sys.stdout.write(("\x1b[%d;1H" % rows) if ANSI else "\n")
        sys.stdout.write(c("1;97;41", fit(" " + msg + "  (any key)", cols - 1)))
        sys.stdout.flush()
        read_key()

    def trash_one(self, full):
        try:
            if HAS_TRASH:
                send2trash(full)
            elif os.path.isdir(full) and not os.path.islink(full):
                shutil.rmtree(full)
            else:
                os.remove(full)
            return True
        except OSError:
            return False

    def delete_sel(self):
        if not self.view:
            return
        marked = [e for e in self.view if e["mark"]]
        note = "to the Trash" if HAS_TRASH else "PERMANENTLY (install 'send2trash' for Trash)"
        if marked:
            tot = human(sum(e["size"] for e in marked))
            if not self.confirm("Delete %d ticked items (%s) %s?" % (len(marked), tot, note)):
                return
            blocked = failed = ok = 0
            for e in marked:
                full = os.path.join(self.path, e["name"])
                if is_protected(full):
                    blocked += 1
                    continue
                if self.trash_one(full):
                    self.forget(full)
                    ok += 1
                else:
                    failed += 1
            self.forget(self.path)
            self.scan()
            if blocked or failed:
                self.status = "! %d deleted, %d protected, %d failed" % (ok, blocked, failed)
            else:
                self.status = "✓ Deleted %d items" % ok
            return
        e = self.view[self.sel]
        full = os.path.join(self.path, e["name"])
        if is_protected(full):
            self.message("'" + e["name"] + "' is a system item - Citrus won't delete it.")
            return
        if not self.confirm("Delete '%s' (%s) %s?" % (e["name"], human(e["size"]), note)):
            return
        if self.trash_one(full):
            self.forget(full)
            self.forget(self.path)
            self.scan()
            self.status = "✓ Deleted 1 item"
        else:
            self.message("Delete failed (file may be in use).")

    def filter_input(self):
        while True:
            self.rebuild()
            self.draw()
            k = read_key()
            if k == "enter":
                return
            if k == "esc":
                self.filter = ""
                self.rebuild()
                return
            if k == "backspace":
                self.filter = self.filter[:-1]
            elif len(k) == 1 and k.isprintable():
                self.filter += k
                self.sel = 0

    def open_in_manager(self):
        e = self.view[self.sel] if self.view else None
        if e and e["is_dir"]:
            self.open_external(os.path.join(self.path, e["name"]))
        else:
            self.open_external(self.path)

    # ----- collect / biggest / dupes -----

    def collect(self, min_size, progress, limit=0):
        files = []
        dirs = 0
        found = 0
        stack = [self.path]
        while stack:
            if self.cancel.is_set():
                break
            d = stack.pop()
            dirs += 1
            try:
                with os.scandir(d) as it:
                    for e in it:
                        try:
                            if e.is_symlink():
                                continue
                            if e.is_dir(follow_symlinks=False):
                                stack.append(e.path)
                            else:
                                sz = e.stat(follow_symlinks=False).st_size
                                if sz >= min_size:
                                    found += 1
                                    if limit <= 0:
                                        files.append((e.path, sz))
                                    else:
                                        # Keep earlier matches on ties, as the stable sort did.
                                        item = (sz, -found, e.path)
                                        if len(files) < limit:
                                            heapq.heappush(files, item)
                                        elif item > files[0]:
                                            heapq.heapreplace(files, item)
                        except OSError:
                            continue
            except OSError:
                continue
            if dirs % 40 == 0:
                progress(dirs, found)
        if limit > 0:
            return [(path, sz) for sz, _, path in sorted(files, reverse=True)]
        return files

    def list_screen(self, title, rows):
        sel = off = 0
        while True:
            cols, r = cols_rows()
            lr = max(1, r - 4)
            out = [c("1;97;44", fit(" " + title, cols - 1)),
                   c("90", fit(" Enter open location - Delete removes - Esc back", cols - 1)),
                   c("90", "─" * (cols - 1))]
            if not rows:
                out.append(c("90", " Nothing found."))
            if sel >= off + lr:
                off = sel - lr + 1
            if sel < off:
                off = sel
            size_w = 11
            for i in range(lr):
                idx = off + i
                if idx >= len(rows):
                    out.append("")
                    continue
                path, sz = rows[idx]
                pw = max(10, cols - 2 - size_w - 2)
                p = ("…" + path[-(pw - 1):]) if len(path) > pw else path.ljust(pw)
                if idx == sel:
                    out.append(c("7", fit(" " + human(sz).rjust(size_w) + "  " + p, cols - 1)))
                else:
                    out.append(c("1;96", " " + human(sz).rjust(size_w) + "  ") + c("37", p))
            tot = human(sum(s for _, s in rows))
            out.append(c("1;92", fit(" " + str(len(rows)) + " files - " + tot + " total   (Esc back)", cols - 1)))
            home()
            sys.stdout.write("\n".join(out) + ("\x1b[J" if ANSI else ""))
            sys.stdout.flush()
            k = read_key()
            if k in ("esc", "q", "Q"):
                return
            elif k == "up":
                sel = max(0, sel - 1)
            elif k == "down":
                sel = min(len(rows) - 1, sel + 1) if rows else 0
            elif k == "pgup":
                sel = max(0, sel - 15)
            elif k == "pgdn":
                sel = min(len(rows) - 1, sel + 15) if rows else 0
            elif k in ("enter", "o", "O") and rows:
                self.open_external(os.path.dirname(rows[sel][0]))
            elif k == "delete" and rows:
                path = rows[sel][0]
                if is_protected(path):
                    self.message("System item - won't delete.")
                elif self.confirm("Delete '%s' (%s)?" % (os.path.basename(path), human(rows[sel][1]))):
                    if self.trash_one(path):
                        rows.pop(sel)
                        sel = min(sel, max(0, len(rows) - 1))

    def biggest(self):
        clear()
        sys.stdout.write(c("1;97;44", " CITRUS - biggest files") + "\n\n")
        sys.stdout.flush()
        self.cancel.clear()

        def prog(d, f):
            sys.stdout.write("\r" + c("30;103", " Scanning... %d folders, %d files " % (d, f)) + "\x1b[K")
            sys.stdout.flush()
            if esc_pressed():
                self.cancel.set()
        files = self.collect(1024 * 1024, prog, limit=500)
        self.list_screen("CITRUS - biggest files (top 500, >= 1 MB)", files)

    def duplicates(self):
        clear()
        sys.stdout.write(c("1;97;44", " CITRUS - duplicate files") + "\n\n")
        sys.stdout.flush()
        self.cancel.clear()

        def prog(d, f):
            sys.stdout.write("\r" + c("30;103", " Finding candidates... %d folders, %d files " % (d, f)) + "\x1b[K")
            sys.stdout.flush()
            if esc_pressed():
                self.cancel.set()
        files = self.collect(1024 * 1024, prog)
        by_size = {}
        for path, sz in files:
            by_size.setdefault(sz, []).append(path)
        dup_rows = []
        wasted = 0
        cand = [(sz, ps) for sz, ps in by_size.items() if len(ps) > 1]
        total = sum(len(ps) for _, ps in cand)
        done = 0
        for sz, paths in cand:
            if self.cancel.is_set():
                break
            by_hash = {}
            for p in paths:
                done += 1
                if done % 8 == 0:
                    sys.stdout.write("\r" + c("30;103", " Comparing... %d/%d " % (done, total)) + "\x1b[K")
                    sys.stdout.flush()
                    if esc_pressed():
                        self.cancel.set()
                        break
                h = self.hash_file(p)
                if h:
                    by_hash.setdefault(h, []).append(p)
            for group in by_hash.values():
                if len(group) > 1:
                    for extra in group[1:]:
                        dup_rows.append((extra, sz))
                        wasted += sz
        dup_rows.sort(key=lambda kv: kv[1], reverse=True)
        self.list_screen("CITRUS - duplicates (" + human(wasted) + " reclaimable, >= 1 MB)", dup_rows)

    @staticmethod
    def hash_file(path):
        try:
            h = hashlib.md5()
            with open(path, "rb") as f:
                for chunk in iter(lambda: f.read(65536), b""):
                    h.update(chunk)
            return h.hexdigest()
        except OSError:
            return None

    # ----- junk -----

    def junk(self):
        items = self.build_junk()
        while True:
            cols, rows = cols_rows()
            out = [c("1;97;45", fit(" CITRUS - junk cleaner", cols - 1)),
                   c("90", " Press a number to clear it - A = clear all - Esc = back"),
                   c("90", "─" * (cols - 1))]
            tot = 0
            for i, (name, path, size) in enumerate(items, 1):
                col = "93" if size > 0 else "90"
                out.append("   " + c("1;96", str(i)) + "  " + c("1;97", fit(name, 30)) + c(col, human(size).rjust(12)))
                tot += size
            out.append("")
            out.append("   Reclaimable total: " + c("1;92", human(tot)))
            home()
            sys.stdout.write("\n".join(out) + ("\x1b[J" if ANSI else ""))
            sys.stdout.flush()
            k = read_key()
            if k in ("esc", "q", "Q"):
                return
            if k in ("a", "A"):
                if self.confirm("Clear ALL junk (%s)? Temp/cache files removed permanently." % human(tot)):
                    for _, path, _ in items:
                        self.clean_dir(path)
                    self.forget(self.path)
                    items = self.build_junk()
            elif k.isdigit() and 1 <= int(k) <= len(items):
                name, path, size = items[int(k) - 1]
                if self.confirm("Clear %s (%s) permanently?" % (name, human(size))):
                    self.clean_dir(path)
                    items = self.build_junk()

    def build_junk(self):
        home_dir = os.path.expanduser("~")
        import tempfile
        cands = [("Temporary files", tempfile.gettempdir())]
        if IS_WIN:
            cands.append(("Windows temp", os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "Temp")))
            la = os.environ.get("LOCALAPPDATA", "")
            cands.append(("Chrome cache", os.path.join(la, "Google", "Chrome", "User Data", "Default", "Cache")))
            cands.append(("Edge cache", os.path.join(la, "Microsoft", "Edge", "User Data", "Default", "Cache")))
        elif sys.platform == "darwin":
            cands.append(("User caches", os.path.join(home_dir, "Library", "Caches")))
            cands.append(("Trash", os.path.join(home_dir, ".Trash")))
        else:
            cands.append(("User cache (~/.cache)", os.path.join(home_dir, ".cache")))
            cands.append(("Trash", os.path.join(home_dir, ".local", "share", "Trash")))
        out = []
        cancel = threading.Event()
        for name, path in cands:
            size = dir_size(path, cancel) if os.path.isdir(path) else 0
            out.append((name, path, size))
        return out

    def clean_dir(self, path):
        if not os.path.isdir(path):
            return
        try:
            for entry in os.scandir(path):
                try:
                    if entry.is_dir(follow_symlinks=False) and not entry.is_symlink():
                        shutil.rmtree(entry.path, ignore_errors=True)
                    else:
                        os.remove(entry.path)
                except OSError:
                    continue
        except OSError:
            pass

    # ----- help -----

    def help_screen(self):
        clear()
        rows = [
            ("up/down PgUp PgDn", "move the selection"),
            ("Enter / right", "open folder, or open file in its app"),
            ("Esc / left / Bksp", "go up a folder"),
            ("1 - 9", "open that numbered row"),
            ("Space", "tick / untick a row (multi-select)"),
            ("Delete", "delete selected - or all ticked - to the Trash"),
            ("O", "open selected in the file manager"),
            ("/", "filter this folder by name"),
            ("S", "cycle sort: size / name / newest"),
            ("B", "find the biggest files anywhere below here"),
            ("U", "find duplicate files below here"),
            ("J", "junk cleaner (temp, caches, Trash)"),
            ("T", "tools (treemap, file types, specs, zip, …)"),
            ("R  C  Q", "rescan - change drive - quit"),
            ("?", "this help"),
        ]
        print(c("1;97;44", " CITRUS - keyboard reference"))
        print()
        for keys, desc in rows:
            print("   " + c("1;96", keys.ljust(20)) + c("37", desc))
        print()
        print(c("90", "   Press any key to go back."))
        read_key()

    # ----- tools (cross-platform) -----

    def text_input(self, prompt):
        s = ""
        while True:
            cols, rows = cols_rows()
            sys.stdout.write(("\x1b[%d;1H" % rows) if ANSI else "\n")
            sys.stdout.write(c("1;97;46", fit(" " + prompt + " " + s + "▏  (Enter go - Esc cancel)", cols - 1)))
            sys.stdout.flush()
            k = read_key()
            if k == "enter":
                return s
            if k == "esc":
                return None
            if k == "backspace":
                s = s[:-1]
            elif len(k) == 1 and k.isprintable():
                s += k

    def tools_menu(self):
        tools = [
            ("a", "Map view (treemap)", self.t_treemap),
            ("b", "File-type breakdown", self.t_filetypes),
            ("c", "Big old files", self.t_oldfiles),
            ("d", "Search whole drive", self.t_search),
            ("e", "Empty folders", self.t_empty),
            ("f", "Zip selected folder", self.t_zip),
            ("g", "Move to another drive/path", self.t_move),
            ("h", "PC specs sheet", self.t_specs),
            ("i", "Export report", self.t_export),
            ("j", "Save snapshot", self.t_savesnap),
            ("k", "Compare to snapshot", self.t_compare),
        ]
        sel = 0
        while True:
            cols, rows = cols_rows()
            clear()
            print(c("1;97;45", fit(" CITRUS - tools", cols - 1)))
            print(c("90", " Press a letter, or arrows + Enter - Esc back"))
            print()
            for i, (key, label, fn) in enumerate(tools):
                if i == sel:
                    print(c("7", " " + fit("  " + key + "  " + label, cols - 2)))
                else:
                    print("  " + c("1;96", key) + "  " + c("37", label))
            k = read_key()
            if k in ("esc", "q", "Q"):
                return
            elif k == "up":
                sel = max(0, sel - 1)
            elif k == "down":
                sel = min(len(tools) - 1, sel + 1)
            elif k == "enter":
                tools[sel][2]()
                return
            else:
                for key, label, fn in tools:
                    if k == key:
                        fn()
                        return

    def _walk(self, progress, keep):
        """Walk self.path; call keep(path, size, mtime, is_dir) for each entry."""
        stack = [self.path]
        dirs = 0
        while stack:
            if self.cancel.is_set():
                break
            d = stack.pop()
            dirs += 1
            try:
                with os.scandir(d) as it:
                    for en in it:
                        try:
                            if en.is_symlink():
                                continue
                            isd = en.is_dir(follow_symlinks=False)
                            if isd:
                                stack.append(en.path)
                                keep(en.path, 0, 0, True)
                            else:
                                st = en.stat(follow_symlinks=False)
                                keep(en.path, st.st_size, st.st_mtime, False)
                        except OSError:
                            continue
            except OSError:
                continue
            if dirs % 40 == 0:
                progress(dirs)

    def t_treemap(self):
        items = sorted([e for e in self.view if e["size"] > 0], key=lambda e: e["size"], reverse=True)
        cols, rows = cols_rows()
        clear()
        print(c("1;97;44", fit(" CITRUS - map of " + self.path, cols - 1)))
        total = sum(e["size"] for e in items) or 1
        avail = max(1, rows - 3)
        palette = ["42", "46", "43", "45", "44", "41", "102", "106"]
        used = 0
        for i, e in enumerate(items):
            h = max(1, int(round(e["size"] / total * avail)))
            if used + h > avail:
                h = avail - used
            if h <= 0:
                break
            bg = palette[i % len(palette)]
            fg = "30" if bg in ("46", "43", "102", "106") else "97"
            label = "  " + e["name"] + "  " + human(e["size"])
            for r in range(h):
                print(c(bg + ";" + fg, fit(label if r == 0 else "", cols - 1)))
            used += h
            if used >= avail:
                break
        sys.stdout.write(c("90", "   biggest = biggest block - press any key"))
        sys.stdout.flush()
        read_key()

    def t_filetypes(self):
        cat_ext = {}
        for cat, exts in (("Video", "mp4 mkv avi mov wmv flv webm m4v"),
                          ("Photos", "jpg jpeg png gif bmp heic tiff webp raw svg"),
                          ("Audio", "mp3 wav flac aac ogg m4a wma"),
                          ("Installers", "exe msi dmg pkg deb rpm appimage apk"),
                          ("Archives", "zip rar 7z tar gz iso bz2 xz"),
                          ("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf csv"),
                          ("Code/Data", "py js ts html css json xml c h cpp go rs sql db"),
                          ("Disk images", "vhd vhdx vmdk img dmg")):
            for x in exts.split():
                cat_ext[x] = cat
        clear()
        cols, rows = cols_rows()
        print(c("1;97;44", fit(" CITRUS - file types under " + self.path, cols - 1)))
        print()
        self.cancel.clear()
        sizes, counts = {}, {}

        def prog(d):
            sys.stdout.write("\r" + c("30;103", " Scanning... %d folders " % d) + "\x1b[K")
            sys.stdout.flush()
            if esc_pressed():
                self.cancel.set()

        def keep(path, sz, mt, isd):
            if not isd:
                ext = os.path.splitext(path)[1].lower().lstrip(".")
                cat = cat_ext.get(ext, "Other")
                sizes[cat] = sizes.get(cat, 0) + sz
                counts[cat] = counts.get(cat, 0) + 1
        self._walk(prog, keep)
        order = sorted(sizes.items(), key=lambda kv: kv[1], reverse=True)
        grand = sum(sizes.values()) or 1
        clear()
        print(c("1;97;44", fit(" CITRUS - file types under " + self.path, cols - 1)))
        print()
        bw = max(10, cols - 45)
        for name, sz in order:
            filled = int(sz / grand * bw)
            print("   " + c("1;97", fit(name, 14)) + c("1;96", human(sz).rjust(10)) + "  "
                  + c("92", "█" * filled) + c("90", "░" * (bw - filled))
                  + c("90", "  %d%% (%d)" % (int(sz / grand * 100), counts.get(name, 0))))
        print()
        print(c("1;92", "   Total " + human(grand) + "   -   press any key"))
        read_key()

    def t_oldfiles(self):
        import time
        clear()
        print(c("1;97;44", " CITRUS - big old files (>=1MB, 1+ year untouched)"))
        print()
        self.cancel.clear()
        cutoff = time.time() - 365 * 86400
        files = []

        def prog(d):
            sys.stdout.write("\r" + c("30;103", " Scanning... %d folders, %d found " % (d, len(files))) + "\x1b[K")
            sys.stdout.flush()
            if esc_pressed():
                self.cancel.set()

        def keep(path, sz, mt, isd):
            if not isd and sz >= 1048576 and mt and mt < cutoff:
                files.append((path, sz))
        self._walk(prog, keep)
        files.sort(key=lambda kv: kv[1], reverse=True)
        self.list_screen("CITRUS - big old files (>=1MB, 1+ year)", files[:500])

    def t_search(self):
        term = self.text_input("Search whole drive for:")
        if not term:
            return
        root = os.path.splitdrive(self.path)[0] + os.sep if IS_WIN else "/"
        clear()
        print(c("1;97;44", " CITRUS - searching " + root + " for '" + term + "'"))
        print()
        self.cancel.clear()
        hits = []
        stack = [root]
        dirs = 0
        while stack:
            if self.cancel.is_set():
                break
            d = stack.pop()
            dirs += 1
            try:
                with os.scandir(d) as it:
                    for en in it:
                        try:
                            if en.is_symlink():
                                continue
                            isd = en.is_dir(follow_symlinks=False)
                            if isd:
                                stack.append(en.path)
                            if term.lower() in en.name.lower() and len(hits) < 1000:
                                sz = 0 if isd else en.stat(follow_symlinks=False).st_size
                                hits.append((en.path, sz))
                        except OSError:
                            continue
            except OSError:
                continue
            if dirs % 40 == 0:
                sys.stdout.write("\r" + c("30;103", " Searching... %d folders, %d hits " % (dirs, len(hits))) + "\x1b[K")
                sys.stdout.flush()
                if esc_pressed():
                    self.cancel.set()
        hits.sort(key=lambda kv: kv[1], reverse=True)
        self.list_screen("CITRUS - '" + term + "' matches", hits)

    def t_empty(self):
        clear()
        print(c("1;97;44", " CITRUS - empty folders under " + self.path))
        print(c("90", "   Scanning..."))
        self.cancel.clear()
        empties = []

        def rec(dirp):
            has = False
            subs = []
            try:
                with os.scandir(dirp) as it:
                    for en in it:
                        try:
                            if en.is_symlink():
                                has = True
                                continue
                            if en.is_dir(follow_symlinks=False):
                                subs.append(en.path)
                            else:
                                has = True
                        except OSError:
                            has = True
            except OSError:
                return False
            subres = []
            all_empty = True
            for s in subs:
                r = rec(s)
                subres.append((s, r))
                if not r:
                    all_empty = False
            if not has and all_empty:
                return True
            for s, r in subres:
                if r:
                    empties.append((s, 0))
            return False
        if rec(self.path):
            empties.append((self.path, 0))
        self.list_screen("CITRUS - empty folders (Delete to remove)", empties)

    def t_zip(self):
        if not self.view:
            return
        e = self.view[self.sel]
        src = os.path.join(self.path, e["name"])
        if not self.confirm("Zip '" + e["name"] + "'?"):
            return
        try:
            if e["is_dir"]:
                shutil.make_archive(src, "zip", src)
            else:
                import zipfile
                with zipfile.ZipFile(src + ".zip", "w", zipfile.ZIP_DEFLATED) as z:
                    z.write(src, e["name"])
            self.forget(self.path)
            self.scan()
            self.status = "✓ Created " + e["name"] + ".zip"
        except Exception as ex:
            self.status = "! Zip failed: " + str(ex)

    def t_move(self):
        if not self.view or not self.view[self.sel]["is_dir"]:
            self.status = "! Pick a folder first (Move only works on folders)"
            return
        src = os.path.join(self.path, self.view[self.sel]["name"])
        if is_protected(src):
            self.status = "! That folder is protected"
            return
        dest_root = self.text_input("Move to which path? (e.g. /Volumes/USB or /mnt/d)")
        if not dest_root:
            return
        dest = os.path.join(dest_root, "CitrusMoved", self.view[self.sel]["name"])
        if not self.confirm("Move to " + dest + " and leave a link behind?"):
            return
        try:
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.move(src, dest)
            try:
                os.symlink(dest, src)
            except Exception:
                pass
            self.forget(self.path)
            self.scan()
            self.status = "✓ Moved to " + dest + " (link left behind)"
        except Exception as ex:
            self.status = "! Move failed: " + str(ex)

    def t_specs(self):
        import platform
        lines = ["PC specifications", "Generated by Citrus on " + str(datetime.now()), "-" * 50]
        lines.append("OS:      " + platform.platform())
        lines.append("Machine: " + platform.machine())
        cpu = platform.processor()
        if not cpu:
            try:
                if sys.platform == "darwin":
                    import subprocess
                    cpu = subprocess.check_output(["sysctl", "-n", "machdep.cpu.brand_string"]).decode().strip()
                elif not IS_WIN:
                    for l in open("/proc/cpuinfo"):
                        if l.startswith("model name"):
                            cpu = l.split(":", 1)[1].strip()
                            break
            except Exception:
                pass
        lines.append("CPU:     " + (cpu or "?"))
        ram = 0
        try:
            if hasattr(os, "sysconf") and "SC_PHYS_PAGES" in os.sysconf_names:
                ram = os.sysconf("SC_PHYS_PAGES") * os.sysconf("SC_PAGE_SIZE")
        except Exception:
            pass
        lines.append("RAM:     " + (human(ram) if ram else "?"))
        try:
            for d in self.list_drives():
                du = shutil.disk_usage(d)
                lines.append("Disk:    " + d + " " + human(du.total))
        except Exception:
            pass
        desktop = os.path.join(os.path.expanduser("~"), "Desktop")
        if not os.path.isdir(desktop):
            desktop = os.path.expanduser("~")
        fn = os.path.join(desktop, "PC-specs-" + datetime.now().strftime("%Y-%m-%d_%H%M") + ".txt")
        try:
            with open(fn, "w") as f:
                f.write("\n".join(lines))
            self.status = "✓ Specs saved: " + fn
        except Exception as e:
            self.status = "! Couldn't save specs: " + str(e)

    def t_export(self):
        try:
            desktop = os.path.join(os.path.expanduser("~"), "Desktop")
            if not os.path.isdir(desktop):
                desktop = os.path.expanduser("~")
            fn = os.path.join(desktop, "Citrus-report-" + datetime.now().strftime("%Y-%m-%d_%H%M") + ".txt")
            lines = ["Citrus disk report", "Folder: " + self.path, "Generated: " + str(datetime.now()), "-" * 60]
            for e in sorted(self.entries, key=lambda e: e["size"], reverse=True):
                lines.append(human(e["size"]).rjust(12) + "  " + ("[folder] " if e["is_dir"] else "         ") + e["name"])
            lines.append("-" * 60)
            lines.append(str(len(self.entries)) + " items")
            with open(fn, "w") as f:
                f.write("\n".join(lines))
            self.status = "✓ Report saved: " + fn
        except Exception as e:
            self.status = "! Export failed: " + str(e)

    def snap_path(self):
        d = os.path.join(os.path.expanduser("~"), ".citrus")
        try:
            os.makedirs(d, exist_ok=True)
        except Exception:
            pass
        return os.path.join(d, "snapshot.txt")

    def t_savesnap(self):
        try:
            with open(self.snap_path(), "w") as f:
                f.write(self.path + "\n")
                for e in self.entries:
                    f.write(str(e["size"]) + "\t" + e["name"] + "\n")
            self.status = "✓ Snapshot saved - run Compare later to see changes"
        except Exception as e:
            self.status = "! Snapshot failed: " + str(e)

    def t_compare(self):
        p = self.snap_path()
        if not os.path.exists(p):
            self.status = "! No snapshot yet - use 'Save snapshot' first"
            return
        old = {}
        oldpath = ""
        try:
            with open(p) as f:
                lines = f.read().splitlines()
            if lines:
                oldpath = lines[0]
            for l in lines[1:]:
                if "\t" in l:
                    sz, nm = l.split("\t", 1)
                    try:
                        old[nm] = int(sz)
                    except ValueError:
                        pass
        except Exception:
            pass
        clear()
        cols, rows = cols_rows()
        print(c("1;97;44", fit(" CITRUS - changes vs snapshot of " + oldpath, cols - 1)))
        print()
        now = {}
        for e in self.entries:
            now[e["name"]] = e["size"]
        shown = 0
        changed = False
        for nm, sz in now.items():
            b = old.get(nm, 0)
            delta = sz - b
            if b == 0:
                print("   " + c("92", "NEW    ") + fit(nm, cols - 25) + c("92", "  +" + human(sz)))
                changed = True
            elif delta != 0:
                col = "92" if delta > 0 else "91"
                print("   " + c(col, "GREW   " if delta > 0 else "SHRANK ") + fit(nm, cols - 25)
                      + c(col, ("  +" if delta > 0 else "  -") + human(abs(delta))))
                changed = True
            shown += 1
            if shown > rows - 6:
                break
        for nm, sz in old.items():
            if nm not in now:
                print("   " + c("91", "GONE   ") + fit(nm, cols - 25) + c("91", "  -" + human(sz)))
                changed = True
        if not changed:
            print(c("90", "   Nothing changed since the snapshot."))
        print()
        print(c("90", "   Press any key to go back."))
        read_key()

    # ----- logo / drive picker -----

    @staticmethod
    def draw_logo(indent):
        # citrus-slice logo, drawn with half-block chars for a smooth circle
        slice_art = [
            "....................",
            "......rrrrrrrr......",
            ".....rrffffffrr.....",
            "....rrffffffffrr....",
            "...rrfwffffffwfrr...",
            "...rfffwffffwfffr...",
            "...rffffwffwffffr...",
            "...rfffffwwfffffr...",
            "...rfffffwwfffffr...",
            "...rffffwffwffffr...",
            "...rfffwffffwfffr...",
            "...rrfwffffffwfrr...",
            "....rrffffffffrr....",
            ".....rrffffffrr.....",
            "......rrrrrrrr......",
            "....................",
        ]
        code = {"r": "22", "f": "149", "w": "231"}  # rind / flesh / segments
        for cr in range(len(slice_art) // 2):
            line = " " * indent
            for x in range(len(slice_art[0])):
                t = slice_art[2 * cr][x]
                b = slice_art[2 * cr + 1][x]
                tf = code.get(t)
                bf = code.get(b)
                if not ANSI:
                    line += "  " if (not tf and not bf) else "██"
                elif not tf and not bf:
                    line += " "
                elif tf and bf and t == b:
                    line += "\x1b[38;5;" + tf + "m█\x1b[0m"
                elif tf and bf:
                    line += "\x1b[38;5;" + tf + "m\x1b[48;5;" + bf + "m▀\x1b[0m"
                elif tf:
                    line += "\x1b[38;5;" + tf + "m▀\x1b[0m"
                else:
                    line += "\x1b[38;5;" + bf + "m▄\x1b[0m"
            print(line)
        print()
        font = {"C": ["###", "#..", "#..", "#..", "###"],
                "I": ["###", ".#.", ".#.", ".#.", "###"],
                "T": ["###", ".#.", ".#.", ".#.", ".#."],
                "R": ["##.", "#.#", "##.", "#.#", "#.#"],
                "U": ["#.#", "#.#", "#.#", "#.#", "###"],
                "S": ["###", "#..", "###", "..#", "###"]}
        for r in range(5):
            line = " " * indent
            for ch in "CITRUS":
                for p in font[ch][r]:
                    line += c("97", "██") if p == "#" else "  "
                line += "  "
            print(line)

    def choose_drive(self):
        drives = self.list_drives()
        clear()
        print()
        self.draw_logo(4)
        print(c("90", "      disk usage explorer"))
        print(c("90", "      by Noah - github.com/Windows-Ctrl-Shift-B"))
        print()
        if len(drives) == 1:
            return drives[0]
        for i, d in enumerate(drives, 1):
            try:
                du = shutil.disk_usage(d)
                info = human(du.free) + " free of " + human(du.total)
                used = 1 - du.free / du.total
                col = "91" if used >= 0.9 else "93" if used >= 0.7 else "92"
                bar = c(col, "█" * int(used * 20)) + c("90", "░" * (20 - int(used * 20)))
            except OSError:
                info, bar = "unreadable", ""
            print("   " + c("1;96", str(i)) + "  " + c("1;97", d.ljust(6)) + " " + bar + "  " + c("37", info))
        print()
        print(c("90", "   Press a number, or Q to quit"))
        while True:
            k = read_key()
            if k in ("q", "Q", "esc"):
                return None
            if k.isdigit() and 1 <= int(k) <= len(drives):
                return drives[int(k) - 1]

    @staticmethod
    def list_drives():
        if IS_WIN:
            import string
            return [(ltr + ":\\") for ltr in string.ascii_uppercase if os.path.exists(ltr + ":\\")]
        return ["/"]

    # ----- main loop -----

    def run(self):
        drive = self.choose_drive()
        if not drive:
            return
        self.path = os.path.abspath(drive)
        self.scan()
        while True:
            self.draw()
            k = read_key()
            if k in ("q", "Q"):
                return
            elif k == "esc":
                self.go_up()
            elif k == "up":
                self.sel = max(0, self.sel - 1)
            elif k == "down":
                self.sel = min(len(self.view) - 1, self.sel + 1) if self.view else 0
            elif k == "pgup":
                self.sel = max(0, self.sel - 15)
            elif k == "pgdn":
                self.sel = min(len(self.view) - 1, self.sel + 15) if self.view else 0
            elif k == "home":
                self.sel = 0
            elif k == "end":
                self.sel = max(0, len(self.view) - 1)
            elif k in ("enter", "right"):
                self.open_sel()
            elif k in ("left", "backspace"):
                self.go_up()
            elif k == "space":
                if self.view:
                    self.view[self.sel]["mark"] = not self.view[self.sel]["mark"]
                    if self.sel < len(self.view) - 1:
                        self.sel += 1
            elif k in ("delete", "d", "D"):
                self.delete_sel()
            elif k in ("r", "R"):
                self.forget(self.path)
                self.scan()
            elif k in ("s", "S"):
                self.sort = (self.sort + 1) % 3
                self.rebuild()
                self.sel = 0
            elif k in ("o", "O"):
                self.open_in_manager()
            elif k == "/":
                self.filter_input()
            elif k in ("b", "B"):
                self.biggest()
                clear()
            elif k in ("u", "U"):
                self.duplicates()
                clear()
            elif k in ("j", "J"):
                self.junk()
                clear()
            elif k in ("t", "T"):
                self.tools_menu()
                clear()
            elif k == "?":
                self.help_screen()
                clear()
            elif k in ("c", "C"):
                drive = self.choose_drive()
                if drive:
                    self.path = os.path.abspath(drive)
                    self.scan()
                else:
                    clear()
            elif k.isdigit() and k != "0":
                i = self.offset + (int(k) - 1)
                if i < len(self.view) and (i - self.offset) < max(1, cols_rows()[1] - 5):
                    self.sel = i
                    self.open_sel()


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--size":
        print(human(dir_size(sys.argv[2], threading.Event())))
        return
    enable_ansi()
    hide_cursor(True)
    try:
        App().run()
    except KeyboardInterrupt:
        pass
    finally:
        hide_cursor(False)
        if ANSI:
            sys.stdout.write("\x1b[0m")
        clear()
        print("Citrus closed.")


if __name__ == "__main__":
    main()


# ===========================================================================
#  Citrus — disk usage explorer
#  Created by Noah
#  GitHub: https://github.com/Windows-Ctrl-Shift-B
#  Copyright (c) 2026 Noah. All rights reserved.
# ===========================================================================
