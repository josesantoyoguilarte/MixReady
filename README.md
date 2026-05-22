# MixReady

**Turn any song into a DJ-ready track.** MixReady automatically generates beat-matched, mixable intros for music files, using a hybrid C# + Python audio-engineering pipeline that mirrors what a professional DJ would do to prepare tracks for a set.

---

## Purpose

DJs spend hours preparing tracks: shortening busy intros, looping drum sections, beat-matching, and creating clean entry points so songs can be mixed smoothly. **MixReady automates that workflow.**

Upload a song and MixReady will:

1. **Analyze** it — BPM, musical key, genre, beat grid, bars, and structure.
2. **Separate stems** (drums / bass / vocals / other) using AI.
3. **Extract a clean groove** from the original track.
4. **Build a beat-aligned intro** by looping that groove for N bars.
5. **Crossfade** the intro into the original song at a beat-locked point you choose on an interactive waveform.
6. Output a single mixable track, ready to drop into a DJ set.

It also supports BPM and key shifting, multi-stem intro construction, and a two-deck "Kitchen" mode for comparing/combining tracks.

---

## Tech Stack

| Layer | Technology |
|------|------------|
| Web / API | ASP.NET Core 8 (Razor Pages + Web API) |
| Audio DSP | C# / NAudio |
| AI Analysis | Python 3 — librosa, Demucs, PyTorch (CPU) |
| Frontend | Razor Pages + WaveSurfer.js + Bootstrap |
| Storage / Queue | Redis (production), in-memory (dev) |
| Background Jobs | Hangfire-style job runners (intro generation, stem separation) |
| Deployment | Docker, docker-compose |

The app is designed with graceful fallback: every Python-powered feature has a pure-C# fallback so it still works if Python isn't installed.

---

## Repository Layout

```
MixReady/
  MixReady/
    Pages/            # Razor Pages (Index, Kitchen, layout)
    Controllers/      # REST API (TracksController, KitchenController)
    Services/         # Track/file/Redis services
    Jobs/             # IntroGenerationJob, StemSeparationJob
    Helpers/          # NAudio DSP: BpmDetector, KeyDetector, GrooveExtractor, ...
    scripts/          # Python: analyze.py, separate.py, change_bpm.py, change_key.py
  Dockerfile
  docker-compose.qa.yml
```

---

## Running on Windows

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3.10+](https://www.python.org/downloads/windows/) (optional, but enables AI features)
- [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) on `PATH`
- Visual Studio 2022 or VS Code (recommended)

### Steps

```powershell
git clone https://github.com/josesantoyoguilarte/MixReady.git
cd MixReady\MixReady

# (Optional) Install Python deps for AI analysis + stem separation
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r scripts\requirements.txt
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
pip install demucs

# Run the app
dotnet run
```

Open https://localhost:7155 in your browser.

> Without Python installed, the app still runs — it falls back to pure-C# BPM/key/genre detection.

---

## Running on macOS

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.10+ (`brew install python@3.11`)
- FFmpeg (`brew install ffmpeg libsndfile`)

### Steps

```bash
git clone https://github.com/josesantoyoguilarte/MixReady.git
cd MixReady/MixReady

# (Optional) Install Python deps
python3 -m venv .venv
source .venv/bin/activate
pip install -r scripts/requirements.txt
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
pip install demucs

# Run
dotnet run
```

Open https://localhost:7155.

> On Apple Silicon (M1/M2/M3), PyTorch will use CPU by default. Demucs works fine but stem separation will be slower than on a beefy x86 machine.

---

## Running with Docker (Cloud / Server)

The included `Dockerfile` builds a self-contained image with .NET 8, Python, PyTorch (CPU), librosa, and Demucs. `docker-compose.qa.yml` provides a multi-container layout suitable for cloud deployment (Azure, AWS, GCP, DigitalOcean, etc.).

### Single-container (local test)

```bash
docker build -t mixready .
docker run -p 8080:8080 mixready
```

Open http://localhost:8080.

### Multi-container (production-ish)

Architecture: **web** (Razor Pages + API) ? **Redis** (queue + track state) ? **worker(s)** (stem separation, intro generation).

```bash
docker compose -f docker-compose.qa.yml build
docker compose -f docker-compose.qa.yml up -d

# Scale workers horizontally
docker compose -f docker-compose.qa.yml up -d --scale worker=3
```

### Deploying to a Cloud VM

1. Provision a Linux VM (Ubuntu 22.04 recommended, **? 8 vCPU / 16 GB RAM** for Demucs).
2. Install Docker + Docker Compose.
3. Clone the repo and run the `docker compose` commands above.
4. Put a reverse proxy (Caddy / nginx / Traefik) in front of port 8080 for TLS.

Recommended cloud sizes:
- **Azure**: Standard_D8s_v5 or larger
- **AWS**: c6i.2xlarge or larger
- **GCP**: n2-standard-8 or larger

Stem separation is CPU-heavy. More cores = faster jobs. The compose file pins `OMP_NUM_THREADS=16` for a 16-core target — adjust to match your host.

---

## Running over Tailscale (Private Mesh Access)

[Tailscale](https://tailscale.com) lets you reach your MixReady server from anywhere on your private WireGuard mesh, without exposing it to the public internet. Ideal for personal use, demos, or shared devices in a band/crew.

### 1. Install Tailscale on the host running MixReady

**Linux / cloud VM:**
```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

**Windows:** Download the installer from [tailscale.com/download](https://tailscale.com/download) and sign in.

**macOS:** `brew install --cask tailscale` then launch the app and sign in.

### 2. Start MixReady bound to all interfaces

Docker already binds to `0.0.0.0:8080`. For `dotnet run`, set:

```bash
ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet run
```

### 3. Find the host's Tailscale IP / MagicDNS name

```bash
tailscale ip -4         # e.g. 100.x.y.z
tailscale status        # shows the host's MagicDNS name, e.g. mixready-vm
```

### 4. Access from any device on your tailnet

```
http://mixready-vm:8080
# or
http://100.x.y.z:8080
```

### 5. (Optional) Enable HTTPS with Tailscale Serve

```bash
sudo tailscale serve --bg --https=443 http://localhost:8080
```

Now reach the app at `https://mixready-vm.tailnet-name.ts.net` — Tailscale provisions a free Let's Encrypt cert automatically.

### 6. (Optional) Funnel — public URL via Tailscale

If you want to share a demo publicly:

```bash
sudo tailscale funnel --bg 8080
```

This exposes the app on a `*.ts.net` URL without opening any firewall ports.

---

## Configuration

Environment variables (set in shell, `appsettings.json`, or compose file):

| Variable | Default | Meaning |
|----------|---------|---------|
| `MIXREADY_MODE` | `local` | `local`, `web`, or `worker` |
| `MIXREADY_STORE` | `memory` | `memory` or `redis` |
| `REDIS_CONNECTION` | — | e.g. `redis:6379` |
| `MIXREADY_STORAGE_ROOT` | `./storage` | Where uploads/output go |
| `DEMUCS_MODEL` | `htdemucs` | `htdemucs` (best) or `hdemucs_mmi` (faster) |
| `OMP_NUM_THREADS` | — | PyTorch CPU thread count |

---

## API

See [`MixReady/MIXREADY_API_SPEC.md`](MixReady/MIXREADY_API_SPEC.md) for the full REST contract (upload, analyze, separate, generate intro, change BPM/key, download).

---

## License

Personal / portfolio project by [Jose Santoyo Guilarte](https://github.com/josesantoyoguilarte). Contact the author for reuse.
