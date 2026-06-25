namespace PicoNode.AI;

public sealed class DiscoveredModel
{
    public string Id { get; set; } = "";
    public string OwnedBy { get; set; } = "";
}

public static class ModelDiscovery
{
    public static async Task<DiscoveredModel[]> DiscoverAsync(
        HttpClient http, ProviderConfig provider, CancellationToken ct)
    {
        try
        {
            var url = $"{provider.BaseUrl}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {provider.ApiKey}");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)) return [];

            return data.EnumerateArray()
                .Select(m => new DiscoveredModel
                {
                    Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    OwnedBy = m.TryGetProperty("owned_by", out var ob) ? ob.GetString() ?? "" : "",
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
