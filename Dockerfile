# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/NCMarket.Core/NCMarket.Core.csproj src/NCMarket.Core/
COPY src/NCMarket.Cli/NCMarket.Cli.csproj   src/NCMarket.Cli/
RUN dotnet restore src/NCMarket.Cli/NCMarket.Cli.csproj

COPY src/ src/
RUN dotnet publish src/NCMarket.Cli/NCMarket.Cli.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /data

COPY --from=build /app /app
COPY docker/ncmarket docker/snapshot-job docker/entrypoint /usr/local/bin/
RUN chmod +x /usr/local/bin/ncmarket /usr/local/bin/snapshot-job /usr/local/bin/entrypoint

# Su Linux .NET risolve LocalApplicationData come $XDG_DATA_HOME: database e cache
# dei nomi item/skill finiscono quindi in /data/NCMarket (vedi AppPaths.cs).
ENV XDG_DATA_HOME=/data

ENTRYPOINT ["/usr/local/bin/entrypoint"]
CMD ["idle"]
