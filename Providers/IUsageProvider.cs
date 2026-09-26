using UsageNotch.Models;

namespace UsageNotch.Providers;

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
