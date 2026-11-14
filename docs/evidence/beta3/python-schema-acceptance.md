# Story 2.5 — Python-приёмка схемы контактов и каналов

Локальные прогоны 14 ноября 2026 года: Windows, Python 3.11.15, Docker Engine
29.8.2 (Linux containers), Compose 5.6.0, PostgreSQL 18.6 (для проверок
коллекторов), .NET SDK 11.0.100-rc.2.26504.105 на хосте и образы .NET 10
для приложения. Исходная ревизия продукта — `f109f099d09a0df68bab9f2942b28ce27e0e34a4`.
GitHub CI не запускался. Использовались выделенные базы и синтетические адреса.

## Проверки новой схемы

| Команда из корня репозитория | Результат |
| --- | --- |
| `python -m pytest tests/tools/performance -q` | 49 тестов и 69 subtests прошли. Зависимости из `scripts/performance/requirements.txt`. |
| `python tests/acceptance/measurements/resource_smoke.py` | PASS. Очередь различает общее число ожидающих и доступных email-доставок; Telegram, NULL и пробельный адрес не учитываются как доступные. Проверены пауза, пустая выборка, статистика и права PostgreSQL. |
| `python tests/acceptance/measurements/postgres_repeat.py` | PASS. Повторный захват даёт 1; освобождение без повтора не увеличивает значение. Вывод — агрегат и нижняя оценка из-за неатомарности журнала. |
| `python tests/acceptance/measurements/crash_smoke.py` | PASS. После SIGKILL сохранён префикс измерений без маркера штатной остановки и тестового секрета. |
| `python tests/acceptance/measurements/stall_smoke.py` | PASS. При сбое записи журнала доставка зафиксирована, кампания не объявлена завершённой; после 120 с — `completion_stalled`. |
| `python tests/acceptance/measurements/control_smoke.py` | PASS на повторе. Сессия остановлена до старта кампании, её БД сохранена до явной очистки, артефакты сохранены после очистки. |
| `python tests/acceptance/measurements/two_worker_crash.py` | PASS на повторе. После SIGKILL одного Worker второй завершил 40 доставок; телеметрия помечена как частичная. |
| `python tests/acceptance/workers/lifecycle/lifecycle.py` | PASS. После SIGTERM сохранены 40 захватов; новый Worker завершил все 1000 доставок. Кампания `Completed`, на доставку ровно одна запись `Sent`. |
| `python tests/acceptance/workers/lifecycle/shutdown-probes.py` | PASS. В режимах `before` и `ignore` сохранены захваты, отсутствуют терминальные записи, оба probe завершились с кодом 0. |
| `python tests/acceptance/workers/lifecycle/startup.py`, `images.py` | PASS. Проверены ожидание миграции, отмена, неизвестная схема и наличие общего файла в образах. Отрицательная проба без файла проверяет ненулевой выход без установления точной причины. |
| `python tests/acceptance/workers/coordination/run.py` (HTTP) | PASS. R11 и W22-01…05 на `packcall-w22-bf62791c`. Проверены параллельный захват, поздняя доставка, восстановление, устаревшая запись и сдвиг часов Worker. Первая попытка под параллельными сборками вышла за бюджет закрепления до остановки; повтор прошёл без ослабления проверок. |
| `python tests/acceptance/operators/privacy.py` | PASS. Действующий HTTP-контракт, валидация и принудительная ошибка без данных запроса в журнале оператора. |
| `python tests/acceptance/operators/operator_contract.py` | PASS, 5 тестов ASGI с прежними полями, повтором запроса и проверкой ошибок. |
| `python tests/acceptance/operators/run.py`, `retention.py` | PASS в обоих режимах. В Linux SDK-контейнере прошли 233 теста .NET без пропусков. Проверка retention прошла на сохранённом HTTP-контейнере. |
| `python tests/acceptance/operators/late_http.py` | PASS. После истечения бюджета сначала сохранено закрепление, затем новый Worker завершил отправку. Поздний HTTP-ответ не изменил доставку, кампанию или журнал. |
| `python tests/acceptance/operators/shutdown.py` | PASS. В режимах `inprocess` и `http` остановка до отправки и в неотзывчивом операторе сохранила захваты и журнал. |
| `python tests/acceptance/operators/config_matrix.py`, `precedence.py`, `prod_compose.py` | PASS. Матрица настроек, приоритеты конфигурации и запрет prod на пустой и мигрированной БД. |
| `python -m unittest discover -s tests/tools/operators -p "test_*.py" -v` | PASS, 4 проверки обвязки. |
| `python tests/acceptance/workers/sm6/run.py`, `run_bc.py` | PASS. В A — все пять веток, в B/C — истечение захвата, штатное восстановление и отклонение устаревшей записи. Прогон из отдельной чистой копии исходной ревизии: используемые SM-6 файлы продукта и фикстур в Story 2.5 не менялись. |

