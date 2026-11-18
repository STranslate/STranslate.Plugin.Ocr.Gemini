namespace STranslate.Plugin.Ocr.Gemini;

internal static class GeminiRequest
{
    public static (string Url, Options? Options) Create(Settings settings, bool stream)
    {
        var model = settings.Model.Trim();
        if (model.Length == 0)
            model = "gemini-flash-latest";

        var endpoint = new UriBuilder(settings.Url);
        if (!stream || endpoint.Path == "/")
        {
            var action = stream ? "streamGenerateContent" : "generateContent";
            endpoint.Path = $"/v1beta/models/{model}:{action}";
        }

        var query = stream ? "alt=sse" : string.Empty;
        if (endpoint.Scheme == Uri.UriSchemeHttps &&
            endpoint.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.Query = query;
            return (endpoint.Uri.AbsoluteUri, new Options
            {
                Headers = new Dictionary<string, string> { ["x-goog-api-key"] = settings.ApiKey }
            });
        }

        endpoint.Query = stream ? $"alt=sse&key={settings.ApiKey}" : $"key={settings.ApiKey}";
        return (endpoint.Uri.ToString(), null);
    }
}
