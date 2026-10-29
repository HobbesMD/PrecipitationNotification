# Build: SDK image. Runtime: chiseled *-extra*, non-root (user "app").
#
# Why -extra: the worker resolves an IANA zone id from NWS (America/New_York)
# with TimeZoneInfo.FindSystemTimeZoneById. Plain chiseled images ship no
# tzdata, so that call would throw on every run. -extra adds tzdata + ICU.
# `docker run --rm <image> --self-check` proves the zone rules are present.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached until the project file changes.
COPY global.json ./
COPY src/Worker/Worker.csproj src/Worker/
RUN dotnet restore src/Worker/Worker.csproj

COPY src/Worker/ src/Worker/
RUN dotnet publish src/Worker/Worker.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Worker.dll"]
