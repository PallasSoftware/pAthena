ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# MapServer compiles source shared with the CharServer (src/Shared: CanonicalMapPolicy and its generated alias data) and
# references the WorldServer contract and runtime projects, which in turn link MapServer source files: all three trees are
# build inputs. The generated world data (the Athena Map Pack AthenaMaps.bin, ~53 MB) is part of src/MapServer and is
# published into the image as MapData/AthenaMaps.bin; it is the normal collision source (no map_cache.dat is needed).
COPY Directory.Build.props ./
COPY src/Shared/ src/Shared/
COPY src/WorldServer/ src/WorldServer/
COPY src/MapServer/ src/MapServer/
RUN dotnet restore src/MapServer/MapServer.csproj -p:RestoreLockedMode=true
RUN dotnet publish src/MapServer/MapServer.csproj -c Release --no-restore -o /app/out

FROM ${RUNTIME_IMAGE}
ARG SOURCE_URL
ARG SOURCE_REVISION
ARG VERSION
LABEL org.opencontainers.image.title="PaThena MapServer" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --from=build /app/out/ ./
# The engine's own configuration, pinned with the engine: map_athena.conf (rates, ruleset) and the world topology, which must
# be the same file the WorldServer image carries (ATHENA_WORLD_PARTITIONS_PATH). Deployment specific values are not baked in:
# a ConfigMap is mounted at /app/conf/k8s and imports map_athena.conf.
COPY conf/templates/map_athena.conf /app/conf/map_athena.conf
COPY conf/world_partitions.json /app/conf/world_partitions.json
RUN mkdir -p /app/conf/k8s /app/log && chown "$APP_UID" /app/log
USER $APP_UID
ENTRYPOINT ["dotnet", "MapServer.dll"]
