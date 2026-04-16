using Hangfire;
using MixReady.Services;
using MixReady.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Hangfire — limit to 2 concurrent workers (demucs is CPU/RAM heavy)
builder.Services.AddHangfire(config =>
    config.UseInMemoryStorage());
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
});

// Application services
builder.Services.AddSingleton<ITrackService, TrackService>();
builder.Services.AddSingleton<IFileStorageService, FileStorageService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHangfireDashboard();

app.Run();
