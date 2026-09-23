// Test adapter for private helpers; never invokes the interactive UI or cleanup tools.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

class WindowsProbe
{
    static int Main(string[] args)
    {
        try
        {
            Type app = Assembly.LoadFrom(args[0]).GetType("Strata");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            if (args[1] == "zip")
                app.GetMethod("MakeZip", flags).Invoke(null, new object[] { args[2], args[3] });
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
            Console.WriteLine("peak_working_set_bytes=" + Process.GetCurrentProcess().PeakWorkingSet64);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
