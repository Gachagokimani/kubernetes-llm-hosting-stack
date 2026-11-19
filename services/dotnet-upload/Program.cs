using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "/shared/uploads";
var chatDir = Environment.GetEnvironmentVariable("CHAT_DIR") ?? "/shared/chats";
var inquiryFile = Path.Combine(chatDir, "_inquiries.json");
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
Directory.CreateDirectory(uploadDir);
Directory.CreateDirectory(chatDir);

string? BuildNpgsqlConnString(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return null;
    if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        return url;

    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var sslMode = url.Contains("sslmode=disable", StringComparison.OrdinalIgnoreCase) ? "Disable" : "Require";
    return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={uri.AbsolutePath.TrimStart('/')};Username={Uri.UnescapeDataString(userInfo[0])};Password={(userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "")};SSL Mode={sslMode};";
}

var connStr = BuildNpgsqlConnString(dbUrl);

async Task InitDbAsync()
{
    if (connStr is null) return;
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();
    var sql = """
        CREATE TABLE IF NOT EXISTS chat_messages (
            id SERIAL PRIMARY KEY,
            session_id VARCHAR(64) NOT NULL,
            role VARCHAR(16) NOT NULL,
            content TEXT NOT NULL,
            sources_count INT DEFAULT 0,
            created_at TIMESTAMPTZ DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_chat_session ON chat_messages(session_id, id);

        CREATE TABLE IF NOT EXISTS proposals (
            id SERIAL PRIMARY KEY,
            filename TEXT UNIQUE NOT NULL,
            file_bytes BIGINT NOT NULL,
            extracted_text TEXT NOT NULL,
            status VARCHAR(16) DEFAULT 'PENDING',
            uploaded_at TIMESTAMPTZ DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS recruiter_inquiries (
            id SERIAL PRIMARY KEY,
            session_id VARCHAR(64),
            recruiter_name TEXT NOT NULL,
            contact_email TEXT NOT NULL,
            company TEXT,
            role_or_topic TEXT NOT NULL,
            created_at TIMESTAMPTZ DEFAULT NOW()
        );
    """;
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

if (connStr is not null)
{
    try { await InitDbAsync(); } catch (Exception ex) { Console.WriteLine($"DB init warning (PVC active): {ex.Message}"); }
}

static string CleanConciergeText(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
    var cleaned = Regex.Replace(raw, @"\*{1,3}([^\*]+?)\*{1,3}", "$1");
    cleaned = cleaned.Replace("*", "");
    cleaned = cleaned.Replace("||", " ");
    cleaned = Regex.Replace(cleaned, @"^\s*\|[\s\-:|]+\|\s*$", "", RegexOptions.Multiline);
    cleaned = Regex.Replace(cleaned, @"\s*\|\s*", " • ");
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

static string ExtractPdfPlainText(byte[] bytes, string filename)
{
    var raw = Encoding.Latin1.GetString(bytes);
    var matches = Regex.Matches(raw, @"\(([^()\\]{2,})\)\s*Tj");
    var sb = new StringBuilder();
    foreach (Match m in matches) sb.Append(m.Groups[1].Value).Append(' ');
    var clean = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    if (string.IsNullOrEmpty(clean))
        clean = $"Offer/Proposal PDF: {filename} ({bytes.Length} bytes)";
    return clean.Length > 4000 ? clean[..4000] : clean;
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "dotnet-enterprise-gateway",
    pvcMounted = Directory.Exists(uploadDir),
    postgresConfigured = connStr is not null,
    mode = connStr is not null ? "side-by-side-pvc+postgres" : "pvc-only"
}));

