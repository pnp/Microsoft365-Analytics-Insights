# One Linux image for both halves of the Container Apps demo:
#
#   /app/web      the analytics web portal       dotnet /app/web/Web.AnalyticsWeb.dll (default)
#   /app/datagen  the synthetic data generator   dotnet /app/datagen/Tests.FakeDataGen.dll demo ...
#
# The container app runs the default command; the scheduled job overrides it to run the generator.
# One image keeps the two in lockstep: the generator writes exactly the schema this build of the
# web app reads, because both come from the same commit and the same compilation.
#
# Build context: a staged copy of the repository holding only .nvmrc, build/ and src/AnalyticsEngine/
# (Deploy-ContainerAppsDemo.ps1 stages it from `git ls-files`, so bin/obj/node_modules and anything
# gitignored - local settings, the environment file - never reach the build).
#
# -p:AnalyticsTargetOS=linux retargets the net10.0-windows projects to plain net10.0 so nothing asks
# for the Windows Desktop runtime; see src/AnalyticsEngine/Directory.Build.props.

ARG DOTNET_VERSION=10.0
# The Node major pinned in /.nvmrc - Web.csproj refuses to build the portal on any other. The deploy
# script and the net10 workflow pass it from .nvmrc; this default only serves a hand-run docker build.
ARG NODE_VERSION=24

FROM node:${NODE_VERSION}-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
# Web.csproj builds the portal SPA itself (npm install + npm run build), so the SDK stage needs Node.
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && node --version && npm --version

WORKDIR /repo
COPY . .

ARG BUILD_CONFIGURATION=Release
# The generator references Tests.UnitTests, which references Web, so three appsettings.json files
# arrive at the same publish path (NETSDK1152). Only the generator's own is meaningful, and it is
# copied over the top afterwards so the choice is deterministic rather than the SDK's.
RUN dotnet publish src/AnalyticsEngine/Web/Web.csproj \
      -c $BUILD_CONFIGURATION -p:AnalyticsTargetOS=linux -p:UseAppHost=false --nologo -o /out/web \
 && dotnet publish src/AnalyticsEngine/Tests.FakeDataGen/Tests.FakeDataGen.csproj \
      -c $BUILD_CONFIGURATION -p:AnalyticsTargetOS=linux -p:UseAppHost=false -p:ErrorOnDuplicatePublishOutputFiles=false \
      --nologo -o /out/datagen \
 && cp src/AnalyticsEngine/Tests.FakeDataGen/appsettings.json /out/datagen/appsettings.json \
 && rm -rf /out/datagen/wwwroot \
 && test -f /out/web/wwwroot/index.html \
 && ! grep -q WindowsDesktop /out/web/Web.AnalyticsWeb.runtimeconfig.json /out/datagen/Tests.FakeDataGen.runtimeconfig.json

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
COPY --from=build /out/web /app/web
COPY --from=build /out/datagen /app/datagen

# The runtime image's non-root user. Container Apps' ingress terminates TLS and forwards plain HTTP
# to this port.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
WORKDIR /app/web
ENTRYPOINT ["dotnet"]
CMD ["/app/web/Web.AnalyticsWeb.dll"]
