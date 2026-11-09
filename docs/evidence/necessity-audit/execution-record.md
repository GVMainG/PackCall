# Исполнение necessity-аудита

## Baseline на HEAD

**12 ноября 2026, 11:32–11:34 MSK.** Ветка `gv`, SHA `b75d7fe7726b1b12255686a2ff73a520037f8942`. До прогона и после проверки обоих TRX `git status --porcelain` вернул пустой вывод. `git merge-base --is-ancestor 47db371 HEAD` вернул 0. Коммит переноса тестов — `47db371b85cfb989decff99a03f3698899e20cf2`. Тестовый каталог восстановлен из `git archive` этого HEAD и подключён в контейнерах только для чтения.

**Окружение.** Windows и PowerShell, Docker Desktop 4.95.0, Docker Linux amd64 Engine 29.8.2. Образ `mcr.microsoft.com/dotnet/sdk:10.0`, digest `sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb`, Ubuntu 24.04 Linux x64, SDK 10.0.404, runtime 10.0.15, сборка Release. Тестовый проект использует Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3 и xunit.runner.visualstudio 3.1.4. Образ БД `postgres:18.6-alpine3.24`, digest `sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5`. Сервер вернул `18.6` на `SHOW server_version;`. TRX проверены Python 3.11.15 на хосте. [Тестовый проект](../../../tests/PackCall.Test/PackCall.Test.csproj), [проверка TRX](../../../scripts/ci/check_trx.py).

| Категория | Выполнено 2 ноября | Выполнено 12 ноября | Прошло | Упало | Пропущено | Разница |
|---|---:|---:|---:|---:|---:|---:|
| Unit | 109 | 130 | 130 | 0 | 0 | +21 |
| Integration | 81 | 82 | 82 | 0 | 0 | +1 |
| Всего | 190 | 212 | 212 | 0 | 0 | +22 |

Сравнение двух пар TRX по классу и полному имени развёрнутого случая показало, что все исторические случаи сохранились в своих категориях. Добавились 10 вариантов `DeliveryStatusContractTests`, 8 вариантов `RuntimeSamplerClockQualityTests`, 3 случая `DeliveryWorkerWarningTests` и 1 случай `MigrationAppliedTests`. [Контракт статусов](../../../tests/PackCall.Test/Unit/Deliveries/DeliveryStatusContractTests.cs), [RuntimeSampler](../../../tests/PackCall.Test/Unit/Measurements/RuntimeSamplerClockQualityTests.cs), [предупреждение Worker](../../../tests/PackCall.Test/Unit/Workers/DeliveryWorkerWarningTests.cs), [применение миграции](../../../tests/PackCall.Test/Integration/Migrations/MigrationAppliedTests.cs).

Обе тестовые команды и обе проверки `check_trx.py --forbid-skipped` завершились с exit 0. `MigrationAppliedTests` подтвердил миграцию `20261028193126_InitialCreate` в целевой БД. В журнале мигратора есть ошибка первого чтения `__EFMigrationsHistory` на пустой БД, после которой мигратор завершился с exit 0. Сборки записали предупреждение `CS8618` для `Recipient.Email`.

Файлы находятся вне Git в `artifacts/test-results/story-1-11/` от корня рабочего дерева. В том же каталоге сохранены `unit.log`, `integration.log`, `dotnet-info.log`, `postgres-version.log` и `migration.log`.

| TRX | SHA-256 |
|---|---|
| `unit.trx` | `d3d128e7a5cbb75985ebc2c9217d8a7579f363ae08a2aba20a9b8bc578e5f018` |
| `integration.trx` | `3690bdc49c8c464c1957b6e85220c894cc6b2ae840b65f826ce1bc93d7f741b8` |

### Команды baseline

Исходный прогон использовал теги `mcr.microsoft.com/dotnet/sdk:10.0` и `postgres:18.6-alpine3.24`, каталог `artifacts/test-results/story-1-11/` и сеть `packcall-story-1-11`. БД запущена командой `docker run -d --name packcall-story-1-11-db --network packcall-story-1-11 -e POSTGRES_DB=packcall_audit -e POSTGRES_PASSWORD postgres:18.6-alpine3.24`. Тесты получали строку подключения через `PACKCALL_TEST_POSTGRES`, мигратор — через `ConnectionStrings__Postgres`. Порт БД на хост не публиковался.

