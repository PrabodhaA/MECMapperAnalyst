using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace MapAnalystFunction;

public class AnalyseFunction
{
    private readonly ILogger<AnalyseFunction> _logger;
    private readonly string _skillContent;
    private readonly string _apiKey;

    public AnalyseFunction(ILogger<AnalyseFunction> logger)
    {
        _logger = logger;

        // Load bundled skill from Skills folder
        var skillPath = Path.Combine(AppContext.BaseDirectory, "Skills", "mec-map-analyst.md");
        _skillContent = File.Exists(skillPath)
            ? File.ReadAllText(skillPath)
            : "You are a helpful MEC map analyst.";

        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
    }

    // ── POST /api/chat ────────────────────────────────────────────────────────
    [Function("Chat")]
    public async Task<HttpResponseData> Chat(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat")] HttpRequestData req)
    {
        // Parse multipart or JSON body
        ChatRequest? chatRequest = null;
        string? mapXml = null;

        var contentType = req.Headers.TryGetValues("Content-Type", out var ctValues)
            ? ctValues.FirstOrDefault() ?? ""
            : "";

        if (contentType.Contains("multipart/form-data"))
        {
            // File upload on first message — parse form data
            var form = await ParseMultipartFormAsync(req);
            mapXml = form.MapXml;
            var requestJson = form.RequestJson;
            if (!string.IsNullOrWhiteSpace(requestJson))
                chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestJson, JsonOptions);
        }
        else
        {
            // Subsequent messages — JSON only
            var body = await req.ReadAsStringAsync();
            chatRequest = JsonSerializer.Deserialize<ChatRequest>(body ?? "", JsonOptions);
        }

        if (chatRequest is null)
            return await ErrorResponse(req, "Invalid request body.", HttpStatusCode.BadRequest);

        if (string.IsNullOrWhiteSpace(chatRequest.Message))
            return await ErrorResponse(req, "Message is required.", HttpStatusCode.BadRequest);

        // Build conversation history for Claude
        var messages = new List<Message>();

        // If map XML is provided (first turn), inject it as context
        if (!string.IsNullOrWhiteSpace(mapXml))
        {
            // Truncate large maps to avoid token limits — strip Links and SchemaOut
            var processedXml = TruncateMapXml(mapXml);
            messages.Add(new Message
            {
                Role = RoleType.User,
                Content = new List<ContentBase>
                {
                    new TextContent
                    {
                        Text = $"I am uploading a MEC mapper XML file for analysis.\n\n<mapper_xml>\n{processedXml}\n</mapper_xml>\n\nPlease confirm you have received the map and tell me the map name, version, and in one sentence what it does. Then wait for my questions."
                    }
                }
            });
            messages.Add(new Message
            {
                Role = RoleType.Assistant,
                Content = new List<ContentBase>
                {
                    new TextContent
                    {
                        Text = chatRequest.MapSummary ?? "Map received. I have parsed the mapper XML and am ready to answer your questions."
                    }
                }
            });
        }

        // Add previous conversation turns
        if (chatRequest.History is { Count: > 0 })
        {
            foreach (var turn in chatRequest.History)
            {
                messages.Add(new Message
                {
                    Role = turn.Role == "user" ? RoleType.User : RoleType.Assistant,
                    Content = new List<ContentBase> { new TextContent { Text = turn.Content } }
                });
            }
        }

        // Add current user message
        messages.Add(new Message
        {
            Role = RoleType.User,
            Content = new List<ContentBase> { new TextContent { Text = chatRequest.Message } }
        });

        // Call Claude API
        try
        {
            var client = new AnthropicClient(_apiKey);
            var claudeRequest = new MessageParameters
            {
                Model = AnthropicModels.Claude35Sonnet,
                MaxTokens = 2048,
                System = new List<SystemMessage>
                {
                    new SystemMessage { Text = _skillContent }
                },
                Messages = messages
            };

            var response = await client.Messages.GetClaudeMessageAsync(claudeRequest);
            var replyText = response.Content.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

            var result = new ChatResponse
            {
                Reply = replyText,
                InputTokens = response.Usage?.InputTokens ?? 0,
                OutputTokens = response.Usage?.OutputTokens ?? 0
            };

            return await JsonResponse(req, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude API call failed");
            return await ErrorResponse(req, "Failed to get response from Claude API. Check your API key.", HttpStatusCode.InternalServerError);
        }
    }

    // ── GET /api/health ───────────────────────────────────────────────────────
    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var result = new { status = "ok", skill = "mec-map-analyst", version = "1.0" };
        return await JsonResponse(req, result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TruncateMapXml(string xml)
    {
        // Strip <Links> and <SchemaOut> sections to reduce token usage
        // These sections are large but have low analytical value for Q&A
        xml = RemoveXmlSection(xml, "Links");
        xml = RemoveXmlSection(xml, "SchemaOut");

        // Hard cap at ~80k chars to stay within Claude context safely
        if (xml.Length > 80000)
            xml = xml[..80000] + "\n<!-- [truncated for size] -->";

        return xml;
    }

    private static string RemoveXmlSection(string xml, string tagName)
    {
        var start = xml.IndexOf($"<{tagName}>", StringComparison.OrdinalIgnoreCase);
        var end = xml.IndexOf($"</{tagName}>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end >= 0)
            xml = xml[..start] + xml[(end + tagName.Length + 3)..];
        return xml;
    }

    private static async Task<(string? MapXml, string? RequestJson)> ParseMultipartFormAsync(HttpRequestData req)
    {
        // Simple multipart parser — reads mapFile and request fields
        string? mapXml = null;
        string? requestJson = null;

        try
        {
            var body = await req.ReadAsByteArrayAsync();
            var bodyText = Encoding.UTF8.GetString(body);

            // Extract boundary
            var contentType = req.Headers.TryGetValues("Content-Type", out var ct) ? ct.FirstOrDefault() ?? "" : "";
            var boundaryIndex = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (boundaryIndex < 0) return (null, null);
            var boundary = "--" + contentType[(boundaryIndex + 9)..].Trim();

            var parts = bodyText.Split(boundary, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains("name=\"mapFile\""))
                {
                    var contentStart = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (contentStart >= 0)
                        mapXml = part[(contentStart + 4)..].TrimEnd('\r', '\n', '-');
                }
                else if (part.Contains("name=\"request\""))
                {
                    var contentStart = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (contentStart >= 0)
                        requestJson = part[(contentStart + 4)..].TrimEnd('\r', '\n', '-');
                }
            }
        }
        catch (Exception)
        {
            // Return nulls — caller handles missing data
        }

        return (mapXml, requestJson);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<HttpResponseData> JsonResponse<T>(HttpRequestData req, T data)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, string message, HttpStatusCode status)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return response;
    }
}

// ── CORS preflight handler ────────────────────────────────────────────────────
public class CorsFunction
{
    [Function("CorsOptions")]
    public HttpResponseData Options(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "{*any}")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        return response;
    }
}

// ── Request / Response models ─────────────────────────────────────────────────
public class ChatRequest
{
    public string Message { get; set; } = "";
    public string? MapSummary { get; set; }
    public List<ConversationTurn>? History { get; set; }
}

public class ConversationTurn
{
    public string Role { get; set; } = "";    // "user" or "assistant"
    public string Content { get; set; } = "";
}

public class ChatResponse
{
    public string Reply { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
