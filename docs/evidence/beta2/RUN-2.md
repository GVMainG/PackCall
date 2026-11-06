# RUN-2 — ремонт приёмки (`startup.py`, `shutdown-probes.py`)

**Результат:** `startup.py` — PASS; `shutdown-probes.py` — фикс применён, прогон требует полного
W21-проекта (worker + probe publish), отложен.
**Commit SHA:** `b69d107dfc61c2702d5e747cc278e7f1d32795cc`

## Дефект

`startup.py`/`shutdown-probes.py` запускали контейнеры прямым `docker run` без обязательных
переменных хоста. Образы и probe (`tests/PackCall.Worker.Probe`) вызывают
`TestOperatorSettings.Parse`, который требует `PACKCALL_ENVIRONMENT` и `TEST_SENDER_MODE`, поэтому
контейнер падал на старте с конфигурационным исключением, а не с понятным сообщением.

## Исправление

В каждый прямой `docker run` добавлены `-e PACKCALL_ENVIRONMENT=test`, `-e
TEST_SENDER_MODE=inprocess`, `-e TEST_SIMULATE_ERRORS=false` (в `startup.py` — через `start()`;
в `shutdown-probes.py` — для probe-контейнера). Строка подключения уже передавалась; теперь
передаётся и режим тестового оператора, поэтому сценарии не зависят от окружения compose.

## Проверка

- `python tests/acceptance/workers/lifecycle/startup.py` → exit 0; `startup.json` фиксирует все
  gate-сценарии: `gateNoDdl=0`, `waitThenReady`, SIGTERM-гейты (Api/Worker), недоступная БД
  (exit 1), неизвестная миграция (exit 1), повторный migrator (exit 0), `migratorSigtermExit=1`,
  `emptyTimeout` (exit 1, ~60s).
- Среда: локальный Compose-проект `packcall-w21` (только `packcall.db`) + собранные образы
  `packcallapi`/`packcallworker`/`packcallmigrator`.

## Команда воспроизведения

```bash
# .env (для compose-интерполяции)
DOCKER_REGISTRY=
PACKCALL_ENVIRONMENT=test
TEST_SENDER_MODE=inprocess
POSTGRES_DB=packcall
POSTGRES_USER=packcall
POSTGRES_PASSWORD=packcall
TEST_SIMULATE_ERRORS=false
TEST_OPERATOR_URL=
TEST_OPERATOR_TIMEOUT_SECONDS=5

docker compose -p packcall-w21 -f docker-compose.yml up -d packcall.db
python tests/acceptance/workers/lifecycle/startup.py
```

Среда: Windows, Docker Desktop (Linux containers), Python 3.13.
