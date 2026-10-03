using System.Globalization;
using System.Runtime.InteropServices;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

public sealed class AnsiEncodingTests
{
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void Ansi_IsTheSystemCodePage_NotTheCurrentCultures(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal((int)GetACP(), ScriptSignatureVerifier.AnsiEncoding().CodePage);
            Assert.Equal((int)GetACP(), ScriptSignatureVerifier.EncodingByName("ANSI").CodePage);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
