using System.Globalization;
using FiscOCRApp.Models;
using FiscOCRApp.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddHttpClient<IOllamaService, OllamaService>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "http://localhost:11434/" : options.BaseUrl.Trim();
    if (!baseUrl.EndsWith('/'))
        baseUrl += "/";
    client.BaseAddress = new Uri(baseUrl);
    var timeout = options.TimeoutSeconds <= 0 ? 180 : options.TimeoutSeconds;
    client.Timeout = TimeSpan.FromSeconds(timeout);
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 12 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
});
builder.Services.AddSingleton<IOcrService, OcrService>();
builder.Services.AddSingleton<IExcelExportService, ExcelExportService>();
builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();
builder.Services.AddSingleton<IRegexFieldExtractor, RegexFieldExtractor>();
builder.Services.AddScoped<IDocumentPipelineService, DocumentPipelineService>();
builder.Services.AddSingleton<DocumentJobQueue>();
builder.Services.AddSingleton<IDocumentJobQueue>(sp => sp.GetRequiredService<DocumentJobQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DocumentJobQueue>());

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "İşlem tamamlanamadı.",
            detail = app.Environment.IsDevelopment() ? exception?.Message : null
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.Services.GetRequiredService<IDocumentRepository>().Initialize();
app.MapControllers();

app.MapGet("/api/health", (IOptions<OllamaOptions> ollama) => Results.Ok(new
{
    status = "ok",
    app = "FiscOCR",
    ollamaModel = ollama.Value.Model,
    ollamaTimeoutSeconds = ollama.Value.TimeoutSeconds,
    time = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
}))
.WithName("Health");

app.MapPost("/api/export", (FiscalDocumentDto? document, IExcelExportService excel) =>
{
    if (document is null)
        return Results.BadRequest(new { message = "Aktarılacak veri yok." });

    Prepare(document);
    if (document.Company.Length == 0 && document.Lines.Count == 0 && document.Total == 0)
        return Results.BadRequest(new { message = "Aktarılacak okunmuş veri yok." });

    var bytes = excel.Export(document);
    var fileName = FileName(document);
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
})
.WithName("ExportDocument");

app.MapPost("/api/export/batch", (List<FiscalDocumentDto>? documents, IExcelExportService excel) =>
{
    if (documents is null || documents.Count == 0)
        return Results.BadRequest(new { message = "Aktarılacak belge seçin." });

    foreach (var document in documents)
        Prepare(document);

    var ready = documents
        .Where(document => document.Company.Length > 0 || document.Lines.Count > 0 || document.Total > 0)
        .ToList();
    if (ready.Count == 0)
        return Results.BadRequest(new { message = "Aktarılacak okunmuş veri yok." });

    var bytes = excel.ExportMany(ready);
    var fileName = $"fiscocr-toplu-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
})
.WithName("ExportDocumentsBatch");

app.MapPost("/api/documents", async (FiscalDocumentDto? document, IDocumentRepository repository, CancellationToken cancellationToken) =>
{
    if (document is null)
        return Results.BadRequest(new { message = "Kaydedilecek veri yok." });

    var error = ValidateForSave(document);
    if (error is not null)
        return Results.BadRequest(new { message = error });

    try
    {
        var saved = document.Id is > 0
            ? await repository.UpdateAsync(document, cancellationToken)
            : await repository.SaveAsync(document, cancellationToken);
        return Results.Ok(saved);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
})
.WithName("SaveDocument");

app.MapGet("/api/documents", async (
    string? q,
    string? type,
    IDocumentRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAsync(q, type, cancellationToken)))
.WithName("ListDocuments");

app.MapGet("/api/documents/{id:long}", async (long id, IDocumentRepository repository, CancellationToken cancellationToken) =>
{
    var document = await repository.GetByIdAsync(id, cancellationToken);
    return document is null
        ? Results.NotFound(new { message = "Kayıt bulunamadı." })
        : Results.Ok(document);
})
.WithName("GetDocument");

app.MapDelete("/api/documents/{id:long}", async (long id, IDocumentRepository repository, CancellationToken cancellationToken) =>
{
    var deleted = await repository.DeleteAsync(id, cancellationToken);
    return deleted
        ? Results.Ok(new { message = "Kayıt silindi.", id })
        : Results.NotFound(new { message = "Kayıt bulunamadı." });
})
.WithName("DeleteDocument");

app.MapGet("/api/backup", (IWebHostEnvironment environment, IOptions<StorageOptions> storage) =>
{
    var configured = string.IsNullOrWhiteSpace(storage.Value.DatabasePath)
        ? "App_Data/fiscocr.db"
        : storage.Value.DatabasePath;
    var fullPath = Path.IsPathRooted(configured)
        ? configured
        : Path.Combine(environment.ContentRootPath, configured);
    fullPath = Path.GetFullPath(fullPath);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { message = "Veritabanı henüz oluşmadı." });

    var bytes = File.ReadAllBytes(fullPath);
    return Results.File(bytes, "application/x-sqlite3", $"fiscocr-yedek-{DateTime.Now:yyyyMMdd-HHmmss}.db");
})
.WithName("BackupDatabase");

