using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace RensaioBackend.Services.Auth.Oidc;

/// <summary>
/// Caches the provider's discovery document and signing keys per issuer.
/// Singleton: ConfigurationManager refreshes keys automatically and handles rollover.
/// </summary>
public class OidcDiscoveryCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public OidcDiscoveryCache(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<OpenIdConnectConfiguration> GetAsync(string issuer, CancellationToken token)
    {
        var manager = _managers.GetOrAdd(issuer, iss =>
        {
            string metadataAddress = iss + "/.well-known/openid-configuration";
            var retriever = new HttpDocumentRetriever(_httpClientFactory.CreateClient(OidcService.HttpClientName))
            {
                RequireHttps = iss.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            };
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                retriever);
        });
        return manager.GetConfigurationAsync(token);
    }

    /// <summary>Forces a refresh on the next call, e.g. after a signature validation failure.</summary>
    public void Invalidate(string issuer)
    {
        if (_managers.TryGetValue(issuer, out var manager))
            manager.RequestRefresh();
    }
}
