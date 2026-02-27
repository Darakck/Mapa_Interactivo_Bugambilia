using MapaInteractivoBugambilia.Data;
using MapaInteractivoBugambilia.Models;
using MapaInteractivoBugambilia.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

// IMPORTANT: protect /admin/* BEFORE static files are served
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

    await http.Response.WriteAsync($"event: version\ndata: {hub.Version}\n\n", ct);
    await http.Response.Body.FlushAsync(ct);

    await foreach (var ver in reader.ReadAllAsync(ct))
    {
        await http.Response.WriteAsync($"event: version\ndata: {ver}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

// ---------------- ADMIN API ----------------
var admin = app.MapGroup("/api/admin").RequireAuthorization();

admin.MapPost("/publish", (UpdateHub hub) =>
{
    hub.Publish();
    return Results.Ok(new { version = hub.Version });
});

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
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    await db.SaveChangesAsync();
    hub.Publish();
    return Results.Ok(new { insertedOrUpdated = parsed.Count, version = hub.Version });
});

admin.MapPut("/projects/{projectKey}/lots/{displayCode}/position", async (
    string projectKey,
    string displayCode,
    UpdatePositionDto dto,
    AppDbContext db,
    UpdateHub hub) =>
{
    var lot = await db.Lots.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.DisplayCode == displayCode);
    if (lot is null) return Results.NotFound();

    lot.X = dto.X;
    lot.Y = dto.Y;
    lot.UpdatedAt = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    hub.Publish();
    return Results.Ok(lot);
});

admin.MapPut("/projects/{projectKey}/lots/{displayCode}/status", async (
    string projectKey,
    string displayCode,
    UpdateStatusDto dto,
    AppDbContext db,
    UpdateHub hub) =>
{
    var lot = await db.Lots.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.DisplayCode == displayCode);
    if (lot is null) return Results.NotFound();

    if (!Enum.TryParse<LotStatus>(dto.Status, ignoreCase: true, out var parsed))
        return Results.BadRequest($"Invalid status '{dto.Status}'. Allowed: {string.Join(", ", Enum.GetNames<LotStatus>())}");

    lot.Status = parsed;
    lot.UpdatedAt = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync();
    hub.Publish();
    return Results.Ok(lot);
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

public record UpdatePositionDto(decimal X, decimal Y);
public record UpdateStatusDto(string Status);
public record ImportTxtDto(string Txt);

public sealed class UpdateHub
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Version => Interlocked.Read(ref _version);

    public void Publish()
    {
        var next = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _version, next);
        _channel.Writer.TryWrite(next);
    }

    public ChannelReader<long> Subscribe() => _channel.Reader;
}