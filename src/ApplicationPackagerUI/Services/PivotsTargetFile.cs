using System.IO;
using System.Text;

namespace ApplicationPackagerUI.Services;

/// <summary>Reads device names from a TXT file (one per line) or a CSV file (the Name, Device, ComputerName or Hostname column, else the first column).</summary>
public static class PivotsTargetFile
{
    public const long MaxBytes = 4L * 1024 * 1024;
    public const int MaxNames = 50_000;

    private static readonly string[] NameColumns = ["Name", "Device", "DeviceName", "ComputerName", "Computer", "Hostname", "Host"];

    /// <summary>The distinct names in file order; a name is trimmed and an empty one is skipped.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/> or holds more than <see cref="MaxNames"/> names.</exception>
    public static IReadOnlyList<string> Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new InvalidDataException($"The file is larger than {MaxBytes / (1024 * 1024)} MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? FromCsv(text) : FromLines(text);
    }

    public static IReadOnlyList<string> FromLines(string text) => Distinct(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<string> FromCsv(string text)
    {
        var rows = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Fields).Where(r => r.Count > 0).ToList();
        if (rows.Count == 0) return [];
        var header = rows[0];
        var column = header.FindIndex(h => NameColumns.Contains(h.Trim(), StringComparer.OrdinalIgnoreCase));
        var body = column >= 0 ? rows.Skip(1) : rows;
        if (column < 0) column = 0;
        return Distinct(body.Select(r => column < r.Count ? r[column] : ""));
    }

    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c is ',' or ';' or '\t') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim().Trim('﻿');
            if (name.Length == 0 || !seen.Add(name)) continue;
            if (list.Count >= MaxNames) throw new InvalidDataException($"The file holds more than {MaxNames} names.");
            list.Add(name);
        }
        return list;
    }
}
