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
        CancellationToken ct,
        ILogger? logger = null
    )
    {
        try
        {
            var url = $"{provider.BaseUrl}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {provider.ApiKey}");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger?.Warning(
                    $"Model discovery failed for provider '{provider.Name}': HTTP {(int)response.StatusCode} from {url}"
                );
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var list = PicoJetson.JsonSerializer.Deserialize<ModelListResponse>(
                Encoding.UTF8.GetBytes(json)
            );
            var models =
                list?.Data.Select(m => new DiscoveredModel { Id = m.Id, OwnedBy = m.OwnedBy })
                    .ToArray()
                ?? [];
            logger?.Debug($"Model discovery for '{provider.Name}': {models.Length} models found");
            return models;
        }
        catch (Exception ex)
        {
            logger?.Error(
                $"Model discovery exception for provider '{provider.Name}' ({provider.BaseUrl}/models): {ex.Message}"
            );
            return [];
        }
    }
}
