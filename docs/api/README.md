# Контракт API

Baseline контракта API — [`docs/api/openapi.v1.json`](openapi.v1.json). Он снят с API на HEAD
(`beta.2`) и отражает текущие эндпоинты и схемы. До `beta.3` API оперирует `recipients`
(`contacts`/`contact_channels` придут в `beta.3`, ADR 0001); изменение API диффится против
baseline, намеренные breaking changes записываются в release notes. До 0.5 API не объявляется
стабильным.

Wire-спеллинг (AD-7): статусы — PascalCase, будущий `Channel` — lowercase (`email`|`telegram`).

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
