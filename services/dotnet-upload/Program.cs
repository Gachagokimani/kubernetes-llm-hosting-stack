using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "/shared/uploads";
var chatDir = Environment.GetEnvironmentVariable("CHAT_DIR") ?? "/shared/chats";
Directory.CreateDirectory(uploadDir);
Directory.CreateDirectory(chatDir);

static string CleanConciergeText(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
    // Remove bold/italic asterisks (**text** or *text*) and stray asterisks
    var cleaned = Regex.Replace(raw, @"\*{1,3}([^\*]+?)\*{1,3}", "$1");
    cleaned = cleaned.Replace("*", "");
    // Remove double-pipes || and markdown table pipe separators
    cleaned = cleaned.Replace("||", " ");
    cleaned = Regex.Replace(cleaned, @"^\s*\|[\s\-:|]+\|\s*$", "", RegexOptions.Multiline);
    cleaned = Regex.Replace(cleaned, @"\s*\|\s*", " • ");
    // Clean stray leading/trailing bullet pipes and markdown headings
    cleaned = Regex.Replace(cleaned, @"^#+\s*", "", RegexOptions.Multiline);
    cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ");
    cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
    return cleaned.Trim();
}

static string SafeSessionId(string? id)
{
    if (string.IsNullOrWhiteSpace(id)) return "anonymous";
    var safe = Regex.Replace(id, @"[^a-zA-Z0-9_\-]", "");
    return string.IsNullOrEmpty(safe) ? "anonymous" : safe[..Math.Min(safe.Length, 64)];
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "pdf-ingest-and-chat-dotnet",
    uploadDir,
    chatDir
}));

app.MapPost("/api/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data" });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No PDF file provided" });

    var safeName = Path.GetFileName(file.FileName);
    if (!safeName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only PDF files are accepted" });

    var destPath = Path.Combine(uploadDir, safeName);
    await using (var stream = File.Create(destPath))
    {
        await file.CopyToAsync(stream);
    }

    return Results.Ok(new
    {
        status = "stored",
        filename = safeName,
        bytes = file.Length,
        path = destPath
    });
});

app.MapGet("/api/chat/history/{sessionId}", async (string sessionId) =>
{
    var safeId = SafeSessionId(sessionId);
    var filePath = Path.Combine(chatDir, $"{safeId}.json");
    if (!File.Exists(filePath))
        return Results.Ok(new { sessionId = safeId, messages = Array.Empty<object>() });

    var json = await File.ReadAllTextAsync(filePath);
    var messages = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json) ?? new();
    return Results.Ok(new { sessionId = safeId, messages });
});

app.MapPost("/api/chat/sanitize-and-save", async (ChatTurnPayload payload) =>
{
    var safeId = SafeSessionId(payload.SessionId);
    var cleanedAnswer = CleanConciergeText(payload.AssistantAnswer ?? "");
    var filePath = Path.Combine(chatDir, $"{safeId}.json");

    List<Dictionary<string, object>> history = new();
    if (File.Exists(filePath))
    {
        var existing = await File.ReadAllTextAsync(filePath);
        history = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existing) ?? new();
    }

    if (!string.IsNullOrWhiteSpace(payload.UserQuery))
    {
        history.Add(new Dictionary<string, object>
        {
            ["role"] = "user",
            ["text"] = payload.UserQuery.Trim(),
            ["timestamp"] = DateTime.UtcNow.ToString("o")
        });
    }

    history.Add(new Dictionary<string, object>
    {
        ["role"] = "assistant",
        ["text"] = cleanedAnswer,
        ["sourcesCount"] = payload.SourcesCount,
        ["timestamp"] = DateTime.UtcNow.ToString("o")
    });

    if (history.Count > 40)
        history = history.Skip(history.Count - 40).ToList();

    await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));

    return Results.Ok(new
    {
        sessionId = safeId,
        cleanedAnswer,
        totalMessages = history.Count
    });
});

app.Run("http://0.0.0.0:8080");

record ChatTurnPayload(string? SessionId, string? UserQuery, string? AssistantAnswer, int SourcesCount);
