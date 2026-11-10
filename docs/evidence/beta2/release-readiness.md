# Готовность v0.5.0-beta.2 — Story 1.22

**Кандидат на момент проверки** `15e5016dfe75f32209e57083ec4700efda094e4e`, ветка `gv`.
Сверка выполнена 12 ноября 2026 года.
[Заметки о релизе](../../releases/v0.5.0-beta.2.md), [релизный гейт](../../EVIDENCE.md#3-теги-и-release-notes).

## Решение о выпуске

Владелец разрешил опубликовать `gv` и annotated-тег `v0.5.0-beta.2` без запуска
GitHub CI. Для этого выпуска согласовано исключение по критерию 1. Результат
проверки остаётся `FAIL-RECORDED`. Триггер CI исключает ровно этот тег.
Annotated-тег указывает на `113d228b6803f9a2b2c98a007925764dc3f5b7b2`.
Файлы в `src`, `config`, `tests` и `scripts` совпадают с кандидатом на момент
проверки. Доказательства локальных прогонов относятся к указанным ниже SHA.

## Сверка критериев

| Выходной критерий Epic 1 | Статус | Evidence |
| --- | --- | --- |
| 1. Зелёный `CI required` и опубликованный лог | **FAIL-RECORDED** | GitHub Checks API вернул HTTP 422, SHA кандидата не найден. Решение владельца не запускать GitHub CI действует. См. проверку ниже. |
| 2. Структура документации и правило SHA | **PASS** | Пути evidence, release notes, ADR и OpenAPI существуют. [Правила evidence](../../EVIDENCE.md). |
| 3. Necessity-аудит и исходные провалы | **PASS** | RUN-2/4/5/6 воспроизведены после исправлений. GAP-01 и GAP-03 классифицированы с остаточными пробелами. В Docker Linux прошли 130 Unit и 82 Integration без пропусков на `b75d7fe7726b1b12255686a2ff73a520037f8942`. Между этим SHA и кандидатом `src`, `config` и `tests/PackCall.Test` совпадают. [Команды и TRX](../necessity-audit/execution-record.md#baseline-на-head), [классификация](../necessity-audit/decisions.md). |
| 4. OpenAPI baseline | **PASS** | Baseline в Git. Живой API кандидата совпал по всем 9 путям и схемам после нормализации локального URL. [Baseline](../../api/openapi.v1.json). |
| 5. ADR и решение по legacy-кампаниям | **PASS** | ADR 0001–0003 в Git. ADR 0002 фиксирует миграцию legacy-кампаний для `beta.3`. [ADR 0002](../../adr/0002-legacy-migration-api-evolution.md). |
| 6. SM-6 на текущей модели | **PASS** | A/B/C и R11/W22 прошли в Docker Linux на `a381d75211fd3f1db5ee8237c8f99773ebfd747a`. Между этим SHA и кандидатом добавлен только отчёт. [SM-6 B/C и повтор A/W22](SM-6-B-C.md). |
| 7. Объём продуктовых изменений и API | **PASS** | От входа в Epic 1 (`47db371b85cfb989decff99a03f3698899e20cf2`) в `src` исправлены `RuntimeSampler` и кодировка предупреждения `DeliveryWorker`. В основном Compose пустой или отсутствующий `TEST_SIMULATE_ERRORS` теперь означает `false`. Дифф живого OpenAPI пуст после нормализации URL. [RUN-4](RUN-4-GAP-01.md). |
| 8. 300k вне гейта тега `beta.2` | **PASS** | Прогон на коммите тега остаётся обязательным условием входа в Epic 2. Story 2.1 ещё открыта. [Статус MEAS](../../../scripts/performance/README.md). |

## Проверка `CI required`

Запрос только на чтение к публичному репозиторию. Для штатного `release_gate.py`
доступного токена не было, поэтому выполнен GET того же Checks API.

```powershell
curl.exe --silent --show-error --max-time 30 `
  -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28' `
  --write-out '%{http_code}' `
  'https://api.github.com/repos/GVMainG/PackCall/commits/15e5016dfe75f32209e57083ec4700efda094e4e/check-runs?check_name=CI%20required&filter=latest'
git ls-remote origin refs/heads/gv refs/tags/v0.5.0-beta.2
git show-ref --verify --quiet refs/tags/v0.5.0-beta.2
```

HTTP 422, `No commit found for SHA: 15e5016dfe75f32209e57083ec4700efda094e4e`.
Удалённая `gv` указывает на `b75d7fe7726b1b12255686a2ff73a520037f8942`.
`git ls-remote` не вернул тег, локальный `git show-ref` завершился с кодом 1.
Результат `CI required` для кандидата неизвестен.
Обычный push в `gv` сам по себе не запустит CI по текущему workflow.
[Триггеры CI](../../../.github/workflows/ci.yml).

По штатному регламенту перед тегом `release_gate.py` проверяет `CI required` на
SHA кандидата. Согласованное выше исключение позволяет опубликовать `beta.2`
с результатом `FAIL-RECORDED`. Вход в Epic 2 требует отдельного прогона 300k
на коммите тега (Story 2.1).

## Сверка OpenAPI

Из корня чистого checkout кандидата выполнена команда:

```powershell
python artifacts/acceptance/beta2-readiness/15e5016/openapi-check.py
```

Скрипт собрал API и Migrator из `git archive` кандидата через Compose SM-6,
запустил отдельный PostgreSQL 18.6 и API в Development на динамическом порту
loopback. После GET `/openapi/v1.json` сравнил JSON с baseline. В живом JSON
заменён только `servers[0].url` (`http://127.0.0.1:58683/` → `http://localhost:18080/`).
В сравнении игнорируется порядок ключей объектов JSON. Разница — 0 строк.
Контейнеры, том, сеть и локальные образы проекта удалены.
[Команда генерации и ограничения baseline](../../api/README.md).

Для повторной сверки собрать `packcall-api` из исходников кандидата и запустить
его в Development по инструкции генерации, с отдельной PostgreSQL 18.6 и
применённой миграцией. Из корня checkout кандидата сохранить ответ во временный
файл и сравнить его с версией в Git:

```powershell
$packcallOpenApi = Join-Path ([IO.Path]::GetTempPath()) 'packcall-beta2-openapi.json'
curl.exe --fail --silent --show-error http://localhost:18080/openapi/v1.json -o $packcallOpenApi
if ($LASTEXITCODE -ne 0) { throw 'OpenAPI download failed' }
@'
import json
from pathlib import Path
import sys

baseline = json.loads(Path("docs/api/openapi.v1.json").read_text(encoding="utf-8-sig"))
live = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
if len(baseline["servers"]) != 1 or len(live["servers"]) != 1:
    raise SystemExit("Unexpected server list")
live["servers"][0]["url"] = baseline["servers"][0]["url"]
if baseline != live:
    raise SystemExit("OpenAPI differs after local server URL normalization")
print(f"OpenAPI matches on {len(live['paths'])} paths")
'@ | python - $packcallOpenApi
```

Окружение — Windows, Python 3.11.15, Docker Engine Linux amd64 29.8.2,
Compose 5.6.0, SDK .NET 10.0.404, API .NET и ASP.NET Core 10.0.14.
Сырые артефакты — `artifacts/acceptance/beta2-readiness/15e5016/`.
Там находятся `openapi-check.py`, `openapi-results.json`, `openapi.live.json`,
`openapi.diff`, `build-materials.json` и ответ `github-check-runs.json`.
