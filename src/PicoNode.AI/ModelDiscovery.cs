
namespace PicoNode.AI;

[PicoSerializable]
public sealed class DiscoveredModel
{
    public string Id { get; set; } = string.Empty;
    public string OwnedBy { get; set; } = string.Empty;
}

public static class ModelDiscovery
{
    public static async Task<DiscoveredModel[]> DiscoverAsync(
        HttpClient http,
        ProviderConfig provider,
        CancellationToken ct
    )
    {
        try
        {
            var url = $"{provider.BaseUrl}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {provider.ApiKey}");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            var list = PicoJetson.JsonSerializer.Deserialize<ModelListResponse>(
                Encoding.UTF8.GetBytes(json)
            );
            return list?.Data
                    .Select(m => new DiscoveredModel { Id = m.Id, OwnedBy = m.OwnedBy })
                    .ToArray()
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}
