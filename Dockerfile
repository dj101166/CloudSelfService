# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG APP_VERSION=0.0.0

COPY CloudSelfService.csproj .
RUN dotnet restore CloudSelfService.csproj

COPY . .
RUN dotnet publish CloudSelfService.csproj \
    -c Release \
    -o /app/publish \
    /p:Version=${APP_VERSION}

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-server sudo gosu \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Remove shipped SSH host keys — regenerated at container startup by entrypoint
RUN rm -f /etc/ssh/ssh_host_*

# Non-root app user
RUN groupadd --gid 1001 appuser && \
    useradd --uid 1001 --gid 1001 --no-create-home --shell /bin/bash appuser && \
    mkdir -p /home/appuser && chown appuser:appuser /home/appuser

ENV TZ=America/New_York
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080 2222

WORKDIR /app
COPY --from=build --chown=appuser:appuser /app/publish .
COPY --chown=root:root docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENTRYPOINT ["docker-entrypoint.sh"]
