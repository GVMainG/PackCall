# PackCall beta.1

Дата: 30.10.2026. Локальный релиз поверх HEAD `97ee06133db8093b80324c30b05737e1eefdca5e`; включает 12 коммитов после `mvp` и принятую реализацию 11.1 с драйверами 2.2. Полный список файлов нового коммита — [release-files-beta.1.txt](release-files-beta.1.txt).

## Изменения от mvp

- Пауза и возобновление кампаний с сохранением StartedAt; освобождение неотправленного хвоста.
- Защита сохранения результата по владельцу, исходному сроку закрепления и времени PostgreSQL после блокировки строки; устаревший результат не создаёт terminal-журнал.
- EF Core InitialCreate и отдельный Migrator вместо EnsureCreated; отдельный Worker, read-only schema gate API/Worker, общий загрузчик конфигурации, отмена и бюджет закрепления по времени БД.
- Два тестовых отправителя: stateless inprocess и отдельный HTTP-оператор FastAPI с lock зависимостей. Обязательная конфигурация, ранний запрет prod, независимые случайные отказы 1% при включённом флаге; без хранения истории в операторе.
- Удалены метрики доставки. Добавлены воспроизводимые контрактные, PostgreSQL и процессные проверки конкуренции, recovery, stale result, паузы, shutdown и сдвига локальных часов.

## Настройки и запуск

Требуются Docker с Compose; для preflight — Python 3. Команды PowerShell выполняются из корня репозитория. Используйте отдельное имя Compose project. Базовый файл не публикует порты; в примерах явно добавлен существующий development overlay (API :8080, PostgreSQL :5432). При занятых портах настройте свой overlay.

Обязательно задать `PACKCALL_ENVIRONMENT=test` и `TEST_SENDER_MODE=inprocess|http` для API, Worker и Migrator. Отсутствующие, пустые и неизвестные значения запрещены. `PACKCALL_ENVIRONMENT=prod` завершается ошибкой до обращения к БД. `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT` эти настройки не заменяют.

`TEST_SIMULATE_ERRORS` по умолчанию false; true включает независимый случайный отказ 1% на каждом корректном вызове. В прямом запуске host пустой bool запрещён, Compose заменяет unset/empty на false. Для HTTP обязателен `TEST_OPERATOR_URL`: абсолютный http(s) URL корня без credentials/query/fragment. `TEST_OPERATOR_TIMEOUT_SECONDS` по умолчанию 5, допустимо 0 < x <= 30; отправка также ограничена cancellation/бюджетом закрепления.

Inprocess:

```powershell
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='inprocess'
python tests/acceptance/story-11-1-test-email-operator/preflight.py -p packcall-beta1 -f docker-compose.yml -f docker-compose.override.yml
if ($LASTEXITCODE -ne 0) { throw 'Preflight failed' }
docker compose -p packcall-beta1 -f docker-compose.yml -f docker-compose.override.yml up -d --build
```

HTTP (выберите этот блок вместо inprocess):

```powershell
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='http'
$env:TEST_OPERATOR_URL='http://packcall.test-email-operator:8000'
python tests/acceptance/story-11-1-test-email-operator/preflight.py -p packcall-beta1-http -f docker-compose.yml -f docker-compose.override.yml -f docker-compose.test-http.yml
if ($LASTEXITCODE -ne 0) { throw 'Preflight failed' }
docker compose -p packcall-beta1-http -f docker-compose.yml -f docker-compose.override.yml -f docker-compose.test-http.yml up -d --build
```

Не запускайте оба примера одновременно с одинаковыми опубликованными портами. API доступен на http://localhost:8080/swagger в Development. HTTP-оператор доступен только внутри Docker network. Compose запускает Migrator после готовности PostgreSQL, API/Worker — после успешного Migrator. Для прямого запуска host обязательна также строка `ConnectionStrings__Postgres`; сначала выполните Migrator, затем API и Worker. Общий JSON загружается из каталога приложения, затем host/environment JSON, env и CLI.

Compose использует `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` (локальные defaults: packcall); задайте их для своего стенда. При смене режима остановите Worker, пересоздайте Migrator с `--force-recreate`, дождитесь exit0, затем пересоздайте API/Worker с согласованными mode/overlay. Старый exited0 Migrator не подтверждает новую конфигурацию. Не удаляйте volume для смены режима.

## Приёмка и воспроизведение

Все 51 существующий файл [source-manifest.json](implementation-artifacts/story-11-1-test-email-operator/source-manifest.json) сверены по SHA-256 с рабочим деревом; удаление MockEmailOptions.cs сверено отдельно с diff/отчётом. Сохранённый Worker.Program.before.txt совпадает с HEAD; прежняя логика сохранена, добавлены только настройки оператора и безопасная обработка ошибки конфигурации. WorkerLeaseBudgetTests.cs не имеет содержательного diff: нормализованный Git blob совпадает с HEAD.

Фактически прочитаны итоговый TRX (148/148, 0 failed/notExecuted), результаты обоих W22, gates (test/build/publish/Docker exit0) и root-verification.json. Код и конфигурация после принятой реализации не менялись; полная приёмка при подготовке релиза повторно не запускалась. Проверены состав индекса и соответствие Git blobs исходникам с учётом нормализации переводов строк. `git diff --cached --check` выявил только четыре косметических замечания в принятых драйверах: пустые строки в конце late_http.py, W22 Dockerfile/compose.yml и пробелы в run.py:66. Они сохранены для точного соответствия манифесту.

Для воспроизведения нужны .NET SDK 10, Docker Compose и Python 3. Основной сценарий: `python tests/acceptance/story-11-1-test-email-operator/run.py`. Процессные проверки обоих режимов и Release gates: [README драйвера](../tests/acceptance/story-2-2-workers/README.md). Python contract: отдельный venv, `pip install -r tests/acceptance/story-11-1-test-email-operator/requirements.txt`, затем `operator_contract.py`; `test_harness_fixes.py` проверяет исправления драйверов. Остальные config/privacy/retention/shutdown/late_http драйверы находятся рядом с run.py, используют результаты успешных изолированных прогонов. Артефакты и venv создаются локально, в коммит не входят.

Основания в локальном рабочем каталоге: planning-artifacts/beta-1-scope.md, implementation-artifacts/story-11-1-test-email-operator/report.md, root-verification.json; main run-20261030-133349, W22 inprocess run-20261030-133556-ffb5d83f, HTTP run-20261030-133922-96a4cfb7. Отчёты и результаты прогонов оставлены локальными.

## Ограничения

Нет SMTP/реальных каналов: Sent подтверждает только приём оператором. Exactly-once и дедупликации внешнего эффекта нет; потеря ответа допускает повторный внешний вызов после recovery. Статус и журнал сохраняются неатомарно. Паритет email проверен boundary fixtures, без исчерпывающего доказательства Unicode. Миллион отправок за час не подтверждён; нагрузочная приёмка 10.x не выполнена. Обновление схемы с существующими данными 1.3, контакты/каналы/группы, новые статусы, retries и прочие отложенные задачи не включены. Исторические docs/user и docs/ai содержат устаревшие сведения о размещении Worker и EnsureCreated; в этом релизе они не изменялись.
