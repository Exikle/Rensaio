using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Scrobbling;

/// <summary>
/// Factory to resolve an <see cref="IExternalSeriesProvider"/> by <see cref="ExternalSeriesProvider"/> enum value.
/// </summary>
public class ExternalSeriesProviderFactory
{
    private readonly IEnumerable<IExternalSeriesProvider> _providers;

    public ExternalSeriesProviderFactory(IEnumerable<IExternalSeriesProvider> providers)
    {
        _providers = providers;
    }

    public IExternalSeriesProvider? GetProvider(ExternalSeriesProvider type)
        => _providers.FirstOrDefault(p => p.ProviderType == type);

    public List<IExternalSeriesProvider> GetAllProviders()
        => _providers.ToList();
}