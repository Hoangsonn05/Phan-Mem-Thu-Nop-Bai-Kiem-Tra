# syntax=docker/dockerfile:1.7

ARG DOTNET_VERSION=10.0
ARG BUILD_CONFIGURATION=Release

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src

COPY . .
RUN dotnet restore "ExamTransfer.sln"

FROM restore AS build
ARG BUILD_CONFIGURATION
RUN dotnet build "ExamTransfer.sln" \
    --configuration "${BUILD_CONFIGURATION}" \
    --no-restore

FROM build AS test
ARG BUILD_CONFIGURATION
ENTRYPOINT ["dotnet", "test", "ExamTransfer.sln", "--configuration", "Release", "--no-build", "--logger", "console;verbosity=normal"]

FROM build AS publish
ARG BUILD_CONFIGURATION
RUN dotnet publish \
    "src/ExamTransfer.LocalServer/ExamTransfer.LocalServer.csproj" \
    --configuration "${BUILD_CONFIGURATION}" \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/ExamTransfer /usr/share/ExamTransfer \
    && chown -R app:app /data/ExamTransfer /usr/share/ExamTransfer /app

COPY --from=publish --chown=app:app /app/publish .

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

EXPOSE 5048/tcp
EXPOSE 5050/udp

USER app

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl --fail --silent --show-error http://127.0.0.1:5048/health || exit 1

ENTRYPOINT ["dotnet", "ExamTransfer.LocalServer.dll"]
