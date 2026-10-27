# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props README.md LICENSE THIRD-PARTY-NOTICES.md ./
COPY src/ src/
# Optional build-only CA bundle for environments with an HTTPS proxy; never stored in a layer.
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    dotnet publish src/GameArchiver.Cli -c Release -a "$TARGETARCH" \
      --self-contained false -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends innoextract ca-certificates \
    && rm -rf /var/lib/apt/lists/*
ENV HOME=/state \
    XDG_CONFIG_HOME=/state/config \
    XDG_DATA_HOME=/state/data
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE THIRD-PARTY-NOTICES.md /usr/share/doc/gamearchiver/
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/gamearchiver
RUN mkdir -p /state /staging /archives /config && chown app:app /state /staging /archives
USER app
STOPSIGNAL SIGTERM
ENTRYPOINT ["/usr/local/bin/gamearchiver"]
CMD ["schedule", "--config", "/config/schedule.json"]
