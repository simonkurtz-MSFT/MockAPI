# 1) Set up the build environment to compile the .NET MockAPI project

# Compile on the target architecture. The AOT SDK includes the native compiler and linker,
# which stay in this stage; CI uses native AMD64 and ARM64 runners, not cross-compilation.
# Pinned image release date (UTC): 2026-10-24 (registry image creation metadata).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-aot@sha256:8a339f2175a99aad64cd618450303b8361d565eebbdf8aad95cdf8e6b54ab9d3 AS build

# The serviced SDK fixes Brotli but predates the curl security update.
# Keep this exact build-only patch until an upstream SDK includes it.
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
RUN dotnet publish src/MockAPI/MockAPI.csproj --configuration Release --no-restore --output /app/publish -p:PublishAot=true -p:TreatWarningsAsErrors=true

# ---------------------------------------------------------------------

# 2) Create the final runtime image with the published .NET MockAPI project

# Native AOT removes the managed runtime and JIT; the executable still needs native OS libraries.
# Pinned image release date (UTC): 2026-10-24 (registry image creation metadata).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14-alpine3.24@sha256:752897bc0b19f05fd5849897815180752103293ffc3b68bd404a4db1e5c3705c AS final
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
