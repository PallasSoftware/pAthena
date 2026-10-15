ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# CharServer.csproj compiles source of src/Shared (CanonicalMapPolicy and its generated alias data).
COPY Directory.Build.props ./
COPY src/Shared/ src/Shared/
COPY src/CharServer/ src/CharServer/
RUN dotnet restore src/CharServer/CharServer.csproj -p:RestoreLockedMode=true
RUN dotnet publish src/CharServer/CharServer.csproj -c Release --no-restore -o /app/out

FROM ${RUNTIME_IMAGE}
ARG SOURCE_URL
ARG SOURCE_REVISION
ARG VERSION
LABEL org.opencontainers.image.title="PaThena CharServer" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --from=build /app/out/ ./
# The engine's own configuration, pinned with the engine: the CharServer needs the start points, start items and PIN
# rules of char_athena.conf (without them no character can be created) and the table names of inter_athena.conf.
# Deployment specific values are not baked in: a ConfigMap is mounted at /app/conf/k8s and imports char_athena.conf.
COPY conf/templates/char_athena.conf conf/templates/inter_athena.conf /app/conf/
COPY conf/import/ /app/conf/import/
RUN mkdir -p /app/conf/k8s /app/log && chown "$APP_UID" /app/log
USER $APP_UID
ENTRYPOINT ["dotnet", "CharServer.dll"]
