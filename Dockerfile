# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first, against the project files only, so that a source-only change
# does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props Akiba.sln ./
COPY src/Akiba.Domain/*.csproj            src/Akiba.Domain/
COPY src/Akiba.Application/*.csproj       src/Akiba.Application/
COPY src/Akiba.Infrastructure/*.csproj    src/Akiba.Infrastructure/
COPY src/Akiba.Web/*.csproj               src/Akiba.Web/
COPY tests/Directory.Build.props          tests/
COPY tests/Akiba.Domain.Tests/*.csproj         tests/Akiba.Domain.Tests/
COPY tests/Akiba.Application.Tests/*.csproj    tests/Akiba.Application.Tests/
COPY tests/Akiba.Infrastructure.Tests/*.csproj tests/Akiba.Infrastructure.Tests/
COPY tests/Akiba.ArchitectureTests/*.csproj    tests/Akiba.ArchitectureTests/
RUN dotnet restore Akiba.sln

COPY . .
RUN dotnet publish src/Akiba.Web/Akiba.Web.csproj \
        --no-restore \
        --configuration Release \
        --output /app

# ---- run ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as a non-root user. This container holds member financial records.
RUN adduser --system --uid 5401 --group akiba
USER akiba

COPY --from=build --chown=akiba:akiba /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=Africa/Nairobi \
    DOTNET_gcServer=0

EXPOSE 8080

ENTRYPOINT ["dotnet", "Akiba.Web.dll"]
