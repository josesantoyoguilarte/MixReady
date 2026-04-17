# ---- Build stage ----
# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MixReady/ MixReady/
WORKDIR /src/MixReady
RUN dotnet publish -c Release -o /app/publish

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install Python + audio dependencies (needed for worker, harmless on web)
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv ffmpeg libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# Create venv and install Python deps
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Install PyTorch CPU-only (must match versions to avoid ABI issues)
RUN pip install --no-cache-dir torch==2.5.1 torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cpu

# Install demucs + audio deps
COPY MixReady/scripts/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt && \
    pip install --no-cache-dir demucs

# Pre-download both demucs models (optional - will download on first use if this fails)
RUN python3 -c "from demucs.pretrained import get_model; get_model('htdemucs')" || echo "htdemucs pre-download skipped"

WORKDIR /app
COPY --from=build /app/publish .
COPY MixReady/scripts/ scripts/

# Convert CRLF to LF in Python scripts (in case git didn't auto-convert)
RUN find scripts/ -name "*.py" -exec sed -i 's/\r$//' {} +

# Shared storage mount point
ENV MIXREADY_STORAGE_ROOT=/app/storage
RUN mkdir -p /app/storage/originals /app/storage/processed /app/storage/stems
VOLUME ["/app/storage"]

# Default: local monolith mode (both web + worker)
ENV MIXREADY_MODE=local
ENV MIXREADY_STORE=memory
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MixReady.dll"]
