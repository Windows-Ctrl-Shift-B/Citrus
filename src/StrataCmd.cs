
// StrataCmd.cs — Strata, a mouse-driven disk usage explorer for the terminal.
// Single standalone .exe, builds with the C# compiler shipped in Windows:
//   csc.exe /optimize /reference:Microsoft.VisualBasic.dll /out:Strata.exe StrataCmd.cs
//
// Mouse + keyboard. Scroll to move, click or press 1-9 to open a folder,
// filter by name, sort, open in Explorer, and a built-in junk cleaner.
// Deletes go to the Recycle Bin and system files are protected.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Management;
using System.IO.Compression;

static class Strata
{
    // -------------------------------------------------------------- model --

    class Entry { public string Name; public bool IsDir; public long Size; public long Modified; public bool Mark; }

    static string CurrentPath;
    static List<Entry> Entries = new List<Entry>();   // raw scan of current folder
    static List<Entry> View = new List<Entry>();       // Entries after filter + sort
    static string Filter = "";
    enum SortMode { SizeDesc, NameAsc, NewestFirst }
    static SortMode CurSort = SortMode.SizeDesc;

    static int Sel, Offset;
    static string Status = "";
    static bool Partial;

    static readonly Dictionary<string, long> Cache =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    static readonly object CacheLock = new object();
    static volatile bool CancelScan;