Повтор из корня чистого checkout указанного SHA использует те же команды архивации, сборки, миграции и тестов, образы по проверенным `RepoDigests` и отдельный подкаталог `replay-*`. PostgreSQL создаётся и запускается отдельными командами для очистки при ошибке запуска.

```powershell
& {
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $false
    $baseline = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $baseline -ne 'b75d7fe7726b1b12255686a2ff73a520037f8942') {
        throw 'Expected checkout of the baseline SHA'
    }
    $workingTree = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingTree) { throw 'Expected a clean checkout' }
    git merge-base --is-ancestor 47db371 HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Missing test relocation' }

    $runId = [guid]::NewGuid().ToString('N')
    $artifactRoot = (New-Item -ItemType Directory "artifacts/test-results/story-1-11/replay-$runId").FullName
    $archiveRoot = (New-Item -ItemType Directory (Join-Path $artifactRoot 'source')).FullName
    git archive --format=tar --output (Join-Path $artifactRoot 'source.tar') $baseline
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    tar -xf (Join-Path $artifactRoot 'source.tar') -C $archiveRoot
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }

    $sdkImage = 'mcr.microsoft.com/dotnet/sdk@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb'
    $postgresImage = 'postgres@sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5'
    $sourceMount = "type=bind,source=$archiveRoot,target=/src,readonly"
    $resultsMount = "type=bind,source=$artifactRoot,target=/results"
    $networkName = "packcall-story-1-11-$runId"
    $dbName = "$networkName-db"

    docker run --rm $sdkImage dotnet --info |
        Tee-Object -FilePath (Join-Path $artifactRoot 'dotnet-info.log')
    if ($LASTEXITCODE -ne 0) { throw 'SDK probe failed' }
    docker run --rm --mount $sourceMount --mount $resultsMount -w /src $sdkImage `
        dotnet test tests/PackCall.Test/PackCall.Test.csproj -c Release --artifacts-path /tmp/packcall-build `
        --filter 'Category=Unit' --logger 'trx;LogFileName=unit.trx' --results-directory /results |
        Tee-Object -FilePath (Join-Path $artifactRoot 'unit.log')
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }

    $savedEnv = @{}
    foreach ($name in 'POSTGRES_PASSWORD', 'PACKCALL_TEST_POSTGRES', 'ConnectionStrings__Postgres') {
        $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $createdNetwork = $false
    $createdDatabase = $false
    try {
        docker network create $networkName
        if ($LASTEXITCODE -ne 0) { throw 'Test network creation failed' }
        $createdNetwork = $true

        $env:POSTGRES_PASSWORD = [guid]::NewGuid().ToString('N')
        $env:PACKCALL_TEST_POSTGRES = "Host=$dbName;Port=5432;Database=packcall_audit;Username=postgres;Password=$env:POSTGRES_PASSWORD"
        $env:ConnectionStrings__Postgres = $env:PACKCALL_TEST_POSTGRES
        docker create --name $dbName --network $networkName -e POSTGRES_DB=packcall_audit `
            -e POSTGRES_PASSWORD $postgresImage
        if ($LASTEXITCODE -ne 0) { throw 'Test PostgreSQL creation failed' }
        $createdDatabase = $true
        docker start $dbName
        if ($LASTEXITCODE -ne 0) { throw 'Test PostgreSQL startup failed' }
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            docker exec $dbName pg_isready -h 127.0.0.1 -U postgres -d packcall_audit | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 1
        }
        if ($LASTEXITCODE -ne 0) { throw 'Test PostgreSQL unavailable' }
        docker exec $dbName psql -U postgres -d packcall_audit -Atc 'SHOW server_version;' |
            Tee-Object -FilePath (Join-Path $artifactRoot 'postgres-version.log')
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL version query failed' }

        docker run --rm --network $networkName --mount $sourceMount -w /src `
            -e ConnectionStrings__Postgres -e PACKCALL_ENVIRONMENT=test -e TEST_SENDER_MODE=inprocess `
            -e TEST_SIMULATE_ERRORS=false $sdkImage dotnet run `
            --project src/PackCall.Migrator/PackCall.Migrator.csproj -c Release --artifacts-path /tmp/packcall-build |
            Tee-Object -FilePath (Join-Path $artifactRoot 'migration.log')
        if ($LASTEXITCODE -ne 0) { throw 'Migrator failed' }
        docker run --rm --network $networkName --mount $sourceMount --mount $resultsMount -w /src `
            -e PACKCALL_TEST_POSTGRES $sdkImage dotnet test tests/PackCall.Test/PackCall.Test.csproj `
            -c Release --artifacts-path /tmp/packcall-build --filter 'Category=Integration' `
            --logger 'trx;LogFileName=integration.trx' --results-directory /results |
            Tee-Object -FilePath (Join-Path $artifactRoot 'integration.log')
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
    }
    finally {
        $cleanupWarnings = @()
        if ($createdDatabase) {
            try {
                docker rm -fv $dbName
                if ($LASTEXITCODE -ne 0) { throw 'docker rm failed' }
            }
            catch { $cleanupWarnings += "Could not remove test database container $dbName" }
        }
        if ($createdNetwork) {
            try {
                docker network rm $networkName
                if ($LASTEXITCODE -ne 0) { throw 'docker network rm failed' }
            }
            catch { $cleanupWarnings += "Could not remove test network $networkName" }
        }
        foreach ($name in $savedEnv.Keys) {
            if ($null -eq $savedEnv[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -LiteralPath "Env:$name" -Value $savedEnv[$name]
            }
        }
        foreach ($warning in $cleanupWarnings) { Write-Warning $warning -WarningAction Continue }
    }

    python scripts/ci/check_trx.py --forbid-skipped (Join-Path $artifactRoot 'unit.trx')
    if ($LASTEXITCODE -ne 0) { throw 'Unit TRX failed validation' }
    python scripts/ci/check_trx.py --forbid-skipped (Join-Path $artifactRoot 'integration.trx')
    if ($LASTEXITCODE -ne 0) { throw 'Integration TRX failed validation' }
    $head = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -ne $baseline) { throw 'HEAD changed during the run' }
    $workingTree = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingTree) { throw 'Checkout changed during the run' }
    Write-Output "Replay results: $artifactRoot"
}
```

12 ноября 2026 года блок выше повторно выполнен на отдельном чистом checkout того же SHA. Оба тестовых прогона и проверки TRX завершились с exit 0. Повтор дал 130 Unit и 82 Integration без ошибок и пропусков. После очистки временной БД и сети исходные значения всех трёх переменных подключения сохранились. Логи и TRX повтора находятся вне Git в `artifacts/test-results/story-1-11/replay-488dc27f3562488d98057ff80f65ed24/`.

### Сравнение имён тестов в TRX

Из корня репозитория, при наличии исторических TRX. Код сравнивает кратность пары «класс, полное имя случая» в каждой категории и показывает изменения по классам. Для сверки повторного запуска укажите его подкаталог `replay-*` в `new_root`.

```powershell
@'
from collections import Counter
from pathlib import Path
import xml.etree.ElementTree as ET

