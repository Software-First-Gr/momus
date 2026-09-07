using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Shop.Api;

/// <summary>
/// Calls this app's own HTTP endpoints instead of reaching into the database directly. The traffic
/// therefore arrives as real requests against real routes, which is what an operation name will be
/// built from once the Momus client lands in M2.
/// </summary>
public sealed class SelfClient(IHttpClientFactory factory, IServer server, IConfiguration configuration)
{
    /// <summary>Endpoints report how many queries they ran in this header, so callers can count without parsing bodies.</summary>
    public const string QueriesHeader = "X-Shop-Queries";

    private Uri? _baseAddress;

    public Uri BaseAddress => _baseAddress ??= Resolve();

    public async Task<int> HitAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var client = factory.CreateClient("self");
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return response.Headers.TryGetValues(QueriesHeader, out var values) &&
               int.TryParse(values.FirstOrDefault(), out var queries)
            ? queries
            : 1;
    }

    private Uri Resolve()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        // "http://[::]:8080" and "http://+:8080" are bind patterns, not addresses a client can use.
        if (address is not null && !address.Contains('+') && !address.Contains("[::]"))
        {
            return new Uri(address);
        }

        var port = address is not null && Uri.TryCreate(address.Replace("+", "localhost").Replace("[::]", "localhost"),
            UriKind.Absolute, out var parsed) ? parsed.Port : 8080;

        return new Uri(configuration["SelfUrl"] ?? $"http://localhost:{port}");
    }
}
