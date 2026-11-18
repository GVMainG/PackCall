# Story 2.16 — восстановление кампании и SM-6

Локальный прогон 18 ноября 2026 года, код
`10d9fe5f18a5c362f893a69a49c4a56ea7b7a1e9` в ветке `gv`.
Оба стенда SM-6 собраны через `git archive` из чистого рабочего дерева.
Windows, Docker Engine 29.8.2 (Linux amd64), Compose 5.6.0, PostgreSQL 18.6,
Python 3.11.15, SDK .NET 10.0.404 и Worker .NET 10.0.15.
Для каждого запуска SM-6 использовались отдельные Compose-проект, временная БД,
синтетический контакт и тестовые операторы. Оба стенда прошли штатную очистку.

Из корня чистого checkout этого SHA:

```powershell
python tests/acceptance/workers/sm6/run.py
python tests/acceptance/workers/sm6/run_bc.py
python tests/acceptance/measurements/stall_smoke.py
```

[Инструкция стенда SM-6](../../../tests/acceptance/workers/sm6/README.md).

| Сценарий | Наблюдение |
| --- | --- |
| A, четыре неизвестных исхода | Таймаут HTTP, исключение, отмена по бюджету закрепления и несовпадение HTTP `result/code`. После первой попытки терминальных записей не было. Каждая доставка вернулась в `Waiting` и получила новое закрепление. После второго, подтверждённого, результата записан один `Sent`. Все четыре кампании завершились. |
| A, подтверждённый отказ | Один вызов, один `Failed` с `SimulatedOperatorRejected`, кампания `Completed`. |
| B, SIGKILL | Первый Worker получил подтверждение, остановлен до записи результата, код выхода 137. До истечения закрепления второй Worker освободил 0 доставок. После срока по часам PostgreSQL он вернул ту же доставку в `Waiting` и заново захватил её. |
| C, устаревшее закрепление | Отдельный Worker передал старый снимок в штатный `ProcessAsync`. Результат `StaleResult`, один PostgreSQL UPDATE затронул 0 строк. Доставка и журнал совпали со снимками до вызова. После подтверждения по действующему закреплению записан один `Sent`, кампания `Completed`. |

В C проверены контакт, канал, адрес и данные закрепления. Сравнение срока
закрепления выполняется по `clock_timestamp()`, в том числе при применении
результата. Отдельный PostgreSQL-тест
`ReleaseCapture_CannotReleaseNewLeaseEvenForTheSameWorker` подтверждает,
что пауза не освобождает доставку с обновлённым сроком закрепления.
Оба отчёта SM-6 содержат **0 ложных `Sent` и 0 ложных `Failed`**.

## Сверка завершения

Миграции применены к пустой PostgreSQL. Release-сборка без предупреждений,
.NET-тесты **313/313**, пропусков 0. PostgreSQL-тесты прерывают обычную
обработку после сохранения `Sent` или `Failed`, затем запускают recovery из
нового scope. Проверены сохранность даты старта, одно обновление даты завершения,
отсутствие новых записей журнала и вызовов оператора, пауза и незавершённые
доставки с адресом и без него, включая другую активную кампанию.

Команды внутри контейнера `mcr.microsoft.com/dotnet/sdk:10.0` с
`PACKCALL_TEST_POSTGRES` и `ConnectionStrings__Postgres` на выделенную БД,
`PACKCALL_ENVIRONMENT=test`, `TEST_SENDER_MODE=inprocess`,
`TEST_SIMULATE_ERRORS=false`:

```sh
dotnet run --project src/PackCall.Migrator -c Release --artifacts-path /tmp/packcall-build
dotnet build PackCall.slnx -c Release --artifacts-path /tmp/packcall-build --no-incremental
dotnet test tests/PackCall.Test/PackCall.Test.csproj -c Release --artifacts-path /tmp/packcall-build --no-build --logger 'trx;LogFileName=story-2-16-final.trx' --results-directory /src/_bmad-output/implementation-artifacts/story-2-16-test-results
```

TRX хранится локально в
`_bmad-output/implementation-artifacts/story-2-16-test-results/story-2-16-final.trx`,
SHA-256 `26b99f3f9c244c3609f9a1551831df26217519331b67a2653641b84b4492bf31`.
Python-проверка `scripts/ci/check_trx.py` с `--forbid-skipped` подтвердила счётчики.

Smoke со сбоем вставки терминальной записи тоже прошёл на чистом коммите.
Итог `Completed`, одна доставка `Sent`, ноль терминальных записей в журнале,
расхождение между наблюдаемыми результатами и БД 0. Артефакты находятся в
`artifacts/performance/af2654d9-c7a2-4102-8cf5-b8776024e371/`, включая
`manifest.json`, `final.json`, `final-journal.json` и `summary.json`.
Манифест фиксирует SDK хоста 11.0.100-rc.2.26504.105, приложение собрано
в Docker с SDK 10.0.404. Анализатор использовал jsonschema 4.26.0.

## Сырые отчёты

| Артефакт вне Git | SHA-256 файла `results.json` |
| --- | --- |
| `artifacts/acceptance/sm6-a/2ad6ee6363df/` | `7859a43c214be5ed19ac566048c1aacc8813e6578ee011d752a136e9e347d775` |
| `artifacts/acceptance/sm6-bc/27b174c94a8b/` | `6f432debabd882552d171f42452401c5b2e603213f1b51c81de427eead69ad53` |

В `control/` каждого прогона сохранены снимки PostgreSQL, отметки вызовов и
записи журнала. В B/C там также лежат результат `docker inspect` первичного
Worker, его исходный снимок закрепления, результат первого recovery и результат
устаревшего обновления.
