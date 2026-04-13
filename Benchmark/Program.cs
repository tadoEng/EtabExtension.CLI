using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public class BenchmarkTableEditing
{
    public static void Main()
    {
        var rows = new List<Dictionary<string, string>>();
        for (int i = 0; i < 10000; i++)
        {
            rows.Add(new Dictionary<string, string> { { "key", $"val{i}" }, { "target", "1.0" } });
        }

        var scaleFactors = new Dictionary<string, double>();
        for (int i = 0; i < 1000; i++)
        {
            scaleFactors.Add($"val{i * 10}", 2.0); // 1000 updates
        }

        var keyField = "key";

        // Baseline (O(N*M))
        var sw1 = Stopwatch.StartNew();
        int modified1 = 0;
        foreach (var (keyValue, factor) in scaleFactors)
        {
            var row = rows.FirstOrDefault(r =>
                r.TryGetValue(keyField, out var v) &&
                string.Equals(v, keyValue, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                modified1++;
            }
        }
        sw1.Stop();
        Console.WriteLine($"Baseline: {sw1.ElapsedMilliseconds} ms, modified: {modified1}");

        // Optimized (O(N+M))
        var sw2 = Stopwatch.StartNew();
        int modified2 = 0;
        var rowLookup = rows
            .Where(r => r.ContainsKey(keyField))
            .GroupBy(r => r[keyField], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (keyValue, factor) in scaleFactors)
        {
            if (rowLookup.TryGetValue(keyValue, out var row))
            {
                modified2++;
            }
        }
        sw2.Stop();
        Console.WriteLine($"Optimized: {sw2.ElapsedMilliseconds} ms, modified: {modified2}");
    }
}
