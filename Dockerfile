FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src
COPY global.json MockAPI.slnx ./
COPY src/MockAPI/MockAPI.csproj src/MockAPI/
RUN dotnet restore src/MockAPI/MockAPI.csproj

COPY src/MockAPI/ src/MockAPI/
RUN dotnet publish src/MockAPI/MockAPI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:TreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final

WORKDIR /app
COPY --from=build --chown=app:app /app/publish ./

USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["./MockAPI"]