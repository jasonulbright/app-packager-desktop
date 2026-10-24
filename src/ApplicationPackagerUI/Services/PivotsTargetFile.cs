using System.IO;
using System.Text;

namespace ApplicationPackagerUI.Services;

/// <param name="Dropped">The CSV names that hold a line break, a comma, a semicolon or another control character: they are not imported.</param>
public sealed record TargetFileNames(IReadOnlyList<string> Names, int Dropped);

/// <summary>Reads device names from a TXT file (one per line) or a CSV file (the Name, Device, ComputerName or Hostname column, else the first column).</summary>
public static class PivotsTargetFile
{
    public const long MaxBytes = 4L * 1024 * 1024;
    public const int MaxNames = 50_000;

    private static readonly string[] NameColumns = ["Name", "Device", "DeviceName", "ComputerName", "Computer", "Hostname", "Host"];

    /// <summary>The distinct names in file order; a name is trimmed and an empty one is skipped.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/> or holds more than <see cref="MaxNames"/> names.</exception>
    public static IReadOnlyList<string> Read(string path) => ReadNames(path).Names;

    /// <summary>The distinct names in file order, and the number of CSV names that were not imported.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/> or holds more than <see cref="MaxNames"/> names.</exception>
    public static TargetFileNames ReadNames(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new InvalidDataException($"The file is larger than {MaxBytes / (1024 * 1024)} MiB.");
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? CsvNames(text) : new TargetFileNames(FromLines(text), 0);
    }

    public static IReadOnlyList<string> FromLines(string text) => Distinct(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<string> FromCsv(string text) => CsvNames(text).Names;

    /// <summary>
    /// The names of the name column of a CSV text. Quoted fields can hold the separator, doubled
    /// quotes and line breaks. The separator is the first comma, semicolon or tab outside quotes in
    /// the first record. A name that holds a line break, a separator of a list target or another
    /// control character is not imported, and <see cref="TargetFileNames.Dropped"/> counts it: the
    /// List target would split it into other names.
    /// </summary>
    public static TargetFileNames CsvNames(string text)
    {
        var rows = Records(text, SeparatorOf(text)).Where(r => r.Count > 1 || r[0].Trim().Length > 0).ToList();
        if (rows.Count == 0) return new TargetFileNames([], 0);
        var header = rows[0];
        var column = header.FindIndex(h => NameColumns.Contains(h.Trim().Trim('﻿'), StringComparer.OrdinalIgnoreCase));
        var body = column >= 0 ? rows.Skip(1) : rows;
        if (column < 0) column = 0;
        var cells = body.Select(r => column < r.Count ? r[column] : "").ToList();
        static bool Unsafe(string n) => n.Trim().Any(c => c is ',' or ';' || char.IsControl(c));
        return new TargetFileNames(Distinct(cells.Where(n => !Unsafe(n))), cells.Count(Unsafe));
    }

    private static char SeparatorOf(string text)
    {
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is ',' or ';' or '\t') return c;
            else if (!quoted && c is '\r' or '\n') break;
        }
        return ',';
    }

    /// <summary>The records of a CSV text (RFC 4180): a line break inside quotes belongs to the field.</summary>
    private static List<List<string>> Records(string text, char separator)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { fields.Add(current.ToString()); current.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(current.ToString());
                current.Clear();
                records.Add(fields);
                fields = [];
            }
            else current.Append(c);
        }
        if (current.Length > 0 || fields.Count > 0)
        {
            fields.Add(current.ToString());
            records.Add(fields);
        }
        return records;
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
