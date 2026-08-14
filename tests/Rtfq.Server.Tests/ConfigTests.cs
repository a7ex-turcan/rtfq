using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Tests;

public class ConfigLoaderTests
{
    const string Minimal = """
        server:
          listen: 127.0.0.1:7420
          auth:
            mode: token
            tokens:
              - id: agent
                secret: ${env:RTFQ_TEST_TOKEN}
                grants:
                  orders: read
        sources:
          - name: orders
            kind: postgres
            dsn: ${env:RTFQ_TEST_DSN}
        """;

    [Fact]
    public void Loads_a_minimal_config()
    {
        Environment.SetEnvironmentVariable("RTFQ_TEST_TOKEN", "s3cret");
        Environment.SetEnvironmentVariable("RTFQ_TEST_DSN", "Host=localhost;Database=orders");

        var result = ConfigLoader.LoadText(Minimal);

        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics));
        Assert.NotNull(result.Config);
        Assert.Single(result.Config!.Sources);
        Assert.Equal("orders", result.Config.Sources[0].Name);
        Assert.Equal(AccessLevel.Read, result.Config.Sources[0].Access);
        Assert.True(result.Config.Server.Auth.Tokens[0].SecretWasReference);
        Assert.Equal("s3cret", result.Config.Server.Auth.Tokens[0].Secret);
    }

    [Fact]
    public void Absent_access_means_read_never_write()
    {
        Environment.SetEnvironmentVariable("RTFQ_TEST_TOKEN", "s3cret");
        Environment.SetEnvironmentVariable("RTFQ_TEST_DSN", "Host=localhost");

        var config = ConfigLoader.LoadText(Minimal).Config!;

        Assert.Equal(AccessLevel.Read, config.Sources[0].Access);
    }

    [Fact]
    public void Unknown_key_is_an_error_with_a_line_number()
    {
        var yaml = """
            server:
              listen: 127.0.0.1:7420
              acess: write
              auth:
                mode: token
                tokens: []
            """;

        var result = ConfigLoader.LoadText(yaml);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Check == "config.unknown_key");
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains("acess", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(3, diagnostic.Line);
    }

    [Fact]
    public void Missing_environment_variable_is_reported_not_silently_empty()
    {
        Environment.SetEnvironmentVariable("RTFQ_DEFINITELY_NOT_SET", null);
        var yaml = """
            server:
              listen: 127.0.0.1:7420
              auth:
                mode: token
                tokens:
                  - id: agent
                    secret: ${env:RTFQ_DEFINITELY_NOT_SET}
            """;

        var result = ConfigLoader.LoadText(yaml);

        Assert.Contains(result.Diagnostics, d => d.Check == "config.secret_unresolved" && d.Severity == Severity.Error);
    }

    [Fact]
    public void File_secrets_are_read_and_trimmed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "  file-secret\n");
            var resolution = SecretResolver.Resolve($"${{file:{path}}}");

            Assert.Null(resolution.Error);
            Assert.True(resolution.WasReference);
            Assert.Equal("file-secret", resolution.Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Vault_references_fail_loudly_rather_than_resolving_to_nothing()
    {
        var resolution = SecretResolver.Resolve("${vault:secret/data/pg}");

        Assert.NotNull(resolution.Error);
        Assert.Contains("not supported", resolution.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("15s", 15)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    public void Durations_use_operator_notation(string text, int expectedSeconds)
    {
        Assert.True(Duration.TryParse(text, out var value));
        Assert.Equal(expectedSeconds, (int)value.TotalSeconds);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("")]
    [InlineData("fifteen")]
    [InlineData("15x")]
    public void Malformed_durations_are_rejected(string text) =>
        Assert.False(Duration.TryParse(text, out _));

    [Theory]
    [InlineData("Host=db;Password=hunter2", true)]
    [InlineData("postgres://user:hunter2@db/orders", true)]
    [InlineData("Host=db;Username=rtfq", false)]
    [InlineData("postgres://db/orders", false)]
    public void Inline_passwords_are_detected_in_both_dsn_forms(string dsn, bool expected) =>
        Assert.Equal(expected, SecretResolver.LooksLikeInlineSecret(dsn));
}
