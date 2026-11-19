# Сверка runtime OpenAPI beta.3 с beta.2

19 ноября 2026. Baseline beta.2 — `50a3ac33a6ba3cafa805bdda5b1646a5d45ffbd2`.
Development API собран из архива коммита `6209783f4b5d68babe342b4eeae9eec6eeef91bc`
и запущен в Compose со свежим PostgreSQL 18.6.
[Окружение и результат smoke](release-readiness.md#чистый-compose).

Из [быстрого старта](../../../README.md#быстрый-старт-compose) в той же Bash-сессии:

```bash
(
  set -e
  tmp=$(mktemp -d)
  trap 'rm -rf -- "$tmp"' EXIT
  curl --fail-with-body -sS "${api:?Сначала запустите быстрый старт}/openapi/v1.json" \
    -o "$tmp/runtime.json"
  python - docs/api/openapi.v1.json "$tmp" <<'PY'
import json
import sys
from pathlib import Path

output = Path(sys.argv[2])
for source, target in [(Path(sys.argv[1]), "beta2"), (output / "runtime.json", "runtime")]:
    schema = json.loads(source.read_text(encoding="utf-8-sig"))
    contract = {key: schema[key] for key in ("paths", "components")}
    (output / f"{target}.normalized.json").write_text(
        json.dumps(contract, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
PY
  git diff --no-index -- "$tmp/beta2.normalized.json" "$tmp/runtime.normalized.json"
)
```

Код выхода 1 означает различия. Сравнение включает `paths` и `components`,
сортирует ключи и исключает динамический URL из `servers`. Baseline остаётся
на месте, повторный запрос и нормализованные копии пишутся во временный каталог.

В фактическом прогоне JSON получен через `urllib` и сохранён с отступами.
Сравнение `git diff --no-index` дало код 1, найдены изменения.
Зафиксированный дифф находится в `artifacts/story-2-19/compose-final/openapi.diff`.
SHA-256 baseline — `9a3594c9f8aa5064919fb62213d1f69065c7105a9fed829585906247f80ef69d`.
SHA-256 сохранённого runtime JSON — `eb4a973491edade50ee288aa295a777c238567727cbece3de3a8ce6d4253b52e`.
Хеши идентифицируют файлы исходного прогона. Порт и сериализация могут изменить
байты повторного захвата. [Зафиксированный baseline](../../api/openapi.v1.json).

| Раздел | Наблюдаемое изменение |
| --- | --- |
| Пути | 9 → 12. Добавлены `GET /api/channels`, `GET/POST /api/contacts` и `GET/PATCH /api/contacts/{id}`. Все пути beta.2 сохранены. |
| `/api/recipients` | Описания операций и схемы ответов сохраняются. Публичные DTO доставок по-прежнему используют `recipientId` и `recipientEmail`. |
| `POST /api/campaigns`, breaking change | `CreateCampaignRequest` требует `name`, `messageText`, `selection` и `channels`. `selection` допускает `all` и `contactIds`. `channels` содержит хотя бы один из `email`, `telegram`. |
| `POST /api/campaigns`, ответы | `201` сохраняет схему `CampaignResponse` и документирует `Location`. Схема `400` уточнена до `ValidationProblemDetails`. Добавлен `500 ProblemDetails`. |
| `POST /api/campaigns/{id}/start` | Коды `200/400/404/409` сохранены. Для `409` в OpenAPI добавлен `application/problem+json`. API возвращает этот конфликт в том числе при отсутствии оператора выбранного канала. |
| Детали кампании | `CampaignDetailsResponse` получает обязательный объект `formation` с количеством выбранных контактов, каналами и количеством созданных и пропущенных доставок. |
| Схемы и метаданные | Компонентов 14 → 22, прежние имена сохранены. Добавлены теги `Contacts` и `Channels`. URL в `servers[0]` отражает выбранный Compose-порт. |

Серверная валидация требует непустой массив UUID `contactIds` при
`selection: "contactIds"`. Этой условной обязательности в OpenAPI нет.

Сравнение JSON по пути и HTTP-методу обнаружило изменения у двух прежних
операций `POST`, создания и запуска кампании. Обе версии OpenAPI объявляют
`CampaignStatus` и `DeliveryStatus` с типом `integer`. В smoke API фактически
вернул строки `Draft`, `InProgress`, `Completed` и `Sent`. Это известное
расхождение декларации и wire-формата. [Контракт API](../../api/README.md).
