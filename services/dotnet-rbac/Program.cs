using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var clientId = Environment.GetEnvironmentVariable("OAUTH_CLIENT_ID") ?? "";
var clientSecret = Environment.GetEnvironmentVariable("OAUTH_CLIENT_SECRET") ?? "";
var authUrl = Environment.GetEnvironmentVariable("OAUTH_AUTH_URL") ?? "https://github.com/login/oauth/authorize";
var tokenUrl = Environment.GetEnvironmentVariable("OAUTH_TOKEN_URL") ?? "https://github.com/login/oauth/access_token";
var userApiUrl = Environment.GetEnvironmentVariable("OAUTH_USER_URL") ?? "https://api.github.com/user";
var allowedAdmins = (Environment.GetEnvironmentVariable("ADMIN_ALLOWED_IDS") ?? "Gachagokimani,gachagokimani@gmail.com")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

var jwtSecret = Environment.GetEnvironmentVariable("RBAC_JWT_SECRET");
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
var signingKey = Encoding.UTF8.GetBytes(jwtSecret);
const string CookieName = "bk_sso_token";

static string Base64UrlEncode(byte[] input) =>
    Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] Base64UrlDecode(string input)
{
    var s = input.Replace('-', '+').Replace('_', '/');
    s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
    return Convert.FromBase64String(s);
}

string MintToken(string subject, string email, string role)
{
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
    var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
    var payloadObj = new { sub = subject, email, role, exp };
    var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payloadObj));
    var unsigned = $"{header}.{payload}";
    using var hmac = new HMACSHA256(signingKey);
    var sig = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));
    return $"{unsigned}.{sig}";
}

(bool Valid, string Subject, string Email, string Role) ValidateToken(string? token)
{
    if (string.IsNullOrWhiteSpace(token)) return (false, "", "", "guest");
    var parts = token.Split('.');
    if (parts.Length != 3) return (false, "", "", "guest");

    var unsigned = $"{parts[0]}.{parts[1]}";
    using var hmac = new HMACSHA256(signingKey);
    var expectedSig = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedSig), Encoding.UTF8.GetBytes(parts[2])))
        return (false, "", "", "guest");

    try
    {
        using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = doc.RootElement;
        var exp = root.GetProperty("exp").GetInt64();
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
            return (false, "", "", "guest");

        var sub = root.GetProperty("sub").GetString() ?? "";
        var email = root.TryGetProperty("email", out var em) ? (em.GetString() ?? "") : "";
        var role = root.GetProperty("role").GetString() ?? "guest";
        return (true, sub, email, role);
    }
    catch
    {
        return (false, "", "", "guest");
    }
}

string? ExtractToken(HttpRequest req)
{
    if (req.Headers.TryGetValue("Authorization", out var auth) &&
        auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return auth.ToString()["Bearer ".Length..].Trim();

    if (req.Cookies.TryGetValue(CookieName, out var cookieVal) && !string.IsNullOrWhiteSpace(cookieVal))
        return cookieVal;

    return null;
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "dotnet-rbac-sso",
    oauthConfigured = !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret)
}));

// 1. Start OAuth 2.0 SSO Redirect
app.MapGet("/api/auth/login", (HttpRequest req) =>
{
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        return Results.BadRequest(new { error = "OAUTH_CLIENT_ID and OAUTH_CLIENT_SECRET must be set in ai-stack-secrets." });

    var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.ToString();
    var redirectUri = $"{proto}://{host}/api/auth/callback";
    var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    var location = $"{authUrl}?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=read:user%20user:email&state={state}";
    return Results.Redirect(location);
});

// 2. OAuth Callback -> Role Assignment -> HttpOnly JWT Cookie
app.MapGet("/api/auth/callback", async (HttpRequest req, HttpResponse resp, string? code) =>
{
    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "Missing OAuth authorization code." });

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Brighton-K8s-RBAC/1.0");
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.ToString();
    var redirectUri = $"{proto}://{host}/api/auth/callback";

    var tokenResp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["client_secret"] = clientSecret,
        ["code"] = code,
        ["redirect_uri"] = redirectUri
    }));
    var tokenJson = await tokenResp.Content.ReadAsStringAsync();
    using var tDoc = JsonDocument.Parse(tokenJson);
    if (!tDoc.RootElement.TryGetProperty("access_token", out var atEl))
        return Results.Unauthorized();

    var accessToken = atEl.GetString()!;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var userJson = await http.GetStringAsync(userApiUrl);
    using var uDoc = JsonDocument.Parse(userJson);
    var login = uDoc.RootElement.TryGetProperty("login", out var lEl) ? (lEl.GetString() ?? "") : "";
    var email = uDoc.RootElement.TryGetProperty("email", out var eEl) ? (eEl.GetString() ?? "") : "";

    var isAdmin = allowedAdmins.Contains(login) || (!string.IsNullOrEmpty(email) && allowedAdmins.Contains(email));
    var role = isAdmin ? "admin" : "viewer";
    var jwt = MintToken(login, email, role);

    resp.Cookies.Append(CookieName, jwt, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = proto.Equals("https", StringComparison.OrdinalIgnoreCase),
        Path = "/",
        MaxAge = TimeSpan.FromHours(12)
    });

    return Results.Redirect("/");
});

// 3. Evaluate Current Session & Return Escalated Permissions
app.MapGet("/api/auth/me", (HttpRequest req) =>
{
    var (valid, sub, email, role) = ValidateToken(ExtractToken(req));
    var isAdmin = valid && role == "admin";
    return Results.Ok(new
    {
        authenticated = valid,
        user = valid ? sub : null,
        email = valid ? email : null,
        role = valid ? role : "guest",
        permissions = isAdmin
            ? new[] { "proposals:read", "proposals:upload", "proposals:delete", "inquiries:read" }
            : Array.Empty<string>()
    });
});

// 4. Policy Decision Point (PDP) for Downstream Microservices (.NET Upload / FastAPI)
app.MapGet("/api/rbac/authorize", (HttpRequest req, string? permission) =>
{
    var (valid, sub, _, role) = ValidateToken(ExtractToken(req));
    var isAdmin = valid && role == "admin";
    if (!isAdmin)
        return Results.Json(new { allowed = false, reason = "Escalated admin SSO role required." }, statusCode: 403);

    return Results.Ok(new { allowed = true, user = sub, role, permission = permission ?? "proposals:access" });
});

// 5. Logout
app.MapPost("/api/auth/logout", (HttpResponse resp) =>
{
    resp.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { status = "logged_out" });
});

app.Run("http://0.0.0.0:8080");
