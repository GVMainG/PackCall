FROM mcr.microsoft.com/dotnet/sdk:10.0 AS native
RUN sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources && apt-get -o Acquire::Retries=3 update && apt-get -o Acquire::Retries=3 install -y --no-install-recommends gcc libc6-dev
COPY tests/acceptance/story-2-2-workers/clock.c /clock.c
RUN gcc -shared -fPIC /clock.c -o /clock.so -ldl
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish tests/PackCall.Workers.Acceptance/PackCall.Workers.Acceptance.csproj -c Release -o /published /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /published .
COPY --from=native /clock.so .
ENV LD_PRELOAD=/app/clock.so
ENTRYPOINT ["dotnet", "PackCall.Workers.Acceptance.dll"]