app.MapFallbackToFile("index.html");
app.Run();

static string FileName(FiscalDocumentDto document)
{
    var type = string.IsNullOrWhiteSpace(document.DocumentType) ? "belge" : document.DocumentType.Trim().ToLowerInvariant();
    type = type.Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
    return $"fiscocr-{type}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
}

static void Prepare(FiscalDocumentDto document)
{
    document.Company = (document.Company ?? "").Trim();
    document.DocumentType = string.IsNullOrWhiteSpace(document.DocumentType) ? "Belge" : document.DocumentType.Trim();
    document.DocumentNo = TrimTo(document.DocumentNo, 40);
    document.TaxNumber = TrimTo(document.TaxNumber, 16);
    document.Date = TrimTo(document.Date, 10);
    document.Currency = string.IsNullOrWhiteSpace(document.Currency) ? "TRY" : document.Currency.Trim();
    document.SourceFileName = TrimTo(document.SourceFileName, 180);
    document.RawText = TrimTo(document.RawText, 3000);
    document.Lines ??= [];
    document.ValidationMessages ??= [];

    if (document.Company.Length > 200)
        document.Company = document.Company[..200];

    if (document.Date is not null &&
        !DateOnly.TryParseExact(document.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        document.Date = null;

    var allowed = new HashSet<string>(StringComparer.Ordinal) { "Fiş", "İrsaliye", "Fatura", "Belge" };
    if (!allowed.Contains(document.DocumentType))
        document.DocumentType = "Belge";

    document.Lines = document.Lines
        .Where(line => !string.IsNullOrWhiteSpace(line.Description) || line.Amount != 0 || line.UnitPrice != 0)
        .Take(80)
        .ToList();

    for (var i = 0; i < document.Lines.Count; i++)
    {
        var line = document.Lines[i];
        line.LineNo = i + 1;
        line.Description = (line.Description ?? "").Trim();
        if (line.Description.Length > 300)
            line.Description = line.Description[..300];
        line.Unit = string.IsNullOrWhiteSpace(line.Unit) ? "AD" : line.Unit.Trim();
        if (line.Unit.Length > 12)
            line.Unit = line.Unit[..12];
        if (line.Quantity <= 0)
            line.Quantity = 1;
        if (line.Quantity > 1_000_000m)
            line.Quantity = 1_000_000m;
        if (line.UnitPrice < 0)
            line.UnitPrice = 0;
        if (line.Amount < 0)
            line.Amount = 0;
    }

    if (document.SubTotal < 0)
        document.SubTotal = 0;
    if (document.TaxTotal < 0)
        document.TaxTotal = 0;
    if (document.Total < 0)
        document.Total = 0;

    DocumentValidation.ReconcileTotals(document);
}

static string? ValidateForSave(FiscalDocumentDto document)
{
    Prepare(document);
    return document.Company.Length == 0
        ? "Firma alanı boş. Kaydetmeden önce firma adını yazın."
        : null;
}

static string? TrimTo(string? value, int max)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;
    var trimmed = value.Trim();
    return trimmed.Length <= max ? trimmed : trimmed[..max];
}