$ErrorActionPreference = 'Stop'
$sha = '6209783f4b5d68babe342b4eeae9eec6eeef91bc'
$fixture = 'packcall-s219-linux-' + [Guid]::NewGuid().ToString('N').Substring(0,10)
$outDir = Join-Path $PWD "artifacts/story-2-19/dotnet-linux-replay/$fixture"
$source = Join-Path $outDir 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$sha | Set-Content -Encoding utf8 "$outDir/source-sha.txt"
git archive --format=tar -o "$outDir/source.tar" $sha
if ($LASTEXITCODE) { throw 'git archive failed' }
tar -xf "$outDir/source.tar" -C $source
if ($LASTEXITCODE) { throw 'tar failed' }
$network = "$fixture-net"
docker network create $network | Out-Null
if ($LASTEXITCODE) { throw 'Docker network failed' }
try {
  docker run --rm -d --name $fixture --network $network --network-alias pg -e POSTGRES_HOST_AUTH_METHOD=trust postgres:18.6-alpine3.24 | Out-Null
  if ($LASTEXITCODE) { throw 'PostgreSQL container failed' }
  $ready = $false
  for ($attempt = 0; $attempt -lt 60; $attempt++) {
    docker exec $fixture pg_isready -h 127.0.0.1 -U postgres *> $null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw 'PostgreSQL not ready' }
  $script = 'set -euo pipefail; dotnet --version; dotnet build PackCall.slnx -c Release --nologo | tee /results/build.log; PACKCALL_ENVIRONMENT=test TEST_SENDER_MODE=inprocess dotnet run --project src/PackCall.Migrator -c Release --no-build | tee /results/migrate.log; dotnet test tests/PackCall.Test/PackCall.Test.csproj -c Release --no-build --nologo --logger "trx;LogFileName=final.trx" --results-directory /results > /results/test.log 2>&1 || { tail -80 /results/test.log; exit 1; }; tail -12 /results/test.log'
  docker run --rm --network $network --workdir /source -v "${source}:/source" -v "${outDir}:/results" -v "$env:USERPROFILE/.nuget/packages:/root/.nuget/packages:ro" -e NuGetAudit=false -e 'PACKCALL_TEST_POSTGRES=Host=pg;Database=postgres;Username=postgres;Pooling=false' -e 'ConnectionStrings__Postgres=Host=pg;Database=postgres;Username=postgres' mcr.microsoft.com/dotnet/sdk:10.0 bash -c $script
  if ($LASTEXITCODE) { throw 'Linux build/test failed' }
  python scripts/ci/check_trx.py "$outDir/final.trx" --forbid-skipped
  if ($LASTEXITCODE) { throw 'TRX gate failed' }
} finally {
  docker rm -f $fixture
  docker network rm $network
}
