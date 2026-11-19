# Контракт API

Baseline контракта API — [`docs/api/openapi.v1.json`](openapi.v1.json). Он отражает эндпоинты
и схемы релиза `beta.2`. Изменение API диффится против
baseline, намеренные breaking changes записываются в release notes. До 0.5 API не объявляется
стабильным.

Wire-спеллинг (AD-7) использует PascalCase для статусов и нижний регистр для каналов.

## API контактов

- `POST /api/contacts` принимает `{"displayName":"Анна","channels":[{"channel":"telegram","address":"-100123"}]}`.
  `displayName` необязателен, длина до 255 символов. Ответ `201` содержит контакт и заголовок `Location`.
- `GET /api/contacts/{id}` возвращает `id`, `displayName`, `createdAt`, `updatedAt` и `channels`.
  У исторических контактов даты могут быть `null`.
- `GET /api/contacts` возвращает `items`, `pageNumber`, `pageSize`, `totalCount`, `totalPages`.
  По умолчанию возвращается страница 1 по 20 контактов, допустимый размер страницы 1–100.
- `PATCH /api/contacts/{id}` принимает `channels` в том же формате и заменяет весь набор адресов.
  Ответ `200` содержит обновлённый контакт. Снимки существующих доставок сохраняются.

Контакт включает хотя бы один адрес канала `email` или `telegram`. Telegram chat ID передаётся строкой.
Ошибки возвращаются как ProblemDetails. Некорректные данные дают `400`, отсутствующий контакт — `404`,
занятый адрес — `409`, откат записи из-за дедлока — `503` с возможностью повторить запрос.

## Совместимость с beta.2

- `POST /api/recipients` принимает `{"email":"legacy@example.test"}` и создаёт контакт с
  одним адресом email. `GET /api/recipients` и `GET /api/recipients/{id}` читают только
  контакты с email, в ответе сохраняется поле `email`. Эти операции работают с новой
  моделью контактов.
- `GET /api/deliveries/{id}` и `GET /api/campaigns/{id}/deliveries` возвращают поля
  `recipientId` и `recipientEmail`. В них передаются UUID контакта и сохранённый
  адрес доставки, в том числе chat ID для Telegram. Для исторического `NULL`
  возвращается пустая строка. Полей `contactId`, `channel` и `addressSnapshot`
  в DTO доставки пока нет.
- Отдельный протокол тестового email-оператора сохраняет `recipient_id` (UUID контакта) и
  `recipient_email` (снимок email) в запросе `POST /v1/email/send`. Остальные поля —
  `delivery_id`, `campaign_id`, `message_text` и `simulate_errors`.
  [Контракт оператора](../../tests/acceptance/operators/README.md).

Прежняя таблица `recipients` и колонки `deliveries.recipient_id` и
`deliveries.recipient_email` относятся к схеме до переноса контактов.
[Миграция beta.2 → beta.3](../adr/0002-legacy-migration-api-evolution.md).

## Каналы кампании

- `GET /api/channels` возвращает `200` и `["email","telegram"]`. Список отражает каналы модели.
  Оператор `email` выбирается через `TEST_SENDER_MODE`. Оператор Telegram запланирован на beta.4.
- `POST /api/campaigns` принимает непустые `name` и `messageText`, обязательные
  `selection` (`all` или `contactIds`) и непустой массив `channels` (`email`, `telegram`).
  Режим `all` допускает отсутствие `contactIds` или `null`. Режим `contactIds` требует
  непустой массив UUID. Режимы и каналы чувствительны к регистру. Повторы UUID и каналов
  учитываются один раз.
- Имя ограничено 255 символами. При выборе `telegram` текст ограничен 4096 символами.
  Лимиты включительны и считаются в кодовых точках Unicode. Имя и текст должны содержать
  непробельные символы. Нулевой символ U+0000 недопустим.
- Запрос возвращает `201 Created` с кампанией в `Draft` и заголовком `Location` после
  формирования. Снимок `all` включает контакты без адресов выбранных каналов.
  Кампания с нулём доставок тоже переходит в `Draft`.
