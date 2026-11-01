# Development

Use the exact .NET SDK version pinned in `global.json`. Locked dependency restore includes SDK-provided ASP.NET assets, so a different SDK patch can require a different lockfile. Docker builds use the same pinned SDK.

[Back to the README](../README.md)

## Run from source

Configure PostgreSQL and S3 values exactly as for Docker, then run:

```powershell
dotnet restore GachaBot.slnx --configfile NuGet.Config
dotnet build GachaBot.slnx --no-restore
dotnet test GachaBot.slnx --no-build --no-restore

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Discord__Enabled = 'false'
$env:Workers__Enabled = 'false'
dotnet run --no-build --no-restore --project src/GachaBot.Web
```

The development profile allows anonymous dashboard access by default and disables automatic workers. Imported sources can still be refreshed manually in the dashboard. To enable Game8 sources locally after the first build, install the pinned Playwright browser:

```powershell
& 'src/GachaBot.Web/bin/Debug/net10.0/playwright.ps1' install chromium
```

`BrowserAutomation__ChromiumSandbox` defaults to `true` for a local host. Docker Compose sets it to `false`, because the Playwright image otherwise cannot start Chromium on hosts that disable unprivileged user namespaces.

