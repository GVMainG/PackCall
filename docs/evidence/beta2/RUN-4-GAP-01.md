# RUN-4 и GAP-01 — ремонт приёмки

**Результат:** PASS (config_matrix); GAP-01 — статически исправлен.
**Commit SHA:** `6af6d8900d143f5f096f04620370fe0e541b987a`

## RUN-4 — пустой `TEST_SIMULATE_ERRORS`

- Дефект: `docker-compose.yml` объявлял `TEST_SIMULATE_ERRORS: ${…:?…}` (обязателен), поэтому
  «пустое значение = имитация выключена» было невыразимо, и `config_matrix.py` падал на `docker
  compose config`.
- Контракт конфигурации: `TestOperatorSettings.Parse` отдаёт `SimulateErrors=false` для
  **null (не задано)**; пустую строку `""` он отклоняет исключением. Нормализацию «пустое →
  выключено» даёт compose-интерполяция `:-false`, а не `Parse` (AD-5 не регламентирует этот
  композ-слой).
- Исправление: `docker-compose.yml` → `TEST_SIMULATE_ERRORS: ${TEST_SIMULATE_ERRORS:-false}`.
- Проверка: `python tests/acceptance/operators/config_matrix.py` → `Configuration matrix PASS`
  (exit 0), c локальным `.env` (DOCKER_REGISTRY=, POSTGRES_*, TEST_SIMULATE_ERRORS=false,
  TEST_OPERATOR_URL=, TEST_OPERATOR_TIMEOUT_SECONDS=5).

## GAP-01 — `lifecycle.py:38`

- Дефект: `assert … where delivery_status=3` — статуса `3` нет в `DeliveryStatus`
  (Waiting=0, InProgress=1, Sent=2, Failed=-1), проверка была мертвой и не ловила `Failed`.
- Исправление: `delivery_status=3` → `delivery_status=-1` (двуязычный контракт AD-5).
- Проверка: enum-контракт подтверждён C#-тестами (Docker Linux, 194 passed); полный прогон
  `lifecycle.py` требует W21-проекта и выполняется отдельно.

## Команды воспроизведения

```bash
# .env (тестовые значения)
DOCKER_REGISTRY=
POSTGRES_DB=packcall
POSTGRES_USER=packcall
POSTGRES_PASSWORD=packcall
TEST_SIMULATE_ERRORS=false
TEST_OPERATOR_URL=
TEST_OPERATOR_TIMEOUT_SECONDS=5

python tests/acceptance/operators/config_matrix.py   # RUN-4 PASS
```

Среда: Windows, Docker Desktop (Linux containers), Python 3.13.
