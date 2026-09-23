# syntax=docker/dockerfile:1
# fyers-backfill — the LOCAL historical-data image.
#
# Clone of the fyers-collector image, but instead of a per-minute capture loop
# it is a bounded, resumable batch job: it pulls 1-minute OHLCV for every NSE
# cash equity and live + expired F&O contract from Fyers and writes one Parquet
# file per instrument to a mounted file share.
#
#   docker build -t fyers-backfill:local .
#   docker run --rm -v /path/to/share:/data --env-file .env fyers-backfill:local status
#
# No secrets are baked in: .env is injected at runtime (--env-file or -e), and
# config.yaml is read from the working dir or mounted. The dataset root defaults
# to /data (mount your share there).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Fyers.slnx ./
COPY Fyers.Core/Fyers.Core.csproj Fyers.Core/
COPY Fyers.Backfill/Fyers.Backfill.csproj Fyers.Backfill/
COPY Fyers.Collector/Fyers.Collector.csproj Fyers.Collector/
COPY Fyers.Token/Fyers.Token.csproj Fyers.Token/
COPY Fyers.Core.Tests/Fyers.Core.Tests.csproj Fyers.Core.Tests/
COPY Fyers.Token.Tests/Fyers.Token.Tests.csproj Fyers.Token.Tests/
COPY Fyers.Backfill.Tests/Fyers.Backfill.Tests.csproj Fyers.Backfill.Tests/
RUN dotnet restore Fyers.Backfill/Fyers.Backfill.csproj
COPY Fyers.Core/ Fyers.Core/
COPY Fyers.Backfill/ Fyers.Backfill/
COPY Fyers.Token/ Fyers.Token/
RUN dotnet publish Fyers.Backfill/Fyers.Backfill.csproj -c Release -o /pub --no-restore

# aspnet (not runtime) because Fyers.Token uses the ASP.NET Core shared framework
# for the OAuth callback host.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /pub .
# aspnet:10.0 already ships a uid-1000 user.
USER 1000
# The OAuth callback port, byte-exact with FYERS_REDIRECT_URI (Fyers app 02).
EXPOSE 8001
# Dataset root — mount the file share here.
VOLUME ["/data"]
ENV FYERS_BACKFILL_ROOT=/data
ENTRYPOINT ["dotnet", "Fyers.Backfill.dll"]
CMD ["status"]