    // If any of these appears anywhere in a path, the item is off-limits.
    // Only the Windows folder tree itself is name-blocked now. Drive roots are
    // blocked separately below. Anything else Windows marks System (Recycle
    // Bin, System Volume Information, Recovery, pagefile/hiberfil/swapfile)
    // is still caught automatically by the attribute check in IsProtected.
    static readonly HashSet<string> ProtectedNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "windows", "winsxs", "system32", "syswow64", "boot", "efi"
    };

    static int ListTop = 5, ListRows = 10, FooterY = 23;
    struct Btn { public int X0, X1; public char Act; }
    static readonly List<Btn> Buttons = new List<Btn>();

    // ------------------------------------------------------- native input --

    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_PROCESSED_INPUT = 0x0001, ENABLE_WINDOW_INPUT = 0x0008,
               ENABLE_MOUSE_INPUT = 0x0010, ENABLE_EXTENDED_FLAGS = 0x0080;
    const ushort KEY_EVENT = 1, MOUSE_EVENT = 2, WINDOW_BUFFER_SIZE_EVENT = 4;
    const uint MOUSE_WHEELED = 0x0004, DOUBLE_CLICK = 0x0002, LEFT_BUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount, wVirtualKeyCode, wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState, dwControlKeyState, dwEventFlags;
    }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleInput(IntPtr h, [Out] INPUT_RECORD[] buf, uint len, out uint read);
    [DllImport("kernel32.dll")]
    static extern bool GetNumberOfConsoleInputEvents(IntPtr h, out uint n);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

    static IntPtr _hIn;
    static uint _savedMode;
    const uint _mouseMode = ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_PROCESSED_INPUT;
    static readonly INPUT_RECORD[] _buf = new INPUT_RECORD[1];

    static void EnableMouse()
    {
        // Grab the *real* console input (CONIN$) so mouse events always arrive,
        // even if stdin was redirected; fall back to the standard handle.
        IntPtr h = CreateFile("CONIN$", 0x80000000u | 0x40000000u, 0x1u | 0x2u, IntPtr.Zero, 3, 0, IntPtr.Zero);
        _hIn = (h == INVALID || h == IntPtr.Zero) ? GetStdHandle(STD_INPUT_HANDLE) : h;
        GetConsoleMode(_hIn, out _savedMode);
        SetConsoleMode(_hIn, _mouseMode);   // mouse ON; QuickEdit / line / echo OFF
    }
    static void RestoreMouse() { if (_hIn != IntPtr.Zero) SetConsoleMode(_hIn, _savedMode); }

    enum Ev { Key, Click, Wheel, Resize }
    struct Input { public Ev Kind; public ushort VK; public char Ch; public int X, Y; public bool WheelUp; }

    static Input ReadEvent()
    {
        SetConsoleMode(_hIn, _mouseMode);   // re-assert every read so nothing disables the mouse
        while (true)
        {
            uint read;
            if (!ReadConsoleInput(_hIn, _buf, 1, out read) || read == 0) continue;
            var r = _buf[0];
            if (r.EventType == KEY_EVENT)
            {
                if (r.KeyEvent.bKeyDown == 0) continue;
                return new Input { Kind = Ev.Key, VK = r.KeyEvent.wVirtualKeyCode, Ch = r.KeyEvent.UnicodeChar };
            }
            if (r.EventType == MOUSE_EVENT)
            {
                uint f = r.MouseEvent.dwEventFlags;
                if ((f & MOUSE_WHEELED) != 0)
                    return new Input { Kind = Ev.Wheel, WheelUp = (short)(r.MouseEvent.dwButtonState >> 16) > 0 };
                if ((f == 0 || (f & DOUBLE_CLICK) != 0) && (r.MouseEvent.dwButtonState & LEFT_BUTTON) != 0)
                    return new Input { Kind = Ev.Click, X = r.MouseEvent.dwMousePosition.X, Y = r.MouseEvent.dwMousePosition.Y };
                continue;
            }
            if (r.EventType == WINDOW_BUFFER_SIZE_EVENT)
                return new Input { Kind = Ev.Resize };
        }
    }

    static bool EscPressedDuringScan()
    {
        uint n, read;
        if (!GetNumberOfConsoleInputEvents(_hIn, out n) || n == 0) return false;
        while (GetNumberOfConsoleInputEvents(_hIn, out n) && n > 0)
        {
            if (!ReadConsoleInput(_hIn, _buf, 1, out read) || read == 0) break;
            var r = _buf[0];
            if (r.EventType == KEY_EVENT && r.KeyEvent.bKeyDown != 0 && r.KeyEvent.wVirtualKeyCode == 0x1B) return true;
        }
        return false;
    }

    // ------------------------------------------------------ fast scanner --

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileEx(string name, int infoLevel, out WIN32_FIND_DATA data,
                                         int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFile(IntPtr h, out WIN32_FIND_DATA data);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFileAttributes(string path);

    static readonly IntPtr INVALID = new IntPtr(-1);
    const uint FA_DIR = 0x10, FA_REPARSE = 0x400, FA_SYSTEM = 0x4;

    static string SearchGlob(string dir) { return @"\\?\" + dir.TrimEnd('\\') + @"\*"; }
    static long FtToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    { return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime; }

    static long DirSize(string root)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (CancelScan) return total;
            string dir = stack.Pop();
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileEx(SearchGlob(dir), 1, out fd, 0, IntPtr.Zero, 2);
            if (h == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    if ((fd.dwFileAttributes & FA_DIR) != 0) stack.Push(dir + "\\" + n);
                    else total += ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                } while (FindNextFile(h, out fd));
            }
            finally { FindClose(h); }
        }
        return total;
    }

    static void ForgetCache(string path)
    {
        lock (CacheLock)
        {
            string prefix = path.TrimEnd('\\') + "\\";
            var stale = new List<string>();
            foreach (var k in Cache.Keys)
                if (k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) stale.Add(k);
            foreach (var k in stale) Cache.Remove(k);
        }
    }

    static string Scan(string path, bool force)
    {
        CancelScan = false;
        Partial = false;
        var results = new List<Entry>();
        var todo = new List<KeyValuePair<Entry, string>>();

        WIN32_FIND_DATA fd;
        IntPtr lh = FindFirstFileEx(SearchGlob(path), 1, out fd, 0, IntPtr.Zero, 2);
        if (lh == INVALID) return "This folder can't be opened (permission denied or unavailable).";
        try
        {
            do
            {
                string name = fd.cFileName;
                if (name == "." || name == "..") continue;
                bool reparse = (fd.dwFileAttributes & FA_REPARSE) != 0;
                bool isDir = (fd.dwFileAttributes & FA_DIR) != 0;
                string full = path.TrimEnd('\\') + "\\" + name;
                var en = new Entry { Name = name, IsDir = isDir, Size = 0, Modified = FtToLong(fd.ftLastWriteTime) };
                results.Add(en);
                if (reparse) { }
                else if (isDir)
                {
                    long cached; bool hit;
                    lock (CacheLock) hit = Cache.TryGetValue(full, out cached);
                    if (hit && !force) en.Size = cached;
                    else todo.Add(new KeyValuePair<Entry, string>(en, full));
                }
                else en.Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
            } while (FindNextFile(lh, out fd));
        }
        finally { FindClose(lh); }

        if (todo.Count > 0)
        {
            int done = 0;
            int workers = Math.Min(32, Math.Max(8, Environment.ProcessorCount * 4));
            var task = Task.Factory.StartNew(() => Parallel.ForEach(todo,
                new ParallelOptions { MaxDegreeOfParallelism = workers }, kv =>
                {
                    long size = DirSize(kv.Value);
                    kv.Key.Size = size;
                    if (!CancelScan) lock (CacheLock) Cache[kv.Value] = size;
                    Interlocked.Increment(ref done);
                }));
            while (!task.IsCompleted)
            {
                DrawProgress(done, todo.Count);
                if (EscPressedDuringScan()) CancelScan = true;
                Thread.Sleep(50);
            }
            try { task.Wait(); } catch { }
            Partial = CancelScan;
        }

        Entries = results;
        return null;
    }

    static void RebuildView()
    {
        View = new List<Entry>();
        foreach (var e in Entries)
            if (Filter.Length == 0 || e.Name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                View.Add(e);
        if (CurSort == SortMode.SizeDesc) View.Sort((a, b) => b.Size.CompareTo(a.Size));
        else if (CurSort == SortMode.NameAsc) View.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        else View.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        if (Sel >= View.Count) Sel = Math.Max(0, View.Count - 1);
        if (Sel < 0) Sel = 0;
    }

    // ------------------------------------------------------------ helpers --

    static string Human(long n)
    {
        double v = n; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? n + " B" : v.ToString("0.0") + " " + u[i];
    }
    static bool IsDriveRoot(string p)
    {
        try { return Path.GetPathRoot(p).TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }
    static bool IsProtected(string p)
    {
        if (IsDriveRoot(p)) return true;
        foreach (var seg in p.Split('\\'))
            if (seg.Length > 0 && ProtectedNames.Contains(seg)) return true;
        uint a = GetFileAttributes(@"\\?\" + p.TrimEnd('\\'));
        if (a != 0xFFFFFFFF && (a & FA_SYSTEM) != 0) return true;
        return false;
    }
    static string Fit(string s, int w)
    {
        if (s == null) s = "";
        if (s.Length > w) return w > 1 ? s.Substring(0, w - 1) + "…" : s.Substring(0, w);
        return s.PadRight(w);
    }
    static int Cols { get { return Math.Max(64, Console.WindowWidth); } }
    static int Rows { get { return Math.Max(16, Console.WindowHeight); } }
    static void W(string s, ConsoleColor c) { Console.ForegroundColor = c; Console.Write(s); }
    static void Line(string s)
    {
        int w = Cols - 1;
        if (s.Length > w) s = s.Substring(0, w);
        Console.Write(s.PadRight(w)); Console.WriteLine();
    }
    static string SortName()
    {
        return CurSort == SortMode.SizeDesc ? "size" : CurSort == SortMode.NameAsc ? "name" : "newest";
    }

    // ---------------------------------------------------------------- UI --

    static void DrawProgress(int done, int total)
    {
        try { Console.SetCursorPosition(0, 5); } catch { return; }
        Console.BackgroundColor = ConsoleColor.DarkYellow;
        Console.ForegroundColor = ConsoleColor.Black;
        string m = " Measuring folders… " + done + "/" + total + "   (Esc cancels) ";
        Console.Write(m.Length > Cols - 1 ? m.Substring(0, Cols - 1) : m.PadRight(Cols - 1));
        Console.ResetColor();
    }

    static void ScanScreen(bool force)
    {
        Console.ResetColor(); Console.Clear();
        W(" " + CurrentPath + "\n\n", ConsoleColor.Cyan);
        W(" Measuring…\n", ConsoleColor.DarkGray); Console.ResetColor();
        Filter = "";
        string err = Scan(CurrentPath, force);
        Status = err == null ? "" : "Can't read this folder: " + err;
        RebuildView();
        Sel = 0; Offset = 0;
    }

    static void Draw()
    {
        int cols = Cols, rows = Rows;
        ListTop = 5;
        ListRows = Math.Max(1, rows - 7);
        FooterY = rows - 1;
        Console.SetCursorPosition(0, 0);

        // 0: title bar with free space
        string disk = "";
        try { var d = new DriveInfo(Path.GetPathRoot(CurrentPath)); disk = Human(d.AvailableFreeSpace) + " free of " + Human(d.TotalSize); }
        catch { }
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White;
        string title = " CITRUS — Disk Usage Explorer";
        int gap = Math.Max(1, cols - 1 - title.Length - disk.Length - 2);
        Line(title + new string(' ', gap) + disk + " ");
        Console.ResetColor();

        // 1: help OR live filter box
        if (Filter.Length > 0)
        {
            Console.BackgroundColor = ConsoleColor.DarkCyan; Console.ForegroundColor = ConsoleColor.White;
            Line(" Filter: " + Filter + "▏   (type to narrow · Enter keep · Esc clear) — " + View.Count + " match(es)");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Line(" click/1-9 open · Space tick · B big · U dupes · / find · S sort · J junk · T tools · Del remove · ? help");
            Console.ResetColor();
        }

        // 2: path  3: rule
        Console.ForegroundColor = ConsoleColor.Cyan; Line(" " + CurrentPath);
        Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1));
        Console.ResetColor();

        int barW = Math.Max(8, Math.Min(20, cols / 6));
        int sizeW = 10, dateW = 10, pctW = 4, numW = 3;
        int nameW = Math.Max(8, cols - barW - sizeW - dateW - pctW - 12);

        if (Sel >= Offset + ListRows) Offset = Sel - ListRows + 1;
        if (Sel < Offset) Offset = Sel;
        if (Offset < 0) Offset = 0;

        long max = 1, totalAll = 0;
        foreach (var e in Entries) totalAll += e.Size;
        foreach (var e in View) if (e.Size > max) max = e.Size;

        for (int row = 0; row < ListRows; row++)
        {
            int idx = Offset + row;
            if (idx >= View.Count) { Line(""); continue; }
            var e = View[idx];
            double frac = (double)e.Size / max;
            int filled = (int)(frac * barW);
            string num = row < 9 ? (row + 1) + ". " : "   ";
            string date = DateStr(e.Modified).PadRight(dateW);
            string pct = (totalAll > 0 ? (int)(e.Size * 100 / totalAll) + "%" : "").PadLeft(pctW);
            char mark = e.Mark ? '•' : ' ';
            if (idx == Sel)
            {
                Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black;
                string bar = new string('█', filled) + new string('░', barW - filled);
                Line(mark + num + (e.IsDir ? "▸ " : "  ") + Fit(e.Name, nameW) + " " + date + " " + Human(e.Size).PadLeft(sizeW) + " " + pct + " " + bar);
                Console.ResetColor();
            }
            else
            {
                W(mark.ToString(), ConsoleColor.Green);
                W(num, ConsoleColor.DarkGray);
                W((e.IsDir ? "▸ " : "  "), ConsoleColor.Yellow);
                W(Fit(e.Name, nameW) + " ", e.Mark ? ConsoleColor.Yellow : (e.IsDir ? ConsoleColor.White : ConsoleColor.Gray));
                W(date + " ", ConsoleColor.DarkGray);
                W(Human(e.Size).PadLeft(sizeW) + " ", ConsoleColor.Cyan);
                W(pct + " ", ConsoleColor.DarkGray);
                W(new string('█', filled), frac >= 0.66 ? ConsoleColor.Red : frac >= 0.33 ? ConsoleColor.Yellow : ConsoleColor.Green);
                W(new string('░', barW - filled), ConsoleColor.DarkGray);
                Console.ResetColor();
                int used = 1 + numW + 2 + nameW + 1 + dateW + 1 + sizeW + 1 + pctW + 1 + barW;
                if (used < cols - 1) Console.Write(new string(' ', cols - 1 - used));
                Console.WriteLine();
            }
        }

        // rule above footer
        Console.SetCursorPosition(0, rows - 2);
        Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1));
        Console.ResetColor();

        // footer buttons + totals
        Console.SetCursorPosition(0, FooterY);
        Buttons.Clear();
        int x = 0;
        x = Button(x, " Up ", 'u', ConsoleColor.DarkCyan);
        x = Button(x, " Open ", 'o', ConsoleColor.DarkBlue);
        x = Button(x, " Delete ", 'd', ConsoleColor.DarkRed);
        x = Button(x, " Sort ", 's', ConsoleColor.DarkGreen);
        x = Button(x, " Big ", 'b', ConsoleColor.DarkYellow);
        x = Button(x, " Dupes ", 'p', ConsoleColor.DarkMagenta);
        x = Button(x, " Junk ", 'j', ConsoleColor.DarkMagenta);
        x = Button(x, " Tools ", 't', ConsoleColor.DarkGreen);
        x = Button(x, " Help ", 'h', ConsoleColor.DarkGray);
        x = Button(x, " Quit ", 'q', ConsoleColor.DarkGray);
        long total = 0; foreach (var e in View) total += e.Size;
        string info = "  " + View.Count + " items · " + Human(total) + " · sort:" + SortName() + (Partial ? " · partial" : "");
        Console.ForegroundColor = ConsoleColor.Green;
        int room = cols - 1 - x;
        Console.Write(info.Length > room ? info.Substring(0, Math.Max(0, room)) : info.PadRight(room));
        Console.ResetColor();

        if (Status.Length > 0)
        {
            Console.SetCursorPosition(0, rows - 2);
            ConsoleColor sc = Status.StartsWith("✓") ? ConsoleColor.Green : ConsoleColor.Red;
            W(" " + Fit(Status, cols - 2), sc); Console.ResetColor();
        }
    }

    static int Button(int x, string label, char act, ConsoleColor bg)
    {
        if (x + 1 + label.Length > Cols - 1) return x;   // no room in a narrow window — skip it
        Console.SetCursorPosition(x, FooterY);
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write(" ");
        int start = x + 1;
        Console.BackgroundColor = bg; Console.ForegroundColor = ConsoleColor.White; Console.Write(label);
        Console.ResetColor();
        Buttons.Add(new Btn { X0 = start, X1 = start + label.Length - 1, Act = act });
        return start + label.Length;
    }

    // yes/cancel bar on the bottom row — returns true if confirmed
    static bool ConfirmBar(string prompt, string yesLabel)
    {
        int y = Rows - 1, cols = Cols;
        Console.SetCursorPosition(0, y);
        Console.BackgroundColor = ConsoleColor.DarkYellow; Console.ForegroundColor = ConsoleColor.Black;
        string yes = " " + yesLabel + " ", can = " Esc = NO ";
        int max = cols - 1 - yes.Length - 1 - can.Length;
        string p = " " + prompt + "  ";
        if (p.Length > max) p = p.Substring(0, Math.Max(0, max));
        Console.Write(p);
        int yesX = Console.CursorLeft;
        Console.BackgroundColor = ConsoleColor.DarkGreen; Console.ForegroundColor = ConsoleColor.White; Console.Write(yes);
        Console.BackgroundColor = ConsoleColor.DarkYellow; Console.Write(" ");
        int canX = Console.CursorLeft;
        Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black; Console.Write(can);
        Console.ResetColor();
        int pad = cols - 1 - (canX + can.Length); if (pad > 0) Console.Write(new string(' ', pad));
        while (true)
        {
            var ev = ReadEvent();
            if (ev.Kind == Ev.Key)
            {
                if (ev.VK == 0x0D || ev.Ch == 'y' || ev.Ch == 'Y') return true;   // Enter = yes
                if (ev.VK == 0x1B || ev.Ch == 'n' || ev.Ch == 'N') return false;  // Esc = no
                // any other key is ignored — you must press Enter or Esc
            }
            else if (ev.Kind == Ev.Click && ev.Y == y)
            {
                if (ev.X >= yesX && ev.X < yesX + yes.Length) return true;
                if (ev.X >= canX && ev.X < canX + can.Length) return false;
            }
            else if (ev.Kind == Ev.Resize) return false;
        }
    }

    static void BlockBar(string msg)
    {
        Console.SetCursorPosition(0, Rows - 1);
        Console.BackgroundColor = ConsoleColor.DarkRed; Console.ForegroundColor = ConsoleColor.White;
        Console.Write(Fit(" " + msg, Cols - 1)); Console.ResetColor();
        ReadEvent();
    }

    // ----------------------------------------------------------- actions --

    static void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath.TrimEnd('\\'));
        if (parent == null) return;
        string child = Path.GetFileName(CurrentPath.TrimEnd('\\'));
        CurrentPath = parent.FullName;
        ScanScreen(false);
        for (int i = 0; i < View.Count; i++)
            if (View[i].IsDir && View[i].Name.Equals(child, StringComparison.OrdinalIgnoreCase)) { Sel = i; break; }
    }

    static void OpenIndex(int idx)
    {
        if (idx < 0 || idx >= View.Count) return;
        Sel = idx;
        string full = Path.Combine(CurrentPath, View[idx].Name);
        if (View[idx].IsDir) { CurrentPath = full; ScanScreen(false); }
        else { try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); } catch { } } // open in its app
    }

    static string DateStr(long ft)
    {
        if (ft <= 0) return "";
        try { return DateTime.FromFileTime(ft).ToString("yyyy-MM-dd"); } catch { return ""; }
    }

    static bool RecycleOne(string target)
    {
        try
        {
            if (Directory.Exists(target))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(target, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------- file-in-use unlock --
    // Uses Windows' own Restart Manager (the same mechanism Explorer uses
    // for "this file is open in X") to find which running app is holding a
    // file open, so a failed delete can close it and retry automatically.

    const int RM_MAX_APP_NAME = 255, RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    // Processes we will never force-close even if Restart Manager reports
    // them as holding a handle — core OS/shell processes, and ourselves.
    static readonly HashSet<string> NeverKill = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "wininit", "services", "lsass",
        "smss", "svchost", "system", "registry", "citrus"
    };

    static List<Process> FindLockingProcesses(List<string> paths)
    {
        var found = new List<Process>();
        uint session;
        if (RmStartSession(out session, 0, Guid.NewGuid().ToString("N")) != 0) return found;
        try
        {
            string[] files = paths.ToArray();
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return found;

            uint needed = 0, count = 0, reasons = 0;
            RM_PROCESS_INFO[] info = new RM_PROCESS_INFO[0];
            int rc = RmGetList(session, out needed, ref count, info, ref reasons);
            if (rc == 234 && needed > 0)   // ERROR_MORE_DATA — resize and retry
            {
                count = needed;
                info = new RM_PROCESS_INFO[count];
                rc = RmGetList(session, out needed, ref count, info, ref reasons);
            }
            if (rc != 0) return found;
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int pid = info[i].Process.dwProcessId;
                    if (!seen.Add(pid)) continue;
                    var p = Process.GetProcessById(pid);
                    if (!NeverKill.Contains(p.ProcessName)) found.Add(p);
                }
                catch { }
            }
        }
        finally { RmEndSession(session); }
        return found;
    }

    // Shallow-capped file walk — used only to probe a folder for lock
    // owners, not for sizing, so a hard cap keeps it fast on huge folders.
    static List<string> CollectFilesCapped(string root, int cap)
    {
        var outp = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && outp.Count < cap)
        {
            string dir = stack.Pop();
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileEx(SearchGlob(dir), 1, out fd, 0, IntPtr.Zero, 2);
            if (h == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    string full = dir + "\\" + n;
                    if ((fd.dwFileAttributes & FA_DIR) != 0) stack.Push(full);
                    else { outp.Add(full); if (outp.Count >= cap) break; }
                } while (FindNextFile(h, out fd));
            }
            finally { FindClose(h); }
        }
        return outp;
    }

    // Deletes `target`. If it's locked by a running app, finds that app,
    // closes it, and retries once. `closedApp` is set (non-null) only when
    // an app actually had to be closed to make the delete succeed.
    static bool TryDeleteWithUnlock(string target, out string closedApp)
    {
        closedApp = null;
        if (RecycleOne(target)) return true;
        if (!File.Exists(target) && !Directory.Exists(target)) return true; // already gone

        List<string> probe = Directory.Exists(target) ? CollectFilesCapped(target, 2000) : new List<string> { target };
        if (probe.Count == 0) probe = new List<string> { target };

        var locking = FindLockingProcesses(probe);
        if (locking.Count == 0) return false;

        var names = new List<string>();
        foreach (var p in locking) { try { names.Add(p.ProcessName); p.Kill(); p.WaitForExit(3000); } catch { } }
        closedApp = string.Join(", ", names.ToArray());
        Thread.Sleep(300);
        return RecycleOne(target);
    }

    // Deletes a list of items on a background thread while the main thread
    // keeps drawing a live progress line — so the console never looks
    // frozen during a big delete. Esc stops queueing further items (the
    // item already in progress is allowed to finish).
    static int _delDone, _delOk, _delUnlocked, _delBlocked, _delFailed;
    static string _delClosed;
    static readonly List<string> LastDeleted = new List<string>();  // for Undo

    static void DeleteWithProgress(List<string> targets, out int ok, out int unlocked, out int blocked, out int failed, out string lastClosed)
    {
        CancelScan = false;
        _delDone = _delOk = _delUnlocked = _delBlocked = _delFailed = 0; _delClosed = "";
        lock (LastDeleted) LastDeleted.Clear();

        // Recycle Bin operations go through the shell (SHFileOperation), which
        // needs an STA thread with COM — a thread-pool thread can silently fail.
        var worker = new Thread(() =>
        {
            foreach (var t in targets)
            {
                if (CancelScan) break;
                if (IsProtected(t)) { _delBlocked++; _delDone++; continue; }
                string closedApp;
                bool success = TryDeleteWithUnlock(t, out closedApp);
                if (success) { _delOk++; if (closedApp != null) { _delUnlocked++; _delClosed = closedApp; } ForgetCache(t); lock (LastDeleted) LastDeleted.Add(t); }
                else _delFailed++;
                _delDone++;
            }
        });
        worker.IsBackground = true;
        try { worker.SetApartmentState(ApartmentState.STA); } catch { }
        worker.Start();

        int spin = 0;
        while (worker.IsAlive)
        {
            try
            {
                Console.SetCursorPosition(0, Rows - 1);
                Console.BackgroundColor = ConsoleColor.DarkYellow; Console.ForegroundColor = ConsoleColor.Black;
                string dots = new string('.', (spin++ % 3) + 1);
                string msg = " Deleting" + dots + " " + _delDone + "/" + targets.Count
                           + (_delClosed.Length > 0 ? "  (closed " + _delClosed + ")" : "")
                           + "   Esc = stop ";
                Console.Write(Fit(msg, Cols - 1)); Console.ResetColor();
            }
            catch { }
            if (EscPressedDuringScan()) CancelScan = true;
            worker.Join(90);
        }
        ok = _delOk; unlocked = _delUnlocked; blocked = _delBlocked; failed = _delFailed; lastClosed = _delClosed;
    }

    static void OpenInExplorer()
    {
        try
        {
            if (View.Count > 0)
            {
                string p = Path.Combine(CurrentPath, View[Sel].Name);
                if (View[Sel].IsDir) Process.Start("explorer.exe", "\"" + p + "\"");
                else Process.Start("explorer.exe", "/select,\"" + p + "\"");
            }
            else Process.Start("explorer.exe", "\"" + CurrentPath + "\"");
        }
        catch { }
    }

    static void DeleteSelected()
    {
        if (View.Count == 0) return;

        var targets = new List<string>();
        string label;
        var marked = new List<Entry>();
        foreach (var e in View) if (e.Mark) marked.Add(e);
        if (marked.Count > 0)
        {
            long tot = 0; foreach (var e in marked) { targets.Add(Path.Combine(CurrentPath, e.Name)); tot += e.Size; }
            label = marked.Count + " ticked items (" + Human(tot) + ")";
        }
        else
        {
            var one = View[Sel];
            string t = Path.Combine(CurrentPath, one.Name);
            if (IsProtected(t)) { BlockBar("⚠  '" + one.Name + "' is a system item — Citrus won't delete it.  (Esc)"); return; }
            targets.Add(t);
            label = "'" + one.Name + "' (" + Human(one.Size) + ")";
        }

        if (!ConfirmBar("⚠  Delete " + label + "?  Moves to the Recycle Bin.", "Enter = YES")) return;

        int ok, unlocked, blocked, failed; string lastClosed;
        DeleteWithProgress(targets, out ok, out unlocked, out blocked, out failed, out lastClosed);
        ForgetCache(CurrentPath);
        ScanScreen(false);

        // Always leave a visible result on the redrawn screen (green = ok).
        if (failed > 0 || blocked > 0)
            Status = "! " + ok + " deleted · " + failed + " couldn't be removed (in use or denied) · " + blocked + " protected";
        else if (unlocked > 0)
            Status = "✓ Deleted " + ok + " — closed " + lastClosed + " to free the file";
        else
            Status = "✓ Deleted " + ok + " item" + (ok == 1 ? "" : "s");
    }

    // type-to-filter, live
    static void FilterInput()
    {
        while (true)
        {
            Draw();
            var ev = ReadEvent();
            if (ev.Kind == Ev.Key)
            {
                if (ev.VK == 0x0D) return;                                   // Enter keeps filter
                if (ev.VK == 0x1B) { Filter = ""; RebuildView(); return; }   // Esc clears
                if (ev.VK == 0x08) { if (Filter.Length > 0) { Filter = Filter.Substring(0, Filter.Length - 1); RebuildView(); } continue; }
                if (!char.IsControl(ev.Ch) && ev.Ch != '\0') { Filter += ev.Ch; Sel = 0; Offset = 0; RebuildView(); }
            }
            else if (ev.Kind == Ev.Wheel || ev.Kind == Ev.Click) return;     // leave edit, keep filter
        }
    }

    // ------------------------------------------------------- junk cleaner --

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    class Junk { public string Name; public string Path; public bool Bin; public long Size; }

    static List<Junk> BuildJunk()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<Junk>
        {
            new Junk { Name = "Your temporary files",     Path = Path.GetTempPath() },
            new Junk { Name = "Windows temporary files",  Path = Path.Combine(win, "Temp") },
            new Junk { Name = "Windows Update leftovers",  Path = Path.Combine(win, "SoftwareDistribution", "Download") },
            new Junk { Name = "Chrome cache",             Path = Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache") },
            new Junk { Name = "Edge cache",               Path = Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache") },
            new Junk { Name = "Opera GX cache",           Path = Path.Combine(local, "Opera Software", "Opera GX Stable", "Cache") },
            new Junk { Name = "Recycle Bin",              Path = "", Bin = true },
        };
        foreach (var j in list)
        {
            if (j.Bin) { var info = new SHQUERYRBINFO(); info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)); if (SHQueryRecycleBin(null, ref info) == 0) j.Size = info.i64Size; }
            else if (Directory.Exists(j.Path)) j.Size = DirSize(j.Path);
        }
        return list;
    }

    static void CleanJunk(Junk j)
    {
        if (j.Bin) { try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); } catch { } return; }
        if (!Directory.Exists(j.Path)) return;
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(j.Path))
            {
                try
                {
                    var attr = File.GetAttributes(e);
                    if ((attr & FileAttributes.Directory) != 0) Directory.Delete(e, true);
                    else { File.SetAttributes(e, FileAttributes.Normal); File.Delete(e); }
                }
                catch { } // locked / in-use files are simply skipped
            }
        }
        catch { }
    }

    static void JunkScreen()
    {
        var items = BuildJunk();
        while (true)
        {
            int cols = Cols;
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkMagenta; Console.ForegroundColor = ConsoleColor.White;
            Line(" CITRUS — Junk Cleaner"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray;
            Line(" Press a number to clear that item · A = clear everything · Esc = back");
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();

            long tot = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var j = items[i];
                W("   " + (i + 1) + "  ", ConsoleColor.Cyan);
                W(Fit(j.Name, 30), ConsoleColor.White);
                W(Human(j.Size).PadLeft(12), j.Size > 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
                Console.WriteLine();
                tot += j.Size;
            }
            Console.WriteLine();
            W("   Reclaimable total: ", ConsoleColor.Gray); W(Human(tot) + "\n", ConsoleColor.Green);
            // pad rest of screen
            for (int r = Console.CursorTop; r < Rows - 1; r++) Line("");

            var ev = ReadEvent();
            if (ev.Kind == Ev.Key)
            {
                if (ev.VK == 0x1B) return;
                if (ev.Ch == 'a' || ev.Ch == 'A')
                {
                    if (ConfirmBar("Clear ALL junk (" + Human(tot) + ")? Temp/cache files are removed permanently.", "YES, CLEAR ALL"))
                    { foreach (var j in items) CleanJunk(j); ForgetCache(CurrentPath); items = BuildJunk(); }
                }
                else if (ev.Ch >= '1' && ev.Ch <= '9')
                {
                    int k = ev.Ch - '1';
                    if (k < items.Count)
                    {
                        var j = items[k];
                        string how = j.Bin ? "Empty the Recycle Bin permanently?" : "Clear " + j.Name + " (" + Human(j.Size) + ") permanently?";
                        if (ConfirmBar(how, "YES, CLEAR")) { CleanJunk(j); items = BuildJunk(); }
                    }
                }
            }
            else if (ev.Kind == Ev.Click)
            {
                int row = ev.Y - 3; // first item row
                if (row >= 0 && row < items.Count)
                {
                    var j = items[row];
                    string how = j.Bin ? "Empty the Recycle Bin permanently?" : "Clear " + j.Name + " (" + Human(j.Size) + ") permanently?";
                    if (ConfirmBar(how, "YES, CLEAR")) { CleanJunk(j); items = BuildJunk(); }
                }
            }
        }
    }

    // recursively collect files at/under root (only >= minSize, junction-safe)
    static void CollectFiles(string root, long minSize, List<KeyValuePair<string, long>> outFiles, int[] dirsDone)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (CancelScan) return;
            string dir = stack.Pop();
            dirsDone[0]++;
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileEx(SearchGlob(dir), 1, out fd, 0, IntPtr.Zero, 2);
            if (h == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    string full = dir + "\\" + n;
                    if ((fd.dwFileAttributes & FA_DIR) != 0) stack.Push(full);
                    else
                    {
                        long sz = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        if (sz >= minSize) outFiles.Add(new KeyValuePair<string, long>(full, sz));
                    }
                } while (FindNextFile(h, out fd));
            }
            finally { FindClose(h); }
        }
    }

    // a full-screen scrollable list of "size  path" rows; returns nothing.
    // onDelete lets Delete work on the highlighted row.
    static void ListScreen(string title, List<KeyValuePair<string, long>> rows, string emptyMsg)
    {
        int sel = 0, off = 0;
        while (true)
        {
            int cols = Cols, rows2 = Rows, listRows = rows2 - 4;
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White;
            Line(" " + title); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" Enter/O open location · Delete removes · Esc back"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();

            if (rows.Count == 0) { Console.ForegroundColor = ConsoleColor.DarkGray; Line(" " + emptyMsg); Console.ResetColor(); }
            if (sel >= off + listRows) off = sel - listRows + 1;
            if (sel < off) off = sel;
            if (off < 0) off = 0;
            int sizeW = 11;
            for (int r = 0; r < listRows; r++)
            {
                int idx = off + r;
                if (idx >= rows.Count) { Line(""); continue; }
                var kv = rows[idx];
                string sz = Human(kv.Value).PadLeft(sizeW);
                int pathW = Math.Max(10, cols - 2 - sizeW - 2);
                string path = kv.Key.Length > pathW ? "…" + kv.Key.Substring(kv.Key.Length - pathW + 1) : kv.Key.PadRight(pathW);
                if (idx == sel)
                {
                    Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black;
                    Line(" " + sz + "  " + path); Console.ResetColor();
                }
                else
                {
                    W(" " + sz + "  ", ConsoleColor.Cyan); W(path, ConsoleColor.White); Console.ResetColor();
                    int used = 1 + sizeW + 2 + pathW; if (used < cols - 1) Console.Write(new string(' ', cols - 1 - used));
                    Console.WriteLine();
                }
            }
            long tot = 0; foreach (var kv in rows) tot += kv.Value;
            Console.SetCursorPosition(0, rows2 - 1);
            Console.ForegroundColor = ConsoleColor.Green; Console.Write(Fit(" " + rows.Count + " files · " + Human(tot) + " total", cols - 1)); Console.ResetColor();

            var ev = ReadEvent();
            if (ev.Kind == Ev.Wheel) { sel = ev.WheelUp ? Math.Max(0, sel - 1) : Math.Min(Math.Max(0, rows.Count - 1), sel + 1); continue; }
            if (ev.Kind == Ev.Click) { if (ev.Y >= 3 && ev.Y < 3 + listRows) { int i = off + (ev.Y - 3); if (i < rows.Count) sel = i; } continue; }
            if (ev.Kind != Ev.Key) continue;
            switch (ev.VK)
            {
                case 0x1B: return;
                case 0x26: if (sel > 0) sel--; break;
                case 0x28: if (sel < rows.Count - 1) sel++; break;
                case 0x21: sel = Math.Max(0, sel - 15); break;
                case 0x22: sel = Math.Min(Math.Max(0, rows.Count - 1), sel + 15); break;
                case 0x0D:
                    if (rows.Count > 0) { try { Process.Start("explorer.exe", "/select,\"" + rows[sel].Key + "\""); } catch { } }
                    break;
                case 0x2E:
                    if (rows.Count > 0)
                    {
                        string t = rows[sel].Key;
                        if (IsProtected(t)) { BlockBar("⚠  '" + Path.GetFileName(t) + "' is a system item — won't delete.  (Esc)"); break; }
                        if (ConfirmBar("Delete '" + Path.GetFileName(t) + "' (" + Human(rows[sel].Value) + ")? → Recycle Bin", "Enter = YES"))
                        {
                            string closedApp;
                            if (TryDeleteWithUnlock(t, out closedApp)) { rows.RemoveAt(sel); if (sel >= rows.Count) sel = Math.Max(0, rows.Count - 1); if (closedApp != null) BlockBar("Deleted — closed " + closedApp + " which had it open.  (Esc)"); }
                            else BlockBar("Delete failed — the file is locked and its app couldn't be closed.  (Esc)");
                        }
                    }
                    break;
                default:
                    if (ev.Ch == 'o' || ev.Ch == 'O') { if (rows.Count > 0) { try { Process.Start("explorer.exe", "/select,\"" + rows[sel].Key + "\""); } catch { } } }
                    if (ev.Ch == 'q' || ev.Ch == 'Q') return;
                    break;
            }
        }
    }

    static void ProgressLine(string msg)
    {
        try { Console.SetCursorPosition(0, 3); } catch { return; }
        Console.BackgroundColor = ConsoleColor.DarkYellow; Console.ForegroundColor = ConsoleColor.Black;
        Console.Write(Fit(" " + msg + "   (Esc cancels) ", Cols - 1)); Console.ResetColor();
    }

    static void BiggestFilesScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — biggest files under " + CurrentPath); Console.ResetColor();
        CancelScan = false;
        var files = new List<KeyValuePair<string, long>>();
        int[] dirs = { 0 };
        var t = Task.Factory.StartNew(() => CollectFiles(CurrentPath, 1024L * 1024, files, dirs)); // >= 1 MB
        while (!t.IsCompleted) { ProgressLine("Scanning for big files… " + dirs[0] + " folders, " + files.Count + " found"); if (EscPressedDuringScan()) CancelScan = true; Thread.Sleep(60); }
        try { t.Wait(); } catch { }
        files.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (files.Count > 500) files = files.GetRange(0, 500);
        ListScreen("CITRUS — biggest files (top " + files.Count + ", ≥ 1 MB)", files, "No files ≥ 1 MB here.");
    }

    static void DuplicatesScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — duplicate files under " + CurrentPath); Console.ResetColor();
        CancelScan = false;
        var files = new List<KeyValuePair<string, long>>();
        int[] dirs = { 0 };
        var t = Task.Factory.StartNew(() => CollectFiles(CurrentPath, 1024L * 1024, files, dirs)); // >= 1 MB
        while (!t.IsCompleted) { ProgressLine("Finding candidates… " + dirs[0] + " folders, " + files.Count + " files"); if (EscPressedDuringScan()) CancelScan = true; Thread.Sleep(60); }
        try { t.Wait(); } catch { }

        // group by size, then hash only the size-collision groups
        var bySize = new Dictionary<long, List<string>>();
        foreach (var kv in files) { List<string> l; if (!bySize.TryGetValue(kv.Value, out l)) { l = new List<string>(); bySize[kv.Value] = l; } l.Add(kv.Key); }
        var candidates = new List<KeyValuePair<long, List<string>>>();
        foreach (var g in bySize) if (g.Value.Count > 1) candidates.Add(g);

        var dupRows = new List<KeyValuePair<string, long>>();
        long wasted = 0;
        int hashed = 0, totalToHash = 0; foreach (var g in candidates) totalToHash += g.Value.Count;
        foreach (var g in candidates)
        {
            if (CancelScan) break;
            var byHash = new Dictionary<string, List<string>>();
            foreach (var path in g.Value)
            {
                if (CancelScan) break;
                hashed++;
                if ((hashed & 7) == 0) { ProgressLine("Comparing files… " + hashed + "/" + totalToHash); if (EscPressedDuringScan()) CancelScan = true; }
                string hh = HashFile(path);
                if (hh == null) continue;
                List<string> l; if (!byHash.TryGetValue(hh, out l)) { l = new List<string>(); byHash[hh] = l; } l.Add(path);
            }
            foreach (var hg in byHash)
                if (hg.Value.Count > 1)
                {
                    // keep first as original; list the rest as reclaimable duplicates
                    for (int i = 1; i < hg.Value.Count; i++) { dupRows.Add(new KeyValuePair<string, long>(hg.Value[i], g.Key)); wasted += g.Key; }
                }
        }
        dupRows.Sort((a, b) => b.Value.CompareTo(a.Value));
        ListScreen("CITRUS — duplicate files (" + Human(wasted) + " reclaimable, ≥ 1 MB)", dupRows,
                   "No duplicate files ≥ 1 MB found here.");
    }

    static string HashFile(string path)
    {
        try
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                return BitConverter.ToString(md5.ComputeHash(fs));
        }
        catch { return null; }
    }

    static void HelpScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — keyboard & mouse"); Console.ResetColor();
        Console.WriteLine();
        string[][] rows2 =
        {
            new[] { "Scroll / ↑ ↓", "move the selection" },
            new[] { "Click a row", "select it (folder opens)" },
            new[] { "1 - 9", "open that numbered row" },
            new[] { "Enter / →", "open folder, or open file in its app" },
            new[] { "← / Backspace", "go up a folder" },
            new[] { "Space", "tick / untick a row (multi-select)" },
            new[] { "Delete", "delete selected - or all ticked - to Recycle Bin" },
            new[] { "O", "open selected in File Explorer" },
            new[] { "/", "filter this folder by name" },
            new[] { "S", "cycle sort: size / name / newest" },
            new[] { "B", "find the biggest files anywhere below here" },
            new[] { "U", "find duplicate files below here" },
            new[] { "J", "junk cleaner (temp, caches, Recycle Bin)" },
            new[] { "R", "rescan   -   C change drive   -   Q quit" },
            new[] { "?", "this help" },
        };
        foreach (var r in rows2) { W("   " + r[0].PadRight(16), ConsoleColor.Cyan); W(r[1] + "\n", ConsoleColor.Gray); }
        Console.WriteLine();
        W("   Press any key to go back.\n", ConsoleColor.DarkGray); Console.ResetColor();
        ReadEvent();
    }

    static ConsoleColor Col(char c)
    {
        return c == 'r' ? ConsoleColor.DarkGreen   // rind
             : c == 'f' ? ConsoleColor.Green        // flesh
             : c == 'w' ? ConsoleColor.White        // segment lines / pith
             : ConsoleColor.Black;
    }

    // the treemap logo rendered with half-block chars (so it looks like the
    // icon: green + lime squares, two teal squares) + CITRUS pixel letters.
    static void DrawLogo(int indent)
    {
        // Citrus-slice logo, drawn with half-block chars for a smooth circle.
        // r = rind, f = flesh, w = segment lines / pith, . = empty.
        string[] slice = {
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
        };
        for (int cr = 0; cr < slice.Length / 2; cr++)
        {
            Console.Write(new string(' ', indent));
            for (int x = 0; x < slice[0].Length; x++)
            {
                char top = slice[2 * cr][x], bot = slice[2 * cr + 1][x];
                if (top == '.' && bot == '.') { Console.ResetColor(); Console.Write(' '); }
                else { Console.ForegroundColor = Col(top); Console.BackgroundColor = Col(bot); Console.Write('▀'); }
            }
            Console.ResetColor(); Console.WriteLine();
        }
        Console.WriteLine();

        // CITRUS wordmark below the logo, in matching pixel letters
        var font = new Dictionary<char, string[]>
        {
            { 'C', new[] { "###", "#..", "#..", "#..", "###" } },
            { 'I', new[] { "###", ".#.", ".#.", ".#.", "###" } },
            { 'T', new[] { "###", ".#.", ".#.", ".#.", ".#." } },
            { 'R', new[] { "##.", "#.#", "##.", "#.#", "#.#" } },
            { 'U', new[] { "#.#", "#.#", "#.#", "#.#", "###" } },
            { 'S', new[] { "###", "#..", "###", "..#", "###" } },
        };
        for (int r = 0; r < 5; r++)
        {
            Console.Write(new string(' ', indent));
            foreach (char c in "CITRUS")
            {
                foreach (char p in font[c][r])
                {
                    if (p == '#') { Console.ForegroundColor = ConsoleColor.White; Console.Write("██"); }
                    else Console.Write("  ");
                }
                Console.Write("  ");
            }
            Console.ResetColor(); Console.WriteLine();
        }
    }

    // ============================ extra tools ============================

    static readonly ConsoleColor[] TreeColors = {
        ConsoleColor.DarkGreen, ConsoleColor.DarkCyan, ConsoleColor.DarkYellow,
        ConsoleColor.DarkMagenta, ConsoleColor.DarkBlue, ConsoleColor.DarkRed,
        ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Blue, ConsoleColor.Magenta
    };

    static List<KeyValuePair<Entry, int[]>> _treeRegions;  // block -> {x0,y0,x1,y1} for clicks

    static void FillBlock(string label, int x, int y, int w, int h, ConsoleColor bg)
    {
        if (w <= 0 || h <= 0) return;
        bool bright = bg == ConsoleColor.Green || bg == ConsoleColor.Cyan || bg == ConsoleColor.Yellow;
        Console.BackgroundColor = bg;
        Console.ForegroundColor = bright ? ConsoleColor.Black : ConsoleColor.White;
        for (int r = 0; r < h; r++)
        {
            try { Console.SetCursorPosition(x, y + r); } catch { continue; }
            Console.Write(new string(' ', Math.Min(w, Math.Max(0, Cols - 1 - x))));
        }
        if (w >= 4 && h >= 1)
        {
            try { Console.SetCursorPosition(x + 1, y); Console.Write(Fit(label, w - 2)); } catch { }
        }
        Console.ResetColor();
    }

    // balanced slice-and-dice treemap
    static void DrawTree(List<Entry> items, int x, int y, int w, int h, bool horiz, int[] ci)
    {
        if (items.Count == 0 || w <= 0 || h <= 0) return;
        if (items.Count == 1)
        {
            FillBlock(items[0].Name + "  " + Human(items[0].Size), x, y, w, h, TreeColors[ci[0]++ % TreeColors.Length]);
            if (_treeRegions != null) _treeRegions.Add(new KeyValuePair<Entry, int[]>(items[0], new[] { x, y, x + w - 1, y + h - 1 }));
            return;
        }
        long total = 0; foreach (var e in items) total += e.Size;
        if (total <= 0) return;
        long half = total / 2, run = 0; int split = 0;
        for (int i = 0; i < items.Count - 1; i++) { run += items[i].Size; split = i + 1; if (run >= half) break; }
        var left = items.GetRange(0, split);
        var right = items.GetRange(split, items.Count - split);
        long ls = 0; foreach (var e in left) ls += e.Size;
        if (horiz)
        {
            int lw = (int)Math.Round(w * (double)ls / total);
            lw = Math.Max(1, Math.Min(w - 1, lw));
            DrawTree(left, x, y, lw, h, !horiz, ci);
            DrawTree(right, x + lw, y, w - lw, h, !horiz, ci);
        }
        else
        {
            int lh = (int)Math.Round(h * (double)ls / total);
            lh = Math.Max(1, Math.Min(h - 1, lh));
            DrawTree(left, x, y, w, lh, !horiz, ci);
            DrawTree(right, x, y + lh, w, h - lh, !horiz, ci);
        }
    }

    static void TreemapScreen()
    {
        var items = new List<Entry>();
        foreach (var e in View) if (e.Size > 0) items.Add(e);
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        while (true)
        {
            Console.ResetColor(); Console.Clear();
            int cols = Cols, rows = Rows;
            Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White;
            Line(" CITRUS — map of " + CurrentPath); Console.ResetColor();
            _treeRegions = new List<KeyValuePair<Entry, int[]>>();
            if (items.Count == 0) { Console.WriteLine(" (nothing to map)"); }
            else { int[] ci = { 0 }; DrawTree(items, 0, 2, cols - 1, rows - 3, true, ci); }
            Console.SetCursorPosition(0, rows - 1);
            Console.BackgroundColor = ConsoleColor.DarkGray; Console.ForegroundColor = ConsoleColor.White;
            Console.Write(Fit(" Click a block to open it · any key to go back", cols - 1)); Console.ResetColor();

            var ev = ReadEvent();
            if (ev.Kind == Ev.Resize) continue;
            if (ev.Kind == Ev.Click)
            {
                Entry hit = null;
                foreach (var kv in _treeRegions)
                {
                    var r = kv.Value;
                    if (ev.X >= r[0] && ev.X <= r[2] && ev.Y >= r[1] && ev.Y <= r[3]) { hit = kv.Key; break; }
                }
                if (hit != null)
                {
                    string full = Path.Combine(CurrentPath, hit.Name);
                    if (hit.IsDir) { CurrentPath = full; ScanScreen(false); return; }   // open folder in the app
                    else { try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); } catch { } }
                }
                continue;   // click on a file or empty space — stay on the map
            }
            return;   // any key exits the map
        }
    }

    static readonly Dictionary<string, string> ExtCat = BuildExtCat();
    static Dictionary<string, string> BuildExtCat()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Action<string, string> add = (cat, exts) => { foreach (var e in exts.Split(' ')) m[e] = cat; };
        add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg");
        add("Photos", "jpg jpeg png gif bmp heic tiff tif webp raw cr2 nef svg");
        add("Audio", "mp3 wav flac aac ogg m4a wma");
        add("Installers", "exe msi msix appx apk");
        add("Archives", "zip rar 7z tar gz iso bz2 xz cab");
        add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt csv");
        add("Code/Data", "cs js ts py html css json xml cpp c h java go rs sql db");
        add("Disk images", "vhd vhdx vmdk img dmg");
        return m;
    }

    static void FileTypesScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — file types under " + CurrentPath); Console.ResetColor();
        Console.WriteLine();
        CancelScan = false;
        var sizes = new Dictionary<string, long>();
        var counts = new Dictionary<string, long>();
        var stack = new Stack<string>(); stack.Push(CurrentPath);
        int dirs = 0;
        while (stack.Count > 0)
        {
            if (CancelScan) break;
            string d = stack.Pop(); dirs++;
            WIN32_FIND_DATA fd; IntPtr hnd = FindFirstFileEx(SearchGlob(d), 1, out fd, 0, IntPtr.Zero, 2);
            if (hnd == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    if ((fd.dwFileAttributes & FA_DIR) != 0) stack.Push(d + "\\" + n);
                    else
                    {
                        long sz = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        int dot = n.LastIndexOf('.');
                        string ext = dot > 0 ? n.Substring(dot + 1) : "";
                        string cat; if (!ExtCat.TryGetValue(ext, out cat)) cat = "Other";
                        long cur; sizes.TryGetValue(cat, out cur); sizes[cat] = cur + sz;
                        counts.TryGetValue(cat, out cur); counts[cat] = cur + 1;
                    }
                } while (FindNextFile(hnd, out fd));
            }
            finally { FindClose(hnd); }
            if (dirs % 40 == 0) { ProgressLine("Scanning… " + dirs + " folders"); if (EscPressedDuringScan()) CancelScan = true; }
        }
        var list = new List<KeyValuePair<string, long>>(sizes);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        long grand = 0; foreach (var kv in list) grand += kv.Value; if (grand == 0) grand = 1;

        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — file types under " + CurrentPath); Console.ResetColor();
        Console.WriteLine();
        int barW = Math.Max(10, Cols - 45);
        foreach (var kv in list)
        {
            int filled = (int)((double)kv.Value / grand * barW);
            long cnt; counts.TryGetValue(kv.Key, out cnt);
            W("   " + kv.Key.PadRight(14), ConsoleColor.White);
            W(Human(kv.Value).PadLeft(10) + "  ", ConsoleColor.Cyan);
            W(new string('█', filled), ConsoleColor.Green); W(new string('░', barW - filled), ConsoleColor.DarkGray);
            W("  " + (int)((double)kv.Value / grand * 100) + "% (" + cnt + ")\n", ConsoleColor.DarkGray);
        }
        Console.WriteLine();
        W("   Total " + Human(grand) + "   —   press any key to go back\n", ConsoleColor.Green);
        Console.ResetColor();
        ReadEvent();
    }

    static void OldFilesScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — big old files (≥ 1 MB, untouched 1+ year)"); Console.ResetColor();
        Console.WriteLine();
        CancelScan = false;
        long cutoff = DateTime.Now.AddYears(-1).ToFileTime();
        var files = new List<KeyValuePair<string, long>>();
        var stack = new Stack<string>(); stack.Push(CurrentPath);
        int dirs = 0;
        while (stack.Count > 0)
        {
            if (CancelScan) break;
            string d = stack.Pop(); dirs++;
            WIN32_FIND_DATA fd; IntPtr hnd = FindFirstFileEx(SearchGlob(d), 1, out fd, 0, IntPtr.Zero, 2);
            if (hnd == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    if ((fd.dwFileAttributes & FA_DIR) != 0) stack.Push(d + "\\" + n);
                    else
                    {
                        long sz = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        long mt = FtToLong(fd.ftLastWriteTime);
                        if (sz >= 1024L * 1024 && mt > 0 && mt < cutoff) files.Add(new KeyValuePair<string, long>(d + "\\" + n, sz));
                    }
                } while (FindNextFile(hnd, out fd));
            }
            finally { FindClose(hnd); }
            if (dirs % 40 == 0) { ProgressLine("Scanning… " + dirs + " folders, " + files.Count + " found"); if (EscPressedDuringScan()) CancelScan = true; }
        }
        files.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (files.Count > 500) files = files.GetRange(0, 500);
        ListScreen("CITRUS — big old files (≥ 1 MB, 1+ year old)", files, "No big old files here.");
    }

    static string TextInput(string prompt)
    {
        string s = "";
        while (true)
        {
            Console.SetCursorPosition(0, Rows - 1);
            Console.BackgroundColor = ConsoleColor.DarkCyan; Console.ForegroundColor = ConsoleColor.White;
            Console.Write(Fit(" " + prompt + " " + s + "▏  (Enter search · Esc cancel)", Cols - 1)); Console.ResetColor();
            var ev = ReadEvent();
            if (ev.Kind != Ev.Key) continue;
            if (ev.VK == 0x0D) return s;
            if (ev.VK == 0x1B) return null;
            if (ev.VK == 0x08) { if (s.Length > 0) s = s.Substring(0, s.Length - 1); continue; }
            if (!char.IsControl(ev.Ch) && ev.Ch != '\0') s += ev.Ch;
        }
    }

    static void SearchDriveScreen()
    {
        string term = TextInput("Search whole drive for:");
        if (string.IsNullOrEmpty(term)) return;
        string root = Path.GetPathRoot(CurrentPath);
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — searching " + root + " for \"" + term + "\""); Console.ResetColor();
        Console.WriteLine();
        CancelScan = false;
        var hits = new List<KeyValuePair<string, long>>();
        var stack = new Stack<string>(); stack.Push(root);
        int dirs = 0;
        while (stack.Count > 0)
        {
            if (CancelScan) break;
            string d = stack.Pop(); dirs++;
            WIN32_FIND_DATA fd; IntPtr hnd = FindFirstFileEx(SearchGlob(d), 1, out fd, 0, IntPtr.Zero, 2);
            if (hnd == INVALID) continue;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    if ((fd.dwFileAttributes & FA_REPARSE) != 0) continue;
                    bool isDir = (fd.dwFileAttributes & FA_DIR) != 0;
                    string full = d.TrimEnd('\\') + "\\" + n;
                    if (isDir) stack.Push(full);
                    if (n.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 && hits.Count < 1000)
                    {
                        long sz = isDir ? 0 : (((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow);
                        hits.Add(new KeyValuePair<string, long>(full, sz));
                    }
                } while (FindNextFile(hnd, out fd));
            }
            finally { FindClose(hnd); }
            if (dirs % 40 == 0) { ProgressLine("Searching… " + dirs + " folders, " + hits.Count + " matches"); if (EscPressedDuringScan()) CancelScan = true; }
        }
        hits.Sort((a, b) => b.Value.CompareTo(a.Value));
        ListScreen("CITRUS — \"" + term + "\" (" + hits.Count + " matches" + (hits.Count >= 1000 ? ", capped" : "") + ")", hits, "No matches found.");
    }

    static void ProgramsScreen()
    {
        var progs = InstalledPrograms();
        int sel = 0, off = 0;
        while (true)
        {
            int cols = Cols, r = Rows, lr = Math.Max(1, r - 4);
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — installed programs (by size)"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" Enter = run its uninstaller · Esc back   (sizes are approximate)"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();
            if (sel >= off + lr) off = sel - lr + 1; if (sel < off) off = sel; if (off < 0) off = 0;
            for (int i = 0; i < lr; i++)
            {
                int idx = off + i;
                if (idx >= progs.Count) { Line(""); continue; }
                string sz = (progs[idx][1] == "0" ? "?" : Human(long.Parse(progs[idx][1]))).PadLeft(10);
                string nm = Fit(progs[idx][0], cols - 2 - 10 - 2);
                if (idx == sel) { Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black; Line(" " + sz + "  " + nm); Console.ResetColor(); }
                else { W(" " + sz + "  ", ConsoleColor.Cyan); W(nm, ConsoleColor.White); Console.ResetColor(); int used = 1 + 10 + 2 + nm.Length; if (used < cols - 1) Console.Write(new string(' ', cols - 1 - used)); Console.WriteLine(); }
            }
            Console.SetCursorPosition(0, r - 1); Console.ForegroundColor = ConsoleColor.Green; Console.Write(Fit(" " + progs.Count + " programs   (Esc back)", cols - 1)); Console.ResetColor();
            var ev = ReadEvent();
            if (ev.Kind == Ev.Wheel) { sel = ev.WheelUp ? Math.Max(0, sel - 1) : Math.Min(Math.Max(0, progs.Count - 1), sel + 1); continue; }
            if (ev.Kind == Ev.Click) { if (ev.Y >= 3 && ev.Y < 3 + lr) { int ci = off + (ev.Y - 3); if (ci < progs.Count) sel = ci; } continue; }
            if (ev.Kind != Ev.Key) continue;
            if (ev.VK == 0x1B) return;
            else if (ev.VK == 0x26) { if (sel > 0) sel--; }
            else if (ev.VK == 0x28) { if (sel < progs.Count - 1) sel++; }
            else if (ev.VK == 0x21) sel = Math.Max(0, sel - 15);
            else if (ev.VK == 0x22) sel = Math.Min(Math.Max(0, progs.Count - 1), sel + 15);
            else if (ev.VK == 0x0D && progs.Count > 0)
            {
                string un = progs[sel][2];
                if (!string.IsNullOrEmpty(un))
                {
                    if (ConfirmBar("Run the uninstaller for '" + progs[sel][0] + "'?", "Enter = YES"))
                    { try { Process.Start(new ProcessStartInfo("cmd.exe", "/c " + un) { UseShellExecute = true }); } catch { } BlockBar("Uninstaller launched — follow its prompts, then rescan.  (any key)"); return; }
                }
                else BlockBar("No uninstaller registered for this program.  (any key)");
            }
        }
    }

    static List<string[]> InstalledPrograms()
    {
        var found = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string[] roots = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
        RegistryKey[] hives = { Registry.LocalMachine, Registry.CurrentUser };
        foreach (var hive in hives)
            foreach (var root in roots)
            {
                try
                {
                    using (var k = hive.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (var app = k.OpenSubKey(sub))
                                {
                                    if (app == null) continue;
                                    string name = app.GetValue("DisplayName") as string;
                                    if (string.IsNullOrEmpty(name)) continue;
                                    object sc = app.GetValue("SystemComponent");
                                    if (sc is int && (int)sc == 1) continue;
                                    if (app.GetValue("ParentKeyName") != null) continue; // updates
                                    long size = 0;
                                    object est = app.GetValue("EstimatedSize");
                                    if (est is int) size = ((long)(int)est) * 1024;
                                    string un = app.GetValue("UninstallString") as string;
                                    found[name] = new string[] { name, size.ToString(), un == null ? "" : un };
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        var list = new List<string[]>(found.Values);
        list.Sort((a, b) => long.Parse(b[1]).CompareTo(long.Parse(a[1])));
        return list;
    }

    static void DriveHealthScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — drive health"); Console.ResetColor();
        Console.WriteLine();
        var rows = new List<string[]>(); // model, size, status, predictFail
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Model,Size,Status,MediaType FROM Win32_DiskDrive"))
                foreach (ManagementObject d in s.Get())
                {
                    string model = (d["Model"] == null ? "" : d["Model"].ToString());
                    long size = 0; long.TryParse(d["Size"] == null ? "0" : d["Size"].ToString(), out size);
                    string status = (d["Status"] == null ? "?" : d["Status"].ToString());
                    rows.Add(new string[] { model, size.ToString(), status });
                }
        }
        catch (Exception e) { W("   Couldn't read drive info: " + e.Message + "\n", ConsoleColor.Red); }

        bool anyPredict = false;
        try
        {
            using (var s = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
                foreach (ManagementObject d in s.Get())
                    try { if ((bool)d["PredictFailure"]) anyPredict = true; } catch { }
        }
        catch { }

        if (rows.Count == 0) W("   No drives reported (need permission?).\n", ConsoleColor.DarkGray);
        foreach (var r in rows)
        {
            bool ok = r[2].Equals("OK", StringComparison.OrdinalIgnoreCase);
            W("   " + Fit(r[0], 40), ConsoleColor.White);
            W(Human(long.Parse(r[1])).PadLeft(10) + "   ", ConsoleColor.Cyan);
            W((ok ? "HEALTHY" : "CHECK: " + r[2]) + "\n", ok ? ConsoleColor.Green : ConsoleColor.Red);
        }
        Console.WriteLine();
        W("   SMART failure prediction: ", ConsoleColor.Gray);
        W((anyPredict ? "⚠ A DRIVE PREDICTS FAILURE — back up now" : "no failures predicted") + "\n", anyPredict ? ConsoleColor.Red : ConsoleColor.Green);
        Console.WriteLine();
        W("   Press any key to go back.\n", ConsoleColor.DarkGray); Console.ResetColor();
        ReadEvent();
    }

    static void ExportReport()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string file = Path.Combine(desktop, "Citrus-report-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Citrus disk report");
            sb.AppendLine("Folder: " + CurrentPath);
            sb.AppendLine("Generated: " + DateTime.Now);
            try { var du = new DriveInfo(Path.GetPathRoot(CurrentPath)); sb.AppendLine("Drive: " + Human(du.AvailableFreeSpace) + " free of " + Human(du.TotalSize)); } catch { }
            sb.AppendLine(new string('-', 60));
            long total = 0;
            var sorted = new List<Entry>(Entries); sorted.Sort((a, b) => b.Size.CompareTo(a.Size));
            foreach (var e in sorted) { sb.AppendLine(Human(e.Size).PadLeft(12) + "  " + (e.IsDir ? "[folder] " : "         ") + e.Name); total += e.Size; }
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(sorted.Count + " items, " + Human(total) + " total");
            File.WriteAllText(file, sb.ToString());
            Status = "✓ Report saved to Desktop: " + Path.GetFileName(file);
        }
        catch (Exception e) { Status = "! Couldn't save report: " + e.Message; }
    }

    // -------- 9. undo last delete (restore from Recycle Bin via shell COM) --

    static void UndoLastDelete()
    {
        List<string> paths;
        lock (LastDeleted) paths = new List<string>(LastDeleted);
        if (paths.Count == 0) { Status = "! Nothing to undo (delete something first)"; return; }
        int restored = 0;
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            object shell = Activator.CreateInstance(shellType);
            object bin = shellType.InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { 10 });
            Type nsType = bin.GetType();
            object items = nsType.InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, bin, null);
            Type itemsType = items.GetType();
            int count = (int)itemsType.InvokeMember("Count", System.Reflection.BindingFlags.InvokeMethod, null, items, null);
            var want = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            for (int i = count - 1; i >= 0; i--)
            {
                object item = itemsType.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                string name = (string)item.GetType().InvokeMember("Name", System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty, null, item, null);
                string folder = (string)nsType.InvokeMember("GetDetailsOf", System.Reflection.BindingFlags.InvokeMethod, null, bin, new object[] { item, 1 });
                string orig = Path.Combine(folder, name);
                if (want.Contains(orig))
                {
                    object verbs = item.GetType().InvokeMember("Verbs", System.Reflection.BindingFlags.InvokeMethod, null, item, null);
                    int vc = (int)verbs.GetType().InvokeMember("Count", System.Reflection.BindingFlags.InvokeMethod, null, verbs, null);
                    for (int v = 0; v < vc; v++)
                    {
                        object verb = verbs.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, verbs, new object[] { v });
                        string vn = ((string)verb.GetType().InvokeMember("Name", System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty, null, verb, null)).Replace("&", "");
                        if (vn.IndexOf("Restore", StringComparison.OrdinalIgnoreCase) >= 0 || vn.IndexOf("undelete", StringComparison.OrdinalIgnoreCase) >= 0)
                        { verb.GetType().InvokeMember("DoIt", System.Reflection.BindingFlags.InvokeMethod, null, verb, null); restored++; break; }
                    }
                }
            }
        }
        catch (Exception e) { Status = "! Undo failed: " + e.Message; return; }
        lock (LastDeleted) LastDeleted.Clear();
        ForgetCache(CurrentPath); ScanScreen(false);
        Status = restored > 0 ? "✓ Restored " + restored + " item(s) from the Recycle Bin" : "! Couldn't find them in the Recycle Bin";
    }

    // ---- generic "locations with sizes" cleaner (Windows deep-clean) -------

    static void LocationCleaner(string title, List<string[]> locs)  // locs: [name, path]
    {
        while (true)
        {
            var sizes = new List<long>();
            long tot = 0;
            foreach (var l in locs) { long s = Directory.Exists(l[1]) ? DirSize(l[1]) : 0; sizes.Add(s); tot += s; }
            int cols = Cols;
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkMagenta; Console.ForegroundColor = ConsoleColor.White; Line(" " + title); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" Number = clear it · A = clear all · Esc back   (some need admin; skipped if denied)"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();
            for (int i = 0; i < locs.Count; i++)
            {
                W("   " + (i + 1) + "  ", ConsoleColor.Cyan);
                W(Fit(locs[i][0], 34), ConsoleColor.White);
                W(Human(sizes[i]).PadLeft(11) + "  ", sizes[i] > 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
                W((Directory.Exists(locs[i][1]) ? "" : "(not present)") + "\n", ConsoleColor.DarkGray);
            }
            Console.WriteLine();
            W("   Reclaimable total: " + Human(tot) + "   (Esc back)\n", ConsoleColor.Green); Console.ResetColor();
            for (int r = Console.CursorTop; r < Rows - 1; r++) Line("");
            var ev = ReadEvent();
            if (ev.Kind == Ev.Key)
            {
                if (ev.VK == 0x1B) return;
                if (ev.Ch == 'a' || ev.Ch == 'A')
                {
                    if (ConfirmBar("Clear ALL of these (" + Human(tot) + ")? Removed permanently.", "Enter = YES"))
                    { foreach (var l in locs) CleanLocation(l[1]); }
                }
                else if (ev.Ch >= '1' && ev.Ch <= '9')
                {
                    int k = ev.Ch - '1';
                    if (k < locs.Count && ConfirmBar("Clear " + locs[k][0] + " (" + Human(sizes[k]) + ") permanently?", "Enter = YES"))
                        CleanLocation(locs[k][1]);
                }
            }
        }
    }

    static void CleanLocation(string path)
    {
        if (!Directory.Exists(path)) return;
        // Windows.old and similar top-level folders: try to remove the whole tree
        try { foreach (var e in Directory.EnumerateFileSystemEntries(path))
            { try { var a = File.GetAttributes(e); if ((a & FileAttributes.Directory) != 0) Directory.Delete(e, true); else { File.SetAttributes(e, FileAttributes.Normal); File.Delete(e); } } catch { } } }
        catch { }
    }

    static void WindowsDeepClean()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sysdrive = Path.GetPathRoot(win);
        var locs = new List<string[]>
        {
            new[] { "Windows.old (old install)", Path.Combine(sysdrive, "Windows.old") },
            new[] { "Windows Update cache", Path.Combine(win, "SoftwareDistribution", "Download") },
            new[] { "Windows temp", Path.Combine(win, "Temp") },
            new[] { "Delivery Optimization cache", Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization") },
            new[] { "Crash minidumps", Path.Combine(win, "Minidump") },
            new[] { "Live kernel reports", Path.Combine(win, "LiveKernelReports") },
        };
        LocationCleaner("CITRUS — Windows deep-clean", locs);
    }

    // -------- 11. debloat preinstalled Store apps --------------------------

    static void DebloatScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — remove preinstalled Store apps"); Console.ResetColor();
        Console.WriteLine(); W("   Reading installed apps…\n", ConsoleColor.DarkGray);
        // Known bloat (friendly name -> package name fragment)
        string[][] bloat = {
            new[]{"Xbox apps","Xbox"}, new[]{"Solitaire Collection","MicrosoftSolitaire"},
            new[]{"Clipchamp","Clipchamp"}, new[]{"Your Phone / Phone Link","YourPhone"},
            new[]{"Get Help","GetHelp"}, new[]{"Feedback Hub","WindowsFeedback"},
            new[]{"Maps","WindowsMaps"}, new[]{"News","BingNews"}, new[]{"Weather","BingWeather"},
            new[]{"People","People"}, new[]{"Mixed Reality Portal","MixedReality"},
            new[]{"3D Viewer","Microsoft3D"}, new[]{"Paint 3D","MSPaint"},
            new[]{"Skype","SkypeApp"}, new[]{"Spotify (Store)","SpotifyAB"},
            new[]{"Disney+","Disney"}, new[]{"TikTok","TikTok"},
        };
        string installed = RunPowerShell("Get-AppxPackage | Select-Object -ExpandProperty Name");
        var present = new List<string[]>();
        foreach (var b in bloat) if (installed.IndexOf(b[1], StringComparison.OrdinalIgnoreCase) >= 0) present.Add(b);

        int sel = 0;
        while (true)
        {
            int cols = Cols, r = Rows;
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — remove preinstalled Store apps"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" Enter = remove for this user · Esc back"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();
            if (present.Count == 0) W("   None of the usual bloat apps are installed.\n", ConsoleColor.DarkGray);
            for (int i = 0; i < present.Count; i++)
            {
                if (i == sel) { Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black; Line("   " + present[i][0]); Console.ResetColor(); }
                else W("   " + present[i][0] + "\n", ConsoleColor.White);
            }
            var ev = ReadEvent();
            if (ev.Kind == Ev.Click) { if (ev.Y >= 3 && ev.Y < 3 + present.Count) sel = ev.Y - 3; continue; }
            if (ev.Kind != Ev.Key) continue;
            if (ev.VK == 0x1B) return;
            else if (ev.VK == 0x26) { if (sel > 0) sel--; }
            else if (ev.VK == 0x28) { if (sel < present.Count - 1) sel++; }
            else if (ev.VK == 0x0D && present.Count > 0)
            {
                if (ConfirmBar("Remove '" + present[sel][0] + "' for the current user?", "Enter = YES"))
                {
                    RunPowerShell("Get-AppxPackage *" + present[sel][1] + "* | Remove-AppxPackage");
                    present.RemoveAt(sel); if (sel >= present.Count) sel = Math.Max(0, present.Count - 1);
                }
            }
        }
    }

    static string RunPowerShell(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"");
            psi.RedirectStandardOutput = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60000);
            return outp;
        }
        catch { return ""; }
    }

    // -------- 12. move a folder to another drive (leave a junction) ---------

    static void MoveFolder()
    {
        if (View.Count == 0 || !View[Sel].IsDir) { Status = "! Pick a folder first (Move only works on folders)"; return; }
        string src = Path.Combine(CurrentPath, View[Sel].Name);
        if (IsProtected(src)) { Status = "! That folder is protected and can't be moved"; return; }
        string letter = TextInput("Move '" + View[Sel].Name + "' to which drive letter? (e.g. D)");
        if (string.IsNullOrEmpty(letter)) return;
        letter = letter.Trim().TrimEnd(':').ToUpper();
        string destRoot = letter + ":\\";
        if (!Directory.Exists(destRoot)) { Status = "! Drive " + destRoot + " isn't available"; return; }
        string dest = Path.Combine(destRoot, "CitrusMoved", View[Sel].Name);
        if (!ConfirmBar("Move to " + dest + " and leave a link behind? (apps keep working)", "Enter = YES")) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            // robocopy /MOVE copies then deletes the source contents
            var rc = Process.Start(new ProcessStartInfo("robocopy.exe", "\"" + src + "\" \"" + dest + "\" /E /MOVE /NFL /NDL /NJH /NJS /NC /NS") { UseShellExecute = false, CreateNoWindow = true });
            rc.WaitForExit();
            try { if (Directory.Exists(src) && Directory.GetFileSystemEntries(src).Length == 0) Directory.Delete(src); } catch { }
            // junction so the old path still resolves
            Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + src + "\" \"" + dest + "\"") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            ForgetCache(CurrentPath); ScanScreen(false);
            Status = "✓ Moved to " + dest + " (link left behind)";
        }
        catch (Exception e) { Status = "! Move failed: " + e.Message; }
    }

    // -------- 13. zip a folder --------------------------------------------

    // Minimal .zip writer using only DeflateStream (in System.dll since .NET 2.0),
    // so it works on Windows 7 / Vista / XP without the .NET 4.5 zip assemblies.
    static uint[] _crc;
    static uint Crc32(byte[] data)
    {
        if (_crc == null)
        {
            _crc = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint x = i;
                for (int k = 0; k < 8; k++) x = (x & 1) != 0 ? 0xEDB88320 ^ (x >> 1) : x >> 1;
                _crc[i] = x;
            }
        }
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++) c = _crc[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    static void W16(Stream s, int v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); }
    static void W32(Stream s, uint v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }

    static void ZipCollect(string baseDir, string cur, List<string> rel)
    {
        try { foreach (var f in Directory.GetFiles(cur)) rel.Add(f.Substring(baseDir.Length).TrimStart('\\')); } catch { }
        try
        {
            foreach (var d in Directory.GetDirectories(cur))
            {
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { }
                ZipCollect(baseDir, d, rel);
            }
        }
        catch { }
    }

    static void MakeZip(string source, string zipPath)
    {
        var rel = new List<string>();
        string baseDir;
        if (Directory.Exists(source)) { baseDir = source; ZipCollect(source, source, rel); }
        else { baseDir = Path.GetDirectoryName(source); rel.Add(Path.GetFileName(source)); }

        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        {
            var central = new List<byte[]>();
            foreach (var name in rel)
            {
                byte[] data;
                try { data = File.ReadAllBytes(Path.Combine(baseDir, name)); } catch { continue; }
                uint crc = Crc32(data);
                byte[] comp;
                using (var ms = new MemoryStream())
                {
                    using (var dz = new DeflateStream(ms, CompressionMode.Compress, true)) dz.Write(data, 0, data.Length);
                    comp = ms.ToArray();
                }
                byte[] nb = System.Text.Encoding.UTF8.GetBytes(name.Replace('\\', '/'));
                uint localOffset = (uint)fs.Position;
                W32(fs, 0x04034b50); W16(fs, 20); W16(fs, 0x0800); W16(fs, 8); W16(fs, 0); W16(fs, 0);
                W32(fs, crc); W32(fs, (uint)comp.Length); W32(fs, (uint)data.Length);
                W16(fs, nb.Length); W16(fs, 0);
                fs.Write(nb, 0, nb.Length);
                fs.Write(comp, 0, comp.Length);

                var cd = new MemoryStream();
                W32(cd, 0x02014b50); W16(cd, 20); W16(cd, 20); W16(cd, 0x0800); W16(cd, 8); W16(cd, 0); W16(cd, 0);
                W32(cd, crc); W32(cd, (uint)comp.Length); W32(cd, (uint)data.Length);
                W16(cd, nb.Length); W16(cd, 0); W16(cd, 0); W16(cd, 0); W16(cd, 0); W32(cd, 0); W32(cd, localOffset);
                cd.Write(nb, 0, nb.Length);
                central.Add(cd.ToArray());
            }
            uint cdStart = (uint)fs.Position;
            foreach (var b in central) fs.Write(b, 0, b.Length);
            uint cdSize = (uint)fs.Position - cdStart;
            W32(fs, 0x06054b50); W16(fs, 0); W16(fs, 0); W16(fs, central.Count); W16(fs, central.Count);
            W32(fs, cdSize); W32(fs, cdStart); W16(fs, 0);
        }
    }

    static void ZipFolder()
    {
        if (View.Count == 0) { return; }
        var e = View[Sel];
        string src = Path.Combine(CurrentPath, e.Name);
        string zip = src + ".zip";
        if (!ConfirmBar("Compress '" + e.Name + "' to " + Path.GetFileName(zip) + "?", "Enter = YES")) return;
        Status = "";
        bool done = false; string err = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (File.Exists(zip)) File.Delete(zip);
                MakeZip(src, zip);
            }
            catch (Exception ex) { err = ex.Message; }
            done = true;
        });
        worker.IsBackground = true; worker.Start();
        int spin = 0;
        while (!done)
        {
            try { Console.SetCursorPosition(0, Rows - 1); Console.BackgroundColor = ConsoleColor.DarkYellow; Console.ForegroundColor = ConsoleColor.Black; Console.Write(Fit(" Compressing" + new string('.', (spin++ % 3) + 1), Cols - 1)); Console.ResetColor(); } catch { }
            worker.Join(120);
        }
        ForgetCache(CurrentPath); ScanScreen(false);
        Status = err == null ? "✓ Created " + Path.GetFileName(zip) : "! Zip failed: " + err;
    }

    // -------- 14. startup manager -----------------------------------------

    static void StartupScreen()
    {
        var entries = new List<string[]>(); // display, hive, subkeypath, valueName
        AddRun(entries, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        AddRun(entries, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
        AddRun(entries, "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        int sel = 0, off = 0;
        while (true)
        {
            int cols = Cols, r = Rows, lr = Math.Max(1, r - 4);
            Console.ResetColor(); Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — startup programs"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" Delete = disable it (removes the startup entry) · Esc back"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray; Line(new string('─', cols - 1)); Console.ResetColor();
            if (sel >= off + lr) off = sel - lr + 1; if (sel < off) off = sel; if (off < 0) off = 0;
            for (int i = 0; i < lr; i++)
            {
                int idx = off + i;
                if (idx >= entries.Count) { Line(""); continue; }
                string label = entries[idx][3] + "   " + entries[idx][0] + "  ·  " + Fit(entries[idx][4], cols - 40);
                if (idx == sel) { Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black; Line(" " + Fit(label, cols - 2)); Console.ResetColor(); }
                else { W(" " + entries[idx][3].PadRight(28), ConsoleColor.White); W(entries[idx][0] + "  ", ConsoleColor.DarkGray); W(Fit(entries[idx][4], Math.Max(6, cols - 34)) + "\n", ConsoleColor.DarkCyan); }
            }
            Console.SetCursorPosition(0, r - 1); Console.ForegroundColor = ConsoleColor.Green; Console.Write(Fit(" " + entries.Count + " startup entries   (Esc back)", cols - 1)); Console.ResetColor();
            var ev = ReadEvent();
            if (ev.Kind == Ev.Wheel) { sel = ev.WheelUp ? Math.Max(0, sel - 1) : Math.Min(Math.Max(0, entries.Count - 1), sel + 1); continue; }
            if (ev.Kind == Ev.Click) { if (ev.Y >= 3 && ev.Y < 3 + lr) { int ci = off + (ev.Y - 3); if (ci < entries.Count) sel = ci; } continue; }
            if (ev.Kind != Ev.Key) continue;
            if (ev.VK == 0x1B) return;
            else if (ev.VK == 0x26) { if (sel > 0) sel--; }
            else if (ev.VK == 0x28) { if (sel < entries.Count - 1) sel++; }
            else if (ev.VK == 0x2E && entries.Count > 0)
            {
                if (ConfirmBar("Disable '" + entries[sel][3] + "' from starting at boot?", "Enter = YES"))
                {
                    try
                    {
                        var hive = entries[sel][1] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                        using (var k = hive.OpenSubKey(entries[sel][2], true)) { if (k != null) k.DeleteValue(entries[sel][3], false); }
                        entries.RemoveAt(sel); if (sel >= entries.Count) sel = Math.Max(0, entries.Count - 1);
                    }
                    catch (Exception ex) { BlockBar("Couldn't disable (needs admin?): " + ex.Message + "  (any key)"); }
                }
            }
        }
    }

    static void AddRun(List<string[]> list, string hiveName, string path)
    {
        try
        {
            var hive = hiveName == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using (var k = hive.OpenSubKey(path))
            {
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                { object v = k.GetValue(name); list.Add(new string[] { hiveName, hiveName, path, name, v == null ? "" : v.ToString() }); }
            }
        }
        catch { }
    }

    // -------- 15. specs sheet ---------------------------------------------

    static void SpecsSheet()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("PC specifications");
        sb.AppendLine("Generated by Citrus on " + DateTime.Now);
        sb.AppendLine(new string('-', 50));
        sb.AppendLine("OS:      " + Wmi("Win32_OperatingSystem", "Caption") + " (" + Wmi("Win32_OperatingSystem", "Version") + ")");
        sb.AppendLine("CPU:     " + Wmi("Win32_Processor", "Name"));
        long ram = 0; long.TryParse(Wmi("Win32_ComputerSystem", "TotalPhysicalMemory"), out ram);
        sb.AppendLine("RAM:     " + (ram > 0 ? Human(ram) : "?"));
        sb.AppendLine("GPU:     " + Wmi("Win32_VideoController", "Name"));
        try { foreach (var d in DriveInfo.GetDrives()) if (d.IsReady && d.DriveType == DriveType.Fixed) sb.AppendLine("Disk:    " + d.Name + " " + Human(d.TotalSize) + " (" + d.DriveFormat + ")"); } catch { }
        string battFull = Wmi(@"root\wmi", "BatteryFullChargedCapacity", "FullChargedCapacity");
        string battDesign = Wmi(@"root\wmi", "BatteryStaticData", "DesignedCapacity");
        long bf, bd;
        if (long.TryParse(battFull, out bf) && long.TryParse(battDesign, out bd) && bd > 0)
            sb.AppendLine("Battery: " + (int)(bf * 100 / bd) + "% health (" + bf + " / " + bd + " mWh)");
        else
        {
            string batt = Wmi("Win32_Battery", "EstimatedChargeRemaining");
            if (!string.IsNullOrEmpty(batt)) sb.AppendLine("Battery: " + batt + "% charge (desktop = no battery otherwise)");
        }
        sb.AppendLine("Machine: " + Wmi("Win32_ComputerSystem", "Manufacturer") + " " + Wmi("Win32_ComputerSystem", "Model"));
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string file = Path.Combine(desktop, "PC-specs-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            File.WriteAllText(file, sb.ToString());
            Status = "✓ Specs saved to Desktop: " + Path.GetFileName(file);
        }
        catch (Exception e) { Status = "! Couldn't save specs: " + e.Message; }
    }

    static string Wmi(string cls, string prop) { return Wmi(null, cls, prop); }
    static string Wmi(string ns, string cls, string prop)
    {
        try
        {
            var scope = ns == null ? "SELECT " + prop + " FROM " + cls : "SELECT " + prop + " FROM " + cls;
            using (var s = ns == null ? new ManagementObjectSearcher(scope) : new ManagementObjectSearcher(ns, scope))
                foreach (ManagementObject o in s.Get())
                { object v = o[prop]; if (v != null) return v.ToString().Trim(); }
        }
        catch { }
        return "";
    }

    // -------- 16. secure free-space wipe (delegates to built-in cipher) ----

    static void SecureWipe()
    {
        string drive = Path.GetPathRoot(CurrentPath);
        if (!ConfirmBar("Securely wipe FREE space on " + drive + "? Overwrites deleted-file data; can take a long time.", "Enter = YES")) return;
        try
        {
            // cipher /w runs in its own window so the user can watch it / cancel it
            Process.Start(new ProcessStartInfo("cmd.exe", "/c title Citrus secure wipe && echo Wiping free space on " + drive + " - this can take a while, leave it running... && cipher /w:" + drive.TrimEnd('\\') + "\\ && echo. && echo Done. Press any key. && pause") { UseShellExecute = true });
            Status = "✓ Secure wipe started in a new window (leave it running)";
        }
        catch (Exception e) { Status = "! Couldn't start wipe: " + e.Message; }
    }

    // -------- 17. empty-folder finder -------------------------------------

    static void EmptyFoldersScreen()
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — empty folders under " + CurrentPath); Console.ResetColor();
        Console.WriteLine(); W("   Scanning…\n", ConsoleColor.DarkGray);
        CancelScan = false;
        var empties = new List<KeyValuePair<string, long>>();
        if (CollectEmpty(CurrentPath, empties)) empties.Add(new KeyValuePair<string, long>(CurrentPath, 0));
        ListScreen("CITRUS — empty folders (Delete to remove)", empties, "No empty folders here.");
    }

    // Returns true if `dir`'s whole subtree has no files. Adds the top-most
    // empty subfolders (the ones you'd actually delete) to outList.
    static bool CollectEmpty(string dir, List<KeyValuePair<string, long>> outList)
    {
        if (CancelScan) return false;
        bool hasFile = false;
        var subs = new List<string>();
        WIN32_FIND_DATA fd; IntPtr h = FindFirstFileEx(SearchGlob(dir), 1, out fd, 0, IntPtr.Zero, 2);
        if (h == INVALID) return false;
        try
        {
            do
            {
                string n = fd.cFileName;
                if (n == "." || n == "..") continue;
                if ((fd.dwFileAttributes & FA_REPARSE) != 0) { hasFile = true; continue; }
                if ((fd.dwFileAttributes & FA_DIR) != 0) subs.Add(dir + "\\" + n);
                else hasFile = true;
            } while (FindNextFile(h, out fd));
        }
        finally { FindClose(h); }

        var subEmpty = new bool[subs.Count];
        bool allEmpty = true;
        for (int i = 0; i < subs.Count; i++) { subEmpty[i] = CollectEmpty(subs[i], outList); if (!subEmpty[i]) allEmpty = false; }
        if (!hasFile && allEmpty) return true;         // whole subtree empty — let parent report it
        for (int i = 0; i < subs.Count; i++)           // this dir has files, so empty children are top-most
            if (subEmpty[i]) outList.Add(new KeyValuePair<string, long>(subs[i], 0));
        return false;
    }

    // -------- 18. scan snapshot & compare ---------------------------------

    static string SnapPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Citrus", "snapshot.txt"); }

    static void SaveSnapshot()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapPath()));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CurrentPath);
            foreach (var e in Entries) sb.AppendLine(e.Size + "\t" + e.Name);
            File.WriteAllText(SnapPath(), sb.ToString());
            Status = "✓ Snapshot saved — run Compare later to see what changed";
        }
        catch (Exception ex) { Status = "! Snapshot failed: " + ex.Message; }
    }

    static void CompareSnapshot()
    {
        if (!File.Exists(SnapPath())) { Status = "! No snapshot yet — use 'Save snapshot' first"; return; }
        var old = new Dictionary<string, long>();
        string oldPath = "";
        try
        {
            var lines = File.ReadAllLines(SnapPath());
            if (lines.Length > 0) oldPath = lines[0];
            for (int i = 1; i < lines.Length; i++)
            { int t = lines[i].IndexOf('\t'); if (t > 0) { long sz; if (long.TryParse(lines[i].Substring(0, t), out sz)) old[lines[i].Substring(t + 1)] = sz; } }
        }
        catch { }
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — changes vs snapshot of " + oldPath); Console.ResetColor();
        Console.WriteLine();
        var now = new Dictionary<string, long>();
        foreach (var e in Entries) now[e.Name] = e.Size;
        var rows = new List<string[]>(); // sign, name, delta
        foreach (var kv in now)
        {
            long before; long b = old.TryGetValue(kv.Key, out before) ? before : 0;
            long delta = kv.Value - b;
            if (b == 0) rows.Add(new[] { "NEW", kv.Key, "+" + Human(kv.Value) });
            else if (delta != 0) rows.Add(new[] { delta > 0 ? "GREW" : "SHRANK", kv.Key, (delta > 0 ? "+" : "-") + Human(Math.Abs(delta)) });
        }
        foreach (var kv in old) if (!now.ContainsKey(kv.Key)) rows.Add(new[] { "GONE", kv.Key, "-" + Human(kv.Value) });
        if (rows.Count == 0) W("   Nothing changed since the snapshot.\n", ConsoleColor.Gray);
        int shown = 0;
        foreach (var r in rows)
        {
            if (shown++ > Rows - 6) break;
            ConsoleColor col = r[0] == "NEW" || r[0] == "GREW" ? ConsoleColor.Green : ConsoleColor.Red;
            W("   " + r[0].PadRight(8), col); W(Fit(r[1], Cols - 30), ConsoleColor.White); W("  " + r[2] + "\n", col);
        }
        Console.WriteLine(); W("   Press any key to go back.\n", ConsoleColor.DarkGray); Console.ResetColor();
        ReadEvent();
    }

    static void RunTool(int id)
    {
        switch (id)
        {
            case 0: TreemapScreen(); break;
            case 1: FileTypesScreen(); break;
            case 2: OldFilesScreen(); break;
            case 3: SearchDriveScreen(); break;
            case 4: EmptyFoldersScreen(); break;
            case 5: WindowsDeepClean(); break;
            case 6: UndoLastDelete(); break;
            case 7: SecureWipe(); break;
            case 8: ProgramsScreen(); break;
            case 9: DebloatScreen(); break;
            case 10: StartupScreen(); break;
            case 11: DriveHealthScreen(); break;
            case 12: SpecsSheet(); break;
            case 13: MoveFolder(); break;
            case 14: ZipFolder(); break;
            case 15: ExportReport(); break;
            case 16: SaveSnapshot(); break;
            case 17: CompareSnapshot(); break;
        }
    }

    // Menu columns. Each row is {key, label, toolId}; toolId "-1" = category header.
    static readonly string[][] MenuLeft = {
        new[] { "", "ANALYZE", "-1" },
        new[] { "a", "Map view (treemap)", "0" },
        new[] { "b", "File-type breakdown", "1" },
        new[] { "c", "Big old files", "2" },
        new[] { "d", "Search whole drive", "3" },
        new[] { "", "CLEAN UP", "-1" },
        new[] { "e", "Empty folders", "4" },
        new[] { "f", "Windows deep-clean", "5" },
        new[] { "g", "Undo last delete", "6" },
        new[] { "h", "Secure-wipe free space", "7" },
    };
    static readonly string[][] MenuRight = {
        new[] { "", "SYSTEM", "-1" },
        new[] { "i", "Installed programs", "8" },
        new[] { "j", "Debloat Store apps", "9" },
        new[] { "k", "Startup manager", "10" },
        new[] { "l", "Drive health", "11" },
        new[] { "m", "PC specs sheet", "12" },
        new[] { "", "FILES", "-1" },
        new[] { "n", "Move to another drive", "13" },
        new[] { "o", "Zip a folder", "14" },
        new[] { "p", "Export report", "15" },
        new[] { "r", "Save snapshot", "16" },
        new[] { "s", "Compare snapshot", "17" },
    };

    static int MenuSelCount(string[][] col) { int n = 0; foreach (var e in col) if (e[2] != "-1") n++; return n; }
    static int MenuNth(string[][] col, int n) { int i = 0; foreach (var e in col) if (e[2] != "-1") { if (i == n) return int.Parse(e[2]); i++; } return -1; }
    static int MenuFindKey(char c)
    {
        string s = c.ToString();
        foreach (var e in MenuLeft) if (e[0] == s) return int.Parse(e[2]);
        foreach (var e in MenuRight) if (e[0] == s) return int.Parse(e[2]);
        return -1;
    }

    static void RenderMenuCol(string[][] col, int x, int w, int colIndex, int selCol, int selRow, List<int[]> regions)
    {
        int y = 3, itemIdx = 0;
        foreach (var e in col)
        {
            try { Console.SetCursorPosition(x, y); } catch { y++; continue; }
            if (e[2] == "-1")
            {
                Console.ForegroundColor = ConsoleColor.Yellow; Console.Write(Fit(e[1], w)); Console.ResetColor();
            }
            else
            {
                bool hl = colIndex == selCol && itemIdx == selRow;
                if (hl)
                {
                    Console.BackgroundColor = ConsoleColor.Gray; Console.ForegroundColor = ConsoleColor.Black;
                    Console.Write(Fit("  " + e[0] + "  " + e[1], w)); Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  " + e[0] + "  ");
                    Console.ForegroundColor = ConsoleColor.White; Console.Write(Fit(e[1], Math.Max(1, w - 5)));
                    Console.ResetColor();
                }
                regions.Add(new[] { int.Parse(e[2]), y, x, x + w });
                itemIdx++;
            }
            y++;
        }
    }

    static void ToolsMenu()
    {
        int selCol = 0, selRow = 0;
        Console.ResetColor(); Console.Clear();
        while (true)
        {
            int cols = Cols, rows = Rows;
            Console.SetCursorPosition(0, 0);
            Console.BackgroundColor = ConsoleColor.DarkMagenta; Console.ForegroundColor = ConsoleColor.White; Line(" CITRUS — tools"); Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Gray; Line(" press a letter · click · or arrows + Enter · Esc back"); Console.ResetColor();
            int leftX = 2, rightX = Math.Max(38, cols / 2);
            int leftW = Math.Max(18, rightX - leftX - 2), rightW = Math.Max(18, cols - 1 - rightX);
            var regions = new List<int[]>();
            RenderMenuCol(MenuLeft, leftX, leftW, 0, selCol, selRow, regions);
            RenderMenuCol(MenuRight, rightX, rightW, 1, selCol, selRow, regions);
            Console.SetCursorPosition(0, rows - 1);
            Console.ForegroundColor = ConsoleColor.Green; Console.Write(Fit(" 18 tools — pick one   (Esc back)", cols - 1)); Console.ResetColor();

            var ev = ReadEvent();
            if (ev.Kind == Ev.Resize) { Console.Clear(); continue; }
            if (ev.Kind == Ev.Click)
            {
                foreach (var r in regions) if (ev.Y == r[1] && ev.X >= r[2] && ev.X <= r[3]) { RunTool(r[0]); return; }
                continue;
            }
            if (ev.Kind != Ev.Key) continue;
            if (ev.VK == 0x1B) return;
            else if (ev.VK == 0x26) { selRow = Math.Max(0, selRow - 1); }
            else if (ev.VK == 0x28) { selRow = Math.Min((selCol == 0 ? MenuSelCount(MenuLeft) : MenuSelCount(MenuRight)) - 1, selRow + 1); }
            else if (ev.VK == 0x25) { selCol = 0; selRow = Math.Min(selRow, MenuSelCount(MenuLeft) - 1); }
            else if (ev.VK == 0x27) { selCol = 1; selRow = Math.Min(selRow, MenuSelCount(MenuRight) - 1); }
            else if (ev.VK == 0x0D) { int t = MenuNth(selCol == 0 ? MenuLeft : MenuRight, selRow); if (t >= 0) { RunTool(t); return; } }
            else if (ev.Ch != '\0') { int t = MenuFindKey(char.ToLower(ev.Ch)); if (t >= 0) { RunTool(t); return; } }
        }
    }

    static string ChooseDrive()
    {
        var drives = new List<DriveInfo>();
        foreach (var d in DriveInfo.GetDrives()) if (d.IsReady) drives.Add(d);
        if (drives.Count == 0) return null;

        Console.ResetColor(); Console.Clear();
        Console.WriteLine();
        DrawLogo(4);
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("      disk usage explorer"); Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("      by Noah · github.com/Windows-Ctrl-Shift-B"); Console.ResetColor();
        Console.WriteLine();
        if (drives.Count == 1) return drives[0].RootDirectory.FullName;

        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White;
        Line(" Choose a drive to scan"); Console.ResetColor(); Console.WriteLine();

        var rowY = new int[drives.Count];
        for (int i = 0; i < drives.Count; i++)
        {
            rowY[i] = Console.CursorTop;
            string info; int filled = 0, barW = 24; ConsoleColor col = ConsoleColor.Green;
            try
            {
                info = Human(drives[i].AvailableFreeSpace) + " free of " + Human(drives[i].TotalSize);
                double used = 1.0 - (double)drives[i].AvailableFreeSpace / drives[i].TotalSize;
                filled = (int)(used * barW);
                col = used >= 0.9 ? ConsoleColor.Red : used >= 0.7 ? ConsoleColor.Yellow : ConsoleColor.Green;
            }
            catch { info = "unreadable"; }
            W("   " + (i + 1) + "  ", ConsoleColor.Cyan);
            W(drives[i].RootDirectory.FullName.PadRight(6), ConsoleColor.White);
            W(new string('█', filled), col); W(new string('░', barW - filled) + "  ", ConsoleColor.DarkGray);
            W(info + "\n", ConsoleColor.Gray);
        }
        Console.WriteLine();
        W("   Click a drive, press its number, or Q to quit\n", ConsoleColor.DarkGray);
        Console.ResetColor();

        while (true)
        {
            var ev = ReadEvent();
            if (ev.Kind == Ev.Key)
            {
                if (ev.Ch == 'q' || ev.Ch == 'Q' || ev.VK == 0x1B) return null;
                int n = ev.Ch - '0';
                if (n >= 1 && n <= drives.Count) return drives[n - 1].RootDirectory.FullName;
            }
            else if (ev.Kind == Ev.Click)
                for (int i = 0; i < drives.Count; i++)
                    if (ev.Y == rowY[i]) return drives[i].RootDirectory.FullName;
        }
    }

    static void CycleSort() { CurSort = (SortMode)(((int)CurSort + 1) % 3); RebuildView(); Sel = 0; Offset = 0; }

    // ------------------------------------------------------------- main --

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--size")
        {
            long s = DirSize(args[1]);
            Console.WriteLine(s + "\t" + Human(s) + "\t" + args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--scan")
        {
            string e2 = Scan(args[1], true);
            if (e2 != null) { Console.WriteLine("ERR: " + e2); return 1; }
            RebuildView();
            long tot = 0;
            foreach (var en in View) { Console.WriteLine(Human(en.Size).PadLeft(11) + "  " + (en.IsDir ? "D" : "F") + "  " + en.Name); tot += en.Size; }
            Console.WriteLine("TOTAL: " + Human(tot) + " over " + View.Count + " items");
            return 0;
        }

        // -------- command-line flags --------
        if (args.Length >= 1 && (args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
        {
            Console.WriteLine("Citrus - disk usage explorer & cleanup tool");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  citrus                  open, then pick a drive");
            Console.WriteLine("  citrus <folder>         open straight at that folder");
            Console.WriteLine("  citrus --biggest <p>    print the biggest files under <p>");
            Console.WriteLine("  citrus --specs          write a PC specs sheet to the Desktop");
            Console.WriteLine("  citrus --version        show version");
            Console.WriteLine("  citrus --help           this help");
            Console.WriteLine();
            Console.WriteLine("In the app: ? = help  ·  T = tools  ·  arrows/scroll/click to move");
            return 0;
        }
        if (args.Length >= 1 && (args[0] == "-v" || args[0] == "--version"))
        {
            Console.WriteLine("Citrus 1.0  (by Noah - github.com/Windows-Ctrl-Shift-B)");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--specs")
        {
            SpecsSheet();
            Console.WriteLine(Status.Length > 0 ? Status.TrimStart('!', ' ', (char)0x2713) : "Specs written to Desktop.");
            return Status.StartsWith("!") ? 1 : 0;
        }
        if (args.Length == 2 && args[0] == "--biggest")
        {
            var files = new List<KeyValuePair<string, long>>();
            int[] dirs = { 0 };
            CollectFiles(args[1], 1024L * 1024, files, dirs);
            files.Sort((a, b) => b.Value.CompareTo(a.Value));
            int n = Math.Min(30, files.Count);
            for (int i = 0; i < n; i++) Console.WriteLine(Human(files[i].Value).PadLeft(11) + "  " + files[i].Key);
            return 0;
        }
        // open straight at a folder if one is passed:  citrus D:\Games
        string startPath = null;
        if (args.Length >= 1 && !args[0].StartsWith("-") && Directory.Exists(args[0])) startPath = Path.GetFullPath(args[0]);

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try { Console.CursorVisible = false; } catch { }
        Console.Title = "Citrus — Disk Usage Explorer";
        try
        {
            int ww = Math.Max(72, Math.Min(100, Console.LargestWindowWidth));
            int wh = Math.Max(20, Math.Min(32, Console.LargestWindowHeight));
            // Shrink the window first so the buffer can be resized down, then make
            // the buffer EXACTLY the window size (no scrollback). This keeps the
            // console's mouse coordinates aligned with what's drawn, so clicks on
            // the footer buttons register correctly.
            Console.SetWindowSize(Math.Min(Console.WindowWidth, ww), Math.Min(Console.WindowHeight, wh));
            Console.SetBufferSize(ww, wh);
            Console.SetWindowSize(ww, wh);
        }
        catch { }
        EnableMouse();

        try
        {
            string drive = startPath != null ? startPath : ChooseDrive();
            if (drive == null) return 0;
            CurrentPath = drive;
            ScanScreen(false);

            while (true)
            {
                Draw();
                var ev = ReadEvent();
                if (ev.Kind == Ev.Resize) { Console.Clear(); continue; }
                if (ev.Kind == Ev.Wheel)
                {
                    Sel = ev.WheelUp ? Math.Max(0, Sel - 1) : Math.Min(Math.Max(0, View.Count - 1), Sel + 1);
                    continue;
                }
                if (ev.Kind == Ev.Click)
                {
                    if (ev.Y >= ListTop && ev.Y < ListTop + ListRows)
                        OpenIndex(Offset + (ev.Y - ListTop));
                    else if (ev.Y >= FooterY - 1)   // footer row (tolerant of an off-by-one click)
                    {
                        char act = '\0';
                        foreach (var b in Buttons) if (ev.X >= b.X0 && ev.X <= b.X1) { act = b.Act; break; }
                        if (act == 'u') GoUp();
                        else if (act == 'o') OpenInExplorer();
                        else if (act == 'd') DeleteSelected();
                        else if (act == 's') CycleSort();
                        else if (act == 'b') { BiggestFilesScreen(); Console.Clear(); }
                        else if (act == 'p') { DuplicatesScreen(); Console.Clear(); }
                        else if (act == 'j') JunkScreen();
                        else if (act == 't') { ToolsMenu(); Console.Clear(); }
                        else if (act == 'h') { HelpScreen(); Console.Clear(); }
                        else if (act == 'q') return 0;
                    }
                    continue;
                }
                switch (ev.VK)
                {
                    case 0x1B: GoUp(); break;              // Esc = go back a folder, not quit
                    case 0x26: if (Sel > 0) Sel--; break;
                    case 0x28: if (Sel < View.Count - 1) Sel++; break;
                    case 0x21: Sel = Math.Max(0, Sel - 15); break;
                    case 0x22: Sel = Math.Min(Math.Max(0, View.Count - 1), Sel + 15); break;
                    case 0x24: Sel = 0; break;
                    case 0x23: Sel = Math.Max(0, View.Count - 1); break;
                    case 0x0D: case 0x27: OpenIndex(Sel); break;
                    case 0x25: case 0x08: GoUp(); break;
                    case 0x2E: DeleteSelected(); break;
                    case 0x20: // Space — tick / untick for multi-select
                        if (View.Count > 0) { View[Sel].Mark = !View[Sel].Mark; if (Sel < View.Count - 1) Sel++; }
                        break;
                    default:
                        if (ev.Ch == 'q' || ev.Ch == 'Q') return 0;
                        if (ev.Ch == 'r' || ev.Ch == 'R') ScanScreen(true);
                        if (ev.Ch == 'd' || ev.Ch == 'D') DeleteSelected();
                        if (ev.Ch == 's' || ev.Ch == 'S') CycleSort();
                        if (ev.Ch == 'o' || ev.Ch == 'O') OpenInExplorer();
                        if (ev.Ch == 'b' || ev.Ch == 'B') { BiggestFilesScreen(); Console.Clear(); }
                        if (ev.Ch == 'u' || ev.Ch == 'U') { DuplicatesScreen(); Console.Clear(); }
                        if (ev.Ch == 'j' || ev.Ch == 'J') { JunkScreen(); Console.Clear(); }
                        if (ev.Ch == 't' || ev.Ch == 'T') { ToolsMenu(); Console.Clear(); }
                        if (ev.Ch == '?') { HelpScreen(); Console.Clear(); }
                        if (ev.Ch == 'c' || ev.Ch == 'C') { string p = ChooseDrive(); if (p != null) { CurrentPath = p; ScanScreen(false); } else { Console.Clear(); } }
                        if (ev.Ch == '/') FilterInput();
                        if (ev.Ch >= '1' && ev.Ch <= '9') { int i2 = Offset + (ev.Ch - '1'); if (i2 < View.Count && (i2 - Offset) < ListRows) OpenIndex(i2); }
                        break;
                }
            }
        }
        finally
        {
            RestoreMouse();
            Console.ResetColor();
            try { Console.CursorVisible = true; } catch { }
            Console.Clear();
            Console.WriteLine("Citrus closed.");
        }
    }
}

// ===========================================================================
//  Citrus — disk usage explorer
//  Created by Noah
//  GitHub: https://github.com/Windows-Ctrl-Shift-B
//  Copyright (c) 2026 Noah. All rights reserved.
// ===========================================================================

