FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

COPY NuGet.Config global.json Directory.Build.props Directory.Packages.props GachaBot.slnx ./
COPY src/GachaBot.Domain/GachaBot.Domain.csproj src/GachaBot.Domain/
COPY src/GachaBot.Application/GachaBot.Application.csproj src/GachaBot.Application/
COPY src/GachaBot.Infrastructure/GachaBot.Infrastructure.csproj src/GachaBot.Infrastructure/
COPY src/GachaBot.Web/GachaBot.Web.csproj src/GachaBot.Web/
RUN dotnet restore src/GachaBot.Web/GachaBot.Web.csproj --configfile NuGet.Config

COPY src/ src/
RUN dotnet publish src/GachaBot.Web/GachaBot.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble AS runtime
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
WORKDIR /app
RUN mkdir -p /app/data/browser-profile && chown -R pwuser:pwuser /app/data
COPY --from=build --chown=pwuser:pwuser /app/publish .

ENV ASPNETCORE_URLS=http://+:8791 \
    DatabaseStorage__RootPath=/app/data/databases \
    MediaArchive__RootPath=/app/data/media \
    MediaArchive__StagingPath=/app/data/media-staging \
    BrowserAutomation__ProfilePath=/app/data/browser-profile
EXPOSE 8791
VOLUME ["/app/data"]
USER pwuser
ENTRYPOINT ["dotnet", "GachaBot.Web.dll"]