old_root = Path('_bmad-output/specs/spec-test-necessity-audit/audit-results/raw')
new_root = Path('artifacts/test-results/story-1-11')
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def cases(path):
    root = ET.parse(path).getroot()
    classes = {
        test.attrib['id']: test.find('t:TestMethod', ns).attrib['className']
        for test in root.findall('./t:TestDefinitions/t:UnitTest', ns)
    }
    return Counter(
        (classes[result.attrib['testId']], result.attrib['testName'])
        for result in root.findall('./t:Results/t:UnitTestResult', ns)
    )

def by_class(cases):
    counts = Counter()
    for (class_name, _), count in cases.items():
        counts[class_name] += count
    return dict(sorted(counts.items()))

for category in ('unit', 'integration'):
    filename = f'{category}.trx'
    if not (old_root / filename).is_file():
        print(f'{category}: historical TRX unavailable, comparison skipped')
        continue
    old, new = cases(old_root / filename), cases(new_root / filename)
    print(f'{category}: removed {by_class(old - new)}, added {by_class(new - old)}')
'@ | python -
```

## Исторический аудит

**2 ноября 2026, Europe/Moscow.** Ниже зафиксированы условия и результаты на момент исходного аудита. Все упомянутые в этой секции файлы `raw/`, JSON инвентаризации, логи и индексы относятся к внешнему архиву `_bmad-output/specs/spec-test-necessity-audit/audit-results/`. Пути запуска и номера строк относятся к историческому checkout. [Актуальные статусы RUN](decisions.md), [GAP](coverage-gaps.md).

Checkout: `72228dd69a81f9eb90eb95978a8a3aceba89fe26`. Git status в начале и после анализа пуст; исходники приложения, тесты, helpers, runners, specification companions и historical transfer evidence не редактировались. Созданы отчёты/служебные генераторы во внешнем архиве и штатные игнорируемые runtime artifacts/venv/publish. Одноразовые скрипты переноса не запускались. Команды с build/publish исполняют исходный код, а не меняют его.

Среда: Windows, Python 3.11.15; установлен SDK **11.0.100-rc.2.26504.105**, который успешно собрал net10.0 (отдельный SDK 10 не установлен). Фактические runtime версии — raw/runtimes.txt. Docker Desktop 4.95.0, Linux Engine 29.8.2, Compose 5.6.0; PostgreSQL `18.6-alpine3.24` / x86_64-pc-linux-musl. Историческое отсутствие Docker к этому запуску неприменимо. raw/environment.json, docker-version.txt, python-packages.txt фиксируют подробности. Hashes всех scope files — inventory-files.json, production/project/config dependencies — source-dependencies.json. Исторические snapshots не использованы как новый baseline.

Запись зависимостей: `python -m pip freeze` в tool-host interpreter завершилась `No module named pip`; вместо неё raw/python-packages.txt записан через стандартный `importlib.metadata` вместе с sys.executable/version. Это сбой команды инвентаризации среды, не failed test. Privacy использовал собственный созданный venv с pip. Все существующие test suites со своими импортами исполнились, зависимости нельзя оценивать только наличием pip в launcher interpreter.

### Выполненные проверки 2 ноября

| Команда из корня | Статус / exit | Наблюдение / raw artifact |
|---|---|---|
| `dotnet test PackCall.Test --filter 'Category=Unit' --logger 'trx;LogFileName=unit.trx' --results-directory _bmad-output/specs/spec-test-necessity-audit/audit-results/raw` | passed / 0 | 109 passed, 0 failed, 0 skipped; unit.log/unit.trx |
| Та же команда с `Category=Integration`, `integration.trx` и выделенной PACKCALL_TEST_POSTGRES | passed / 0 | 81 passed, 0 failed, 0 skipped; integration.log/integration.trx; source parameters раскрыты, включая PG theories |
| `python -m unittest discover -s tests/tools/performance -p 'test_*.py' -v` | passed / 0 | 18 methods; python-performance.log |
| `python -m unittest discover -s tests/tools/operators -p 'test_*.py' -v` | passed / 0 | 4 methods с внутренними controls; python-operators.log |
| `python tests/acceptance/operators/operator_contract.py` | passed / 0 | 5 methods, fixture/negative matrices; operator-contract.log |
| `python tests/acceptance/workers/coordination/run.py`, inprocess | passed / 0 | R11 independent pause/shutdown + W22-01–05; coord.log; w22-inprocess/run-*/results.json |
| Та же команда, http | passed / 0 | все те же blocks в HTTP topology; w22-http.log; w22-http/run-*/results.json |
| `python tests/acceptance/workers/coordination/verify.py`, сначала inprocess, затем http | passed / 0, 0 | оба regression TRX 190 passed без skip, build/publish Api/Worker/Migrator и release Docker image; verify-inprocess.log, verify-http.log, соответствующие run-*/gates.json и gate-*.log |
| `python tests/acceptance/operators/run.py` | passed / 0 | absent/empty mandatory keys и оба runtime modes прошли; operator-run.log; operator-baseline/results.json, commands.jsonl, results/full.trx |
| `python tests/acceptance/operators/config_matrix.py` | failed / 1 | остановился на line 17, empty TEST_SIMULATE_ERRORS; последующие blocks не исполнены. config-matrix.log, UNC-09 |
| `python tests/acceptance/operators/precedence.py` | passed / 0 | три image × два keys, invalid overrides и CLI precedence; precedence.log, operator-extra/precedence.json |
| `python tests/acceptance/operators/prod_compose.py` | passed / 0 | empty/prepared DB и stale successful migrator guard; prod-compose.log, operator-extra/prod-compose.json |
| `python tests/acceptance/operators/privacy.py` | passed / 0 после подготовки | первая попытка FileNotFoundError: требуемого venv не было; создан штатный venv, pip requirements, повтор прошёл. privacy.log, privacy-deps.log, operator-extra/privacy-python.json; первоначальный setup error не засчитывался |
| `python tests/acceptance/operators/retention.py` | passed / 0 | только после passed operator run; retention.log, operator-extra/retention.json/retention-logs.json |
| `python tests/acceptance/operators/late_http.py` | failed / 1 | marker wait line 66 timed out; поздние row/fence assertions не достигнуты. late-http.log; W22 HTTP run/*late-old.log; UNC-12 |
| `python tests/acceptance/operators/shutdown.py` | passed / 0 | inprocess/http × before/ignore probes, lease/journal unchanged, time bound; shutdown.log и operator-extra/shutdown-probes.json |
| `python tests/acceptance/workers/lifecycle/configuration.py` | passed / 0 | реальные опубликованные host, missing file/connection, other cwd, priorities/options; configuration.log, lifecycle-extra/configuration.json |
| `python tests/acceptance/workers/lifecycle/lifecycle.py` | failed / 1 | line 33 owner after API restart; последующие assertions not-run. lifecycle.log, lifecycle-worker.log/state/rows, UNC-10 |
| `python tests/acceptance/workers/lifecycle/images.py` | passed / 0 | shared-file shell и missing-file host branches трёх images; images.log, lifecycle-extra/images.json; диагностическое повторение Worker: FileNotFoundException |
| `python tests/acceptance/workers/lifecycle/compose-failure.py` | passed / 0 | мигратор exit=1, API/Worker created, не running; compose-failure.log и lifecycle-extra/compose-failure.json |
| lifecycle `startup.py`, `shutdown-probes.py` | blocked-by-runner / не запускались | RUN-2 mandatory settings отсутствуют в direct docker run; startup-config-diagnostic.log доказывает преждевременный config exit. Не применять как passed gate/schema/signal evidence |
| `python tests/acceptance/measurements/postgres_repeat.py` | passed / 0 | fresh isolated PG, aggregate repeated capture/lower-bound/no identifiers; postgres-repeat.log |
| `python tests/acceptance/measurements/resource_smoke.py` | passed / 0 | denied role, SQL timeout, queue paused/empty/age, PG/Docker/Windows samples; resource-smoke.log |
| `python tests/acceptance/measurements/crash_smoke.py` | passed / 0 | SIGKILL после двух flushed окон, prefix/no stop marker/no canary; smokes.log; measurement-artifacts/c7cd*/smoke-result.json |
| `python tests/acceptance/measurements/control_smoke.py` | passed / 0 | stop-owned до processing epoch, artifacts preserved после owned cleanup; control-smoke.log |
| `python tests/acceptance/measurements/stall_smoke.py` | failed / 1 | run дошёл до manifest completion_stalled; analyze упал unmarked_clock_jump **до финальных четырёх asserts**; stall-smoke.log, stall-invalid-records.json, measurement-artifacts/a3d4*/manifest/final/journal/runtime files |
| `python tests/acceptance/measurements/two_worker_crash.py` | passed / 0 | 40 deliveries, два process instance, SIGKILL, survivor completion, telemetry partial/reconciliation=0; two-worker.log, measurement-artifacts/e27*/summary.json |

### Профили исторических прогонов

Файл `raw/compose.env` содержит только синтетические defaults: пустой DOCKER_REGISTRY; тестовые POSTGRES_DB, POSTGRES_USER и POSTGRES_PASSWORD; TEST_SIMULATE_ERRORS=false; пустой TEST_OPERATOR_URL; timeout=5. Он **не** содержит PACKCALL_ENVIRONMENT/TEST_SENDER_MODE: это позволяет исходному operator runner проверить missing/empty без скрытого fallback. Корневой .env отсутствовал и не создавался.

```powershell
$audit=(Resolve-Path '_bmad-output/specs/spec-test-necessity-audit/audit-results').Path
$env:COMPOSE_ENV_FILES="$audit/raw/compose.env"
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='inprocess'
# operator run, config_matrix, precedence, prod_compose исполнялись с этим профилем.
```

ENV-1: создан только новый `packcall-necessity-audit-pg`, published `127.0.0.1:55439:5432`, image postgres:18.6-alpine3.24, DB packcall_audit, user postgres, синтетический локальный password. PACKCALL_TEST_POSTGRES установлен только в дочерней PowerShell с Host/Port/DB выше; не использовалась dev DB. Каждый C# PG case создал случайную schema и удалил её через PostgresTestBase. Запуск container: `docker run -d --name packcall-necessity-audit-pg -e POSTGRES_PASSWORD -e POSTGRES_DB=packcall_audit -p 127.0.0.1:55439:5432 postgres:18.6-alpine3.24`. Пароль задаётся заново при повторе, секреты рабочих источников не публикуются.

W22: `PACKCALL_ENVIRONMENT=test`, первый `TEST_SENDER_MODE=inprocess`, `W22_OUTPUT=$audit/raw/w22-inprocess`; второй http, `TEST_OPERATOR_URL=http://operator:8000`, `W22_OUTPUT=$audit/raw/w22-http`. Каждый run создаёт свой project и W22_CONTROL. Verify запускается только после passed run с тем же W22_OUTPUT; смотрит saved DB running state и восстанавливает его. Projects: `packcall-w22-b31a8b52`, `packcall-w22-faa92dc8`. Runtime sources/images и exact command/options в commands.jsonl; observed locks/survivor/clock snapshots сохранены отдельно от outcome.

