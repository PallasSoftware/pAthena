ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY Directory.Build.props ./
COPY src/Shared/ src/Shared/
COPY src/LoginServer/ src/LoginServer/
RUN dotnet restore src/LoginServer/LoginServer.csproj -p:RestoreLockedMode=true
RUN dotnet publish src/LoginServer/LoginServer.csproj -c Release --no-restore -o /app/out

FROM ${RUNTIME_IMAGE}
ARG SOURCE_URL
ARG SOURCE_REVISION
ARG VERSION
LABEL org.opencontainers.image.title="PaThena LoginServer" \
      org.opencontainers.image.source="${SOURCE_URL}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --from=build /app/out/ ./
RUN mkdir -p /app/conf /app/log && chown "$APP_UID" /app/log
USER $APP_UID
ENTRYPOINT ["dotnet", "LoginServer.dll"]
