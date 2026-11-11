# Измерения PackCall

## Исторический сеанс на 300 000 доставок

Сеанс `15369c7c-df29-464e-9f99-cc8a9ee5f275` по профилю `windows-http-300k`
выполнен на коммите `8397dd105dd2c68886b0a5fa3bbe4cb1a36b9ec6`.
Исходные артефакты хранятся локально вне Git в
`artifacts/performance/15369c7c-df29-464e-9f99-cc8a9ee5f275/`.

- `manifest.json` фиксирует SHA в `commit` и один Worker в `participants`.
- `profile.json` фиксирует `workers=1`, `settings.operator_mode=http` и
  `settings.simulate_errors=false`. HTTP-запросы обслуживал локальный тестовый
  email-оператор `PackCall.TestEmailOperator` в Docker-сервисе `http`.
- `final.json` фиксирует 300 000 `Sent` (`sent`) и 0 `Failed` (`failed`).

Интервал по монотонному полю `elapsed_start_s` событий driver в `lifecycle.jsonl`
от `processing` до `draining` составил `1006.625 − 140.781 = 865.844 с`.
Начало соответствует границе запроса запуска `elapsed_start_s` в
`processing-epoch.json`.
Метка `draining` показывает момент, когда driver при опросе увидел, что все
доставки в терминальных статусах и кампания в `Completed`. Точное время
последнего `Sent` эта метка не фиксирует.

Средняя скорость за измеренный интервал получена делением
`300000 / 865.844 ≈ 346 доставок/с`. Часовая экстраполяция при той же скорости
составляет `300000 / 865.844 × 3600 ≈ 1,25 млн доставок/ч`.
Часового прогона на 1 млн доставок не было. Производительность 1 млн доставок/ч
не сертифицирована.

В `summary.json` поле `quality` содержит `partial`, а `detection.status` равно
`insufficient_data`.
Данных недостаточно для определения стабильного уровня скорости и выявления замедлений.

Для воспроизведения исторического сценария запустите команду на чистом checkout
коммита `8397dd105dd2c68886b0a5fa3bbe4cb1a36b9ec6`.

```powershell
python scripts/performance/run.py --profile windows-http-300k --workers 1
```

## Статус критериев MEAS

Входной baseline Story 2.1 выполнен по профилю `windows-http-300k` на теге
`v0.5.0-beta.2`. Медиана контрольных `off` повторной серии — 644,96 доставки/с.
Команды, границы окон и результаты приведены в
[отчёте Story 2.1](../../docs/evidence/beta2/300k-baseline.md).

| Критерий | Статус |
| --- | --- |
| MEAS-1 — два независимых Worker в общей сессии | Пройден. В каждом сеансе 300k оба Worker внесли вклад, журнал и итог БД сверены. |
| MEAS-2 — парные прогоны с выключенными и включёнными измерениями | Пройден. Выполнены две серии по три пары throughput и две пары с одинаковым зондом для p95. |
| MEAS-3 — бюджет накладных расходов | Пройден после повтора. Медиана потери throughput 2,72%, рост p95 +1,05% и +5,08%. Память, CPU и файлы в бюджете. |
| MEAS-4 — проверка privacy и формата артефактов | Пройден. Проверены форматы, контрольные суммы и содержимое 496 файлов. |
| MEAS-5 — новый профиль на 1 млн доставок | Ожидает отдельного 1M-сеанса. Этот прогон относится только к MEAS-5. |

Если бюджет MEAS-3 на baseline не выполнен, старт `beta.3` блокируется до
успешного повторного прогона или решения владельца, зафиксированного в ADR.
Бюджет MEAS-3 и профиль на 1 млн доставок описаны в
[ADR 0003](../../docs/adr/0003-telegram-modes-1m-profile.md).

Небольшие smoke-прогоны проверяют работу драйвера и сборщиков. Статус MEAS-1 и
MEAS-2 подтверждён полными 300k-сеансами.

## Команды

Выполняйте команды PowerShell из корня репозитория. Для офлайн-анализа нужны
Python 3.11+ и зависимости из `requirements.txt`.

