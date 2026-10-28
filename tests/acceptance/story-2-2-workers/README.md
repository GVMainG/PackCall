# Story 2.2: независимые процессы и общая очередь

Из корня репозитория:

```powershell
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='inprocess'
$env:W22_OUTPUT='_bmad-output/implementation-artifacts/story-11-1-test-email-operator/w22-inprocess'
python tests/acceptance/story-2-2-workers/run.py
python tests/acceptance/story-2-2-workers/verify.py
```

Для HTTP вместо первого блока:

```powershell
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='http'
$env:TEST_OPERATOR_URL='http://operator:8000'
$env:W22_OUTPUT='_bmad-output/implementation-artifacts/story-11-1-test-email-operator/w22-http'
python tests/acceptance/story-2-2-workers/run.py
python tests/acceptance/story-2-2-workers/verify.py
```

HTTP driver сам собирает образ operator. verify использует тот же W22_OUTPUT, временно запускает только сохранённую БД и возвращает её исходное состояние в finally.

Требуются Docker Compose, Python 3 и .NET SDK. `run.py` каждый раз создаёт случайный Compose project `packcall-w22-*`, собственный PostgreSQL volume и динамические порты только на 127.0.0.1. Dev Compose не используется. Команды, исходы, SQL-запросы/ответы, снимки всех сохранённых полей и журнала находятся в отдельном `run-*` под `_bmad-output/implementation-artifacts/story-2-2-workers`; последняя попытка записана в `latest-run.txt`. Неудачные попытки сохраняются.

Процесс `PackCall.Workers.Acceptance` регистрирует production AddStorage/AddDeliveryProcessing, SchemaGate и DeliveryWorker; заменён только IMessageSender. Sender записывает атомарный снимок и ожидает файла release, соблюдая cancellation. Никакого собственного алгоритма захвата, recovery или результата у driver нет. Отдельный одноразовый процесс передаёт старый снимок production ProcessAsync с действующим CancellationToken, чтобы отмена штатного Worker не скрывала проверку SQL fence.

W22-01 использует table lock PostgreSQL как общий барьер: два capture SQL реально ждут снятия блокировки; pg_stat_activity сохраняется до COMMIT. W22-02/04 используют SIGKILL и не редактируют срок lease. Ожидания ограничены и проверяют состояние, а не предполагаемую задержку. W22-03 сравнивает полностью deliveries, campaigns и journal до/после stale ProcessAsync. Перед проверкой уже terminal доставки fixture вновь открывает кампанию, поскольку production service иначе отвергает вызов до SQL; доставка и её журнал не редактируются.

W22-05 собирает LD_PRELOAD shim CLOCK_REALTIME: реальные DateTime.UtcNow в процессах отличаются на ±3600 секунд, CLOCK_MONOTONIC остаётся системным. Изменения часов компьютера и PostgreSQL нет. Вход sender подтверждает положительный предварительный бюджет, удержание sender до реального истечения проверяет cancellation/recovery, старый снимок проверяет fence, release нового вызова — сохранение результата. Это не проверка пользовательского TimeProvider.GetUtcNow.

`verify.py` запускает весь PackCall.Core.Test с PACKCALL_TEST_POSTGRES из последнего проекта, проверяет TRX на отсутствие skipped/notExecuted, собирает и публикует API/Worker/Migrator в Release, собирает production Worker Dockerfile. API/Migrator Dockerfiles собраны самим run.py.

После изучения артефактов остановить **только** созданный проект (имя взять из results.json):

```powershell
docker compose -p <project-from-results> -f tests/acceptance/story-2-2-workers/compose.yml down
```

Требуется прежнее значение W22_CONTROL (путь `<run>/control`) при разборе Compose. Одноразовые probe-контейнеры, созданные compose run с явным именем, можно удалить отдельно по точным именам из commands.jsonl. Volume и свидетельства автоматически не удаляются.

Приёмка не обещает исключения повторного внешнего эффекта после потери ответа. Журнал остаётся неатомарным с записью доставки. Нагрузочная приёмка, retries и каналы не входят в этот driver.
