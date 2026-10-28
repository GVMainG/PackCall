# campaign-lifecycle Specification

## Purpose

Редактирование, запуск, пауза, возобновление, завершение и удаление существующей кампании. Создание доставок описано в [campaign-formation](../campaign-formation/spec.md), выполнение отправок — в [delivery-processing](../delivery-processing/spec.md). Планирование по времени, отмена кампании и повторный запуск завершённой кампании не реализованы.

Базовая спецификация по коду на 2026-10-31. SHALL фиксирует реализацию, а не планы. Существующие тесты не объявляются пройденными.

## Входы, результаты и ошибки

| Операция | Вход | Результат |
|---|---|---|
| `PATCH /api/campaigns/{id:guid}` | `name` и/или `messageText` | `200` с кампанией; пропущенное или `null` поле сохраняет прежнее значение |
| `POST /api/campaigns/{id:guid}/start`, `/pause`, `/resume` | UUID | `200` с текущей кампанией после команды |
| `DELETE /api/campaigns/{id:guid}` | UUID | `204` либо `404` |

Отсутствующая кампания даёт `404`; недопустимый переход и запрет удаления — `409`; пустые поля при редактировании/запуске и PATCH без обоих полей — `400`. Название и сообщение при редактировании должны быть непустыми и не состоять из пробелов.

## Requirements

### Requirement: Редактирование черновика

Система SHALL изменять имя и сообщение только у `Draft`; успешное изменение обновляет `UpdatedAt`.

#### Scenario: Частичный PATCH

- **GIVEN** существует черновик с текстом сообщения
- **WHEN** PATCH передаёт только новое непустое имя
- **THEN** имя меняется, текст сохраняется.

### Requirement: Допустимые команды статуса

Система SHALL поддерживать `Draft → InProgress`, `InProgress → Paused`, `Paused → InProgress`. Повторный `start` для `InProgress`, `pause` для `Paused` и `resume` для `InProgress` SHALL не менять даты самим повторным переходом. Первый запуск задаёт `StartedAt`; возобновление сохраняет его. Запуск требует непустых имени и сообщения.

#### Scenario: Повторный запуск

- **GIVEN** кампания уже `InProgress`
- **WHEN** повторяется `start`
- **THEN** первый `StartedAt`, даты и доставки не меняются.

#### Scenario: Недопустимая команда

- **GIVEN** кампания находится в `Draft` или `Completed`
- **WHEN** выполняется `pause` или `resume`
- **THEN** операция отклоняется с `409`.

### Requirement: Условное сохранение статуса

Сохранение команд статуса SHALL обновлять строку только при совпадении ожидаемого предыдущего статуса. При проигранном обновлении сервис SHALL перечитать кампанию и принять уже достигнутый целевой статус либо вернуть конфликт.

#### Scenario: Конкурентное изменение

- **GIVEN** статус в БД изменился после чтения
- **WHEN** выполняется условная запись с прежним ожидаемым статусом
- **THEN** она не перезаписывает несовпадающий статус.

### Requirement: Завершение по терминальным доставкам

Проверка завершения SHALL переводить только `InProgress` в `Completed`, когда нет `Waiting` и `InProgress` доставок, задавая `CompletedAt` и `UpdatedAt`. Проверка вызывается после сохранённого и записанного в журнал результата отправки, а также при `resume` без незавершённых доставок.

#### Scenario: Последняя доставка завершилась отказом

- **GIVEN** остальные доставки уже терминальные и кампания `InProgress`
- **WHEN** последняя доставка сохраняется как `Failed` и дальнейшие операции успешны
- **THEN** кампания становится `Completed`, а не `Failed`.

#### Scenario: Пауза во время последней отправки

- **GIVEN** отправка начата до паузы
- **WHEN** оператор отвечает успешно во время `Paused`
- **THEN** доставка может стать `Sent`, кампания остаётся `Paused`; последующий `resume` завершает кампанию без новой отправки этой доставки.

### Requirement: Удаление

Сервис SHALL запрещать удаление прочитанной кампании в `InProgress` и `Paused`. Удаление допустимой кампании SHALL каскадно удалять доставки, но не получателей.

#### Scenario: Удаление запущенной кампании

- **WHEN** клиент удаляет кампанию, прочитанную как `InProgress`
- **THEN** API возвращает `409`.

## Реализация и существующие тесты

- [Campaign](../../../src/PackCall.Core/Entities/Campaign.cs), [переходы статусов](../../../src/PackCall.Core/Entities/CampaignStatus.cs), [CampaignService](../../../src/PackCall.Core/Services/CampaignService.cs), [CampaignRepository](../../../src/PackCall.Infrastructure/Repositories/CampaignRepository.cs).
- [CampaignTests](../../../PackCall.Core.Test/CampaignTests.cs): `PauseAndResume_PreserveFirstStartAndMakeRepeatedRequestsHarmless`, `PauseAndResume_RejectDraftAndCompletedCampaigns`, `UpdateDetails_WhenCampaignIsInProgress_Throws`.
- [CampaignWorkflowTests](../../../PackCall.Core.Test/CampaignWorkflowTests.cs): оба workflow-сценария с fake-хранилищем.
- [CampaignPausePostgresTests](../../../PackCall.Core.Test/CampaignPausePostgresTests.cs): `StatusUpdates_CannotOverwriteConcurrentPauseOrCompletion`, `PauseOnLastDelivery_ResumeCompletesWithoutSendingAgain`, `HttpPauseAndResume_ReturnStatesAndRejectInvalidOperations`.

## Спорное поведение и вероятные дефекты

- Вероятный дефект: `start` кампании без доставок не вызывает проверку завершения; воркер не имеет работы, которая вызовет её позже. Она может остаться `InProgress`.
- Проверки статуса для редактирования и удаления отделены от безусловной записи/удаления. Защита команд статуса не распространяется на эти операции; возможны гонки с запуском.
- Статус кампании `Failed` объявлен, но переход в него не найден. `Completed` означает окончание обработки, а не успех всех отправок.
- Отдельного периодического завершения кампаний нет; сбой между финализацией доставки и проверкой кампании может оставить `InProgress`.

## Области, которые не удалось проверить

.NET-набор прерван без итогового результата. PostgreSQL-тесты не подтверждены запуском: `PACKCALL_TEST_POSTGRES` не настроен. Перечисленные вероятные дефекты выведены из кода, воспроизводящие тесты для них не добавлялись и не запускались.
