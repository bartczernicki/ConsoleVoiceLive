using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ConsoleLiveWeb.Services;

public sealed class WebIqMcpToolService
{
    private const string DefaultEndpoint = "https://api.microsoft.ai/v3/mcp/";
    private const string ApiKeyHeaderName = "x-apikey";
    private const string PlaceholderApiKey = "=";
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

            if (webTool is null)
            {
                throw new WebIqMcpException(
                    "WebIQ MCP connected, but the query-based web tool was not found.");
            }

            return new WebIqMcpToolContext(
                httpClient,
                transport,
                client,
                [webTool],
                webTool.Name);
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
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return QueryParameterNames.Any(queryParameterName =>
            properties.EnumerateObject().Any(property =>
                property.Name.Equals(queryParameterName, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record WebIqMcpSettings(
        Uri Endpoint,
        IDictionary<string, string> Headers,
        TimeSpan ConnectionTimeout);
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
    private readonly McpClient _client;

    public WebIqMcpToolContext(
        HttpClient httpClient,
        HttpClientTransport transport,
        McpClient client,
        IReadOnlyList<AITool> tools,
        string toolName)
    {
        _httpClient = httpClient;
        _transport = transport;
        _client = client;
        Tools = tools;
        ToolName = toolName;
    }

    public IReadOnlyList<AITool> Tools { get; }

    public string ToolName { get; }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
