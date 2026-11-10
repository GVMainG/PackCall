# SM-6 B и C. Остановка Worker и устаревшее закрепление

Прогон начат 12.11.2026 в 15:54 МСК. Стенд собран на чистом рабочем
дереве из архива коммита `a381d75211fd3f1db5ee8237c8f99773ebfd747a`.
Обе проверки прошли, код выхода 0.

Из корня checkout указанного SHA:

```powershell
python tests/acceptance/workers/sm6/run_bc.py
```

Окружение — Windows, Python 3.11.15, Docker Engine Linux amd64
29.8.2, Compose 5.6.0, .NET SDK 10.0.404,
тестовый Worker на .NET 10.0.15, PostgreSQL 18.6.
Использованы отдельные Compose-проект и БД, штатный inprocess тестовый оператор,
Worker, сервис обработки и репозиторий. После запуска кампании стенд читал
PostgreSQL и управлял тестовыми процессами и барьерами.
[Инструкция запуска](../../../tests/acceptance/workers/sm6/README.md).

| Образ | Digest прогона |
| --- | --- |
| `mcr.microsoft.com/dotnet/sdk:10.0` | `sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb` |
| `mcr.microsoft.com/dotnet/runtime:10.0` | `sha256:80a51954ac70925e5783cb1ed35129a4243dcb1cddef20066b67346e17db284d` |
| `mcr.microsoft.com/dotnet/aspnet:10.0` | `sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee` |
| `postgres:18.6-alpine3.24` | `sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5` |
| `python:3.12.10-slim` | `sha256:fd95fa221297a88e1cf49c55ec1828edd7c5a428187e67b5d1805692d11588db` |

| Проверка | Наблюдение |
| --- | --- |
| B, ответ подтверждён | Первый оператор вернул `accepted`. Тестовый барьер удерживал ответ до SIGKILL. Доставка оставалась `InProgress` с действующим закреплением, журнал содержал только запись захвата. |
| B, остановка и проход recovery | После SIGKILL контейнер завершился с кодом 137. Новый Worker с закрытым захватом выполнил проход recovery и освободил 0 доставок. Снимок в 12:55:15.552322 UTC показал прежнюю строку и журнал. Срок первого закрепления истекал в 12:55:32.817425 UTC. |
| B, восстановление и захват | В 12:55:32.883960 UTC новый Worker записал возврат той же доставки в `Waiting`. После открытия захвата он получил новое закрепление до 12:56:33.113977 UTC. |
| C, условный UPDATE | Отдельный процесс вызвал штатный `ProcessAsync` с исходным снимком и подтверждённым ответом. Перехватчик EF Core зарегистрировал один выполненный PostgreSQL `UPDATE` доставки с результатом 0 строк. Сервис сообщил `StaleResult`. Владелец, срок, остальные поля строки и журнал совпали до и после вызова. Терминальной записи ещё не было. |
| C, завершение | После открытия барьера новый Worker сохранил `Sent` и единственную терминальную запись с `AcceptedByProvider`. Журнал прошёл `0 → 1 → 0 → 1 → 2`, кампания достигла `Completed`. |

В C одновременно передаются прежний владелец и прежний срок закрепления.
Отдельные предикаты проверяют случаи `owner`, `captured` с тем же владельцем
и `expired` в тесте
[`Result_StaleLease_DoesNotChangeRowOrJournal`](../../../tests/PackCall.Test/Integration/Deliveries/DeliveryResultLeasePostgresTests.cs).
Внешний оператор симулирован. По NFR-2 потеря результата допускает повторный
внешний вызов, поэтому здесь проверяется уникальность терминальной записи в БД.

Сырые артефакты вне Git — `artifacts/acceptance/sm6-bc/0b8afbd090a5/`.
SHA-256 файла `results.json` —
`ad3dde1ee38c19c54b9a89d5d5696b65b88d1576216bab2dd3a513f550fc82c3`.
В `build-materials.json` зафиксированы материалы BuildKit. В `control/worker-loss/` лежат исходный снимок захвата,
отметка оператора, состояние убитого контейнера и снимок до истечения.
В `control/recovery/` лежат отметка первого прохода recovery, снимки
`Waiting`, повторного захвата и финала. В `control/stale-result/` лежат
отметка оператора, исход `ProcessAsync` с числом строк UPDATE, состояние
процесса и снимок после попытки. После прогона списки Docker по имени и
меткам проекта подтвердили отсутствие его контейнеров, тома, сети и образов.

На том же SHA в отдельных Docker Linux проектах прошли регрессии:

- `python tests/acceptance/workers/sm6/run.py` — все пять веток SM-6 A.
  Результат — `artifacts/acceptance/sm6-a/c806d7be3506/results.json`.
- `python tests/acceptance/workers/coordination/run.py` с
  `PACKCALL_ENVIRONMENT=test` и `TEST_SENDER_MODE=inprocess` — все семь
  сценариев R11/W22. Результат — `artifacts/acceptance/sm6-bc/w22-inprocess/run-20261112-155438-ce3db616/results.json`.
  [Инструкция запуска W22](../../../tests/acceptance/workers/coordination/README.md).
