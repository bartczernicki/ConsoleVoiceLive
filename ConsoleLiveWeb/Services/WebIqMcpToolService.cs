using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ConsoleLiveWeb.Services;

public sealed class WebIqMcpToolService
{
    private const string DefaultEndpoint = "https://api.microsoft.ai/v3/mcp/";
    private const string ApiKeyHeaderName = "x-apikey";
    private const string PlaceholderApiKey = "=";
    private const int MaxRawSummaryLength = 4000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] QueryParameterNames =
    [
        "query",
        "q",
        "searchQuery",
        "search",
        "question",
        "prompt",
        "input",
        "text",
        "request"
    ];

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public WebIqMcpToolService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<WebIqLookupResponse> LookupAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return WebIqLookupResponse.Failure("Provide a WebIQ query.");
        }

        try
        {
            await using WebIqMcpToolContext context =
                await CreateWebToolContextAsync(cancellationToken).ConfigureAwait(false);
            var arguments = new Dictionary<string, object?>
            {
                [context.QueryParameterName] = query.Trim()
            };

            CallToolResult result = await context.Client
                .CallToolAsync(context.ToolName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            WebIqLookupResponse normalized = NormalizeToolResult(query, result);
            return result.IsError == true
                ? WebIqLookupResponse.Failure(normalized.RawSummary)
                : normalized;
        }
        catch (WebIqMcpException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WebIqMcpException($"WebIQ lookup failed: {ex.Message}", ex);
        }
    }

    public async Task<WebIqMcpToolContext> CreateWebToolContextAsync(
        CancellationToken cancellationToken = default)
    {
        WebIqMcpSettings settings = LoadSettings();
        HttpClient httpClient = _httpClientFactory.CreateClient();
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = settings.Endpoint,
            Name = "WebIQ-MCP",
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = settings.ConnectionTimeout,
            AdditionalHeaders = settings.Headers
        };
        var transport = new HttpClientTransport(
            transportOptions,
            httpClient,
            _loggerFactory,
            ownsHttpClient: false);

        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(
                transport,
                loggerFactory: _loggerFactory,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            IList<McpClientTool> tools = await client
                .ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            McpClientTool? webTool = tools.FirstOrDefault(tool =>
                tool.Name.Equals("web", StringComparison.OrdinalIgnoreCase) &&
                CanCallWithQuery(tool.JsonSchema));
            string? queryParameterName = webTool is null
                ? null
                : GetQueryParameterName(webTool.JsonSchema);

            if (webTool is null || string.IsNullOrWhiteSpace(queryParameterName))
            {
                throw new WebIqMcpException(
                    "WebIQ MCP connected, but the query-based web tool was not found.");
            }

            return new WebIqMcpToolContext(
                httpClient,
                transport,
                client,
                [webTool],
                webTool.Name,
                queryParameterName);
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            httpClient.Dispose();

            if (ex is OperationCanceledException)
            {
                throw;
            }

            if (ex is WebIqMcpException)
            {
                throw;
            }

            throw new WebIqMcpException(
                $"WebIQ MCP request failed: {SanitizeErrorMessage(ex.Message, settings.Headers)}",
                ex);
        }
    }

    private static string SanitizeErrorMessage(string message, IDictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No error details were provided.";
        }

        string sanitized = message.ReplaceLineEndings(" ");

        foreach (string headerValue in headers.Values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            sanitized = sanitized.Replace(headerValue, "[redacted]", StringComparison.Ordinal);
        }

        return sanitized.Trim();
    }

    private static WebIqLookupResponse NormalizeToolResult(string query, CallToolResult result)
    {
        string contentText = ExtractContentText(result);
        string structuredJson = result.StructuredContent is null
            ? string.Empty
            : JsonSerializer.Serialize(result.StructuredContent, JsonOptions) ?? string.Empty;
        string rawSummary = FirstNonEmpty(structuredJson, contentText, "WebIQ returned no content.");
        string answer = ExtractAnswer(structuredJson)
            ?? ExtractAnswer(contentText)
            ?? contentText
            ?? rawSummary;
        IReadOnlyList<WebIqLookupSource> sources =
            ExtractSources(structuredJson).Concat(ExtractSources(contentText))
                .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(8)
                .ToArray();

        return WebIqLookupResponse.Success(
            string.IsNullOrWhiteSpace(answer) ? $"WebIQ returned information for: {query}" : answer.Trim(),
            sources,
            Truncate(rawSummary, MaxRawSummaryLength));
    }

    private static string ExtractContentText(CallToolResult result)
    {
        string text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>()
                .Select(block => block.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(text)
            ? JsonSerializer.Serialize(result.Content, JsonOptions) ?? string.Empty
            : text;
    }

    private static string? ExtractAnswer(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryParseJson(value, out JsonDocument? document))
        {
            return value.Trim();
        }

        using (document)
        {
            return FindFirstString(
                document!.RootElement,
                ["answer", "summary", "snippet", "description", "text", "content", "result"]);
        }
    }

    private static IReadOnlyList<WebIqLookupSource> ExtractSources(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TryParseJson(value, out JsonDocument? document))
        {
            return [];
        }

        using (document)
        {
            var sources = new List<WebIqLookupSource>();
            CollectSources(document!.RootElement, sources);
            return sources;
        }
    }

    private static void CollectSources(JsonElement element, List<WebIqLookupSource> sources)
    {
        if (sources.Count >= 8)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? url = FindFirstString(element, ["url", "uri", "link", "sourceUrl", "source_url"]);
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme is "http" or "https")
            {
                sources.Add(new WebIqLookupSource(
                    FindFirstString(element, ["title", "name", "source", "site"]),
                    uri.ToString()));
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectSources(property.Value, sources);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectSources(item, sources);
            }
        }
    }

    private static string? FindFirstString(JsonElement element, IReadOnlyCollection<string> propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                string? value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? nested = property.Value.ValueKind switch
            {
                JsonValueKind.Object => FindFirstString(property.Value, propertyNames),
                JsonValueKind.Array => property.Value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Object ? FindFirstString(item, propertyNames) : null)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static bool TryParseJson(string value, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private WebIqMcpSettings LoadSettings()
    {
        string endpoint = GetString("WebIQ-MCP:url", DefaultEndpoint);
        string type = GetString("WebIQ-MCP:type", "http");

        if (!type.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebIqMcpException("WebIQ-MCP:type must be \"http\".");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            throw new WebIqMcpException("WebIQ-MCP:url must be an absolute URL.");
        }

        Dictionary<string, string> headers = _configuration
            .GetSection("WebIQ-MCP:headers")
            .GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Key) && !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!.Trim(), StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue(ApiKeyHeaderName, out string? apiKey) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            apiKey == PlaceholderApiKey)
        {
            throw new WebIqMcpException(
                "Set WebIQ-MCP:headers:x-apikey in secrets.appsettings.json to your WebIQ MCP API key.");
        }

        return new WebIqMcpSettings(
            endpointUri,
            headers,
            TimeSpan.FromSeconds(30));
    }

    private string GetString(string key, string fallback)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool CanCallWithQuery(JsonElement schema)
    {
        return !string.IsNullOrWhiteSpace(GetQueryParameterName(schema));
    }

    private static string? GetQueryParameterName(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string queryParameterName in QueryParameterNames)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Name.Equals(queryParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Name;
                }
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record WebIqMcpSettings(
        Uri Endpoint,
        IDictionary<string, string> Headers,
        TimeSpan ConnectionTimeout);
}

