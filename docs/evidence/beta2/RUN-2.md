# RUN-2 — ремонт приёмки (`startup.py`, `shutdown-probes.py`)

**Результат:** PASS (оба сценария).
**Commit SHA:** `c12e0f0f7abf56e1a4b70ad6b15b77682d022010` (финальный; предыдущие фиксы `b69d107`, `e881f54`)

## Дефект

`startup.py`/`shutdown-probes.py` запускали контейнеры прямым `docker run` без обязательных
переменных хоста (`PACKCALL_ENVIRONMENT`, `TEST_SENDER_MODE`). Образы и probe
(`tests/PackCall.Worker.Probe`) вызывают `TestOperatorSettings.Parse`, который без валидного
режима падает на старте, поэтому сценарии падали неинформативно.

## Исправление

Общий набор `HOST_ENV` в `support.py` (`PACKCALL_ENVIRONMENT=test`,
`TEST_SENDER_MODE=inprocess`, `TEST_SIMULATE_ERRORS=false`), передаётся в каждом прямом
`docker run`; в `startup.py` обязательные флаги ставятся **после** `*extra`, чтобы caller не
переопределил тест-режим (строку подключения по-прежнему можно переопределить для негативных
сценариев). `TEST_OPERATOR_URL`/`TEST_OPERATOR_TIMEOUT_SECONDS` нужны только для Compose-интерполяции
и в direct `docker run` не обязательны (inprocess).

## Проверка

- `python tests/acceptance/workers/lifecycle/startup.py` → exit 0; `startup.json` фиксирует все
  gate-сценарии (gateNoDdl, SIGTERM-гейты, недоступная БД, неизвестная миграция, повторный
  migrator, emptyTimeout ~60s).
- `python tests/acceptance/workers/lifecycle/shutdown-probes.py` → exit 0; оба режима
  (`before` ~0.3s, `ignore` ~30.3s) с `leaseRetained=true`, `noTerminalJournal=true`.
- Probe опубликован: `dotnet publish tests/PackCall.Worker.Probe -c Release -o $env:TEMP/packcall-w21-probe`.

## Команда воспроизведения

```bash
# .env (для Compose-интерполяции): DOCKER_REGISTRY=, PACKCALL_ENVIRONMENT=test,
# TEST_SENDER_MODE=inprocess, POSTGRES_DB/USER/PASSWORD=packcall, TEST_SIMULATE_ERRORS=false,
# TEST_OPERATOR_URL=, TEST_OPERATOR_TIMEOUT_SECONDS=5
docker compose -p packcall-w21 -f docker-compose.yml up -d
docker exec packcall-w21-packcall.db-1 psql -U packcall -d packcall -Atc \
  "INSERT INTO recipients(id,email) VALUES(gen_random_uuid(),'probe@example.test') ON CONFLICT DO NOTHING"
dotnet publish tests/PackCall.Worker.Probe/PackCall.Worker.Probe.csproj -c Release -o $env:TEMP/packcall-w21-probe
python tests/acceptance/workers/lifecycle/startup.py
python tests/acceptance/workers/lifecycle/shutdown-probes.py
```

Среда: Windows, Docker Desktop (Linux containers), Python 3.13.
