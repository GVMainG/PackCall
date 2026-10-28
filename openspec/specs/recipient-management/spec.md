# recipient-management Specification

## Purpose

Управление общей аудиторией будущих кампаний: создание получателя и чтение получателей через API. Изменение, удаление, импорт, подписки и проверка существования почтового ящика не реализованы этим контрактом.

Базовая спецификация по коду на 2026-10-31. SHALL фиксирует существующее поведение. Ссылки на тесты означают наличие проверок, а не подтверждённый успешный запуск.

## Входы, результаты и ошибки

| Операция | Вход | Результат |
|---|---|---|
| `POST /api/recipients` | JSON `email` | `201`, `Location` на чтение ресурса, `RecipientResponse` |
| `GET /api/recipients/{id:guid}` | UUID | `200` с получателем либо `404` |
| `GET /api/recipients` | `pageNumber=1`, `pageSize=20` по умолчанию | `200`, `items`, `pageNumber`, `pageSize`, `totalCount`, `totalPages` |

Получатель содержит `id`, нормализованный `email`, `createdAt`, `updatedAt`. Ошибка email и недопустимая пагинация отображаются в `400`; дубликат — в `409`. Ошибки сохранения, кроме распознанного нарушения уникальности email, не превращаются в ошибку дубликата.

## Requirements

### Requirement: Нормализация и проверка email

Сервис SHALL применять `Trim().ToLowerInvariant()`, отклонять пустой результат, длину свыше 255 единиц UTF-16 и адрес, для которого `MailAddress.TryCreate` неуспешен либо разобранный `Address` не равен нормализованному входу.

#### Scenario: Создание нормализованного получателя

- **GIVEN** адрес ` User@Example.com ` отсутствует в аудитории
- **WHEN** клиент создаёт получателя с этим email
- **THEN** сохраняется `user@example.com`, новый UUID и даты создания/изменения в UTC; API возвращает `201`.

#### Scenario: Некорректный email

- **WHEN** передан пустой, синтаксически некорректный или слишком длинный email
- **THEN** сервис отклоняет запрос; API возвращает `400`.

### Requirement: Уникальность получателя

Сервис SHALL проверять существование email без учёта регистра. Репозиторий SHALL преобразовывать нарушение индекса `ix_recipients_email` в `RecipientAlreadyExistsException`.

#### Scenario: Повтор в другом регистре

- **GIVEN** существует `user@example.com`
- **WHEN** создаётся `USER@EXAMPLE.COM`
- **THEN** новый получатель не создаётся, API возвращает `409`.

### Requirement: Чтение аудитории

Система SHALL сортировать страницы по UUID получателя, требовать положительные номер и размер страницы и проверять переполнение смещения. Новый получатель SHALL использоваться при последующем формировании кампаний; готовые доставки автоматически не дополняются.

#### Scenario: Последняя неполная страница

- **GIVEN** существуют три получателя
- **WHEN** запрошены `pageNumber=2`, `pageSize=2`
- **THEN** возвращается один элемент, `totalCount=3`, `totalPages=2`.

#### Scenario: Отсутствующий получатель

- **WHEN** запрашивается корректный UUID отсутствующего получателя
- **THEN** API возвращает `404`.

## Реализация и существующие тесты

- [CampaignService](../../../src/PackCall.Core/Services/CampaignService.cs): `CreateRecipientAsync`, `GetRecipientsAsync`, `GetRecipientAsync`.
- [RecipientRepository](../../../src/PackCall.Infrastructure/Repositories/RecipientRepository.cs), [контроллер](../../../src/PackCall.Api/Controllers/RecipientsController.cs), [обработка ошибок](../../../src/PackCall.Api/Middleware/ApiExceptionHandler.cs).
- [CampaignServiceTests](../../../PackCall.Core.Test/CampaignServiceTests.cs): `CreateRecipient_RejectsInvalidEmail`, `CreateRecipient_RejectsDuplicateIgnoringCase`.
- [ApiControllerTests](../../../PackCall.Core.Test/ApiControllerTests.cs): `CreateRecipient_ReturnsCreatedResourceAndIncludesItInNewCampaign`, `ReadEndpoints_ReportMissingResourcesAndPageTotals` — прямые вызовы контроллеров с fake-репозиториями.

## Спорное поведение и вероятные дефекты

- Верхнего ограничения `pageSize` нет. Счётчик и содержимое страницы читаются разными запросами без общего снимка.
- Индекс БД уникален по сохранённой строке. Равенство без регистра для произвольных записей в обход сервиса не обеспечивается самим индексом.

## Области, которые не удалось проверить

Успешное прохождение тестового набора не подтверждено: запущенный .NET-набор был прерван без итогового результата. Конкурентное создание получателей на реальном PostgreSQL не проверено; `PACKCALL_TEST_POSTGRES` не настроен. Проверки маршрутизации и model binding не следуют из прямых вызовов контроллеров.
