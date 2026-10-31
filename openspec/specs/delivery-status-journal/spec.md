# delivery-status-journal Specification

## Purpose

Сохранение истории переходов статуса доставки в PostgreSQL. Это диагностический журнал доставки; журналирования всех действий над кампанией и получателями, HTTP-чтения, event sourcing и транзакционного outbox нет.

Базовая спецификация по коду на 2026-10-31. SHALL фиксирует текущие вызовы записи, без гарантии атомарности с изменением доставки. Наличие теста не означает его прохождения.

## Входы, результаты и ошибки

`IDeliveryStatusJournal.AppendAsync(StatusChangeEntry, CancellationToken)` сохраняет запись с генерируемым числовым `id`, `campaignId`, `deliveryId`, `previousStatus`, `newStatus`, nullable `workerId`, `occurredAt`, nullable `operatorResult`. Длина `workerId` ограничена в БД 255 символами, `operatorResult` — 1000. `null` entry отклоняется; ошибки БД и отмена пробрасываются вызывающему сервису.

## Requirements

### Requirement: Журнал захвата и освобождения

После захвата сервис SHALL записывать для каждой возвращённой доставки переход `Waiting → InProgress`, владельца и время захвата с пустым `operatorResult`. После возврата просроченного закрепления и успешного освобождения из-за паузы SHALL записываться `InProgress → Waiting` без результата оператора.

#### Scenario: Успешный захват

- **WHEN** захват возвращает доставку и запись журнала успешна
- **THEN** сохраняется переход `Waiting → InProgress` с UUID кампании/доставки и владельцем.

#### Scenario: Освобождение при паузе не применилось

- **GIVEN** исходное закрепление уже заменено
- **WHEN** `ReleaseCapturedAsync` не возвращает строку
- **THEN** сервис не добавляет для этого вызова запись освобождения.

### Requirement: Журнал принятого терминального результата

Сервис SHALL записывать `InProgress → Sent` или `InProgress → Failed` только после успешного `TryCompleteAsync`. В записи SHALL использоваться исходный владелец; `operatorResult` равен `SendResult.Error`, если он не `null`, иначе `AcceptedByProvider`.

#### Scenario: Подтверждённый приём

- **WHEN** успешный результат сохранён в доставку и запись журнала успешна
- **THEN** журнал содержит `Sent` и `AcceptedByProvider`.

#### Scenario: Устаревший результат

- **WHEN** условное сохранение возвращает `false`
- **THEN** терминальная запись от этого вызова отсутствует.

#### Scenario: Ошибка оператора

- **WHEN** sender выбрасывает исключение до подтверждённого результата
- **THEN** сервис не добавляет терминальную запись.

### Requirement: Независимое хранение записей

Реализация журнала SHALL создавать отдельный DbContext и выполнять отдельный `SaveChangesAsync` для каждой записи. Таблица журнала SHALL хранить идентификаторы без FK к кампании и доставке; каскадное удаление кампании не удаляет журнал по этим идентификаторам.

#### Scenario: Удаление кампании с историей

- **GIVEN** журнал уже содержит записи кампании
- **WHEN** кампания допустимо удаляется
- **THEN** каскад доставки не распространяется на строки журнала.

## Реализация и существующие тесты

- [StatusChangeEntry](../../../src/PackCall.Core/Journaling/StatusChangeEntry.cs), [DeliveryProcessingService](../../../src/PackCall.Core/Services/DeliveryProcessingService.cs).
- [DeliveryStatusJournal](../../../src/PackCall.Infrastructure/Journaling/DeliveryStatusJournal.cs), [запись](../../../src/PackCall.Infrastructure/Journaling/DeliveryStatusJournalRecord.cs), [модель хранения](../../../src/PackCall.Infrastructure/AppDbContext.cs), [миграция](../../../src/PackCall.Infrastructure/Migrations/20261028193126_InitialCreate.cs).
- [DeliveryResultLeasePostgresTests](../../../PackCall.Core.Test/DeliveryResultLeasePostgresTests.cs): `Result_ActiveLease_CompletesWithJournal`, `Result_StaleLease_DoesNotChangeRowOrJournal`.
- [OperatorPostgresTests](../../../PackCall.Core.Test/OperatorPostgresTests.cs): `OperatorErrorsPreserveLeaseWithoutTerminalJournal`, `ConfirmedOperatorResultsAreTerminal`.
- [DeliveryProcessingServiceTests](../../../PackCall.Core.Test/DeliveryProcessingServiceTests.cs): `Process_UnappliedStorage_DoesNotJournalOrCheckCompletion`.

## Спорное поведение и вероятные дефекты

- Между изменением доставки и журналом нет общей транзакции. Сбой, отмена или аварийная остановка в этом промежутке оставляют переход без записи. Механизма сверки или восстановления пропущенных записей не найдено.
- Вероятный дефект атрибуции восстановления: SQL возврата просроченных строк очищает `worker_id` до `RETURNING`, а сервис использует возвращённый `WorkerId` для журнала. Поэтому прежний владелец теряется, в запись попадает `null`.
- Инициализация `Waiting` при формировании не журналируется как переход. Кампании имеют собственные статусы, но их переходы в эту таблицу не записываются.
- Автоматической очистки/retention журнала нет. Сохранение после удаления кампании подтверждено моделью и миграцией, отдельный тест удаления истории не найден.

## Области, которые не удалось проверить

Итогового успешного .NET-прогона нет: запуск прерван. PostgreSQL-тесты не подтверждены запуском, переменная `PACKCALL_TEST_POSTGRES` не настроена. Сбои между SQL и журналом, сохранение истории при удалении и потеря прежнего владельца при восстановлении отдельно не воспроизводились.
