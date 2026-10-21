# 1) Set up the build environment to compile the .NET MockAPI project

# Compile on the target architecture. The AOT SDK includes the native compiler and linker,
# which stay in this stage; CI uses native AMD64 and ARM64 runners, not cross-compilation.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24-aot@sha256:574e01812ecd2f33a79cb61b0398320abbd0589125b1e0a6fea00806ba630cb4 AS build

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
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13-alpine3.24@sha256:1a5dbee0a1d2bb1d038e212e8ccdbc12783b5cfbed866fe75d126ee80178a52a AS final
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
