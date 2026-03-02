using MapaInteractivoBugambilia.Data;
using MapaInteractivoBugambilia.Models;
using MapaInteractivoBugambilia.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Enums como string en JSON (Available/Reserved/Sold...)
builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// SSE update broadcaster
builder.Services.AddSingleton<UpdateHub>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opt =>
    {
        opt.LoginPath = "/admin/login.html";
        opt.AccessDeniedPath = "/admin/login.html";
        opt.Cookie.Name = "BugambiliaEditorAuth";
        opt.Cookie.SameSite = SameSiteMode.Lax;
        opt.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// DB migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

// Protect /admin/* BEFORE static files are served
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // allow login page and auth endpoints
    if (path.Equals("/admin/login.html", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // protect everything under /admin
    if (path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase))
    {
        if (!(context.User?.Identity?.IsAuthenticated ?? false))
        {
            context.Response.Redirect("/admin/login.html");
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------- PUBLIC API ----------------
app.MapGet("/api/projects/{projectKey}/lots", async (string projectKey, AppDbContext db) =>
{
    var lots = await db.Lots
        .AsNoTracking()
        .Where(x => x.ProjectKey == projectKey && x.LotType == LotType.Lot)
        .OrderBy(x => x.Block).ThenBy(x => x.LotNumber)
        .ToListAsync();

    return Results.Ok(lots);
});

// ---- SSE updates stream (public) ----
app.MapGet("/api/updates/stream", async (HttpContext http, UpdateHub hub, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.Connection = "keep-alive";
    http.Response.ContentType = "text/event-stream";

    var reader = hub.Subscribe();

    try
    {
        // initial version
        await http.Response.WriteAsync($"event: version\ndata: {hub.Version}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        await foreach (var ver in reader.ReadAllAsync(ct))
        {
            await http.Response.WriteAsync($"event: version\ndata: {ver}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal: client disconnected / page refreshed
    }
});

// ---------------- ADMIN API (requires login) ----------------
var admin = app.MapGroup("/api/admin").RequireAuthorization();

// List ALL lots for editor (includes non-Lot types)
admin.MapGet("/projects/{projectKey}/lots", async (string projectKey, AppDbContext db) =>
{
    var lots = await db.Lots
        .AsNoTracking()
        .Where(x => x.ProjectKey == projectKey)
        .OrderBy(x => x.Block).ThenBy(x => x.LotNumber)
        .ToListAsync();

    return Results.Ok(lots);
});

// Create new lot
admin.MapPost("/projects/{projectKey}/lots", async (string projectKey, UpsertLotDto dto, AppDbContext db, UpdateHub hub) =>
{
    if (string.IsNullOrWhiteSpace(dto.DisplayCode))
        return Results.BadRequest("DisplayCode is required (e.g. A-2).");

    if (string.IsNullOrWhiteSpace(dto.Block))
        return Results.BadRequest("Block is required (e.g. A).");

    if (dto.LotNumber <= 0)
        return Results.BadRequest("LotNumber must be > 0.");

    var code = dto.DisplayCode.Trim();

    var exists = await db.Lots.AnyAsync(x => x.ProjectKey == projectKey && x.DisplayCode == code);
    if (exists) return Results.Conflict($"Lot '{code}' already exists.");

    var lot = new Lot
    {
        ProjectKey = projectKey,
        DisplayCode = code,
        Block = dto.Block.Trim(),
        LotNumber = dto.LotNumber,
        AreaM2 = dto.AreaM2,
        AreaV2 = dto.AreaV2,
        LotType = dto.LotType,
        Status = dto.Status,
        X = dto.X,
        Y = dto.Y,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    db.Lots.Add(lot);
    await db.SaveChangesAsync();
    hub.Publish();

    return Results.Ok(lot);
});

// Update whole lot by Id
admin.MapPut("/projects/{projectKey}/lots/{id:guid}", async (string projectKey, Guid id, UpsertLotDto dto, AppDbContext db, UpdateHub hub) =>
{
    var lot = await db.Lots.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Id == id);
    if (lot is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(dto.DisplayCode))
        return Results.BadRequest("DisplayCode is required (e.g. A-2).");

    if (string.IsNullOrWhiteSpace(dto.Block))
        return Results.BadRequest("Block is required (e.g. A).");

    if (dto.LotNumber <= 0)
        return Results.BadRequest("LotNumber must be > 0.");

    var newCode = dto.DisplayCode.Trim();

    if (!string.Equals(lot.DisplayCode, newCode, StringComparison.OrdinalIgnoreCase))
    {
        var exists = await db.Lots.AnyAsync(x => x.ProjectKey == projectKey && x.DisplayCode == newCode && x.Id != id);
        if (exists) return Results.Conflict($"Lot '{newCode}' already exists.");
        lot.DisplayCode = newCode;
    }

    lot.Block = dto.Block.Trim();
    lot.LotNumber = dto.LotNumber;
    lot.AreaM2 = dto.AreaM2;
    lot.AreaV2 = dto.AreaV2;
    lot.LotType = dto.LotType;
    lot.Status = dto.Status;
    lot.X = dto.X;
    lot.Y = dto.Y;
    lot.UpdatedAt = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    hub.Publish();

    return Results.Ok(lot);
});

// Delete lot by Id
admin.MapDelete("/projects/{projectKey}/lots/{id:guid}", async (string projectKey, Guid id, AppDbContext db, UpdateHub hub) =>
{
    var lot = await db.Lots.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Id == id);
    if (lot is null) return Results.NotFound();

    db.Lots.Remove(lot);
    await db.SaveChangesAsync();
    hub.Publish();

    return Results.Ok(new { deleted = true, id });
});

// Import TXT
admin.MapPost("/projects/{projectKey}/import-txt", async (string projectKey, ImportTxtDto dto, AppDbContext db, UpdateHub hub) =>
{
    var parsed = BugambiliaTxtImporter.ParseLots(projectKey, dto.Txt);

    foreach (var lot in parsed)
    {
        var existing = await db.Lots.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.DisplayCode == lot.DisplayCode);
        if (existing is null)
        {
            db.Lots.Add(lot);
        }
        else
        {
            existing.Block = lot.Block;
            existing.LotNumber = lot.LotNumber;
            existing.AreaM2 = lot.AreaM2;
            existing.AreaV2 = lot.AreaV2;
            existing.LotType = lot.LotType;
            // keep existing.Status / X / Y
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    await db.SaveChangesAsync();
    hub.Publish();

    return Results.Ok(new { insertedOrUpdated = parsed.Count, version = hub.Version });
});

// ---------------- AUTH ----------------
app.MapPost("/auth/login", async (HttpContext http) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    var adminUser = builder.Configuration["AdminAuth:Username"];
    var adminPass = builder.Configuration["AdminAuth:Password"];

    if (username != adminUser || password != adminPass)
        return Results.Unauthorized();

    var claims = new List<Claim> { new Claim(ClaimTypes.Name, username) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/admin/editor.html");
});

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/admin/login.html");
});

app.Run();

// ---------------- DTOs ----------------
public record UpdatePositionDto(decimal X, decimal Y);
public record UpdateStatusDto(string Status);
public record ImportTxtDto(string Txt);

public record UpsertLotDto(
    string DisplayCode,
    string Block,
    int LotNumber,
    decimal? AreaM2,
    decimal? AreaV2,
    LotType LotType,
    LotStatus Status,
    decimal? X,
    decimal? Y
);

// ---------------- SSE Broadcast Hub ----------------
public sealed class UpdateHub
{
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Version => Interlocked.Read(ref _version);

    private readonly object _gate = new();
    private readonly List<Channel<long>> _subs = new();

    public void Publish()
    {
        var next = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _version, next);

        List<Channel<long>> subsSnapshot;
        lock (_gate)
        {
            subsSnapshot = _subs.ToList();
        }

        foreach (var ch in subsSnapshot)
        {
            ch.Writer.TryWrite(next);
        }
    }

    public ChannelReader<long> Subscribe()
    {
        var ch = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        lock (_gate)
        {
            _subs.Add(ch);
        }

        return ch.Reader;
    }
}