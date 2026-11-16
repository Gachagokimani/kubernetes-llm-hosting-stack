using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 20 * 1024 * 1024);

var app = builder.Build();
var uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "/shared/uploads";
Directory.CreateDirectory(uploadDir);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "pdf-ingest-dotnet", uploadDir }));

app.MapPost("/api/pdf/upload", async (IFormFile file) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Empty file payload." });

    using var stream = file.OpenReadStream();
    byte[] header = new byte[5];
    int read = await stream.ReadAsync(header.AsMemory(0, 5));
    if (read < 5 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-")
        return Results.BadRequest(new { error = "Invalid file signature. Only valid PDF files are accepted." });

    var safeName = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Path.GetFileName(file.FileName)}";
    var destPath = Path.Combine(uploadDir, safeName);

    stream.Position = 0;
    await using var fs = File.Create(destPath);
    await stream.CopyToAsync(fs);

    return Results.Ok(new { message = "PDF stored in shared PVC", filename = safeName, bytes = file.Length });
}).DisableAntiforgery();

app.Run("http://0.0.0.0:5000");
