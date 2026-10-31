# database-lifecycle Specification

## Purpose

Хранение сущностей PackCall в PostgreSQL, применение EF Core migrations отдельным Migrator и read-only барьер запуска API/Worker. Согласование записей доставки описано в [worker-coordination](../worker-coordination/spec.md). Резервное копирование, восстановление, автоматический rollback и проверка фактической структуры по каталогу БД не реализованы этим механизмом.

Базовая спецификация по модели, миграции и startup-коду на 2026-10-31. SHALL фиксирует реализованный контракт. Проверка текста миграции не означает её успешного выполнения.

## Входы, результаты и ошибки

Все три .NET-процесса используют `ConnectionStrings:Postgres`. В исходниках присутствует одна миграция `20261028193126_InitialCreate`. Migrator вызывает `Database.MigrateAsync`; при успешном завершении возвращает `0`, при перехваченной ошибке или отмене — `1`. API/Worker схему не мигрируют.

| Таблица | Основные ограничения |
|---|---|
| `campaigns` | UUID PK; обязательные `name`/`message_text` в миграции; name до 255; числовой status; nullable даты |
| `recipients` | UUID PK; обязательный email до 255; уникальный `ix_recipients_email`; nullable даты |
| `deliveries` | UUID PK; обязательные FK кампании и получателя; уникальный `(campaign_id, recipient_id)`; email-снимок до 255; обязательные status/created/updated; nullable completed/worker/expiry |
| `delivery_status_journal` | Генерируемый bigint PK; UUID кампании и доставки без FK; статусы и время; nullable worker до 255 и результат до 1000 |

Даты сопоставлены с `timestamp with time zone`. PK сущностей кампании, получателя и доставки формируются приложением. Кроме уникальности и FK, доменные переходы статусов обеспечиваются кодом; CHECK-ограничений допустимых переходов в миграции не найдено. Числовые статусы кампании: `Draft=0`, `InProgress=1`, `Completed=2`, `Forming=3`, `Paused=4`, `Failed=-1`; доставки: `Waiting=0`, `InProgress=1`, `Sent=2`, `Failed=-1`.

## Requirements

### Requirement: Отдельное применение миграций

Migrator SHALL разобрать общую конфигурацию и настройки тестового оператора до обращения к БД, запустить host, создать DbContext и вызвать `MigrateAsync` с токеном остановки. API и Worker SHALL использовать проверку схемы без вызова миграций.

#### Scenario: Схема ещё не создана

- **GIVEN** БД доступна и миграции применимы
- **WHEN** Migrator успешно выполняет `MigrateAsync`
- **THEN** применяются недостающие известные миграции и процесс завершается с `0`.

#### Scenario: Ошибка миграции

- **WHEN** применение миграций выбрасывает ошибку
- **THEN** Migrator завершается с `1`, сообщая тип исключения без подробностей подключения.

### Requirement: Точное совпадение истории перед запуском

`SchemaGate` SHALL получать известные и применённые миграции и разрешать запуск при равенстве отсортированных списков. Отсутствие известных миграций и наличие неизвестной применённой миграции SHALL завершать барьер ошибкой. Недостающая миграция SHALL приводить к ожиданию.

#### Scenario: Мигратор завершился во время ожидания

- **GIVEN** история БД ещё неполна
- **WHEN** она становится в точности равной известной истории
- **THEN** барьер завершается успешно и допускает запуск приложения.

#### Scenario: Неизвестная миграция

- **GIVEN** в истории БД есть идентификатор, отсутствующий в сборке
- **WHEN** выполняется проверка
- **THEN** возникает `InvalidOperationException`, приложение не запускает работу через этот барьер.

### Requirement: Ограниченное и отменяемое ожидание

Барьер SHALL связывать startup-токен с `ApplicationStopping`, задавать deadline 60 секунд, повторять проверку после задержки 2 секунды. `NpgsqlException` и `TimeoutException` SHALL приводить к ожиданию доступности БД; остальные ошибки не маскируются как временная недоступность. Hosted services SHALL запускаться последовательно с lifecycle-проверкой перед рабочими сервисами.

#### Scenario: Остановка во время ожидания

- **WHEN** `ApplicationStopping` отменяется до готовности схемы
- **THEN** ожидание прекращается с отменой, штатная работа приложения не начинается.

### Requirement: Уникальность и каскады хранения

БД SHALL запрещать повтор пары кампании и получателя в доставках и повтор одинаковой сохранённой строки email получателя. Удаление кампании или получателя SHALL каскадно удалять связанные доставки. Журнал SHALL оставаться независимым от этих каскадов.

#### Scenario: Повторная доставка получателю в одной кампании

- **WHEN** в БД вставляется вторая доставка с той же парой `campaign_id`, `recipient_id`
- **THEN** уникальный индекс запрещает вставку.

#### Scenario: Удаление допустимой кампании

- **WHEN** кампания удалена из таблицы
- **THEN** связанные доставки удаляются, получатели и независимые записи журнала не удаляются этим каскадом.

## Реализация и существующие тесты

- [Migrator](../../../src/PackCall.Migrator/Program.cs), [SchemaGate](../../../src/PackCall.Infrastructure/Hosting/SchemaGate.cs), [регистрация барьера](../../../src/PackCall.Infrastructure/Hosting/PackCallHosting.cs), [регистрация хранилища](../../../src/PackCall.Infrastructure/ServiceCollectionExtensions.cs).
- [Модель](../../../src/PackCall.Infrastructure/AppDbContext.cs), [InitialCreate](../../../src/PackCall.Infrastructure/Migrations/20261028193126_InitialCreate.cs), [snapshot](../../../src/PackCall.Infrastructure/Migrations/AppDbContextModelSnapshot.cs).
- [WorkerPostgresTests](../../../PackCall.Core.Test/WorkerPostgresTests.cs): `SchemaGate_WaitsForExactHistory_RejectsUnknown_ObservesCancellation`. Тест создаёт историю миграций в изолированной схеме; он не доказывает правильность физической структуры по этой истории.
- [CampaignPausePostgresTests](../../../PackCall.Core.Test/CampaignPausePostgresTests.cs): fixture создаёт изолированную схему и таблицы через `IRelationalDatabaseCreator.CreateTablesAsync`, что не эквивалентно прогону Migrator.
- [Compose](../../../docker-compose.yml): зависимость от завершения Migrator и healthcheck PostgreSQL; [acceptance runner](../../../tests/acceptance/story-11-1-test-email-operator/run.py) и [W22 runner](../../../tests/acceptance/story-2-2-workers/run.py) содержат проверки реальных процессов.

## Спорное поведение и вероятные дефекты

- `SchemaGate` доверяет таблице истории. Совпавшая история не выявляет ручное удаление столбца/таблицы, изменение индекса или структуры.
- Уникальность email в БД относится к строке, а нормализация регистра — к API-сервису. Запись в обход сервиса не получает автоматически его валидацию.
- Nullable-даты и числовые статусы не обеспечивают корректность доменного состояния сами по себе.
- Настроенный deadline проверки не является доказанной верхней границей всей процедуры старта и не включает длительность миграций.

## Области, которые не удалось проверить

Текущий PostgreSQL не использовался для проверки: `PACKCALL_TEST_POSTGRES` не настроен. Итоговый результат .NET-набора не получен, запуск прерван. Реальное применение InitialCreate, соответствие живой схемы snapshot, уникальные ограничения/каскады и запуск Compose в этой работе не подтверждены выполнением.
