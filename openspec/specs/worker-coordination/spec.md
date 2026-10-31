# worker-coordination Specification

## Purpose

Совместная обработка общей PostgreSQL-очереди независимыми воркерами: захват, временное владение, защита записи результата и возврат просроченной работы. Саму отправку описывает [delivery-processing](../delivery-processing/spec.md). Продление закреплений и гарантия единственности внешней отправки не реализованы.

Базовая спецификация по коду на 2026-10-31. SHALL фиксирует существующие алгоритмы. Тестовые сценарии ниже проверяемы, но не объявляются успешно выполненными.

## Входы, результаты и ошибки

Захват принимает положительный `batchSize`, непустой `workerId`, положительный `lockDuration` и токен отмены; возвращает успешно захваченные доставки. Некорректные параметры отклоняются исключениями аргументов. Возврат просроченных закреплений возвращает список в репозитории и количество в сервисе. Условное сохранение терминального результата возвращает `bool`.

Воркер имеет `Parallelism` циклов обработки и отдельный цикл восстановления. Каждый цикл захватывает до `BatchSize` и обрабатывает пакет последовательно. Значения и проверка настроек описаны в [runtime-configuration](../runtime-configuration/spec.md).

## Requirements

### Requirement: Атомарный захват по времени PostgreSQL

Репозиторий SHALL выбирать только `Waiting` у кампаний `InProgress`, сортировать по UUID, ограничивать размер пакета и захватывать строки через `FOR UPDATE OF delivery SKIP LOCKED`. В одном SQL-запросе SHALL назначаться `InProgress`, владелец, `UpdatedAt` и общий срок пакета по `clock_timestamp()` после выбора заблокированного пакета.

#### Scenario: Два конкурирующих захвата

- **GIVEN** несколько воркеров захватывают одну очередь
- **WHEN** их SQL выполняются конкурентно
- **THEN** уже заблокированные строки пропускаются, один захват не получает строки другого действующего захвата.

#### Scenario: Единое время пакета

- **WHEN** одним запросом захвачено несколько доставок
- **THEN** у них общий `UpdatedAt` и `LockExpiresAt = UpdatedAt + lockDuration`.

### Requirement: Бюджет обработки закреплённой доставки

Воркер SHALL проверять владельца и наличие срока, получать текущее время PostgreSQL, вычитать прошедшее монотонное время запроса и запас `min(5 секунд, LockDuration / 10)`. При неположительном остатке отправка SHALL пропускаться. Положительный остаток SHALL ограничивать связанный токен обработки без изменения срока пакета.

#### Scenario: Хвост пакета не помещается в остаток

- **GIVEN** срок доставки почти истёк до её очереди
- **WHEN** вычисленный остаток неположителен
- **THEN** оператор не вызывается; доставка ожидает восстановления.

#### Scenario: Обработка превысила остаток

- **WHEN** истекает выделенный бюджет
- **THEN** токен обработки отменяется; штатная отмена бюджета не завершает весь цикл воркера.

### Requirement: Условное сохранение результата владельца

`TryCompleteAsync` SHALL принимать только терминальный результат, отсоединять переданный объект от EF до проверок и SQL, блокировать строку и сохранять результат только при `InProgress`, совпавших исходных владельце и сроке, а также сроке строго позже текущего времени БД после ожидания блокировки.

#### Scenario: Старый снимок при том же workerId

- **GIVEN** доставка повторно захвачена тем же `workerId`, но с новым сроком
- **WHEN** старый снимок пытается сохранить ответ
- **THEN** несовпадение срока запрещает запись.

#### Scenario: Срок истёк во время ожидания блокировки

- **GIVEN** SQL сохранения ждал блокировку до истечения срока
- **WHEN** блокировка получена
- **THEN** проверка актуального времени отклоняет результат.

### Requirement: Возврат истёкших закреплений

