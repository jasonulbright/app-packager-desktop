using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests;

public sealed class SecretMaskTests
{
    [Theory]
    [InlineData("DB_PASSWORD=Zq9secret", "Zq9secret")]
    [InlineData("userPassword=Zq9secret next", "Zq9secret")]
    [InlineData("ClientSecretValue=Zq9secret", "Zq9secret")]
    [InlineData("my-api-key: Zq9secret", "Zq9secret")]
    [InlineData("{\"password\":\"Zq9secret\",\"user\":\"lab\\\\admin\"}", "Zq9secret")]
    [InlineData("{ \"Password\" : \"Zq9 secret with spaces\" }", "Zq9 secret with spaces")]
    [InlineData("{\"client_secret\":\"Zq9secret\"}", "Zq9secret")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("authorization=Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("The request sent Bearer 0a1b2c3d4e5f6a7b8c9d and failed.", "0a1b2c3d4e5f6a7b8c9d")]
    [InlineData("password=Hunter2 next", "Hunter2")]
    [InlineData("-Password 'P@ss word'", "P@ss word")]
    public void ASecretInEachKnownShapeIsMasked(string text, string secret)
    {
        var masked = SecretMask.Apply(text);
        Assert.DoesNotContain(secret, masked);
        Assert.Contains(SecretMask.Mask, masked);
    }

    [Fact]
    public void TheKeyAndTheJsonQuotesStayReadable()
    {
        Assert.Equal("DB_PASSWORD=***", SecretMask.Apply("DB_PASSWORD=Zq9secret"));
        Assert.Equal("{\"password\":\"***\",\"user\":\"lab\"}", SecretMask.Apply("{\"password\":\"Zq9secret\",\"user\":\"lab\"}"));
        Assert.Equal("Authorization: Basic ***", SecretMask.Apply("Authorization: Basic dXNlcjpwYXNzd29yZA=="));
        Assert.Equal("Bearer *** sent", SecretMask.Apply("Bearer eyJhbGciOiJIUzI1NiJ9.e30.c2ln sent"));
    }

    [Theory]
    [InlineData("Enter the password in Settings.")]
    [InlineData("The token expired at 10:00.")]
    [InlineData("Passwordless sign-in is on.")]
    [InlineData("tokens: 5")]
    [InlineData("TokenCount=5")]
    [InlineData("accessTokenExpiry: 300")]
    [InlineData("Basic settings: saved")]
    [InlineData("The bearer instruments were counted.")]
    [InlineData("Bearer of bad news")]
    [InlineData("DB_PASSWORD_FILE=C:\\secrets\\db.txt")]
    [InlineData("Collection SMS00001 has 3 members; site ABC.")]
    [InlineData("{\"Name\":\"7-Zip\",\"Version\":\"24.09\"}")]
    public void OrdinaryTextIsLeftAlone(string text)
    {
        Assert.Equal(text, SecretMask.Apply(text));
    }

    [Fact]
    public void AKnownSecretIsMaskedWhereverItAppears()
    {
        Assert.Equal("bad value *** here", SecretMask.Apply("bad value s3cr3t here", ["s3cr3t"]));
    }
}
