# PackCall

Система массовой рассылки кампаний. Мейнтейнер создаёт кампанию с сообщением, Worker захватывает
доставки пакетами и отправляет их через оператор канала, фиксируя результат и журнал; гарантии
жизненного цикла (захват, закрепление, пауза, recovery, завершение) описаны в
[`docs/guarantees.md`](docs/guarantees.md), доказательства — в [`docs/EVIDENCE.md`](docs/EVIDENCE.md).

На `beta.2` (текущий HEAD) система работает с **одним** каналом email через mock-оператор; модель
`contacts`/`channels` и Telegram — в следующих вехах (`beta.3`/`beta.4`, см.
[`docs/adr/`](docs/adr/)). Ни одна фича не заявляется, если её нет в коде.

## Требования к стенду

- .NET SDK 10 (target `net10.0`)
- Docker Engine (Linux containers) + Compose v2
- Python 3.11+ (для приёмки и offline-инструментов)
- PostgreSQL 18 (для интеграционных тестов)

## Быстрый старт (Compose)

```bash
# 1. тестовые значения окружения (не используйте рабочий .env)
cat > .env <<'EOF'
DOCKER_REGISTRY=
PACKCALL_ENVIRONMENT=test
TEST_SENDER_MODE=inprocess
TEST_SIMULATE_ERRORS=false
TEST_OPERATOR_URL=
TEST_OPERATOR_TIMEOUT_SECONDS=5
POSTGRES_DB=packcall
POSTGRES_USER=packcall
POSTGRES_PASSWORD=packcall
EOF

# 2. поднять и собрать (migrator применяет EF-миграции и завершается; api/worker ждут его)
docker compose up -d --build

# 3. проверка работоспособности
docker compose ps
docker compose logs packcall.api
curl -s "http://localhost:8080/api/recipients?pageNumber=1&pageSize=1"
```

Сервисы Compose: `packcall.api`, `packcall.worker`, `packcall.migrator`, `packcall.db`
(имена заданы явно, без коротких алиасов). API публикуется на `8080` из
`docker-compose.override.yml` (применяется Compose v2 автоматически); БД — `5432`, поэтому
локальный PostgreSQL 18 на том же порту конфликтует. Проверка `curl` выполняется с хоста (в
образе `aspnet:10.0` curl не гарантирован). Более полный перечень проверок —
[`tests/README.md`](tests/README.md); зависимости Python (`jsonschema`, `pytest`) —
[`scripts/performance/requirements.txt`](scripts/performance/requirements.txt).

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
