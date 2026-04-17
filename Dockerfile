# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MixReady/MixReady.csproj MixReady/
RUN dotnet restore MixReady/MixReady.csproj
COPY MixReady/ MixReady/
WORKDIR /src/MixReady
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install Python + audio dependencies (needed for worker, harmless on web)
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv ffmpeg libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# Create venv and install Python deps
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
COPY MixReady/scripts/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt && \
    pip install --no-cache-dir librosa soundfile

WORKDIR /app
COPY --from=build /app/publish .
COPY MixReady/scripts/ scripts/

# Shared storage mount point
RUN mkdir -p /app/storage/originals /app/storage/processed /app/storage/stems
VOLUME ["/app/storage"]

# Default: local monolith mode (both web + worker)
ENV MIXREADY_MODE=local
ENV MIXREADY_STORE=memory
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MixReady.dll"]
