using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The cell quoting and the new-file write that the CSV and HTML report writers share.</summary>
internal static class ReportFile
{
    /// <summary>A quoted CSV cell; a value a spreadsheet would read as a formula starts with an apostrophe.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 with a byte order mark to a new file
    /// &lt;prefix&gt;-&lt;stamp&gt;.&lt;extension&gt; in <paramref name="folder"/>; an existing name gets a
    /// number. The folder must already be checked for links.
    /// </summary>
    public static string WriteNew(string folder, string prefix, string extension, string content, DateTimeOffset now)
    {
        Directory.CreateDirectory(folder);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{prefix}-{stamp}.{extension}" : $"{prefix}-{stamp}-{n}.{extension}";
            var path = Path.Combine(folder, name);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, encoding);
                writer.Write(content);
                return path;
            }
            catch (IOException) when (File.Exists(path) && n < 1000)
            {
            }
        }
    }
}
