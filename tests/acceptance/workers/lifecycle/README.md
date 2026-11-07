# Приёмка Worker 2.1

Запускать из корня PackCall в Windows/PowerShell. Требуются Python 3.11+ (только стандартная библиотека), .NET SDK с поддержкой net10.0, Docker Desktop с Linux containers и Docker Compose v2. Нужны доступ к NuGet/registry и свободный localhost:55432. Сценарии выполняются последовательно.

Используются только отдельные проекты/контейнеры `packcall-w21`, `packcall-w21-failure`, `packcall-w21-testdb`, `w21-*`. Не подключайте рабочие данные: lifecycle очищает campaigns/deliveries/journal тестового стенда; startup пересоздаёт только тестовые БД w21empty/w21await и временно меняет migration history в тестовой БД. Стандартные Compose credentials должны оставаться тестовыми значениями packcall.

Перед командами создайте отдельный локальный `.env` с тестовыми defaults (не используйте рабочий `.env`):

```dotenv
DOCKER_REGISTRY=
POSTGRES_DB=packcall
POSTGRES_USER=packcall
POSTGRES_PASSWORD=packcall
PACKCALL_ENVIRONMENT=test
TEST_SENDER_MODE=inprocess
TEST_SIMULATE_ERRORS=false
TEST_OPERATOR_URL=
TEST_OPERATOR_TIMEOUT_SECONDS=5
```

Переменные процесса имеют приоритет над `.env`; очистите конфликтующие настройки или явно задайте те же test-значения. База/пользователь/пароль должны оставаться `packcall`: сценарии используют эти credentials напрямую.

```powershell
docker run -d --name packcall-w21-testdb -e POSTGRES_PASSWORD=w21test -p 127.0.0.1:55432:5432 postgres:18.6-alpine3.24
docker compose -p packcall-w21 -f docker-compose.yml -f tests/acceptance/workers/lifecycle/compose.acceptance.yml up --build -d
docker compose -p packcall-w21 -f docker-compose.yml stop packcall.worker
# Для lifecycle/shutdown нужно ровно 1000 тестовых recipients в выделенной БД:
docker exec packcall-w21-packcall.db-1 psql -U packcall -d packcall -c "INSERT INTO recipients(id,email,created_at,updated_at) SELECT gen_random_uuid(), 'w21-'||i||'@example.com',clock_timestamp(),clock_timestamp() FROM generate_series(1,1000) i;"
python tests/acceptance/workers/lifecycle/configuration.py
python tests/acceptance/workers/lifecycle/lifecycle.py
python tests/acceptance/workers/lifecycle/startup.py
dotnet publish tests/PackCall.Worker.Probe/PackCall.Worker.Probe.csproj -c Release -o $env:TEMP/packcall-w21-probe
python tests/acceptance/workers/lifecycle/shutdown-probes.py
python tests/acceptance/workers/lifecycle/images.py
python tests/acceptance/workers/lifecycle/compose-failure.py
```

Перед начальным наполнением recipients остановите Worker; до запуска lifecycle оставьте API/БД доступными. Повторно не добавляйте recipients: lifecycle ожидает ровно 1000. Дождитесь готовности PostgreSQL перед configuration.py. Startup временно меняет history; не запускайте одновременно другие сценарии. Lifecycle ждёт recovery до115с, startup включает полный timeout60с, shutdown — budget30с.

JSON-свидетельства сохраняются в `_bmad-output/implementation-artifacts/story-2-1-worker`, отчёт и TRX остаются там же. Скрипты не удаляют volumes. После проверки остановите тестовые сервисы/контейнеры; сохраните volumes, если они нужны для повторной проверки. Не используйте рабочий Compose project name.

Исходное ограничение: `images.py` использует прямой `docker run` и не передаёт весь обязательный
production environment (`PACKCALL_ENVIRONMENT`/`TEST_SENDER_MODE`). `startup.py` и
`shutdown-probes.py` передают обязательный env через общий `HOST_ENV` из `support.py`
(RUN-2, исправлено в beta.2). `.env` применяется к Compose, но не переносится автоматически в
direct `docker run`.
