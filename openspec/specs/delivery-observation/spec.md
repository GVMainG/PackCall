# delivery-observation Specification

## Purpose

Чтение состояния кампаний и доставок через HTTP API для наблюдения за обработкой. Управление кампаниями описано в [campaign-lifecycle](../campaign-lifecycle/spec.md). Подписки, поток событий, поиск, пользовательские фильтры и HTTP-чтение журнала не реализованы.

Базовая спецификация по коду на 2026-10-31. SHALL описывает реализацию; ссылки на тесты не означают успешного запуска.

## Входы, результаты и ошибки

| Запрос | Результат `200` |
|---|---|
| `GET /api/campaigns` | Страница `CampaignResponse` |
| `GET /api/campaigns/{id:guid}` | Объект `campaign` и статистика `deliveries` |
| `GET /api/campaigns/{id:guid}/deliveries` | Страница `DeliveryResponse` |
| `GET /api/deliveries/{id:guid}` | `DeliveryResponse` |

Пагинация: `pageNumber=1`, `pageSize=20` по умолчанию. Страница содержит `items`, `pageNumber`, `pageSize`, `totalCount`, `totalPages`. Кампания содержит `id`, `name`, `messageText`, `status`, `createdAt`, `updatedAt`, `startedAt`, `completedAt`. Доставка содержит `id`, `campaignId`, `recipientId`, `recipientEmail`, `status`, `createdAt`, `updatedAt`, `completedAt`, `workerId`, `lockExpiresAt`. Статистика содержит `total`, `waiting`, `inProgress`, `sent`, `failed`.

Корректный UUID отсутствующего ресурса даёт `404`. Неположительные параметры страниц или переполнение смещения дают `400`. Маршруты используют ограничение `:guid`; контракт отсутствующего ресурса не следует переносить на произвольную строку идентификатора.

## Requirements

### Requirement: Состояние доставки

API SHALL возвращать сохранённое состояние доставки, включая снимок email и поля закрепления. `Sent` SHALL интерпретироваться как подтверждение приёма оператором, без гарантии доставки письма в ящик.

#### Scenario: Чтение завершённой доставки

- **GIVEN** в БД сохранена доставка `Sent`
- **WHEN** запрашивается её UUID
- **THEN** возвращается `Sent` с датами, снимком email и сохранёнными полями доставки.

#### Scenario: Неизвестный ресурс

- **WHEN** запрашивается корректный UUID отсутствующей кампании или доставки, включая страницу доставок отсутствующей кампании
- **THEN** API возвращает `404`.

### Requirement: Страницы и количество элементов

Страницы SHALL сортироваться по UUID сущности. `totalPages` SHALL вычисляться как округление `totalCount / pageSize` вверх; для пустой выборки результат равен нулю. Верхний предел размера страницы не задан.

#### Scenario: Страница за пределами списка

- **GIVEN** список содержит меньше элементов, чем смещение запрошенной страницы
- **WHEN** переданы положительные параметры без переполнения
- **THEN** `items` пуст, а счётчики отражают общее количество элементов списка.

#### Scenario: Неположительный размер

- **WHEN** передан `pageSize=0`
- **THEN** запрос отклоняется с `400`.

### Requirement: Статистика кампании

API SHALL группировать доставки кампании по статусам и возвращать нули для отсутствующих групп. `total` SHALL включать все доставки кампании.

#### Scenario: Только ожидающие доставки

- **GIVEN** у кампании одна доставка `Waiting`
- **WHEN** клиент читает кампанию
- **THEN** статистика содержит `total=1`, `waiting=1`, остальные счётчики равны нулю.

### Requirement: Сериализация ответа

HTTP-приложение SHALL использовать строковую сериализацию enum и стандартные JSON-имена полей ASP.NET Core. Отображение дат SHALL формировать UTC `DateTimeOffset`; nullable-даты сохраняют `null`.

#### Scenario: Отсутствующая дата завершения

- **GIVEN** доставка ещё не завершена
- **WHEN** формируется её DTO
- **THEN** `completedAt` остаётся `null`.

## Реализация и существующие тесты

- [CampaignsController](../../../src/PackCall.Api/Controllers/CampaignsController.cs), [DeliveriesController](../../../src/PackCall.Api/Controllers/DeliveriesController.cs), [DTO и преобразование](../../../src/PackCall.Api/Models/ResponseMapping.cs), [настройка JSON](../../../src/PackCall.Api/Program.cs).
- [DeliveryRepository](../../../src/PackCall.Infrastructure/Repositories/DeliveryRepository.cs): `Get`, `GetPage`, `GetStatusCountsAsync`; [CampaignRepository](../../../src/PackCall.Infrastructure/Repositories/CampaignRepository.cs): `GetPage`.
- [ApiControllerTests](../../../PackCall.Core.Test/ApiControllerTests.cs): `CampaignEndpoints_CreateReadAndPatchPreserveOtherField`, `ReadEndpoints_ReportMissingResourcesAndPageTotals`. Это прямые вызовы контроллеров; вторая проверка пагинации использует список получателей, а не все виды страниц.
- [CampaignPausePostgresTests](../../../PackCall.Core.Test/CampaignPausePostgresTests.cs): `HttpPauseAndResume_ReturnStatesAndRejectInvalidOperations` — существующая HTTP-проверка части поведения.

## Спорное поведение и вероятные дефекты

- Кампания и её статистика, а также количество и элементы страницы читаются отдельными запросами без общего снимка. Атомарный согласованный ответ при параллельной обработке не гарантирован.
- Даты в DTO принудительно трактуются как UTC; это не преобразование произвольного локального времени.
- Пользовательская авторизация/аутентификация в конфигурации HTTP-приложения не найдена; из доступности endpoint нельзя выводить разграничение доступа.

## Области, которые не удалось проверить

Успешный прогон .NET-тестов не получен: запуск прерван. Полные HTTP-контракты сериализации, model binding и всех ошибок не подтверждены прямыми тестами контроллеров. PostgreSQL-тесты, Docker-приёмка и поведение больших страниц не проверены запуском.
