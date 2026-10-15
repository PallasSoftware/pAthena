ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Athena.World.Runtime and Athena.World.Monsters compile source files of src/MapServer (collision, movement and the monster
# domain), so both trees are build inputs.
COPY Directory.Build.props ./
COPY src/WorldServer/ src/WorldServer/
COPY src/MapServer/ src/MapServer/
RUN dotnet restore src/WorldServer/Athena.World/Athena.World.csproj -p:RestoreLockedMode=true
RUN dotnet publish src/WorldServer/Athena.World/Athena.World.csproj -c Release --no-restore -o /app/out

FROM ${RUNTIME_IMAGE}
ARG SOURCE_URL
ARG SOURCE_REVISION
ARG VERSION
LABEL org.opencontainers.image.title="PaThena WorldServer" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --from=build /app/out/ ./
# World data the silo refuses to start without:
#  - the shared map-ownership topology, from the engine checkout (the MapServer reads the same file from its own image);
#  - the collision source: db/map_cache.dat of the pinned rAthena submodule (legacy/rathena; the release workflow checks it out).
# ATHENA_WORLD_PARTITIONS_PATH and ATHENA_WORLD_MAP_CACHE_PATH point at them (deployment manifest).
COPY conf/world_partitions.json /app/conf/world_partitions.json
COPY legacy/rathena/db/map_cache.dat /app/data/map_cache.dat
# Orleans listens on the silo port 11111 and the client gateway port 30000 of the pod address it is told to advertise
# (Orleans__Endpoints__AdvertisedIPAddress, the pod IP). Neither port is ever published outside the cluster.
RUN mkdir -p /app/log && chown "$APP_UID" /app/log
USER $APP_UID
ENTRYPOINT ["dotnet", "Athena.World.dll"]