```powershell
python -m pip install -r scripts/performance/requirements.txt
```

Для запуска сеансов дополнительно нужны Windows, Git, .NET SDK 10+ и работающий
Docker Desktop с Linux-контейнерами. Driver проверяет минимум 4 GiB свободного
места перед запуском. Полный сеанс может потребовать больше места. До измерения
вручную отключите переход Windows в спящий режим и зафиксируйте фоновую нагрузку.

Пример малого сеанса с двумя Worker. Driver выводит путь к папке после завершения.
После успешного прогона можно проанализировать его и удалить его Docker-ресурсы.

```powershell
$smokeSession = python scripts/performance/run.py --profile small-http --count 60 --workers 2
if ($LASTEXITCODE -ne 0) { throw 'Не удалось выполнить smoke-прогон' }
$manifest = Get-Content -Raw -LiteralPath (Join-Path $smokeSession 'manifest.json') -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
if ($manifest.workload_status -ne 'completed') {
    throw "Статус сеанса $($manifest.workload_status). Docker-ресурсы сеанса сохранены."
}
python scripts/performance/analyze.py --session "$smokeSession"
if ($LASTEXITCODE -ne 0) { throw 'Ошибка анализа. Docker-ресурсы сеанса сохранены.' }
python scripts/performance/cleanup.py --session "$smokeSession"
```

Для остановки активного сеанса из другого окна PowerShell найдите его контейнеры.
Введите UUID из метки `packcall.performance.session` выбранного сеанса. Пример
использует каталог по умолчанию `artifacts/performance`.

```powershell
docker ps --filter 'label=packcall.performance.session' --format 'table {{.Names}}\t{{.Labels}}'
$activeSessionId = [guid](Read-Host 'UUID активного сеанса')
$activeSession = Join-Path 'artifacts/performance' $activeSessionId
python scripts/performance/stop.py --session "$activeSession"
```

`run` создаёт отдельные project, volume, images и единственную кампанию. API публикуется
только на случайном localhost-порту, PostgreSQL не публикуется. Пароль живёт только
в окружении driver и конфигурации Docker; в measurement artifacts его нет. Compose
описание генерируется из `session.compose_profile` во временной папке; в артефактах
остаются только allowlisted inventory и registry ресурсов с session labels.
Release Dockerfiles переиспользуются без dev override. `profile.json` фиксируется
перед запросом запуска кампании. Изменения этого файла во время обработки прерывают сеанс.

Ready barrier ожидает файлы API и всех workers, затем сравнивает фактические worker
settings с manifest. `clock-initial.json`/`clock-final.json` сохраняют offset и RTT/2
для часов контейнеров и PostgreSQL. `processing-epoch.json` содержит границы запроса
start и бизнес StartedAt. Подготовка и пограничное окно не входят в processing rate.

Нормальное окончание требует и terminal count, и campaign Completed. Затем worker
останавливается, дожидаясь своих in-flight операций в рамках host shutdown; снимаются
final DB/journal/repeat aggregates. 120 s без прогресса дают stalled; при всех terminal
и незавершённой кампании — completion_stalled. Driver не исправляет статусы и не
перезапускает worker. Workload status и telemetry status независимы.

Ctrl+C или `stop` сохраняют файлы и volume. `cleanup` проверяет метки и удаляет
контейнеры и volume выбранного сеанса. Артефакты, Docker-сеть и образы сохраняются.
Повторный run всегда создаёт новый session UUID. Не используйте dev compose project
для этого workflow.

Эта команда проверяет подготовку 300k-кампании без отправок.

```powershell
$preparedSession = python scripts/performance/run.py --profile windows-http-300k --prepare-only
if ($LASTEXITCODE -ne 0) { throw 'Не удалось подготовить сеанс' }
Get-Content -Raw -LiteralPath (Join-Path $preparedSession 'initial.json') -ErrorAction Stop
```

