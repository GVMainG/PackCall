# SM-6 A. Неизвестный исход оператора

Из корня чистого checkout, с Python с поддержкой `tarfile.data_filter`
и Docker Compose в режиме Linux. Прогон проверен на Python 3.11.15.

```powershell
python tests/acceptance/workers/sm6/run.py
```

Сценарий собирает стенд из указанного в отчёте commit SHA. Он создаёт отдельные
Compose-проект и PostgreSQL. После прогона удаляются контейнеры, том и
локальные образы проекта.
Снимки строк, журнал, события оператора и `results.json` сохраняются вне Git в
`artifacts/acceptance/sm6-a/<run-id>/`.

| Ветка | Режим | Первая попытка | Итог |
| --- | --- | --- | --- |
| `timeout` | HTTP | Истёк срок ожидания ответа оператора | Recovery, повтор, `Sent` |
| `exception` | Inprocess | Исключение оператора | Recovery, повтор, `Sent` |
| `cancellation` | Inprocess | Worker отменил вызов по бюджету закрепления | Recovery, повтор, `Sent` |
| `mismatch` | HTTP | HTTP 200 с несовпадающими `result/code` | Recovery, повтор, `Sent` |
| `rejection` | Inprocess | Подтверждённый отказ | `Failed` |

Тестовый барьер задерживает повторный захват, пока сценарий не увидит `Waiting`
и запись восстановления. PostgreSQL и Worker выполняют переходы по штатным
правилам. Сценарий проверяет новое закрепление, точную последовательность
записей журнала и `Completed` для каждой кампании. Нарушение любой проверки
даёт ненулевой код выхода.
