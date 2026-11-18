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
`/api/recipients` по-прежнему обслуживает email-получателей.

## Каналы кампании

- `GET /api/channels` возвращает `200` и `["email","telegram"]`. Список отражает каналы модели.
  Оператор `email` выбирается через `TEST_SENDER_MODE`. Оператор Telegram запланирован на beta.4.
- `POST /api/campaigns` формирует email-доставки при пропущенном или равном `null` поле `channels`
  и при явном наборе из `email`. Пустой массив, неизвестные имена, пустые и `null`-элементы дают
  `400` с ProblemDetails. Имена чувствительны к регистру. Явный выбор `telegram` также даёт `400`
  до записи кампании. Запрос синхронно возвращает `201` после формирования доставок и перехода в
  `Draft`. Ошибка формирования возвращает `500 application/problem+json` без внутренних подробностей.
  Публичная выборка `all` или `contactIds` с каналами запланирована на Story 2.13.
- `GET /api/campaigns/{id}` возвращает `campaign`, распределение `deliveries` по статусам и
  `formation`: `selectedContactCount`, отсортированные `channels`, `createdDeliveryCount` и
  `skippedDeliveryCount`. Пропуски равны числу сохранённых пар «контакт × канал» за вычетом
  числа записанных доставок. В `Forming` поле `skippedDeliveryCount` равно `null`. Если для
  исторической кампании каналы не записаны, `channels` содержит пустой массив.
- `POST /api/campaigns/{id}/start` возвращает `409` с ProblemDetails и именем канала, если для
  сохранённого канала черновика нет оператора. Черновик сохраняет статус. Для существующих кампаний
  без сохранённых каналов применяется их исходный канал `email`. Запуск кампании в `Forming`
  также возвращает `409`.

## Регенерация

Baseline снимается с запущенного API (требуется образ `packcall-api` и доступный PostgreSQL,
схема создана):

```bash
docker run -d --name openapi-gen --network <net> \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e PACKCALL_ENVIRONMENT=test -e TEST_SENDER_MODE=inprocess \
  -e "ConnectionStrings__Postgres=Host=<db>;Database=<db>;Username=<user>;Password=<pass>" \
  -p 18080:8080 packcall-api
curl -s --retry 10 --retry-connrefused --retry-delay 2 \
  http://localhost:18080/openapi/v1.json -o docs/api/openapi.v1.json
docker stop openapi-gen && docker rm openapi-gen
```

`MapOpenApi()` доступен только в Development-окружении (см. `src/PackCall.Api/Program.cs`).
JSON генерируется, а не правится вручную.

## Известные ограничения baseline beta.2

- `CampaignStatus`/`DeliveryStatus` в схеме — `"type": "integer"` без enum: `Microsoft.AspNetCore.OpenApi`
  не применяет `JsonStringEnumConverter` к схеме. Фактический wire-формат — PascalCase-строки
  (AD-7; `JsonStringEnumConverter` в `Program.cs`). Выравнивание схемы (string-enum) — в `beta.3`.
- `pageNumber/pageSize/totalCount/totalPages` рендерятся как union `integer|string` с pattern,
  допускающим `0`/отрицательные — артефакт генератора; реальная валидация — `pageNumber ≥ 1`,
  `pageSize` 20/100 (AD-7).
- Нет `operationId`, `security`/`securitySchemes`, описаний/примеров и единого набора кодов ошибок
  — это «как есть» на HEAD; аутентификация отсутствует по NFR-8 (демо/локальный стенд).
- `info.version` = `1.0.0` (дефолт генератора); версия контракта — имя файла `v1`; `beta.N` —
  вехи релиза. `servers[0].url` — хост генерации (`localhost:18080`).

## Политика диффа

- Любое изменение API сравнивается с `openapi.v1.json`; намеренность и breaking-статус
  указываются в PR и release notes; молчаливые breaking changes запрещены.
- Baseline, устаревший относительно кода, — красный флаг ревью, а не «допустимое расхождение».
- Автоматическая проверка дрейфа (перегенерация + diff в CI) — отдельная задача `beta.3`.
