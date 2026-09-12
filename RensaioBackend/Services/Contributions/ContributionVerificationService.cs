using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// The response shape returned by the cloud contribution worker
/// (RensaioContributionDB.CF) at GET /contributor?contributor={UUID}.
/// </summary>
public class ContributorVerificationResponse
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("admin")]
    public bool Admin { get; set; }

    [JsonPropertyName("ban_reason")]
    public string? BanReason { get; set; }
}

/// <summary>
/// Result of a contributor verification attempt against the cloud
/// contribution database (RensaioContributionDB.CF).
/// </summary>
public sealed class ContributionVerificationResult
{
    public bool Verified { get; init; }

    public string? Error { get; init; }

    public string? BanReason { get; init; }

    public bool IsAdmin { get; init; }

    public static ContributionVerificationResult Success(bool isAdmin) =>
        new() { Verified = true, IsAdmin = isAdmin };

    public static ContributionVerificationResult Failed(string error) =>
        new() { Verified = false, Error = error };

    public static ContributionVerificationResult Banned(string banReason) =>
        new() { Verified = false, Error = "Contributor is banned.", BanReason = banReason };
}

/// <summary>
/// Verifies a Contributor Id against the cloud contribution database
/// (RensaioContributionDB.CF) using the worker's public, read-only
/// GET /contributor?contributor={UUID} endpoint. A contributor is considered
/// <b>verified</b> when the worker returns HTTP 200 with <c>active: true</c>.
/// </summary>
public class ContributionVerificationService
{
    private const string HttpClientName = "ContributionVerification";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ContributionVerificationService> _logger;

    public ContributionVerificationService(
        IHttpClientFactory httpClientFactory,
        ILogger<ContributionVerificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether a Contributor Id is registered and active on the given
    /// contribution server. Network failures, malformed responses, or any HTTP
    /// status other than 200 translate into a non-verified result with an error.
    /// </summary>
    /// <param name="serverUrl">Base URL of the contribution worker, e.g. https://contribution.rensaio.net</param>
    /// <param name="contributorId">Contributor UUID to verify.</param>
    /// <param name="token">Cancellation token.</param>
    public async Task<ContributionVerificationResult> VerifyAsync(
        string serverUrl,
        string contributorId,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(contributorId))
        {
            return ContributionVerificationResult.Failed("Contributor Id is empty.");
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return ContributionVerificationResult.Failed("Contribution server URL is empty.");
        }

        string endpoint = serverUrl.TrimEnd('/') +
            "/contributor?contributor=" + Uri.EscapeDataString(contributorId);

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(endpoint, token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return ContributionVerificationResult.Failed(
                    "Contributor not found in the contribution database.");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var banned = await ReadBodyAsync<ContributorVerificationResponse>(response, token).ConfigureAwait(false);
                return ContributionVerificationResult.Banned(banned?.BanReason ?? "no reason provided");
            }

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return ContributionVerificationResult.Failed(
                    $"Contribution server rejected verification ({response.StatusCode}): {Truncate(body, 300)}");
            }

            var contributor = await ReadBodyAsync<ContributorVerificationResponse>(response, token).ConfigureAwait(false);
            if (contributor == null)
            {
                return ContributionVerificationResult.Failed(
                    "Contribution server returned an unexpected response.");
            }

            if (!contributor.Active)
            {
                return ContributionVerificationResult.Banned(contributor.BanReason ?? "no reason provided");
            }

            return ContributionVerificationResult.Success(contributor.Admin);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Contribution verification HTTP failure for contributor {ContributorId}", contributorId);
            return ContributionVerificationResult.Failed(
                $"Failed to reach contribution server: {ex.Message}");
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpResponseMessage response, CancellationToken token)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}