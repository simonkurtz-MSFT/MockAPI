# 1) Set up the build environment to compile the .NET MockAPI project

# Compile on the target architecture. The AOT SDK includes the native compiler and linker,
# which stay in this stage; CI uses native AMD64 and ARM64 runners, not cross-compilation.
# https://mcr.microsoft.com/en-us/artifact/mar/dotnet/sdk/tag/11.0.100-alpine3.24-aot
# Pinned image release date (UTC): 2026-11-10 (registry image creation metadata).
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24-aot@sha256:a6a89666d050f5d4806aa03edbc677291c7fb92191b5b2a8f3bccdec869ab87a AS build

# Keep this exact build-only curl patch until an upstream SDK includes it.
RUN apk add --upgrade --no-cache curl=8.23.0-r0 libcurl=8.23.0-r0

# The developer CLI passes the host's enabled HTTPS NuGet feed for approved proxy support.
# ARG keeps this restore-only setting in the build stage; nuget.org is the portable default.
# Do not pass credentials here: build arguments are not secrets. Host NuGet config is not copied.
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /src
# Restore before copying source so code-only edits can reuse the dependency layer.
COPY src/MockAPI/MockAPI.csproj src/MockAPI/
RUN dotnet restore src/MockAPI/MockAPI.csproj --source "$NUGET_SOURCE" -p:PublishAot=true

COPY src/MockAPI/ src/MockAPI/
COPY config/trailhead-aoai.json config/trailhead-inventory.json config/trailhead-shipping.json config/
RUN dotnet publish src/MockAPI/MockAPI.csproj --configuration Release --no-restore --output /app/publish -mt -p:PublishAot=true -p:TreatWarningsAsErrors=true

# ---------------------------------------------------------------------

# 2) Create the final runtime image with the published .NET MockAPI project

# Native AOT removes the managed runtime and JIT; the executable still needs native OS libraries.
# https://mcr.microsoft.com/en-us/artifact/mar/dotnet/runtime-deps/tag/11.0.0-alpine3.24
# Pinned image release date (UTC): 2026-11-10 (registry image creation metadata).
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-alpine3.24@sha256:5597d4a0c9ea3fb84fc210333013d761fc350d5925fc9042f62992675e15b58b AS final
EXPOSE 8080

WORKDIR /app
# Prepare the persistence directory for the non-root app; mounted volumes need compatible ownership too.
RUN mkdir -p /data && chown app:app /data

USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

VOLUME ["/data"]

COPY --from=build --chown=app:app /app/publish ./
ENTRYPOINT ["./MockAPI"]