Late/shutdown: исходные scripts жёстко читают `.../story-11-1-test-email-operator/w22-http/latest-run.txt`. Такого указателя не было; создан **только новый ignored pointer** копированием audit raw/w22-http/latest-run.txt на текущий successful HTTP run. Старые pointers/runs не перезаписывались. late-control был свежим. После failed late finally остановил своих workers/fixture/db/operator; shutdown затем использовал ту же выделенную DB, очистил synthetic tables и остановил DB в finally. Source runners не менялись.

Privacy: `python -m venv _bmad-output/implementation-artifacts/story-11-1-test-email-operator/venv`; его Scripts/python.exe `-m pip install -r tests/acceptance/operators/requirements.txt`; затем исходный privacy.py. Два Uvicorn процесса завершены его finally.

Lifecycle: дополнительный новый `packcall-necessity-config-pg` с портом `127.0.0.1:55432`, DB postgres и пароль через POSTGRES_PASSWORD — для configuration.py. Для runtime lifecycle выполнены `docker compose -p packcall-w21 -f docker-compose.yml -f tests/acceptance/workers/lifecycle/compose.acceptance.yml up --build -d`, stop packcall.worker, INSERT ровно 1000 recipients как в README, затем lifecycle.py. Создана свежая volume (до setup её не было). После failure собраны worker/API logs, .State/RestartCount и SQL distribution. `compose-failure.py` использовал отдельный `packcall-w21-failure`. Startup с изменением history и direct shutdown probes не запускались из-за RUN-2.

