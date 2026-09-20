# The container race control starts beside a race server. It shares the server's network, reads the
# preset's dd-grid.json and the server's content, and drives the field until the race is over.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY DDGrid.sln ./
COPY src/ src/
COPY tools/ tools/
RUN dotnet publish tools/DDGrid.Drive/DDGrid.Drive.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app ./
# The grid and pit boxes of every track a race can be run on: the race servers do not carry the models
# these come from, so they ship with the bots.
COPY data/tracks/ ./data/tracks/
ENTRYPOINT ["dotnet", "/app/DDGrid.Drive.dll"]
