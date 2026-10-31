# Измерения PackCall

Массовые измерения не проходили приёмку стоимости. До пунктов 8.3–8.4 используйте
малые сеансы; наличие рабочего драйвера не является доказательством бюджета overhead.

## Команды

Требуются Windows, работающий Docker Desktop с Linux containers, Python 3.11+,
.NET SDK 10+ для локальных тестов, минимум 4 GiB свободного места (preflight floor,
не оценка достаточности места для полного сеанса). До измерения вручную отключите
sleep Windows и зафиксируйте фоновую нагрузку; driver настройки питания не меняет.

```powershell
python scripts/performance/run.py --profile small-http --count 30
python scripts/performance/run.py --profile small-http --count 60 --workers 2
python scripts/performance/analyze.py --session artifacts/performance/<session-id>
python scripts/performance/stop.py --session artifacts/performance/<session-id>
python scripts/performance/cleanup.py --session artifacts/performance/<session-id>
```

`run` создаёт отдельные project, volume, images и единственную кампанию. API публикуется
только на случайном localhost-порту, PostgreSQL не публикуется. Пароль живёт только
в окружении driver и конфигурации Docker; в measurement artifacts его нет. Compose
описание генерируется из `session.compose_profile` во временной папке; в артефактах
остаются только allowlisted inventory и registry ресурсов с session labels.
Release Dockerfiles переиспользуются без dev override. `profile.json` фиксируется
перед API start; изменения этого файла во время обработки прерывают сеанс.

Ready barrier ожидает файлы API и всех workers, затем сравнивает фактические worker
settings с manifest. `clock-initial.json`/`clock-final.json` сохраняют offset и RTT/2
для часов контейнеров и PostgreSQL. `processing-epoch.json` содержит границы запроса
start и бизнес StartedAt. Подготовка и пограничное окно не входят в processing rate.

Нормальное окончание требует и terminal count, и campaign Completed. Затем worker
останавливается, дожидаясь своих in-flight операций в рамках host shutdown; снимаются
final DB/journal/repeat aggregates. 120 s без прогресса дают stalled; при всех terminal
и незавершённой кампании — completion_stalled. Driver не исправляет статусы и не
перезапускает worker. Workload status и telemetry status независимы.

Ctrl+C или `stop` сохраняют файлы и volume. `cleanup` проверяет labels всех известных
контейнеров и volume до удаления, артефакты сохраняет. Повторный run всегда создаёт
новый session UUID. Не используйте dev compose project для этого workflow.

Подготовка без отправок (проверка 6.2):

```powershell
python scripts/performance/run.py --profile windows-http-300k --prepare-only
```

Она создаёт 300000 Waiting в Draft, не создаёт worker и не вызывает start. Полный
`run --profile windows-http-300k` оставлен для отдельного разрешённого этапа 8.4
после off/on проверки 8.3; здесь он не выполнялся.

## Офлайн-анализ

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
python -m pip install -r scripts/performance/requirements.txt
python -m unittest discover -s scripts/performance -p 'test_*.py' -v
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
Это конструктивная граница; накладные расходы всего процесса проверяются отдельно
в 8.3. При shutdown очередь сбрасывается в пределах 5 s и оставшегося бюджета host.

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
python scripts/performance/crash_smoke.py
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
python scripts/performance/resource_smoke.py
```

Проверено на Docker Desktop WSL2: VM 16 CPU и 22 435 520 512 bytes RAM на момент
smoke. Это снимок, driver обязан обновлять inventory перед каждым сеансом.
Недоступны в текущем collector: VM swap, cgroup throttling, Windows disk bytes/s,
runtime exception count. Docker CLI I/O округлён; нет точности raw kernel counters.
Нет отдельного collector-контейнера: collector — Python-процесс Windows с собственными
CPU/RSS samples. Тест проверяет read-only роль, отказ в доступе, отмену pg_sleep по
statement_timeout, paused/empty queue и неизменность CreatedAt age после recovery.
БД фикстуры отдельная, Docker порты не публикуются.
