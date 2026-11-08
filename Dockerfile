# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config BusinessHarness.slnx ./
COPY locks/ locks/
COPY external/MAGICore/ external/MAGICore/
COPY external/AgentConnector/ external/AgentConnector/
COPY external/AgentSpace/Directory.Build.props external/AgentSpace/
COPY external/AgentSpace/src/AgentSpace.Application/ external/AgentSpace/src/AgentSpace.Application/
COPY external/AgentSpace/src/AgentSpace.Domain/ external/AgentSpace/src/AgentSpace.Domain/
COPY external/AgentSpace/src/AgentSpace.Infrastructure/ external/AgentSpace/src/AgentSpace.Infrastructure/
COPY src/ src/
COPY README.md LICENSE ./
RUN dotnet restore src/BusinessHarness.Cli/BusinessHarness.Cli.csproj --locked-mode --configfile NuGet.Config
RUN dotnet publish src/BusinessHarness.Cli/BusinessHarness.Cli.csproj -c Release --no-restore -o /app --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
LABEL org.opencontainers.image.authors="Jihad Khawaja <jihadkhawaja@outlook.com>" \
      org.opencontainers.image.licenses="MIT"
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE ./
COPY external/MAGICore/LICENSE ./licenses/MAGICore/LICENSE
COPY external/AgentConnector/LICENSE ./licenses/AgentConnector/LICENSE
COPY external/AgentSpace/LICENSE ./licenses/AgentSpace/LICENSE
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/BusinessHarness.Cli.dll"]
CMD ["--help"]
