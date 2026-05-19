using System.Net.Http;

namespace DotnetAssemblyMcp.Server;

/// <summary>
/// Implements the <c>--health-check</c> CLI flag. Probes the HTTP <c>/health</c>
/// endpoint and returns a process exit code (0 healthy, 1 anything else). Used
/// by systemd <c>ExecStartPre</c>, Docker HEALTHCHECK fallbacks, and any other
/// supervisor that prefers an exec probe over an httpGet probe.
/// </summary>
internal static class HealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            await Console.Error.WriteLineAsync("health-check: empty URL").ConfigureAwait(false);
            return 1;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Console.Error.WriteLineAsync($"health-check: invalid URL '{url}'").ConfigureAwait(false);
            return 1;
        }

        using var client = new HttpClient { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(uri).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync(
                $"health-check: {uri} returned HTTP {(int)response.StatusCode}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"health-check: {uri} failed — {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
