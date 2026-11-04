# ADR 0002 — Legacy-кампании при миграции и контракт эволюции API

- **Статус:** принято (beta.2)
- **Дата:** 2026-11-11
- **Связи:** AD-5, AD-7 · AR-11 · PRD §8.1 · addendum §2.1, §2.3

## Контекст

`beta.3` переносит модель `recipient` в `contacts`/`contact_channels` и вводит снимок выборки
(ADR 0001). Существующие кампании и доставки при этом не должны потеряться или потребовать
ручного вмешательства; одновременно надо зафиксировать, как API меняется до 0.5 и как такие
изменения видны в диффе.

## Решение

### Миграция существующих данных (addendum §2.3)

- `recipients` → `contacts` + `contact_channels` (`channel=email`, `address=email`).
- `deliveries.recipient_id` → `contact_id`; `recipient_email` → `address_snapshot`; добавляется
  `channel='email'` для существующих строк.
- Существующие кампании получают `campaign_contacts`/`campaign_channels` по фактическим
  доставкам; **legacy-режим не вводится** — после миграции работает только новая модель.
- Одна EF-миграция. Обязательны: `pg_dump` до миграции, проверенный restore, down-миграция
  **или** задокументированная процедура restore + повторный деплой предыдущего тега, проверенная
  на копии БД. Репетиция на дампе `beta.1` с ≥1 000 000 доставок (время, блокировки, restore) —
  в evidence `beta.3`.
- Endpoint `recipients` заменяется `contacts`; до 0.5 breaking changes фиксируются в release
  notes.

### Контракт эволюции API (AD-7)

- Единый envelope списков: `PageResponse{items, pageNumber, pageSize, totalCount, totalPages}`.
- `pageNumber ≥ 1`; `pageSize` default 20, max 100 на всех list-эндпоинтах.
- Ошибки — ProblemDetails (400 валидация / 404 не найдено / 409 конфликт статуса / 500 с
  компенсацией).
- Wire-спеллинг енумов: статусы — PascalCase (текущий `JsonStringEnumConverter`, замораживается
  OpenAPI baseline в beta.2); `Channel` — lowercase (`email`|`telegram`).
- API не объявляется стабильным до 0.5; изменения API диффятся против
  `docs/api/openapi.v1.json`, breaking changes указываются в PR и release notes; молчаливые
  breaking changes запрещены.

## Последствия

- Миграция обратима (down-миграция или проверенный restore), решения принимаются до старта
  `beta.3`, а не на проде.
- Смена endpoint `recipients`→`contacts` — осознанный breaking change, фиксируется в release
  notes `beta.3`.
- OpenAPI baseline (`docs/api/openapi.v1.json`, Story 1.19) — эталон диффа; расхождение кода и
  baseline — красный флаг ревью, а не «допустимое расхождение».

## Трассировка

AD-7 (контракт эволюции API), AD-5 (владение схемой и миграции), AR-11, PRD §8.1, addendum §2.1,
§2.3. Документ — вход для стори `beta.3` (Stories 2.2–2.4).

## Ревью

Принято после ревью 2026-11-11: правила сверены с addendum §2.3 и AD-7; расхождений не выявлено.