Measurement: штатные scripts создают UUID-owned projects/containers и не публикуют DB порт. Запущены только small fixtures/sessions, без throughput/overhead acceptance. Artifact paths/stdout сохранены, копии свежих артефактов индексированы в artifact-index.json с SHA-256. Resource/repeat/crash fixtures сами удалили только свои контейнеры. Control smoke выполнил explicit owned cleanup; остальные sessions сохраняют volumes/artifacts.

### Ограничения исторического аудита

2 ноября прошли все 190 C# вариантов и отдельные Python suites. 87 source methods, 135 InlineData записей и 190 развёрнутых случаев согласованы с TRX; data matrices/внутренние assertions не смешиваются с discovered count. Все C# CHK имеют passed source-method run records; каждая Python oracle имеет отдельную строку и loop context. Для failed scripts ранние частично исполненные блоки не объявлены все passed: статус conservatively not-run, фактическая ошибка записана отдельно.

Связность отчётов проверена отдельно: raw/report-validation.txt — уникальность CHK IDs, отсутствие orphan checks между file inventory и guarantee-map, 190 C# source cases со статусом passed, наличие семи обязательных документов. Это проверка целостности отчёта, не тест продукта и не доказательство эквивалентности кандидатов.

Негативные inputs, reject-terminal SQL constraint, cancellation blockers, checksum corruption и failure proxies уже существуют в тестах; они выполнялись как часть исходных suites. Новых mutations и изменений кода не было. Поэтому разделять baseline success, существующий negative control и **ещё не выполненный** proof удаления обязательно.

Параллельно исполнялись только независимые выделенные topology/suites. Для будущего сравнения перед/после применять последовательные сопоставимые runs: текущие elapsed durations не являются измерением скорости сокращения, а шум среды мог влиять на timing. Lifecycle owner и runtime clock причины не скрыты. В report нет общего «все acceptance passed».

Cleanup evidence: raw/cleanup.log. Остановлены только созданные этим аудитом оставшиеся W21 и audit DB containers; W22/operator/performance runners остановили свои projects. Volumes и новые результаты сохранены; существующий `frpc-lm-studio` и старые stopped resources не затрагивались. Повтор не должен использовать те же DB/session/control как pristine baseline.
