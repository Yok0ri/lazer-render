using LazerRender.Api.Services;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// H-4 (audit): the osu! access token and avatar key used to be handed to the engine as command-line
/// arguments, which made them readable by any local process for the whole render
/// (<c>ps auxww</c>, <c>/proc/&lt;pid&gt;/cmdline</c> — up to two hours by default). They now travel in
/// an owner-only secrets file, whose path is the only thing on the command line.
/// </summary>
public sealed class SecretsChannelTests
{
    [Fact]
    public void Credentials_are_passed_as_a_secrets_file_path_not_as_arguments()
    {
        var invocation = new RenderInvocation(
            "job1", "/tmp/replay.osr", "/tmp/config.json", "/tmp/out", "/tmp/storage",
            "cpu", DownloadMissing: false,
            SecretsFilePath: "/tmp/jobs/job1/secrets.json",
            RedactedValues: new[] { "TOKEN-VALUE", "AVATAR-KEY" });

        List<string> args = RendererProcessRunner.BuildArgs(invocation).ToList();

        Assert.Contains("--secrets-file", args);
        Assert.Contains("/tmp/jobs/job1/secrets.json", args);

        // The flags that used to carry the credential are gone, and no value leaked into the list.
        Assert.DoesNotContain("--osu-user-token", args);
        Assert.DoesNotContain("--avatar-api-key", args);
        Assert.DoesNotContain("--osu-user-token-expires-in", args);
        Assert.DoesNotContain("TOKEN-VALUE", args);
        Assert.DoesNotContain("AVATAR-KEY", args);
    }

    [Fact]
    public void No_secrets_file_is_passed_when_the_render_needs_no_credentials()
    {
        var invocation = new RenderInvocation(
            "job1", "/tmp/replay.osr", "/tmp/config.json", "/tmp/out", "/tmp/storage",
            "cpu", DownloadMissing: false, SecretsFilePath: null, RedactedValues: Array.Empty<string>());

        List<string> args = RendererProcessRunner.BuildArgs(invocation).ToList();

        Assert.DoesNotContain("--secrets-file", args);
    }

    [Fact]
    public void Every_known_credential_is_redacted_from_engine_log_lines()
    {
        const string line = "[runtime] osu! API login: injected TOKEN-VALUE for AVATAR-KEY";

        string redacted = RendererProcessRunner.Redact(line, new[] { "TOKEN-VALUE", "AVATAR-KEY" });

        Assert.DoesNotContain("TOKEN-VALUE", redacted);
        Assert.DoesNotContain("AVATAR-KEY", redacted);
        Assert.Equal("[runtime] osu! API login: injected [redacted] for [redacted]", redacted);
    }

    [Fact]
    public void Redaction_ignores_empty_entries_and_is_a_no_op_without_secrets()
    {
        const string line = "nothing sensitive here";

        Assert.Equal(line, RendererProcessRunner.Redact(line, Array.Empty<string>()));
        Assert.Equal(line, RendererProcessRunner.Redact(line, new[] { "", null! }));
    }

    /// <summary>
    /// M-7 (audit): the engine has no reason to echo a credential, but a token in a shape we were not
    /// handed explicitly must still not reach the log.
    /// </summary>
    [Fact]
    public void A_bearer_token_shape_is_redacted_even_when_not_supplied_as_a_secret()
    {
        const string line = "[runtime] request Authorization: Bearer abc123def456";

        string redacted = RendererProcessRunner.Redact(line, Array.Empty<string>());

        Assert.DoesNotContain("abc123def456", redacted);
        Assert.Contains("Bearer [redacted]", redacted);
    }
}