По `rg -n 'recipient_id|recipient_email|\brecipients\b' tests/acceptance -g '*.py' -g '*.sql'`
в приёмке остались только HTTP-маршрут `/api/recipients` и поля запросов
оператора `recipient_id`/`recipient_email`. Прямые SQL-вставки доставок задают
список колонок и пишут `contact_id/channel/address_snapshot`. Бизнес-значения
сверены с C# и AD-5: доставка `Waiting=0 → InProgress=1 → Sent=2/Failed=-1`,
восстановление `InProgress=1 → Waiting=0`; кампании `Draft=0`, `Forming=3`,
`InProgress=1`, `Paused=4`, `Completed=2`. Проверка отсутствия терминальной
записи при остановке учитывает `2` и `-1`.

Порядок запуска стендов: [lifecycle](../../../tests/acceptance/workers/lifecycle/README.md),
[coordination](../../../tests/acceptance/workers/coordination/README.md),
[operators](../../../tests/acceptance/operators/README.md),
[SM-6](../../../tests/acceptance/workers/sm6/README.md).

В этом прогоне W22 проверялся по сценариям `run.py` в HTTP-режиме. Отдельная
обвязка `coordination/verify.py` с Windows testhost, сборкой и публикацией
не запускалась. Полный набор .NET проверен через операторскую приёмку в Linux.

## Подготовка нагрузки и малая кампания

- `python scripts/performance/run.py --profile windows-http-300k --workers 2 --prepare-only`.
  Проверены 300 000 контактов, email-каналов, уникальных адресов, контактов снимка
  и доставок с совпадающим адресом. В снимке один канал. Кампания `Draft`, все
  300 000 доставок `Waiting`. Успешный повтор с готовыми локальными образами
  сохранил `initial.json` и `audience.json` в сеансе `8561c2b6-d237-409e-b828-fe778b686884`.
- `python scripts/performance/run.py --profile small-http --count 60 --workers 2`.
  Все 60 снимков совпали, все доставки дошли до `Sent`, кампания — до `Completed`.
  Сеанс `3742b45f-00c2-43d5-810e-3e3aff903381`.

Первый запуск 300k остановился во время формирования. На повторе подготовка
подтверждена. Таймаут создания кампании для нового профиля 300k увеличен
до 15 минут. Он применяется до начала отправок. Команды для обычного запуска
и очистки приведены в [README измерений](../../../scripts/performance/README.md).
В малом сценарии остановки отдельно сверены все шесть счётчиков `audience.json`.
На изолированном PostgreSQL сид отклонил базу с одной строкой журнала
(`performance_database_not_empty`), контакты и каналы остались пустыми.

## Совместимость beta.2

`python scripts/performance/baseline.py --product-root $product --output artifacts/story-2-5/beta2 --smoke`
запущен с чистой отдельной копией `v0.5.0-beta.2`
(`50a3ac33a6ba3cafa805bdda5b1646a5d45ffbd2`). Повторная серия
`1f401839-9986-493f-bf80-f03ec40acdd0` дала `smoke_pass`: четыре
валидных сеанса по 6 000 доставок, включая пару с зондом задержек.
Первая серия отклонена после сброса счётчика PostgreSQL. Пороговые значения
и проверки исторического протокола сохранены.
