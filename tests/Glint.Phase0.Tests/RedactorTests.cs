using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class RedactorTests
{
    private readonly DeterministicRedactor _redactor = new();

    [Fact]
    public void RedactsFieldSecretsBeforeShapeRules()
    {
        var result = _redactor.Redact(
            "api_key=AKIA1234567890ABCDEF email=user@example.com");

        Assert.DoesNotContain("AKIA1234567890ABCDEF", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", result.Text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:field]", result.Text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:email]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsJwtAndHighEntropyToken()
    {
        var result = _redactor.Redact(
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.ABCD_efgh-1234567890 " +
            "aB9+xY2_zQ7=K8pLmN4rT6vW");

        Assert.Equal(2, result.Total);
        Assert.DoesNotContain("eyJ", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DropsPrivateKeyFrames()
    {
        var reason = SecretSniffer.ShouldDrop(
            "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----");

        Assert.Equal("private-key-material", reason);
    }

    [Fact]
    public void DoesNotTreatOrdinaryTextAsSecretFrame()
    {
        Assert.Null(SecretSniffer.ShouldDrop("Working on the migration and tests."));
    }
}
