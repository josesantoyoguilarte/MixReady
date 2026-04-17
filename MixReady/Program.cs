using Hangfire;
using Hangfire.Redis.StackExchange;
using MixReady.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Environment-based configuration ---
// MIXREADY_MODE: "web" | "worker" | unset (local dev = monolith, both web+worker)
// MIXREADY_STORE: "redis" | unset (local dev = in-memory)
// REDIS_CONNECTION: Redis connection string (required when MIXREADY_STORE=redis)

var mode = Environment.GetEnvironmentVariable("MIXREADY_MODE") ?? "local";
var store = Environment.GetEnvironmentVariable("MIXREADY_STORE") ?? "memory";
var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
var isWeb = mode is "web" or "local";
var isWorker = mode is "worker" or "local";

// --- Web services (only if serving HTTP) ---
if (isWeb)
{
    builder.Services.AddRazorPages();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

// --- Hangfire ---
if (store == "redis")
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConn));
    builder.Services.AddHangfire(config => config.UseRedisStorage(redisConn));
}
else
{
    builder.Services.AddHangfire(config => config.UseInMemoryStorage());
}

if (isWorker)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = int.TryParse(
            Environment.GetEnvironmentVariable("MIXREADY_WORKERS"), out var w) ? w : 2;
    });
}

// --- Application services ---
if (store == "redis")
{
    builder.Services.AddSingleton<ITrackService, RedisTrackService>();
}
else
{
    builder.Services.AddSingleton<ITrackService, TrackService>();
}

builder.Services.AddSingleton<IFileStorageService, FileStorageService>();

var app = builder.Build();

// --- Pipeline (only if serving HTTP) ---
if (isWeb)
{
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    // Only redirect to HTTPS in production (Docker QA runs HTTP)
    if (app.Environment.IsProduction())
    {
        app.UseHttpsRedirection();
    }

    app.UseStaticFiles();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseRouting();
    app.UseAuthorization();

    app.MapRazorPages();
    app.MapControllers();
    app.MapHangfireDashboard();
}

app.Run();
