using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One entry of the certificate picker; an empty thumbprint is the no-selection entry.</summary>
public sealed record SigningCertificateOption(string Thumbprint, string Label, CodeSigningCandidate? Candidate)
{
    public const string NoneLabel = "(none selected)";
    public override string ToString() => Label;
}

/// <summary>
/// The Script Signing section of Settings. Edits stay here until the page saves; the
/// certificate is chosen by thumbprint only, never by subject or list position.
/// </summary>
public partial class ScriptSigningEditor : ObservableObject
{
    public static readonly string[] StoreLocations = [SigningPolicy.CurrentUser, SigningPolicy.LocalMachine];

    /// <summary>The digest every signature uses; Settings offers no other.</summary>
    public const string FixedHashAlgorithm = "SHA256";

    private readonly Func<SigningCertificateService> _certificates;
    private readonly Func<SigningPolicy, SigningConfigurationTestResult> _test;
    private string _thumbprint = "";
    private bool _loading;

    [ObservableProperty] private bool _signDetection;
    [ObservableProperty] private bool _signRequirements;
    [ObservableProperty] private bool _signDeployment;
    [ObservableProperty] private bool _requireDetection;
    [ObservableProperty] private bool _requireRequirements;
    [ObservableProperty] private bool _requireDeployment;
    [ObservableProperty] private string _storeLocation = SigningPolicy.CurrentUser;
    [ObservableProperty] private string _timestampServer = "";
    [ObservableProperty] private bool _timestampRequired;
    [ObservableProperty] private SigningCertificateOption? _selectedCertificate;
    [ObservableProperty] private string _certificateDetail = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testBusy;

    public ObservableCollection<SigningCertificateOption> Certificates { get; } = [];
    public IReadOnlyList<string> StoreLocationOptions => StoreLocations;

    public bool TestIdle => !TestBusy;
    partial void OnTestBusyChanged(bool value) => OnPropertyChanged(nameof(TestIdle));

    public ScriptSigningEditor(Func<SigningCertificateService>? certificates = null,
        Func<SigningPolicy, SigningConfigurationTestResult>? test = null)
    {
        _certificates = certificates ?? (() => new SigningCertificateService());
        _test = test ?? (policy => new ScriptSigningService(_certificates()).TestConfiguration(policy));
    }

    /// <summary>Shows a stored policy. The certificate list is read for its store location.</summary>
    public void Load(SigningPolicy? stored)
    {
        var policy = SigningPolicy.FromSettings(stored);
        _loading = true;
        try
        {
            SignDetection = policy.SignDetection;
            SignRequirements = policy.SignRequirements;
            SignDeployment = policy.SignDeployment;
            RequireDetection = policy.RequireDetection;
            RequireRequirements = policy.RequireRequirements;
            RequireDeployment = policy.RequireDeployment;
            StoreLocation = policy.StoreLocation;
            TimestampServer = policy.TimestampServer;
            TimestampRequired = policy.TimestampRequired;
            _thumbprint = policy.CertificateThumbprint;
        }
        finally
        {
            _loading = false;
        }
        TestResult = "";
        RefreshCertificates();
    }

    /// <summary>The policy the controls describe, normalized the way settings store it.</summary>
    public SigningPolicy ToPolicy() => SigningPolicy.FromSettings(new SigningPolicy
    {
        SignDetection = SignDetection,
        SignRequirements = SignRequirements,
        SignDeployment = SignDeployment,
        RequireDetection = RequireDetection,
        RequireRequirements = RequireRequirements,
        RequireDeployment = RequireDeployment,
        CertificateThumbprint = SelectedCertificate?.Thumbprint ?? _thumbprint,
        StoreLocation = StoreLocation,
        TimestampServer = TimestampServer,
        TimestampRequired = TimestampRequired,
        HashAlgorithm = FixedHashAlgorithm
    });

    partial void OnStoreLocationChanged(string value)
    {
        if (!_loading) RefreshCertificates();
    }

    partial void OnSelectedCertificateChanged(SigningCertificateOption? value)
    {
        if (value is not null) _thumbprint = value.Thumbprint;
        CertificateDetail = DescribeSelection(value);
    }

    /// <summary>Reads the store again and keeps the selected thumbprint when it is still listed.</summary>
    [RelayCommand]
    public void RefreshCertificates()
    {
        var keep = _thumbprint;
        Certificates.Clear();
        Certificates.Add(new SigningCertificateOption("", SigningCertificateOption.NoneLabel, null));
        IReadOnlyList<CodeSigningCandidate> candidates = [];
        string? failure = null;
        try { candidates = _certificates().ListCandidates(StoreLocation); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { failure = ex.Message; }
        foreach (var c in candidates)
            Certificates.Add(new SigningCertificateOption(c.Thumbprint, Label(c), c));

        SelectedCertificate = Certificates.FirstOrDefault(o => o.Thumbprint.Length > 0
                                  && string.Equals(o.Thumbprint, keep, StringComparison.OrdinalIgnoreCase))
                              ?? Certificates[0];
        if (SelectedCertificate.Thumbprint.Length == 0) _thumbprint = keep;
        if (failure is not null)
            CertificateDetail = "Certificate enumeration failed: " + failure;
        else if (candidates.Count == 0)
            CertificateDetail = $@"No code-signing certificate with a private key was found in {StoreLocation}\My.";
    }

    public static string Label(CodeSigningCandidate c) =>
        $"{c.Subject}  (expires {c.NotAfter:yyyy-MM-dd})" + (c.Usable ? "" : "  [unusable]");

    private static string DescribeSelection(SigningCertificateOption? option)
    {
        if (option?.Candidate is not { } c)
            return "No certificate selected. Signing cannot run until one is chosen by thumbprint.";
        return string.Join("   ",
            "Subject: " + c.Subject,
            "Issuer: " + c.Issuer,
            $"Valid: {c.NotBefore:yyyy-MM-dd} to {c.NotAfter:yyyy-MM-dd}",
            "Thumbprint: " + c.Thumbprint,
            "Usable: " + (c.Usable ? "yes" : "no - " + c.Reason));
    }

    /// <summary>
    /// Signs and verifies a temporary file with the selected certificate off the UI
    /// thread. Nothing is published and nothing is saved.
    /// </summary>
    [RelayCommand]
    public async Task TestConfigurationAsync()
    {
        if (TestBusy) return;
        TestBusy = true;
        TestResult = "Testing...";
        try
        {
            var policy = ToPolicy();
            var result = await Task.Run(() => _test(policy));
            TestResult = FormatResult(result);
        }
        catch (Exception ex)
        {
            TestResult = "Test failed: " + ex.Message;
        }
        finally
        {
            TestBusy = false;
        }
    }

    /// <summary>The test outcome with signature integrity and host trust stated separately.</summary>
    public static string FormatResult(SigningConfigurationTestResult result) =>
        string.Join(" ", new[]
        {
            $"Status: {result.SignatureStatus}.",
            $"Signature intact: {(result.SignatureIntact ? "yes" : "no")}.",
            $"Trusted on this host: {(result.TrustedOnThisHost ? "yes" : "no")}.",
            $"Timestamp verified: {result.TimestampVerified}.",
            result.Reason
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
