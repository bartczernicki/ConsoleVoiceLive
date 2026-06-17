#pragma warning disable OPENAI001

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.ClientModel;

namespace ConsoleLiveWeb.Services;

public sealed class AgentQuestionService
{
    private const string DefaultInstructions =
        "You are a helpful assistant. Answer clearly and concisely because your response will be read aloud.";
    private const string WebIqInstructions =
        "You are a helpful assistant. Use the available WebIQ MCP web tool before answering. " +
        "Base your answer on the retrieved real-time web information, include concise source references when useful, " +
        "and answer clearly and briefly because your response will be read aloud.";
    private const string PlaceholderEndpoint = "https://YOUR_AZURE_OPENAI_RESOURCE.openai.azure.com";
    private const string PlaceholderApiKey = "YOUR_AZURE_OPENAI_KEY";
    private const string PlaceholderDeploymentName = "YOUR_DEPLOYMENT_NAME";

    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentQuestionService> _logger;
    private readonly IServiceProvider _services;
    private readonly WebIqMcpToolService _webIqMcpToolService;

    public AgentQuestionService(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<AgentQuestionService> logger,
        IServiceProvider services,
        WebIqMcpToolService webIqMcpToolService)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _services = services;
        _webIqMcpToolService = webIqMcpToolService;
    }

    public async Task<AgentQuestionResponse> AskAsync(
        string question,
        bool useWebIq = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return AgentQuestionResponse.Failure("Enter a question for the agent.");
        }

        AgentQuestionSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            return AgentQuestionResponse.Failure(ex.Message);
        }

        try
        {
            await using WebIqMcpToolContext? webIqToolContext = useWebIq
                ? await _webIqMcpToolService.CreateWebToolContextAsync(cancellationToken).ConfigureAwait(false)
                : null;
            IList<AITool>? tools = webIqToolContext?.Tools.ToList();

            var client = new AzureOpenAIClient(
                new Uri(settings.Endpoint),
                new ApiKeyCredential(settings.ApiKey));
            ResponsesClient responsesClient = client.GetResponsesClient();
            AIAgent agent = responsesClient.AsAIAgent(
                model: settings.ModelDeploymentName,
                instructions: useWebIq ? WebIqInstructions : DefaultInstructions,
                name: "AgentTextToSpeech",
                description: useWebIq
                    ? "Answers concise questions after using WebIQ MCP grounding."
                    : "Answers concise questions for browser text-to-speech playback.",
                tools: tools,
                loggerFactory: _loggerFactory,
                services: _services);

            AgentResponse response = await agent.RunAsync(
                question.Trim(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string answer = response.Text.Trim();

            return string.IsNullOrWhiteSpace(answer)
                ? AgentQuestionResponse.Failure("The agent returned an empty response.")
                : AgentQuestionResponse.Success(answer, useWebIq);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentQuestionResponse.Failure("The agent request timed out.");
        }
        catch (WebIqMcpException ex) when (useWebIq)
        {
            _logger.LogWarning(ex, "WebIQ grounding failed for Agent Text to Speech.");
            return AgentQuestionResponse.Failure(
                $"WebIQ grounding failed: {SanitizeErrorMessage(ex.Message, settings)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent Text to Speech request failed.");
            return AgentQuestionResponse.Failure(
                $"The agent request failed: {SanitizeErrorMessage(ex.Message, settings)}");
        }
    }

    private AgentQuestionSettings LoadSettings()
    {
        string endpoint = GetString("AzureOpenAI:Endpoint");
        string apiKey = GetString("AzureOpenAI:APIKey");
        string modelDeploymentName = GetString("AzureOpenAI:ModelDeploymentName");

        if (string.IsNullOrWhiteSpace(endpoint) ||
            endpoint.Equals(PlaceholderEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:Endpoint in secrets.appsettings.json to your Azure OpenAI endpoint.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("AzureOpenAI:Endpoint must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Equals(PlaceholderApiKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:APIKey in secrets.appsettings.json to your Azure OpenAI API key.");
        }

        if (string.IsNullOrWhiteSpace(modelDeploymentName) ||
            modelDeploymentName.Equals(PlaceholderDeploymentName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:ModelDeploymentName in secrets.appsettings.json to your Azure OpenAI deployment name.");
        }

        return new AgentQuestionSettings(
            endpointUri.ToString(),
            apiKey,
            modelDeploymentName);
    }

    private string GetString(string key)
    {
        return _configuration[key]?.Trim() ?? string.Empty;
    }

    private static string SanitizeErrorMessage(string message, AgentQuestionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No error details were provided.";
        }

        return message
            .Replace(settings.ApiKey, "[redacted]", StringComparison.Ordinal)
            .ReplaceLineEndings(" ")
            .Trim();
    }

    private sealed record AgentQuestionSettings(
        string Endpoint,
        string ApiKey,
        string ModelDeploymentName);
}

public sealed record AgentQuestionResponse(
    bool Succeeded,
    string? Answer,
    bool UsedWebIq,
    string? ErrorMessage)
{
    public static AgentQuestionResponse Success(string answer, bool usedWebIq)
    {
        return new AgentQuestionResponse(true, answer, usedWebIq, null);
    }

    public static AgentQuestionResponse Failure(string errorMessage)
    {
        return new AgentQuestionResponse(false, null, false, errorMessage);
    }
}
