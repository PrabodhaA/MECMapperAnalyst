using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace MapAnalystFunction
{
    public class AnalyseFunction
    {
        private readonly ILogger<AnalyseFunction> _logger;
        private readonly string _skillContent;
        private readonly string _apiKey;

        public AnalyseFunction(ILogger<AnalyseFunction> logger)
        {
            _logger = logger;
            var skillPath = Path.Combine(AppContext.BaseDirectory, "Skills", "mec-map-analyst.md");
            _skillContent = File.Exists(skillPath) ? File.ReadAllText(skillPath) : "You are a helpful MEC map analyst.";
            _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        }

        [Function("Chat")]
        public async Task<HttpResponseData> Chat(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat")] HttpRequestData req)
        {
            ChatRequest? chatRequest = null;
            string? mapXml = null;

            var contentType = req.Headers.TryGetValues("Content-Type", out var ctValues)
                ? ctValues.FirstOrDefault() ?? "" : "";

            if (contentType.Contains("multipart/form-data"))
            {
                var form = await ParseMultipartFormAsync(req);
                mapXml = form.MapXml;
                if (!string.IsNullOrWhiteSpace(form.RequestJson))
                    chatRequest = JsonSerializer.Deserialize<ChatRequest>(form.RequestJson, JsonOptions);
            }
            else
            {
                var body = await req.ReadAsStringAsync();
                chatRequest = JsonSerializer.Deserialize<ChatRequest>(body ?? "", JsonOptions);
            }

            if (chatRequest is null)
                return await ErrorResponse(req, "Invalid request body.", HttpStatusCode.BadRequest);
            if (string.IsNullOrWhiteSpace(chatRequest.Message))
                return await ErrorResponse(req, "Message is required.", HttpStatusCode.BadRequest);

            var messages = new List<Message>();

            if (!string.IsNullOrWhiteSpace(mapXml))
            {
                var processedXml = TruncateMapXml(mapXml);
                messages.Add(new Message
                {
                    Role = RoleType.User,
                    Content = new List<ContentBase>
                    {
                        new TextContent { Text = $"I am uploading a MEC mapper XML file for analysis.\n\n<mapper_xml>\n{processedXml}\n</mapper_xml>\n\nPlease confirm receipt: tell me the map name, version, and one sentence summary. Then wait for questions." }
                    }
                });
                messages.Add(new Message
                {
                    Role = RoleType.Assistant,
                    Content = new List<ContentBase>
                    {
                        new TextContent { Text = chatRequest.MapSummary ?? "Map received. Ready for your questions." }
                    }
                });
            }

            if (chatRequest.History != null)
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

            messages.Add(new Message
            {
                Role = RoleType.User,
                Content = new List<ContentBase> { new TextContent { Text = chatRequest.Message } }
            });

            try
            {
                var client = new AnthropicClient(_apiKey);
                var parameters = new MessageParameters
                {
                    Model = "claude-haiku-4-5-20251001",
                    MaxTokens = 2048,
                    System = new List<SystemMessage> { new SystemMessage(_skillContent) },
                    Messages = messages,
                    Stream = false,
                    Temperature = 0.3m
                };

                var response = await client.Messages.GetClaudeMessageAsync(parameters);
                var replyText = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";

                return await JsonResponse(req, new ChatResponse
                {
                    Reply = replyText,
                    InputTokens = response.Usage?.InputTokens ?? 0,
                    OutputTokens = response.Usage?.OutputTokens ?? 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claude API call failed");
                return await ErrorResponse(req, $"Claude API error: {ex.Message}", HttpStatusCode.InternalServerError);
            }
        }

        [Function("Health")]
        public async Task<HttpResponseData> Health(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        {
            return await JsonResponse(req, new { status = "ok", skill = "mec-map-analyst", version = "1.0" });
        }

        private static string TruncateMapXml(string xml)
        {
            xml = RemoveXmlSection(xml, "Links");
            xml = RemoveXmlSection(xml, "SchemaOut");
            if (xml.Length > 80000)
                xml = xml.Substring(0, 80000) + "\n<!-- [truncated] -->";
            return xml;
        }

        private static string RemoveXmlSection(string xml, string tagName)
        {
            var s = xml.IndexOf("<" + tagName + ">", StringComparison.OrdinalIgnoreCase);
            var e = xml.IndexOf("</" + tagName + ">", StringComparison.OrdinalIgnoreCase);
            if (s >= 0 && e >= 0)
                xml = xml.Substring(0, s) + xml.Substring(e + tagName.Length + 3);
            return xml;
        }

        private static async Task<(string? MapXml, string? RequestJson)> ParseMultipartFormAsync(HttpRequestData req)
        {
            string? mapXml = null;
            string? requestJson = null;
            try
            {
                using var ms = new MemoryStream();
                await req.Body.CopyToAsync(ms);
                var body = Encoding.UTF8.GetString(ms.ToArray());
                var ct = req.Headers.TryGetValues("Content-Type", out var ctv) ? ctv.FirstOrDefault() ?? "" : "";
                var bi = ct.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
                if (bi < 0) return (null, null);
                var boundary = "--" + ct.Substring(bi + 9).Trim();
                foreach (var part in body.Split(new[] { boundary }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ci = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (ci < 0) continue;
                    var content = part.Substring(ci + 4).TrimEnd('\r', '\n', '-');
                    if (part.Contains("name=\"mapFile\"")) mapXml = content;
                    else if (part.Contains("name=\"request\"")) requestJson = content;
                }
            }
            catch { }
            return (mapXml, requestJson);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static async Task<HttpResponseData> JsonResponse<T>(HttpRequestData req, T data)
        {
            var res = req.CreateResponse(HttpStatusCode.OK);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            await res.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
            return res;
        }

        private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, string msg, HttpStatusCode status)
        {
            var res = req.CreateResponse(status);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = msg }));
            return res;
        }
    }

    public class CorsFunction
    {
        [Function("CorsOptions")]
        public HttpResponseData Options(
            [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "{*any}")] HttpRequestData req)
        {
            var res = req.CreateResponse(HttpStatusCode.OK);
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            return res;
        }
    }

    public class ChatRequest
    {
        public string Message { get; set; } = "";
        public string? MapSummary { get; set; }
        public List<ConversationTurn>? History { get; set; }
    }

    public class ConversationTurn
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class ChatResponse
    {
        public string Reply { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