public sealed record WebIqLookupSource(string? Title, string Url);

public sealed record WebIqLookupResponse(
    bool Succeeded,
    string Answer,
    IReadOnlyList<WebIqLookupSource> Sources,
    string RawSummary,
    string? ErrorMessage)
{
    public static WebIqLookupResponse Success(
        string answer,
        IReadOnlyList<WebIqLookupSource> sources,
        string rawSummary)
    {
        return new WebIqLookupResponse(true, answer, sources, rawSummary, null);
    }

    public static WebIqLookupResponse Failure(string errorMessage)
    {
        return new WebIqLookupResponse(false, string.Empty, [], errorMessage, errorMessage);
    }
}

public sealed class WebIqMcpException : Exception
{
    public WebIqMcpException(string message)
        : base(message)
    {
    }

    public WebIqMcpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WebIqMcpToolContext : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClientTransport _transport;

    public WebIqMcpToolContext(
        HttpClient httpClient,
        HttpClientTransport transport,
        McpClient client,
        IReadOnlyList<AITool> tools,
        string toolName,
        string queryParameterName)
    {
        _httpClient = httpClient;
        _transport = transport;
        Client = client;
        Tools = tools;
        ToolName = toolName;
        QueryParameterName = queryParameterName;
    }

    public McpClient Client { get; }

    public IReadOnlyList<AITool> Tools { get; }

    public string ToolName { get; }

    public string QueryParameterName { get; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
