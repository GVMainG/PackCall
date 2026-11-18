# Story 2.17 — приёмка выборки SM-2

18 ноября 2026 года. Проверенный коммит ветки `gv` — `a9c094a725297bf88090152f014cfd8eb25515ba`. Полный прогон из чистого рабочего дерева, локальный Docker Linux, PostgreSQL 18.6 и .NET SDK 10.0.404. Использованы изолированная схема, публичный HTTP API, штатный Worker с одним обработчиком и тестовый email-оператор с записью каждого вызова.

| Наблюдение | Результат |
| --- | --- |
| Восемь комбинаций выбора контакта, канала и наличия адреса | Пять контактов, три выбранных, один выбранный канал email. Сформированы две доставки. |
| Два PATCH после формирования | Новый email и замена прежнего подтверждены чтением контактов. Кампания осталась в Draft. Снимки контактов и каналов, ID и сохранённые адреса доставок совпали. Worker использовал исходный email. |
| Фактические вызовы оператора | 2 попытки по 2 уникальным ID, 0 повторов, 0 вызовов вне выборки. Проверены ID кампании, доставки и контакта, канал, адрес и текст для каждого вызова. |
| Подтверждённые исходы | В БД и терминальном журнале 1 Sent и 1 Failed, кампания Completed. |
| Смешанная кампания | Обе доставки сформированы. Ответ на запуск 409 ProblemDetails с указанием telegram. Кампания Draft, обе доставки Waiting, захват пуст, 0 вызовов. |

## Воспроизведение

Из корня репозитория в SDK-контейнере, с `PACKCALL_TEST_POSTGRES` и `ConnectionStrings__Postgres` для выделенной PostgreSQL, `PACKCALL_ENVIRONMENT=test`, `TEST_SENDER_MODE=inprocess`, `TEST_SIMULATE_ERRORS=false`:

```sh
dotnet run --project src/PackCall.Migrator -c Release --artifacts-path /tmp/packcall-build
dotnet build PackCall.slnx -c Release --artifacts-path /tmp/packcall-build --no-incremental
dotnet test tests/PackCall.Test/PackCall.Test.csproj -c Release --artifacts-path /tmp/packcall-build --no-build --logger 'trx;LogFileName=story-2-17-final.trx' --results-directory /src/_bmad-output/implementation-artifacts/story-2-17-test-results
```

Release без предупреждений, 314 тестов прошли, 0 ошибок и 0 пропусков.
Локальный отчёт `_bmad-output/implementation-artifacts/story-2-17-test-results/story-2-17-final.trx`, SHA-256 `dece81a451207e7c8e842623a27e70950fb2fcf7fb9f9b4f2b10d568a66fbb73`.
В TRX проверены `WorkerSendsOnlySelectedPairsUsingDraftAddresses` и оба варианта `StartRejectsMissingOperatorBeforeWorkerCanCapture`.
