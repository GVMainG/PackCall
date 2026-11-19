# PackCall

Система массовой рассылки кампаний. Мейнтейнер создаёт кампанию с сообщением, Worker захватывает
доставки пакетами и отправляет их через оператор канала, фиксируя результат и журнал; гарантии
жизненного цикла (захват, закрепление, пауза, recovery, завершение) описаны в
[`docs/guarantees.md`](docs/guarantees.md), доказательства — в [`docs/EVIDENCE.md`](docs/EVIDENCE.md).

API контактов позволяет создавать, читать и заменять адреса email и Telegram.
Список каналов доступен в `GET /api/channels`. Для отправки по `email` реестр использует
один тестовый оператор из `TEST_SENDER_MODE` (`inprocess` или `http`).
Worker захватывает доставки активных кампаний с зарегистрированным оператором и непустым
снимком адреса. При отправке он выбирает оператора по сохранённому каналу и передаёт ему
снимок адреса. Если для любого канала кампании отсутствует оператор, `start` возвращает `409`.
Домен контактов принимает непустой набор с одним адресом на канал. Email хранится без
крайних пробелов и в нижнем регистре, Telegram chat ID — как ненулевое целое Int64 в
десятичной форме. Адрес уникален в пределах канала. Замена набора применяется целиком
и сохраняет снимки сформированных доставок.

