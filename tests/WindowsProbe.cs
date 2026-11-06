// Test adapter for private helpers; never invokes the interactive UI or cleanup tools.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class WindowsProbe
{
    static int Main(string[] args)
    {
        try
        {
            Type app = Assembly.LoadFrom(args[0]).GetType("Strata");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var process = Process.GetCurrentProcess();
            double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            var elapsed = Stopwatch.StartNew();
            if (args[1] == "zip")
                app.GetMethod("MakeZip", flags).Invoke(null, new object[] { args[2], args[3] });
            else if (args[1] == "crc")
                CheckCrc(app, flags);
            else if (args[1] == "scan")
                BenchmarkScan(app, flags, args[2], int.Parse(args[3], CultureInfo.InvariantCulture));
            else if (args[1] == "scan-check")
                CheckScan(app, flags, args[2]);
            else if (args[1] == "top")
            {
                var keep = app.GetMethod("KeepLargest", flags);
                var random = new Random(42);
                foreach (int limit in new[] { 1, 30, 500, 2000 })
                {
                    var rows = new List<KeyValuePair<string, long>>();
                    var expected = new List<long>();
                    for (int i = 0; i < 1000; i++)
                    {
                        long size = random.Next(100);
                        expected.Add(size);
                        keep.Invoke(null, new object[] { rows, i.ToString(), size, limit });
                        if (rows.Count > limit) throw new Exception("Result limit exceeded");
                    }
                    if (!rows.Select(x => x.Value).OrderByDescending(x => x).SequenceEqual(expected.OrderByDescending(x => x).Take(limit)))
                        throw new Exception("Incorrect largest files");
                }
            }
            elapsed.Stop();
            Console.WriteLine("elapsed_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
            Console.WriteLine("cpu_ms=" + (process.TotalProcessorTime.TotalMilliseconds - cpuStart).ToString("F3", CultureInfo.InvariantCulture));
            Console.WriteLine("peak_working_set_bytes=" + process.PeakWorkingSet64);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    // Independent polynomial implementation, deliberately using no lookup tables.
    static uint ReferenceCrc(uint crc, byte[] bytes, int count)
    {
        for (int i = 0; i < count; i++)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }

    static void CheckCrc(Type app, BindingFlags flags)
    {
        var update = (Func<uint, byte[], int, uint>)Delegate.CreateDelegate(
            typeof(Func<uint, byte[], int, uint>), app.GetMethod("Crc32", flags));
        byte[] vector = Encoding.ASCII.GetBytes("123456789");
        if ((update(0xFFFFFFFFu, vector, vector.Length) ^ 0xFFFFFFFFu) != 0xCBF43926u)
            throw new Exception("CRC32 known vector mismatch");
        var random = new Random(43);
        foreach (int length in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 63, 64, 65, 4097, 65536 })
        {
            var data = new byte[length + 9]; // nonzero tail must be ignored by count
            random.NextBytes(data);
            foreach (uint seed in new[] { 0u, 0xFFFFFFFFu, 0x12345678u })
                if (update(seed, data, length) != ReferenceCrc(seed, data, length))
                    throw new Exception("CRC32 count/seed mismatch at length " + length);
            uint expected = ReferenceCrc(0xFFFFFFFFu, data, length), actual = 0xFFFFFFFFu;
            int offset = 0;
            var chunk = new byte[19];
            while (offset < length)
            {
                int count = Math.Min(random.Next(1, chunk.Length + 1), length - offset);
                Array.Copy(data, offset, chunk, 0, count);
                actual = update(actual, chunk, count);
                offset += count;
            }
            if (actual != expected) throw new Exception("Incremental CRC32 mismatch at length " + length);
        }
    }

    // Twenty warm-fixture passes reduce startup/timer noise; outputs are per pass.
    // Scans only the named fixture; excludes the interactive UI and reflection overhead.
    static void BenchmarkScan(Type app, BindingFlags flags, string root, int workers)
    {
        if (workers < 1) throw new ArgumentOutOfRangeException("workers");
        var measure = (Func<string, long>)Delegate.CreateDelegate(
            typeof(Func<string, long>), app.GetMethod("DirSize", flags));
        string[] directories = Directory.GetDirectories(root);
        long total = 0;
        var process = Process.GetCurrentProcess();
        double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        var beginMethod = app.GetMethod("BeginScanWork", flags);
        var endMethod = app.GetMethod("EndScanWork", flags);
        var begin = beginMethod == null ? null : (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), beginMethod);
        var end = endMethod == null ? null : (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), endMethod);
        const int repeats = 20;
        for (int pass = 0; pass < repeats; pass++)
        {
            Parallel.ForEach<string, int>(directories, new ParallelOptions { MaxDegreeOfParallelism = workers },
                () => begin == null ? 0 : begin(),
                (directory, state, priority) =>
                {
                    Interlocked.Add(ref total, measure(directory));
                    return priority;
                },
                priority => { if (end != null) end(priority); });
            foreach (string file in Directory.EnumerateFiles(root)) total += new FileInfo(file).Length;
        }
        watch.Stop();
        Console.WriteLine("scan_elapsed_ms=" + (watch.Elapsed.TotalMilliseconds / repeats).ToString("F3", CultureInfo.InvariantCulture));
        Console.WriteLine("scan_cpu_ms=" + ((process.TotalProcessorTime.TotalMilliseconds - cpuStart) / repeats).ToString("F3", CultureInfo.InvariantCulture));
        Console.WriteLine("scan_total_bytes=" + total / repeats);
        Console.WriteLine("scan_workers=" + workers);
        Console.WriteLine("scan_repeats=" + repeats);
    }

    // The caller owns the temporary parent directory and its eventual cleanup.
    static void CheckScan(Type app, BindingFlags flags, string temporaryRoot)
    {
        var workers = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), app.GetMethod("ScanWorkers", flags));
        int[] processors = { 1, 2, 4, 128 }, expectedWorkers = { 1, 1, 3, 4 };
        for (int i = 0; i < processors.Length; i++)
            if (workers(processors[i]) != expectedWorkers[i]) throw new Exception("Incorrect scan worker budget");

        var begin = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), app.GetMethod("BeginScanWork", flags));
        var end = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), app.GetMethod("EndScanWork", flags));
        ThreadPriority originalPriority = Thread.CurrentThread.Priority;
        int? firstState = null;
        for (int pass = 0; pass < 2; pass++)
        {
            int previous = begin();
            try
            {
                if (firstState.HasValue && previous != firstState.Value)
                    throw new Exception("Background mode was not restored between scan workers");
                firstState = previous;
                if (previous != int.MinValue && Thread.CurrentThread.Priority != ThreadPriority.BelowNormal)
                    throw new Exception("Scan priority fallback was not applied");
            }
            finally { end(previous); }
            if (Thread.CurrentThread.Priority != originalPriority)
                throw new Exception("Scan work leaked its thread priority");
        }

        string root = Path.Combine(Path.GetFullPath(temporaryRoot), "scan-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long expectedTotal = 0;
        for (int i = 0; i < 48; i++)
        {
            string path = Path.Combine(root, "file-" + i.ToString("D2", CultureInfo.InvariantCulture));
            int size = 64 + i;
            File.WriteAllBytes(path, new byte[size]);
            expectedTotal += size;
            if (size >= 80) expected[path] = size;
        }
        string unicode = Path.Combine(nested, "caf\u00e9.txt");
        File.WriteAllBytes(unicode, new byte[256]);
        expected[unicode] = 256;
        File.WriteAllBytes(Path.Combine(root, "tiny"), new byte[3]);
        expectedTotal += 259;

        var measure = (Func<string, long>)Delegate.CreateDelegate(typeof(Func<string, long>), app.GetMethod("DirSize", flags));
        var walk = (Action<string, long, int[], Action<string, long, long>>)Delegate.CreateDelegate(
            typeof(Action<string, long, int[], Action<string, long, long>>), app.GetMethod("WalkFiles", flags));
        var cancel = app.GetField("CancelScan", flags);
        try
        {
            cancel.SetValue(null, false);
            if (measure(root) != expectedTotal) throw new Exception("Incorrect directory byte total");
            var actual = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            walk(root, 80, new int[1], (path, size, modified) => actual.Add(path, size));
            if (actual.Count != expected.Count || expected.Any(row => !actual.ContainsKey(row.Key) || actual[row.Key] != row.Value))
                throw new Exception("Filtered walk returned incorrect file paths or sizes");
            int callbacks = 0;
            walk(root, 0, new int[1], (path, size, modified) =>
            {
                callbacks++;
                cancel.SetValue(null, true);
            });
            if (callbacks != 1) throw new Exception("Cancelled walk continued inside the wide directory");
            if (measure(root) != 0) throw new Exception("Pre-cancelled size scan did not stop");
        }
        finally { cancel.SetValue(null, false); }
    }
}