- Невалидный запрос, в том числе запрос без `selection` или `channels` либо с отсутствующим
  UUID контакта, возвращает `400 application/problem+json` с причиной в `errors`.
  Валидация не оставляет кампанию, её снимок или доставки. При сбое формирования действует
  компенсационное удаление, а API возвращает обезличенный `500 application/problem+json`.
- `GET /api/campaigns/{id}` возвращает `campaign`, распределение `deliveries` по статусам и
  `formation`: `selectedContactCount`, отсортированные `channels`, `createdDeliveryCount` и
  `skippedDeliveryCount`. Пропуски равны числу сохранённых пар «контакт × канал» за вычетом
  числа записанных доставок. В `Forming` поле `skippedDeliveryCount` равно `null`. Если для
  исторической кампании каналы не записаны, `channels` содержит пустой массив.
- `POST /api/campaigns/{id}/start` возвращает `409` с ProblemDetails и именем канала, если для
  сохранённого канала черновика нет оператора. Черновик сохраняет статус. Пустой
  снимок каналов исторической кампании допустим. Повторный запуск в `InProgress`
  возвращает `200` с прежним `startedAt`. Запуск в `Forming`, `Paused`, `Failed` или
  `Completed` возвращает `409`.
- `start` и `resume` возвращают `200` с `Completed`, `startedAt` и `completedAt`,
  если у кампании нет доставок в `Waiting` и `InProgress`. При наличии таких
  доставок кампания переходит в `InProgress`. `resume` сохраняет `startedAt`.
- `DELETE /api/campaigns/{id}` возвращает `409` в `InProgress` и `Paused`.
  Ответ `204` подтверждает удаление кампании, её снимка и доставок. Журнал
  остаётся в БД. Для отсутствующей кампании API возвращает `404`.

## Сверка с текущим OpenAPI

Схему beta.3 можно получить с запущенного стенда, до его очистки. В той же Bash-сессии
используйте переменную `$api` из [быстрого старта](../../README.md#быстрый-старт-compose).

```bash
openapi_current=$(mktemp) &&
  curl --fail-with-body -sS "${api:?Сначала дождитесь готовности API}/openapi/v1.json" -o "$openapi_current" &&
  git diff --no-index -- docs/api/openapi.v1.json "$openapi_current"
```

`servers[0].url` различается из-за динамического порта стенда. Код выхода `git diff`
`1` означает, что найдены различия.

`MapOpenApi()` доступен в Development. По сравнению с baseline beta.2 добавились
`/api/channels`, `/api/contacts` и `/api/contacts/{id}`. У создания кампании в схеме
теперь обязательны `selection` и `channels` вместе с `name` и `messageText`.
Детали кампании включают `formation`, а схема `DeliveryResponse` сохраняет прежние поля.
Генератор по-прежнему описывает статусы числами, в ответах API используются строки PascalCase.
Полный список существенных различий зафиксирован в [сверке OpenAPI beta.3](../evidence/beta3/openapi-diff.md).

## Известные ограничения baseline beta.2

- `CampaignStatus`/`DeliveryStatus` в схеме — `"type": "integer"` без enum: `Microsoft.AspNetCore.OpenApi`
  не применяет `JsonStringEnumConverter` к схеме. Фактический wire-формат — PascalCase-строки
  (AD-7, `JsonStringEnumConverter` в `Program.cs`).
- `pageNumber/pageSize/totalCount/totalPages` рендерятся как union `integer|string` с pattern,
  допускающим `0`/отрицательные — артефакт генератора; реальная валидация — `pageNumber ≥ 1`,
  `pageSize` 20/100 (AD-7).
- Нет `operationId`, `security`/`securitySchemes`, описаний/примеров и единого набора кодов ошибок
  в зафиксированной схеме beta.2. Аутентификация отсутствует по NFR-8 (демо/локальный стенд).
- `info.version` = `1.0.0` (дефолт генератора); версия контракта — имя файла `v1`; `beta.N` —
  вехи релиза. `servers[0].url` — хост генерации (`localhost:18080`).

## Политика диффа

- Любое изменение API сравнивается с `openapi.v1.json`; намеренность и breaking-статус
  указываются в PR и release notes; молчаливые breaking changes запрещены.
- Baseline beta.2 фиксирует контракт той ревизии. Схема работающего API генерируется из кода и
  сравнивается с ним при подготовке выпуска.