// Dual-write Offer/Proposal PDF to PVC (/shared/uploads) AND PostgreSQL side-by-side
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

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var bytes = ms.ToArray();
    var extracted = ExtractPdfPlainText(bytes, safeName);

    // 1. Always write binary PDF to PVC
    var destPath = Path.Combine(uploadDir, safeName);
    await File.WriteAllBytesAsync(destPath, bytes);

    // 2. Side-by-side write to PostgreSQL when configured
    bool dbSaved = false;
    if (connStr is not null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO proposals (filename, file_bytes, extracted_text, status)
                VALUES (@fn, @sz, @txt, 'PENDING')
                ON CONFLICT (filename) DO UPDATE
                SET file_bytes = EXCLUDED.file_bytes, extracted_text = EXCLUDED.extracted_text, status = 'PENDING', uploaded_at = NOW();
            """, conn);
            cmd.Parameters.AddWithValue("fn", safeName);
            cmd.Parameters.AddWithValue("sz", bytes.LongLength);
            cmd.Parameters.AddWithValue("txt", extracted);
            await cmd.ExecuteNonQueryAsync();
            dbSaved = true;
        }
        catch (Exception ex) { Console.WriteLine($"Postgres proposal sync warning: {ex.Message}"); }
    }

    return Results.Ok(new
    {
        status = "stored",
        filename = safeName,
        bytes = bytes.LongLength,
        pvcPath = destPath,
        postgresSynced = dbSaved
    });
});

// Read session history from PostgreSQL first, falling back to PVC JSON
app.MapGet("/api/chat/history/{sessionId}", async (string sessionId) =>
{
    var safeId = SafeSessionId(sessionId);
    if (connStr is not null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT role, content, sources_count, created_at FROM chat_messages WHERE session_id = @sid ORDER BY id ASC LIMIT 40;", conn);
            cmd.Parameters.AddWithValue("sid", safeId);
            await using var reader = await cmd.ExecuteReaderAsync();
            var list = new List<object>();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    role = reader.GetString(0),
                    text = reader.GetString(1),
                    sourcesCount = reader.GetInt32(2),
                    timestamp = reader.GetDateTime(3).ToString("o")
                });
            }
            if (list.Count > 0)
                return Results.Ok(new { sessionId = safeId, source = "postgres", messages = list });
        }
        catch (Exception ex) { Console.WriteLine($"Postgres history read fallback to PVC: {ex.Message}"); }
    }

    var filePath = Path.Combine(chatDir, $"{safeId}.json");
    if (!File.Exists(filePath))
        return Results.Ok(new { sessionId = safeId, source = "none", messages = Array.Empty<object>() });

    var json = await File.ReadAllTextAsync(filePath);
    var messages = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json) ?? new();
    return Results.Ok(new { sessionId = safeId, source = "pvc", messages });
});

// Dual-write sanitized chat turn to BOTH PVC and PostgreSQL
app.MapPost("/api/chat/sanitize-and-save", async (ChatTurnPayload payload) =>
{
    var safeId = SafeSessionId(payload.SessionId);
    var cleanedAnswer = CleanConciergeText(payload.AssistantAnswer ?? "");

    // 1. Write to PVC JSON
    var filePath = Path.Combine(chatDir, $"{safeId}.json");
    List<Dictionary<string, object>> history = File.Exists(filePath)
        ? (JsonSerializer.Deserialize<List<Dictionary<string, object>>>(await File.ReadAllTextAsync(filePath)) ?? new())
        : new();

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
    if (history.Count > 40) history = history.Skip(history.Count - 40).ToList();
    await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));

    // 2. Side-by-side write to PostgreSQL
    bool dbSaved = false;
    if (connStr is not null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            if (!string.IsNullOrWhiteSpace(payload.UserQuery))
            {
                await using var uCmd = new NpgsqlCommand(
                    "INSERT INTO chat_messages (session_id, role, content, sources_count) VALUES (@sid, 'user', @txt, 0);", conn);
                uCmd.Parameters.AddWithValue("sid", safeId);
                uCmd.Parameters.AddWithValue("txt", payload.UserQuery.Trim());
                await uCmd.ExecuteNonQueryAsync();
            }
            await using var aCmd = new NpgsqlCommand(
                "INSERT INTO chat_messages (session_id, role, content, sources_count) VALUES (@sid, 'assistant', @txt, @src);", conn);
            aCmd.Parameters.AddWithValue("sid", safeId);
            aCmd.Parameters.AddWithValue("txt", cleanedAnswer);
            aCmd.Parameters.AddWithValue("src", payload.SourcesCount);
            await aCmd.ExecuteNonQueryAsync();
            dbSaved = true;
        }
        catch (Exception ex) { Console.WriteLine($"Postgres chat write warning: {ex.Message}"); }
    }

    return Results.Ok(new { sessionId = safeId, cleanedAnswer, pvcSaved = true, postgresSynced = dbSaved });
});

app.MapGet("/api/chat/export/{sessionId}", async (string sessionId) =>
{
    var safeId = SafeSessionId(sessionId);
    var sb = new StringBuilder();
    sb.AppendLine($"Brighton Kimani — AI Portfolio Concierge Transcript ({safeId})");
    sb.AppendLine($"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
    sb.AppendLine(new string('=', 60)).AppendLine();

    var filePath = Path.Combine(chatDir, $"{safeId}.json");
    if (File.Exists(filePath))
    {
        var items = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(await File.ReadAllTextAsync(filePath)) ?? new();
        foreach (var m in items)
            sb.AppendLine($"{(m["role"].GetString() == "user" ? "Visitor" : "AI Concierge")}: {m["text"].GetString()}\n");
    }
    return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
});

app.MapPost("/api/inquiries", async (InquiryPayload req) =>
{
    if (string.IsNullOrWhiteSpace(req.RecruiterName) || string.IsNullOrWhiteSpace(req.ContactEmail) || string.IsNullOrWhiteSpace(req.RoleOrTopic))
        return Results.BadRequest(new { error = "Name, email, and role/topic are required." });

    // Dual-write inquiry to PVC + Postgres
    var list = File.Exists(inquiryFile)
        ? (JsonSerializer.Deserialize<List<InquiryPayload>>(await File.ReadAllTextAsync(inquiryFile)) ?? new())
        : new();
    list.Add(req);
    await File.WriteAllTextAsync(inquiryFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));

    if (connStr is not null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO recruiter_inquiries (session_id, recruiter_name, contact_email, company, role_or_topic) VALUES (@sid, @n, @e, @c, @r);", conn);
            cmd.Parameters.AddWithValue("sid", SafeSessionId(req.SessionId));
            cmd.Parameters.AddWithValue("n", req.RecruiterName.Trim());
            cmd.Parameters.AddWithValue("e", req.ContactEmail.Trim());
            cmd.Parameters.AddWithValue("c", (object?)req.Company?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", req.RoleOrTopic.Trim());
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"Postgres inquiry write warning: {ex.Message}"); }
    }

    return Results.Ok(new { status = "received", message = "Your inquiry has been logged for Brighton Kimani." });
});

app.MapGet("/api/analytics", async () =>
{
    var pvcSessions = Directory.GetFiles(chatDir, "fp-*.json").Length;
    var pvcProposals = Directory.GetFiles(uploadDir, "*.pdf").Length;
    if (connStr is not null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                SELECT
                    (SELECT COUNT(DISTINCT session_id) FROM chat_messages),
                    (SELECT COUNT(*) FROM chat_messages WHERE role = 'user'),
                    (SELECT COUNT(*) FROM proposals),
                    (SELECT COUNT(*) FROM recruiter_inquiries);
            """, conn);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return Results.Ok(new
            {
                mode = "side-by-side-pvc+postgres",
                uniqueSessions = r.GetInt64(0),
                totalQuestions = r.GetInt64(1),
                uploadedProposals = r.GetInt64(2),
                recruiterInquiries = r.GetInt64(3)
            });
        }
        catch { }
    }
    return Results.Ok(new { mode = "pvc-only", uniqueSessions = pvcSessions, uploadedProposals = pvcProposals });
});

app.Run("http://0.0.0.0:8080");

record ChatTurnPayload(string? SessionId, string? UserQuery, string? AssistantAnswer, int SourcesCount);
record InquiryPayload(string? SessionId, string RecruiterName, string ContactEmail, string? Company, string RoleOrTopic);
