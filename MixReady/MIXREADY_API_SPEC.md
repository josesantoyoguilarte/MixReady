# MixReady API - MVP Specification (.NET 8)

## Overview

MixReady is a web API that allows users to:

* Upload a song
* Generate a DJ-friendly intro (via Python audio worker)
* Download the processed track

This is the **MVP version** focused on:

* File upload
* Background processing
* Integration with Python script
* File download

---

## Tech Stack

* .NET 8
* ASP.NET Core Web API
* Swagger (OpenAPI)
* Hangfire (for background jobs)
* Local file storage (for MVP)
* Python script integration (Process.Start)

---

## Project Structure

Create solution:

MixReady.sln

Projects:

* MixReady.Api

Inside MixReady.Api:

/Controllers
/Services
/Models
/Jobs
/Storage
/Helpers

---

## Models

### Track

```csharp
public class Track
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; }
    public string FilePath { get; set; }
    public string ProcessedFilePath { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

---

## Storage

Use local folders:

* /storage/originals/
* /storage/processed/

Create a FileStorageService:

```csharp
public interface IFileStorageService
{
    Task<string> SaveOriginalAsync(IFormFile file);
    string GetProcessedPath(Guid trackId);
}
```

---

## Controllers

### TracksController

Routes:

#### Upload Track

POST /api/tracks/upload

* Accepts: multipart/form-data
* Returns: trackId

```csharp
[HttpPost("upload")]
public async Task<IActionResult> Upload(IFormFile file)
```

---

#### Generate Intro

POST /api/tracks/{id}/generate-intro

* Triggers background job
* Returns jobId

```csharp
[HttpPost("{id}/generate-intro")]
public IActionResult GenerateIntro(Guid id)
```

---

#### Download Processed Track

GET /api/tracks/{id}/download

* Returns processed MP3 file

```csharp
[HttpGet("{id}/download")]
public IActionResult Download(Guid id)
```

---

## Background Job (Hangfire)

Create:

IntroGenerationJob.cs

```csharp
public class IntroGenerationJob
{
    public async Task Execute(Guid trackId)
    {
        // 1. Get track
        // 2. Call Python script
        // 3. Save processed file path
    }
}
```

---

## Python Integration

Call Python script using:

```csharp
var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "python",
        Arguments = $"script.py \"{inputPath}\" \"{outputPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }
};

process.Start();
process.WaitForExit();
```

---

## Services

### TrackService

Responsible for:

* Creating Track records
* Retrieving tracks
* Updating processed file path

---

## Dependency Injection

Register services in Program.cs:

```csharp
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<ITrackService, TrackService>();
```

---

## Hangfire Setup

Install package:

* Hangfire.AspNetCore

Configure in Program.cs:

```csharp
builder.Services.AddHangfire(config =>
    config.UseInMemoryStorage());

builder.Services.AddHangfireServer();
```

---

## Swagger

Enable Swagger for testing endpoints.

---

## MVP Flow

1. Upload file
2. Save to /storage/originals/
3. Create Track record
4. Call GenerateIntro
5. Enqueue Hangfire job
6. Job calls Python script
7. Save processed file to /storage/processed/
8. User downloads file

---

## Future Improvements (DO NOT IMPLEMENT NOW)

* Database (PostgreSQL or SQL Server)
* Authentication
* Azure Blob Storage
* Python API (FastAPI instead of script)
* Waveform preview
* Multiple intro styles

---

## Goal

The API should allow:

* Uploading one song
* Generating one intro edit
* Downloading the processed file

No UI required for MVP.
