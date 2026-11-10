# SM-6 A. Неизвестный исход оператора

Прогон 12 ноября 2026 года в 15:21 МСК. Исходный SHA
`4fabd0fd5323e2196813806d3f3c3402aef03bc4`. Стенд собран из архива этого
коммита на чистом рабочем дереве. Итоговый код выхода — 0.

Из корня checkout указанного SHA:

```powershell
python tests/acceptance/workers/sm6/run.py
```

Окружение — Windows, Python 3.11.15, Docker Engine Linux 29.8.2, Compose 5.6.0
и PostgreSQL 18.6 (`postgres:18.6-alpine3.24`). Образы собирались с .NET SDK 10.0.
Runtime тестового Worker и Migrator — .NET 10.0.15, API — .NET и ASP.NET Core
10.0.14. HTTP-фикстура — `python:3.12.10-slim`.
[Инструкция запуска](../../../tests/acceptance/workers/sm6/README.md).

| Ветка | Первая попытка | Строка и журнал после первой попытки | Итог |
| --- | --- | --- | --- |
| `timeout` | HTTP-адаптер получил `TestOperatorTimeoutException`. Фикстура зарегистрировала запрос и удержала ответ за пределами срока ожидания. | Доставка оставалась нетерминальной. После истечения лизинга зафиксированы `Waiting` и запись восстановления. | Новый лизинг, затем `Sent` с `AcceptedByProvider`. |
| `exception` | Inprocess-оператор выбросил `InvalidOperationException`. | Доставка оставалась нетерминальной. После истечения лизинга зафиксированы `Waiting` и запись восстановления. | Новый лизинг, затем `Sent` с `AcceptedByProvider`. |
| `cancellation` | Inprocess-вызов завершился `TaskCanceledException`. Зафиксирована отмена переданного Worker токена и совпадение токена исключения. | Доставка оставалась нетерминальной. После истечения лизинга зафиксированы `Waiting` и запись восстановления. | Новый лизинг, затем `Sent` с `AcceptedByProvider`. |
| `mismatch` | HTTP 200, `application/json`, пара `accepted/SimulatedOperatorRejected`. Адаптер выбросил `TestOperatorProtocolException`. | Доставка оставалась нетерминальной. После истечения лизинга зафиксированы `Waiting` и запись восстановления. | Новый лизинг, затем `Sent` с `AcceptedByProvider`. |
| `rejection` | Inprocess-оператор вернул подтверждённый отказ `SimulatedOperatorRejected`. | Ровно одна попытка и терминальная запись с кодом отказа. | `Failed=-1`. |

Во всех четырёх ветках неизвестного исхода до повтора отсутствовали терминальные
строки и записи журнала. Снимок `recovered.json` показывает снятый лизинг,
`Waiting` и время обновления не раньше истечения первого лизинга.
`recaptured.json` показывает новое действующее закрепление той же доставки.
Точные последовательности журнала — `0 → 1 → 0 → 1 → 2` для повтора и
`0 → 1 → -1` для отказа. Финальные строки и журнал проверены после остановки
Worker. Все пять кампаний достигли `Completed`. Ложных `Sent` и `Failed` — 0.
Переходы выполнили Worker и PostgreSQL без ручных правок БД.

Сырые артефакты вне Git — `artifacts/acceptance/sm6-a/8f3adc6a769f/`.
`results.json` содержит SHA и исходы. SHA-256 этого файла —
`6df9f9cfc4881d107f6d37723c224a806fe730029ba746bb728be7075a627a82`.
Снимки строки, журнала и отметки вызовов находятся в `control/<ветка>/`.
Проверка после прогона подтвердила удаление контейнеров, тома, сети и локальных
образов изолированного проекта.

Совместимость общего acceptance Worker проверена отдельным запуском
`tests/acceptance/workers/coordination/run.py` в режиме `inprocess` на
`4564500c170dbe9a096909661753867da29f0faa`. Все семь сценариев R11 и W22 прошли.
Результаты сохранены в
`artifacts/acceptance/sm6-a/w22-inprocess/run-20261112-151036-f555c7f6/results.json`.
[Приёмка Worker](../../../tests/acceptance/workers/coordination/README.md).