Восстановление SHALL возвращать `InProgress` с `lockExpiresAt <= clock_timestamp()` в `Waiting`, очищать владельца и срок, обновлять время. После ожидания блокировки SHALL использоваться актуальное состояние; терминальные строки и заменённые действующие закрепления не возвращаются.

#### Scenario: Аварийно остановленный воркер

- **GIVEN** его доставка осталась `InProgress` с истёкшим сроком
- **WHEN** восстановление успешно выполняется
- **THEN** доставка становится `Waiting` и может быть захвачена вновь у запущенной кампании.

### Requirement: Независимые циклы и обработка ошибок

Воркер SHALL выполнять восстановление отдельно от циклов отправки, ждать `IdleDelay` при пустом захвате и `ErrorDelay` после ошибки. Необработанная ошибка внутри пакета SHALL передаваться в обработчик ошибки цикла; оставшиеся закрепления не получают принудительного результата.

#### Scenario: Ошибка одной отправки

- **WHEN** обработка элемента выбрасывает ошибку
- **THEN** текущий проход пакета прерывается, цикл делает задержку; просроченная незавершённая работа может вернуться через восстановление.

## Реализация и существующие тесты

- [DeliveryRepository](../../../src/PackCall.Infrastructure/Repositories/DeliveryRepository.cs): `CaptureBatchAsync`, `TryCompleteAsync`, `ReleaseExpiredLocksAsync`; [DeliveryWorker](../../../src/PackCall.Worker/Background/DeliveryWorker.cs); [PostgresLeaseClock](../../../src/PackCall.Infrastructure/PostgresLeaseClock.cs).
- [WorkerLeaseBudgetTests](../../../PackCall.Core.Test/WorkerLeaseBudgetTests.cs): `Budget_UsesDatabaseClockAndElapsed_WithoutChangingBatchExpiry`, `Budget_CancelsActiveProcessing_ThenContinuesLaneWithoutChangingLease`.
- [WorkerPostgresTests](../../../PackCall.Core.Test/WorkerPostgresTests.cs): `Capture_UsesOneDatabaseTimestamp_RecoveryPreservesLeaseReplacedWhileWaiting`.
- [DeliveryResultLeasePostgresTests](../../../PackCall.Core.Test/DeliveryResultLeasePostgresTests.cs): `Result_ActiveLease_CompletesWithJournal`, `Result_StaleLease_DoesNotChangeRowOrJournal`, `Result_StorageErrorOrCancellation_DetachesBeforeSqlFailure`, `Result_CompletesBeforeExpiry_RecoveryWaitingOnCommitCannotRequeueTerminal`, `Result_ConcurrentSql_RespectsWinnerAndTimeAfterLock`.
- [DeliveryWorkerTests](../../../PackCall.Core.Test/DeliveryWorkerTests.cs): `Worker_ProcessesJsonConfiguredBatchesWithLimitedParallelism`.
- [Многопроцессная приёмка](../../../tests/acceptance/story-2-2-workers/README.md): W22-01..05, включая SIGKILL и рассинхронизацию часов процессов.

## Спорное поведение и вероятные дефекты

- Срок относится ко всему пакету, а не выдаётся заново каждому элементу. Большой пакет может многократно терять хвост по времени; ограничений числа попыток и dead-letter очереди нет.
- Восстановление не ограничено размером пакета и использует ожидание блокировок, а не `SKIP LOCKED`.
- `LockDuration + RecoveryInterval <= 2 минуты` — проверка конфигурации, не гарантия времени восстановления при блокировках, недоступности БД или остановке воркеров.
- SQL защищает сохранение состояния, но не исключает повторный вызов оператора после неопределённого исхода.

## Области, которые не удалось проверить

.NET-набор прерван без результата; `PACKCALL_TEST_POSTGRES` не настроен. PostgreSQL-сценарии, W22-приёмка, аварийное завершение процессов и нагрузочные характеристики в этой работе запуском не подтверждены.
