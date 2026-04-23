using AspNetCoreRateLimit;
using DotNetEnv;
using Microsoft.OpenApi.Models;
using WhipAI.Services;
using WhipAI.Skills;

var builder = WebApplication.CreateBuilder(args);

// --- .env ---
DotNetEnv.Env.Load();

// --- Rate limiting (per-IP, 60 req/min by default — see appsettings.json) ---
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

// --- CORS — WhipAI is server-to-server only; browsers never hit it
//     directly. Keep CORS restrictive (localhost for dev tooling only). ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("Internal", policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "http://localhost:4201",
                "http://localhost:8080"
              )
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// --- HTTP client factory (for any outbound calls the skills need) ---
builder.Services.AddHttpClient();

// --- Controllers ---
// No JWT authentication middleware — WhipAI gates controllers with the
// [ApiKeyAuth] filter (shared secret with WhipBridge). That pattern fits
// server-to-server better than bearer JWTs, which were designed to carry
// user identity from the browser.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// --- Swagger (dev only, matching WhipBridge's pattern) ---
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "WhipAI", Version = "v1" });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "x-api-key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Shared secret matching the server's WHIPAI_API_KEY env var.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" },
            },
            Array.Empty<string>()
        },
    });
});

// --- WhipAI-specific services ---
// AnthropicService owns the Claude client (singleton — reuses HttpClient).
// WhipAI is stateless: callers persist responses themselves (via their own
// SPs), so no MySQL dependency lives here.
builder.Services.AddSingleton<AnthropicService>();

// --- Skill registry ---
// Each skill is registered as a singleton so its system prompt is loaded once
// from disk (allowing live edits between deploys without rebuilding the project).
// Add new skills here as the catalog grows.
builder.Services.AddSingleton<ArgyleDriverReviewSkill>();
builder.Services.AddSingleton<CheckrBackgroundReviewSkill>();
builder.Services.AddSingleton<SkillRegistry>(sp =>
    new SkillRegistry(new ISkill[]
    {
        sp.GetRequiredService<ArgyleDriverReviewSkill>(),
        sp.GetRequiredService<CheckrBackgroundReviewSkill>(),
    })
);

// --- URL binding (dev uses launchSettings.json, prod uses PORT env var) ---
if (!builder.Environment.IsDevelopment())
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8081";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

// --- Pipeline ---
app.UseIpRateLimiting();
app.UseCors("Internal");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "WhipAI v1");
        c.RoutePrefix = "swagger";
    });
}

app.MapControllers();

// --- Health check (no auth) for infra probes ---
app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "WhipAI",
    version = "0.1.0",
    time = DateTime.UtcNow,
}));

app.Run();
