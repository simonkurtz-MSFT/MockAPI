# 1) Set up the build environment to compile the .NET MockAPI project

# Compile on the target architecture. The AOT SDK includes the native compiler and linker,
# which stay in this stage; CI uses native AMD64 and ARM64 runners, not cross-compilation.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400-alpine3.24-aot@sha256:cd255a72d14d70260bb9b8dc493c3c37ce0877ef534f85ac1c931628364d7059 AS build

# The developer CLI passes the host's enabled HTTPS NuGet feed for approved proxy support.
# ARG keeps this restore-only setting in the build stage; nuget.org is the portable default.
# Do not pass credentials here: build arguments are not secrets. Host NuGet config is not copied.
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /src
# Restore before copying source so code-only edits can reuse the dependency layer.
COPY src/MockAPI/MockAPI.csproj src/MockAPI/
RUN dotnet restore src/MockAPI/MockAPI.csproj --source "$NUGET_SOURCE" -p:PublishAot=true

COPY src/MockAPI/ src/MockAPI/
COPY config/mockapi.json config/
RUN dotnet publish src/MockAPI/MockAPI.csproj --configuration Release --no-restore --output /app/publish -p:PublishAot=true -p:TreatWarningsAsErrors=true

# ---------------------------------------------------------------------

# 2) Create the final runtime image with the published .NET MockAPI project

# Native AOT removes the managed runtime and JIT; the executable still needs native OS libraries.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.11-alpine3.24@sha256:379b17d7d388a2a1b5330bfc2429a01091f85e255d3bce7981d65927d786c000 AS final
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
