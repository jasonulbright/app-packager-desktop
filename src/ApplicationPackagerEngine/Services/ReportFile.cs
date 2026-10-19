using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The cell quoting and the new-file write that the CSV and HTML report writers share.</summary>
internal static class ReportFile
{
    public const int MaxNames = 1000;

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
    public static string WriteNew(string folder, string prefix, string extension, string content, DateTimeOffset now) =>
        WriteNew(folder, prefix, extension, w => w.Write(content), now);

    /// <summary>
    /// Only the create of the file tries the next name; a failure while writing deletes the
    /// partial file and is thrown. After <paramref name="maxNames"/> taken names an
    /// <see cref="IOException"/> is thrown.
    /// </summary>
    public static string WriteNew(string folder, string prefix, string extension, Action<TextWriter> write, DateTimeOffset now, int maxNames = MaxNames)
    {
        Directory.CreateDirectory(folder);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        for (var n = 1; n <= maxNames; n++)
        {
            var name = n == 1 ? $"{prefix}-{stamp}.{extension}" : $"{prefix}-{stamp}-{n}.{extension}";
            var path = Path.Combine(folder, name);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }

            try
            {
                using (var writer = new StreamWriter(stream, encoding))
                    write(writer);
                return path;
            }
            catch
            {
                stream.Dispose();
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        throw new IOException("No free report file name was found.");
    }
}