Команда создаёт 300 000 доставок в статусе `Waiting` в кампании `Draft`
и завершает подготовку до запуска Worker и кампании. CLI `run.py` запускает
обработку со включённым сбором измерений. Для пар off/on используется
[`baseline.py`](baseline.py) по [протоколу Story 2.1](../../docs/evidence/beta2/300k-baseline.md#воспроизведение).

После проверки `initial.json` при необходимости удалите Docker-ресурсы этого сеанса.

```powershell
python scripts/performance/cleanup.py --session "$preparedSession"
```

## Офлайн-анализ

Для примера нужны сохранённые артефакты исторического сеанса по указанному пути.
`analyze` перезаписывает производные отчёты в папке сеанса. Чтобы сохранить
исторические отчёты, запустите анализ на копии.

```powershell
$historicalSession = Resolve-Path -LiteralPath 'artifacts/performance/15369c7c-df29-464e-9f99-cc8a9ee5f275' -ErrorAction Stop
$analysisSession = Join-Path ([System.IO.Path]::GetTempPath()) ("packcall-perf-" + [guid]::NewGuid())
Copy-Item -LiteralPath $historicalSession.Path -Destination $analysisSession -Recurse -ErrorAction Stop
python scripts/performance/analyze.py --session "$analysisSession"
```

`analyze` читает сохранённые JSONL и initial/final snapshots, создаёт summary JSON,
series CSV, resource-series CSV, HTML и SVG без CDN и без БД. Plotting реализован
стандартной библиотекой Python; единственная внешняя зависимость — зафиксированный
jsonschema для валидации. Версия анализатора хранится в summary.
Отдельные series каждого instance не выдаются за общий throughput. Совместимые
полные histogram windows объединяются по counts с допуском границ 1 s; другие
остаются отдельными. p95 хранит bucket bounds/overflow/count, n<20 помечается.
Скорость partial window делится на фактическую monotonic duration. Отсутствующий
источник и reset не заменяются нулями. Cumulative reconciliation показывается
отдельно от суммы доступных interval deltas; неизвестный хвост не восстанавливается.
Ресурсные точки и observed volume имеют общую elapsed шкалу; p95 показан границами
корзины. Короткие smoke обычно дают insufficient_data, это ожидаемо.

Сводные 60-s окна требуют полного покрытия всех workers и достаточной очереди по
соседним snapshots. Baseline — первые пять стабильных окон, slowdown — три окна
ниже порога. Summary сохраняет чувствительность warmup 30/60/120 s и порогов
5/10/20%. Эти метки — эвристика, не доказательство причины.

## Контракт v1

`schemas/*.v1.json` — JSON Schema 2020-12 для manifest, interval, sample,
lifecycle и metric definitions. `examples/` содержит только синтетические данные.
`contract.validate` дополнительно проверяет связи полей: длительность и monotonic
границы, count/buckets, пустые распределения, missing и единицы terminal.
Ошибки возвращают фиксированный код, без значений входного документа.

```powershell
python -m pytest tests/tools/performance -q
```

Единицы: длительности операций — ms, интервалы — s, CPU — s/cores,
память и I/O — bytes, доставки — deliveries. Missing означает неизвестное
значение с причиной; это не ноль. Для CPU denominator хранится в определении.
Гистограммы имеют отдельную корзину нуля, границы `0.01 * 1.1^k` до
первой границы >=600000 ms и overflow. Count включает overflow; min/max/sum
сохраняются без округления. Процентили должны использовать nearest-rank и
объединённые counts совместимых окон, а не среднее p95.

## Локальный сборщик

`MeasurementOptions.Enabled` по умолчанию false. В отключённом режиме
`MeasurementCollector` не создаёт listener, таймеры или файлы и возвращает null
вместо окна. Включение требует SessionId, OutputDirectory и положительный Interval.
Worker и API подключают host только при Enabled=true. Пример параметров:
`PerformanceMeasurement__Enabled=true`, `PerformanceMeasurement__SessionId=<UUID>`,
`PerformanceMeasurement__OutputDirectory=<папка сеанса>`,
`PerformanceMeasurement__Interval=00:00:10`.
ProcessInstanceId создаётся заново для каждого экземпляра; PID не используется
как идентичность. `EffectiveMeasurementSettings` содержит только разрешённые
поля, без connection strings, адресов, сообщений и exception text.

События поступают через `PackCall.Performance` Meter. Обработка callback защищена
от исключений, категории задаются enum. На hot path нет I/O. Счётчики и
гистограммы закрываются под общей блокировкой; событие относится к окну, в котором
оно записано. Счётчики сохраняют delta и cumulative в пределах экземпляра.
Закрытие пустого окна сохраняет нули известных серий. Время измеряется TimeProvider
monotonic clock; отклонение UTC более секунды отмечает clock jump. Частичное окно
хранит фактическую длительность. Максимум 512 серий на процесс; превышение
учитывается как dropped measurements. Этот предел ограничивает массивы гистограмм,
очередь writer дополнительно ограничена тремя записями не более 1 MiB каждая.

## Writer и аварии

У каждого instance своя папка `processes/<UUID>`. `process.json` содержит безопасное
отображение роли/worker ID и готовность; `stopped.json` создаётся после shutdown flush.
JSONL flush выполняется для каждого закрытого окна. Ротация — 32 MiB, общий
бюджет записей сеанса — 2 GiB, ранние файлы не удаляются. Межпроцессная reservation
хранится в `.byte-budget`. Переполнение очереди теряет интервал и увеличивает
dropped; disk error или невозможность reservation прекращает writer, а не обработку.
Файлы и checksum заменяются атомарно. Только закрытые файлы получают SHA-256.

Объём удерживаемых данных ограничен: три буфера по 1 MiB, один текущий буфер,
active/closed массивы максимум 512 гистограмм по 191 элементу, словари и метаданные.
Накладные расходы всего процесса проверяются на baseline по MEAS-3.
При shutdown очередь сбрасывается в пределах 5 s и оставшегося бюджета host.

SIGKILL может потерять текущее открытое окно, три ожидающих записи и текущую
запись writer. Без задержек типичная потеря — текущее 10-s окно; при overflow нет
фиксированной гарантии в секундах. Последняя читаемая граница является пределом
наблюдения. Flush оставляет данные в OS cache: авария Windows/VM/питания может
потерять и этот префикс. Fsync не включён.

`reader.py` допускает только оборванную последнюю строку; повреждение полной
строки, конфликт sequence и неверный checksum — ошибки. Одинаковые дубли
дедуплицируются. Gaps, reset и отсутствие stop marker сохраняются как quality.
Reader не изменяет исходные файлы и не восстанавливает хвост нулями.

```powershell
python tests/acceptance/measurements/crash_smoke.py
```

Этот малый smoke собирает Release probe, запускает отдельный контейнер .NET,
ждёт два окна по 200 ms и отправляет SIGKILL. Он не создаёт доставок, оставляет
артефакты в `artifacts/performance/<UUID>` и удаляет только собственный контейнер.

## Сбор ресурсов и границы стенда

Runtime sample: 5 s; Docker: 5 s; Windows/PostgreSQL: 15 s; queue: 30 s;
table stats: 60 s. SQL выполняется в read-only transaction, queue timeout 1000 ms,
остальная статистика 500 ms. Один внешний scheduler вызывает collectors последовательно;
после sample следующий срок рассчитывается от его окончания. Catch-up и немедленных
повторов нет. Duration, lag, CPU/RSS collector входят в sample; пропущенный запрос
сохраняет missing с причиной. Смена stats_reset/server_start отмечается reset.

```powershell
python tests/acceptance/measurements/resource_smoke.py
```

Проверено на Docker Desktop WSL2: VM 16 CPU и 22 435 520 512 bytes RAM на момент
smoke. Это снимок, driver обязан обновлять inventory перед каждым сеансом.
Недоступны в текущем collector: VM swap, cgroup throttling, Windows disk bytes/s,
runtime exception count. Docker CLI I/O округлён; нет точности raw kernel counters.
Нет отдельного collector-контейнера: collector — Python-процесс Windows с собственными
CPU/RSS samples. Тест проверяет read-only роль, отказ в доступе, отмену pg_sleep по
statement_timeout, paused/empty queue и неизменность CreatedAt age после recovery.
БД фикстуры отдельная, Docker порты не публикуются.
