# Финальная локальная регрессия beta.3

19 ноября 2026, ветка `gv`, исходники `6209783f4b5d68babe342b4eeae9eec6eeef91bc`.
Offline Python и SM-6 запущены на чистом дереве. .NET и итоговый Compose smoke
выполнены из отдельной распакованной копии `git archive` этого SHA.
Хост Windows, Python 3.11.15, Docker Engine 29.8.2 (Linux), Compose 5.6.0.
Для временных БД использовался PostgreSQL 18.6.

## Результаты

| Проверка | Команда из корня | Результат и локальные артефакты |
| --- | --- | --- |
| .NET Release | `dotnet build PackCall.slnx -c Release --nologo` | PASS, 0 предупреждений и ошибок. `artifacts/story-2-19/dotnet-linux-verified/build.log`. |
| .NET Unit + Integration | `dotnet test tests/PackCall.Test/PackCall.Test.csproj -c Release --no-build --nologo --logger "trx;LogFileName=final.trx" --results-directory /results` | PASS, 314 из 314, 0 ошибок, 0 пропусков, 41 с. По расположению классов 168 Unit и 146 Integration. В том же каталоге `final.trx`, `test.log`, `migrate.log`, `source-sha.txt`. |
| SM-2 | В составе .NET Integration, фильтр для отдельного запуска в [тестовом README](../../../tests/README.md). | PASS, 3 из 3 по TRX. Проверены отправка по выборке и два случая запуска без оператора. |
| Python performance | `python -m pytest tests/tools/performance -q --junitxml=artifacts/story-2-19/python/performance.xml` | PASS, 73 теста и 69 subtests, JUnit 142, 0 пропусков. pytest 9.2.0, jsonschema 4.26.0. |
| Python operators | `python -m unittest discover -s tests/tools/operators -p "test_*.py" -v` | PASS, 4 из 4, 0 пропусков. `artifacts/story-2-19/python/operators.log`. |
| Python CI tooling | `python -m unittest discover -s tests/tools/ci -p "test_*.py" -v` | PASS, 47 из 47, 0 пропусков. `artifacts/story-2-19/python/ci-offline.log`. |
| HTTP contract | `python tests/acceptance/operators/operator_contract.py` | PASS, 5 из 5, 0 пропусков. FastAPI 0.115.12, HTTPX 0.28.1. `artifacts/story-2-19/python/operator-contract-final.log`. |
| PostgreSQL repeat | `python tests/acceptance/measurements/postgres_repeat.py` | PASS. Один повторный захват, ноль повторов при одном освобождении. Пропуск записи журнала маркируется как нижняя оценка, экспорт агрегирован. `artifacts/story-2-19/python/postgres-repeat-final.log`. |
| SM-6 A | `python tests/acceptance/workers/sm6/run.py` | PASS, 5 из 5. Timeout, исключение, отмена и ошибка протокола восстановились до `Sent`. Подтверждённый отказ дал `Failed`. Все кампании `Completed`. `artifacts/acceptance/sm6-a/ead1d1a6ff3d/results.json`. |
| SM-6 B/C | `python tests/acceptance/workers/sm6/run_bc.py` | PASS, 2 из 2. B подтвердил SIGKILL, истечение аренды в PostgreSQL и перезахват. C подтвердил `StaleResult`, ноль изменённых строк, сохранённый журнал и одну терминальную запись. `artifacts/acceptance/sm6-bc/0730458cb651/results.json`. |
| Чистый Compose, email и OpenAPI | [Воспроизведение](replay-compose.py) | PASS. Независимый проект `packcall-s219-e028e158c152`. Схема сравнения, ответы API и журнал в `artifacts/story-2-19/compose-final/`. [Сверка OpenAPI](openapi-diff.md). |
| Команда обновления из README | Блок Bash из [инструкции обновления](../../../README.md#обновление-с-beta2-до-beta3), отдельные имя и порт | PASS. Проект `packcall-s219-upgrade-0842a15d44`: первый GET вернул 0 контактов. Созданный для проверки контакт сохранился после `stop` и повторного `up` с тем же именем. `artifacts/story-2-19/upgrade-readme/commands.log`. При проверке редактировались документы, исходники и Compose-файлы соответствовали проверяемому SHA. |

.NET запущен в `mcr.microsoft.com/dotnet/sdk:10.0` (SDK 10.0.404) с отдельными
Docker-сетью и PostgreSQL `postgres:18.6-alpine3.24`. Сборка использовала локальный
кеш NuGet в режиме только чтения и `NuGetAudit=false`. Перед тестами выполнен
`dotnet run --project src/PackCall.Migrator -c Release --no-build` с настройками
`PACKCALL_ENVIRONMENT=test` и `TEST_SENDER_MODE=inprocess`. Строка
`PACKCALL_TEST_POSTGRES` указывала на эту БД с `Pooling=false`.
TRX проверен через `python scripts/ci/check_trx.py <TRX> --forbid-skipped`.
Для воспроизведения из корня репозитория выполните
`pwsh -NoProfile -File docs/evidence/beta3/replay-dotnet.ps1` и
`python docs/evidence/beta3/replay-compose.py`. Python нужен с поддержкой
`tarfile.data_filter`. Оба скрипта берут исходники указанного выше SHA через
`git archive`, поднимают отдельные тестовые ресурсы и записывают сырые
артефакты каждого повтора в новый каталог внутри `artifacts/story-2-19/`.
Скрипт .NET использует предварительно заполненный локальный
`%USERPROFILE%/.nuget/packages`. [Скрипт .NET](replay-dotnet.ps1).
[Скрипт Compose](replay-compose.py).

Оба сохранённых сценария успешно повторены после ревью. Compose
`packcall-s219-5e5c5e5fa653` подтвердил завершение кампании, а .NET
`packcall-s219-linux-4257ad642b` повторно дал 314 из 314 и 0 пропусков.
Сырые результаты находятся в одноимённых каталогах `compose-replay` и
`dotnet-linux-replay` внутри `artifacts/story-2-19/`.

Windows testhost получил `Access Denied`. Разведочные Linux-прогоны выявили
необходимость предварительной миграции и ограничение соединений. В итоговом
прогоне Migrator отработал до тестов, PostgreSQL был выделенным и pooling выключен.
Два первых запуска PostgreSQL repeat остановились с `fixture_command_failed`
на первом `psql`. Диагностический запуск и итоговый штатный повтор завершились
успешно.

Финальный прогон сосредоточен на обязательных SM-6, чистом Compose и проверках
Python из таблицы. Отдельные наборы lifecycle, coordination, полная Docker-матрица
operators и измерительные fault smoke оставлены в объёме предыдущей расширенной
приёмки схемы. Их PASS относится к SHA
`f109f099d09a0df68bab9f2942b28ce27e0e34a4` в указанном там объёме.
[Историческая матрица Python-приёмки](python-schema-acceptance.md).

## Чистый Compose

Использованы `docker-compose.yml`, `docker-compose.env` (test/inprocess,
без имитации отказов) и Development overlay с динамическим loopback-портом
из [быстрого старта](../../../README.md#быстрый-старт-compose). В цикле готовности
повторялись HTTP-запросы до ответа. Первый запуск скрипта завершился на
раннем разрыве TCP. Во втором запуске сравнение полных объектов контакта
оказалось строже проверки публичного контракта. Итоговый прогон сверил ID
и email через `Location`.

В новом проекте Migrator завершился с кодом 0. История EF содержит
`InitialCreate`, `ContactsChannels` и `DeliveryCaptureIndex`. API и Worker
работали, начальный счётчик контактов равнялся нулю.

1. `POST /api/contacts` создал один email-контакт. Запрос по `Location`
   подтвердил тот же идентификатор и адрес.
2. `POST /api/campaigns` с `selection=contactIds` и `channels=["email"]`
   вернул `201` и `Draft`. `POST /api/campaigns/{id}/start` вернул `200`
   и `InProgress`. Чтение затем показало `Completed`, 1 `Sent`, 0 `Failed`.
3. В `delivery_status_journal` записаны `Waiting → InProgress → Sent` и
   `AcceptedByProvider` в единственной терминальной записи.
4. `GET /openapi/v1.json` вернул `200`. После проверки удалены контейнеры,
   сеть, том и три образа отдельного проекта. Сохранены `results.json`,
   `compose-ps.jsonl`, `compose-down.log` и `openapi.runtime.json`.

## NFR-6 (SM-5)

При ревью проверены потребители абстракций контактов, снимков и операторов.
`IContactRepository` используют контроллер контактов и сервис кампаний.
Снимки формирует `CampaignService` для текущего API, а Worker захватывает
и отправляет по сохранённым каналу и адресу. `IChannelOperatorAvailability`
проверяет запуск кампании, а `IMessageSender` вызывается при обработке доставки.
API и Worker используют общие регистрации keyed DI. Реестр хранит один активный
email-оператор, `MockEmailSender` или `HttpTestEmailSender`. Строковые каналы
`email` и `telegram` заданы моделью, оператор Telegram пока отсутствует.
В проверенных путях композиции и зависимостях нет plugin discovery, SPI,
CQRS/ES или дополнительного фреймворка маршрутизации. NFR-6 по этим изменениям
подтверждён. [Регистрация](../../../src/PackCall.Infrastructure/ServiceCollectionExtensions.cs).
[Каналы](../../../src/PackCall.Core/Entities/ContactChannel.cs).
[Обработка доставки](../../../src/PackCall.Core/Services/DeliveryProcessingService.cs).

## Допуск и тег

| Гейт | Состояние |
| --- | --- |
| Локальная сборка, полный .NET, offline Python, выбранная приёмка, SM-6 A/B/C, чистый Compose | PASS по результатам выше. |
| OpenAPI и NFR-6 | Выполнены. Сохранены различия и известное расхождение формата статусов. |
| UTF-8, ссылки и whitespace | PASS. `python scripts/ci/check_utf8_sources.py` проверил 129 исходников C#, с `--paths` проверены 8 изменённых документов. Проверены относительные ссылки и якоря в документах. `git diff --check` и `git diff --no-index --check` для новых файлов без замечаний. |
| Производительность индекса захвата | Результат `inconclusive`, разброс 7,04% при допуске 5%. Владелец одобрил выпуск без повторного замера и подтверждённого PASS. [Исходный прогон](capture-index-migration.md). |
| GitHub `CI required` | Владелец одобрил выпуск без GitHub CI. Ручной запуск и проверка через `release_gate.py` пропущены по его указанию. |
| Аннотированный тег `v0.5.0-beta.3` | Владелец одобрил публикацию ветки `gv` и тега с исключением для двух гейтов выше. Тег ставится на коммит с решением о выпуске. [Стандартный порядок](../../releases/README.md#runbook-тега-dod-roadmap-9). |
