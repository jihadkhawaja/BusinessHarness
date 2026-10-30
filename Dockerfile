# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config BusinessHarness.slnx ./
COPY locks/ locks/
COPY external/MAGICore/ external/MAGICore/
COPY src/ src/
COPY README.md LICENSE ./
RUN dotnet restore src/BusinessHarness.Cli/BusinessHarness.Cli.csproj --locked-mode --configfile NuGet.Config
RUN dotnet publish src/BusinessHarness.Cli/BusinessHarness.Cli.csproj -c Release --no-restore -o /app --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:10.0.14@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS runtime
LABEL org.opencontainers.image.authors="Jihad Khawaja <jihadkhawaja@outlook.com>" \
      org.opencontainers.image.licenses="MIT"
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE ./
COPY external/MAGICore/LICENSE ./licenses/MAGICore/LICENSE
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/BusinessHarness.Cli.dll"]
CMD ["--help"]