Отправка через Telegram запланирована на `beta.4`.
См. [контракт API контактов](docs/api/README.md#api-контактов).
См. [контракт каналов кампании](docs/api/README.md#каналы-кампании).
См. [решения по модели контактов и снимкам](docs/adr/0001-contact-model-selection-snapshot.md).

Внутренняя операция `ICampaignService.CreateCampaignSnapshotAsync` сохраняет кампанию в `Forming`,
выбранные контакты и каналы одной транзакцией. Выборка `all` включает контакты без адреса в
выбранных каналах. В явной выборке повторы ID сворачиваются, отсутствующий ID откатывает запись.
`ICampaignService.FormDeliveriesFromSnapshotAsync` сохраняет ожидающие доставки для выбранных пар
«контакт × канал», у которых есть адрес на момент формирования, и возвращает число доставок.
Вызов рассчитан на однократное формирование. Повторная вставка существующей пары отклоняется целиком.
Перегрузка `ICampaignService.CreateCampaignAsync` с выборкой выполняет обе операции
синхронно и переводит кампанию в `Draft` после записи доставок. Пока сохраняется `Forming`,
кампания доступна для чтения, запуск возвращает `409`. При ошибке или отмене до публикации
сервис пытается удалить кампанию, её снимок и доставки. Если удаление завершается ошибкой,
кампания остаётся в `Forming` и воркер не захватывает её доставки.
Детали кампании показывают итоги формирования по сохранённым данным.
`POST /api/campaigns` принимает `name`, `messageText`, явную выборку
`selection` (`all` или `contactIds`) и непустой массив `channels` (`email`,
`telegram`). В режиме `contactIds` передайте непустой массив UUID в поле
`contactIds`. Запрос возвращает `201 Created` и `Location` после перехода
в `Draft`, в том числе при нуле доставок. При отсутствии `selection` или
`channels` API возвращает `400`.

Историческую кампанию с пустым снимком каналов можно запустить.
Повторный запуск в `InProgress` сохраняет
`startedAt`. После паузы `resume` продолжает существующие доставки с прежними ID
и датой запуска. Если при запуске или возобновлении нет доставок в `Waiting`
и `InProgress`, ответ сразу содержит `Completed`, `startedAt` и `completedAt`.
Исторические доставки без адреса учитываются по своему статусу. Повторный запуск
в `Completed` возвращает `409`.

При старте и затем каждые `RecoveryInterval` Worker завершает кампании в
`InProgress`, если у них больше нет доставок в `Waiting` и `InProgress`.
Это восстанавливает завершение кампании после сбоя между записью последнего
результата и обновлением кампании. Сверка сохраняет `startedAt`, записывает
`completedAt` один раз и не затрагивает доставки, оператор и журнал.

Удаление из `InProgress` или `Paused` возвращает `409`. В остальных статусах
ответ `204` подтверждает удаление кампании, её снимка и доставок. Записи журнала
сохраняются. Запрос к отсутствующей кампании возвращает `404`.

## Требования к стенду

- .NET SDK 10 (target `net10.0`)
- Docker Engine (Linux containers) + Compose v2
- Python 3.11+ (для приёмки и offline-инструментов)
- PostgreSQL 18 (для интеграционных тестов)

## Быстрый старт (Compose)

Выполните из корня репозитория в Bash с `curl` 7.76 или новее и Python 3.11+.
Для проверки создаётся отдельный Compose-проект
с чистым томом PostgreSQL. Настройки берутся из `docker-compose.env` (режим
`test/inprocess`, имитация отказов выключена).

```bash
unset api
project=$(python -c 'import uuid; print("packcall-smoke-" + uuid.uuid4().hex[:12])') || project=

smoke_compose() (
  unset PACKCALL_ENVIRONMENT TEST_SENDER_MODE TEST_SIMULATE_ERRORS TEST_OPERATOR_URL
  unset TEST_OPERATOR_TIMEOUT_SECONDS POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD
  DOCKER_REGISTRY=packcall-quickstart/ docker compose --env-file docker-compose.env \
    -p "${project:?Имя тестового Compose-проекта не создано}" -f docker-compose.yml -f - "$@" <<'YAML'
services:
  packcall.api:
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_HTTP_PORTS: 8080
    ports:
      - "127.0.0.1::8080"
YAML
)

smoke_compose up -d --build &&
  smoke_compose ps --all &&
  address=$(smoke_compose port packcall.api 8080) &&
  curl --fail-with-body -sS --retry 30 --retry-delay 1 --retry-all-errors --max-time 3 \
    "http://${address:?Порт API не опубликован}/api/contacts?pageSize=1" &&
  api="http://$address"
```

В `ps --all` ожидается `Exited (0)` у `packcall.migrator`. Он применяет миграции
после готовности БД. API и Worker ждут его успешного завершения. После ответа
списка контактов создайте контакт и прочитайте его по возвращённому `Location`.

```bash
created=$(curl --fail-with-body -sS -i -H 'Content-Type: application/json' \
  --data '{"displayName":"Compose smoke","channels":[{"channel":"email","address":"compose-smoke@example.test"}]}' \
  "${api:?Сначала дождитесь готовности API}/api/contacts") &&
  printf '%s\n' "$created" &&
  location=$(printf '%s\n' "$created" | awk 'tolower($1) == "location:" { sub(/\r$/, "", $2); print $2 }') &&
  curl --fail-with-body -sS -i "${location:?В ответе создания отсутствует Location}"
```

Ожидаются `201 Created` и `200 OK` с одинаковыми `id` и `compose-smoke@example.test`.
При ошибке проверьте `smoke_compose logs packcall.migrator packcall.api`. После проверки
удалите стенд вместе с его временным томом командой `smoke_compose down --volumes`.

Явные `-f` и `--env-file` позволяют выполнить сценарий при существующем рабочем
`.env`. API доступен с хоста на автоматически выделенном порту `127.0.0.1`, БД
доступна сервисам проекта на `packcall.db:5432`. Обычный локальный запуск с
`docker-compose.override.yml` публикует API на `8080` и БД на `5432`.
Для проверок внешнего тестового email-оператора есть отдельные
`docker-compose.test-http.yml` и `docker-compose.test-http.env`.
Более полный перечень проверок —
[`tests/README.md`](tests/README.md); зависимости Python (`jsonschema`, `pytest`) —
[`scripts/performance/requirements.txt`](scripts/performance/requirements.txt).

## Обновление с beta.2 до beta.3

Остановите API и Worker beta.2, используя имя и файлы Compose старого проекта.
Из корня исходников beta.3 выполните в Bash. При первом запуске выберите
свободное имя проекта, например `packcall-beta3`. Убедитесь, что этого имени
нет в `docker compose ls --all` и тома `packcall-beta3_packcall-postgres-data`
нет в `docker volume ls`. Выберите свободный порт, например `18083`, и
используйте выбранное имя и порт для последующих запусков.

```bash
project=packcall-beta3
port=18083

beta3_compose() (
  unset PACKCALL_ENVIRONMENT TEST_SENDER_MODE TEST_SIMULATE_ERRORS TEST_OPERATOR_URL
  unset TEST_OPERATOR_TIMEOUT_SECONDS POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD
  DOCKER_REGISTRY="$project/" docker compose --env-file docker-compose.env \
    -p "$project" -f docker-compose.yml -f - "$@" <<YAML
services:
  packcall.api:
    environment:
      ASPNETCORE_HTTP_PORTS: 8080
    ports:
      - "127.0.0.1:$port:8080"
YAML
)

beta3_compose up -d --build &&
  beta3_compose ps --all &&
  curl --fail-with-body -sS --retry 30 --retry-delay 1 --retry-all-errors --max-time 3 \
    "http://127.0.0.1:$port/api/contacts?pageSize=1"
```

Первый запуск создаёт пустой том. Migrator применяет миграции и завершается
с кодом 0, после чего запускаются API и Worker. API доступен на
`http://127.0.0.1:18083`, PostgreSQL доступен сервисам проекта внутри Docker.
Проверка списка контактов выполняет только чтение. Для повторного запуска
выполните тот же блок, а для остановки с сохранением данных используйте
`beta3_compose stop`. Быстрый старт выше предназначен для временного стенда.
[Проверка отправки тестовой кампании](docs/evidence/beta3/release-readiness.md#чистый-compose).

Данные beta.2 в новую базу не переносятся. Старый том остаётся у владельца.
Сохраните его отдельно и используйте исходные
настройки beta.2, если потребуется снова открыть прежнюю установку. Гарантия
переноса распространяется на данные, созданные в 1.0 и позднее, при последующих
поддерживаемых обновлениях.
Политика обновления и история репетиции переноса описаны в [ADR 0002](docs/adr/0002-legacy-migration-api-evolution.md).

## Структура и ссылки

- [`docs/architecture/`](docs/architecture/) — компоненты, модель данных, потоки, решения as-is.
- [`docs/guarantees.md`](docs/guarantees.md) — матрица «гарантия → проверка → команда».
- [`docs/EVIDENCE.md`](docs/EVIDENCE.md) — правила доказательств и evidence по тегам.
- [`docs/adr/`](docs/adr/) — архитектурные решения (ADR 0001–0003).
- [`docs/api/openapi.v1.json`](docs/api/openapi.v1.json) — контракт API (baseline).

## CI

CI — GitHub Actions, workflow `.github/workflows/ci.yml`.

- Триггеры: pull request, push в `master`, теги `v*`, ручной запуск (`workflow_dispatch`).
- Обязательный check: **`CI required`** — агрегирующий job. Он зелёный только тогда, когда все
  обязательные job завершились успешно; упавший, пропущенный (skipped) или отсутствующий job —
  ошибка, а не skip.
- Job'ы и их отображаемые имена:
  - `utf8_sources` — «Strict UTF-8 sources»: строгая UTF-8 исходников C#;
  - `build_and_unit` — «Build and unit tests»: `dotnet build PackCall.slnx -c Release` и
    unit-тесты (`--filter "Category=Unit"`); отдельный шаг проверяет по TRX, что тесты реально
    исполнились, а не были отфильтрованы;
  - `integration` — «Integration tests (PostgreSQL)»: сервис `postgres:18.6-alpine3.24`,
    применение EF-миграций через `PackCall.Migrator` и интеграционные тесты
    (`--filter "Category=Integration"`) с `PACKCALL_TEST_POSTGRES`; TRX-гейт требует
    executed > 0 и **0 skipped**;
  - `python_offline` — «Python offline checks»: `pip install -r scripts/performance/requirements.txt`
    и `python -m pytest tests/tools/performance` (без внешних сервисов);
  - `docker_build` — «Docker image builds»: сборка `PackCall.Api`, `PackCall.Worker`,
    `PackCall.Migrator` и `PackCall.TestEmailOperator` (без push/логина);
  - `ci_required` — «CI required»: агрегирующий обязательный check.
- Логи и TRX: `ci-logs-<sha>-<run_id>` (сборка/unit), `integration-logs-<sha>-<run_id>`
  (миграции/интеграционные тесты), `python-offline-logs-<sha>-<run_id>` и
  `docker-build-logs-<sha>-<run_id>`; хранение 14 дней.
- Локальные предпосылки: .NET SDK 10, Python 3.11+, Docker Engine.
- Миграции применяются к схеме `public`; интеграционные тесты работают в собственных временных
  схемах (`PostgresTestBase`). Факт применения миграции проверяет `MigrationAppliedTests`.
- Включение обязательного check: GitHub Settings → Branches → защитить `master` и добавить
  required check `CI required`. Это действие владельца, из репозитория не настраивается.

### Тег релиза

Тег выпускается только для коммита с зелёным `CI required`:

```bash
export GITHUB_TOKEN=...   # не передавайте токен аргументом: он попадёт в историю shell и в ps
python3 scripts/ci/release_gate.py --commit <40-hex-sha>
```

Нужен scope `Checks: Read` (fine-grained token) или `repo` (classic PAT). Гейт падает (exit 1),
если `CI required` отсутствует, опубликован не GitHub Actions, не завершён или не успешен, и
(exit 2) при ошибке окружения: нет токена или репозитория, 401/403/5xx, сеть или некорректный JSON.
Пока гейт красный, тег не ставится. Вывод гейта (check-run id, `html_url`, время завершения)
сохраняется как evidence вехи в `docs/evidence/<tag>/`. Правила тегов и evidence —
[`docs/EVIDENCE.md`](docs/EVIDENCE.md).
