# Tasks

Все пункты — будущая реализация после отдельного запуска apply. Контракты: [spec](specs/runtime-performance-measurement/spec.md); решения и бюджеты: [design](design.md). Существующие тесты не реорганизуются и не удаляются. Числа CPU/RAM выясняются preflight; это не незакрытое архитектурное решение.

## 1. Формат и отключаемый контур измерений

- [x] 1.1 Добавить версионированные схемы manifest, interval, sample, lifecycle, metric definitions и примеры без пользовательских данных; проверка: валидный пример читается, неверные единицы/schema и отсутствующие обязательные поля отклоняются.
- [x] 1.2 Добавить явные настройки включения и no-op режим диагностики, SessionId/ProcessInstanceId и allowlist effective settings; проверка: disabled не создаёт файлов и не запускает сборщики, два старта дают разные instance IDs, секрет-маркеры не попадают в сериализацию.
- [x] 1.3 Реализовать monotonic windows, счётчики delta/cumulative и фиксированные bucket histograms по design; проверка: fake time, параллельная запись/закрытие окна, нулевое и частичное окна сохраняют count/sum и не теряют события.
- [x] 1.4 Подключить локальный MeterListener и изолировать ошибки callback от бизнес-вызовов; проверка: exception в диагностике не меняет исход вызываемой операции, число категорий и используемая память ограничены. Описать формат/единицы и включение в `scripts/performance/README.md`.

## 2. Подтверждения и полное окончание обработки

- [x] 2.1 Инструментировать IMessageSender и TryCompleteAsync, различить accepted/rejected/timeout/transport/etc и applied/not_applied/error/cancelled; проверка на управляемых sender/repository: только true сохранения увеличивает terminal.sent/failed.
- [x] 2.2 Зафиксировать исходы существующих веток DeliveryProcessingService без изменения его публичного контракта: pause, stale result, terminal_path_complete, error/cancel после applied; проверка: ошибка журнала после true сохраняет terminal counter и не увеличивает terminal_path_complete, Failed остаётся отдельным от Sent.
- [x] 2.3 Добавить таймеры journal.append по назначению, campaign.check и campaign.persist с исходами; проверка: контролируемые задержки каждой зависимости попадают только в соответствующий таймер и его родительские операции, no-op check не увеличивает campaign.completed.
- [x] 2.4 Документировать точки подтверждения, неизвестный commit и пересечение длительностей в каталоге метрик; проверка: для всех операторских и storage исходов есть однозначное определение и контрактная проверка, существующие релевантные unit tests сохраняют результат.

## 3. Захват, бюджет lease и recovery

- [x] 3.1 Добавить capture.storage/capture.total и calls/empty/rows с записью rows сразу при подтверждении репозитория; проверка: ошибка capture journal не скрывает уже захваченные строки и не считается пустым захватом.
- [x] 3.2 Добавить batch.wait, lease.check и processing.total/started в DeliveryWorker; проверка: последовательный пакет с fake time показывает возрастающее ожидание, пропуск lease не вызывает sender, wall-clock jump не меняет длительности.
- [x] 3.3 Добавить recovery.storage/recovery.total/calls/rows и различение отмены host/lease; проверка: SQL release succeeded + journal failure отражаются раздельно, recovery не превращается в число состоявшихся повторов и не приписывается неизвестному прежнему владельцу.
- [x] 3.4 Добавить итоговый read-only агрегат observed_repeat_captures по существующему журналу и описание его нижней границы; проверка на малой фикстуре: repeated capture, release без recapture и пропуск journal различаются, export не содержит отдельных DeliveryId/email/сообщений.

## 4. Файловый writer и частичные результаты

- [x] 4.1 Реализовать per-instance JSONL writer, atomic manifest replace, bounded queue, flush/rotation/лимит сеанса; проверка: закрытые окна читаются до финализации, overflow и disk error не блокируют обработку, память <=16 MiB на процесс, ранние файлы не удаляются.
- [x] 4.2 Реализовать bounded shutdown flush и marker завершения процесса; проверка: штатная остановка сохраняет последний partial interval в пределах 5 s и действующего shutdown budget, авария не создаёт искусственную длительность in-flight операций.
- [x] 4.3 Добавить reader префикса, duplicate/conflict detection, gaps/reset и checksums закрытых файлов; проверка: оборванная последняя строка допускается с partial status, повреждение середины/конфликт sequence выявляются, сохранённый префикс не изменяется.
- [x] 4.4 Документировать пределы потери хвоста и OS-cache при crash; проверка отдельным SIGKILL smoke: два сброшенных окна доступны офлайн, нет ложного completed; secret/payload canary отсутствует во всех файлах и безопасных сообщениях ошибок.

## 5. Ресурсы и очередь с ограниченной стоимостью

- [x] 5.1 Добавить выборочный сбор runtime/process CPU/RSS/heap/allocations/GC/ThreadPool и собственных затрат collector; проверка на живом процессе: единицы и reset определены, неподдерживаемая метрика missing, периоды и sample duration записаны; обновить каталог метрик.
- [x] 5.2 Добавить Docker snapshots для Worker/API/DB/HTTP/collector и Windows/VM inventory/samples с allowlist; проверка на Docker Desktop: зафиксированы backend, лимиты, cache semantics, bytes и CPU denominator, недоступный throttling/I/O помечен, полный inspect/env не экспортируется.
- [x] 5.3 Добавить агрегированные PostgreSQL activity/waits/database/WAL/I/O/table-size snapshots без query text; проверка: read-only роль/недостаточные права/reset статистики обрабатываются, collection connection исключён из рабочих, статистика не сбрасывается и timing не включается автоматически.
- [x] 5.4 Добавить scoped queue snapshots: Waiting eligible/total, InProgress/expired, Sent/Failed и oldest age; проверка: одна snapshot-граница, пустая очередь даёт null age, paused Waiting не выдаётся за доступную работу, recovery не сбрасывает смысл CreatedAt age.
- [x] 5.5 Ограничить время запросов и частоты по design, не допускать catch-up/retry storm; проверка: искусственно медленный запрос отменяется, следующий sample идёт по расписанию с gap/lag, collector cost виден. В runbook указать недоступные на этом backend источники.

## 6. Изолированный прогон Windows HTTP 300k

- [x] 6.1 Создать performance profile/Compose overlay и driver preflight с уникальным project/volume, Release images, bind output и безопасным inventory; проверка: dev project/БД не меняются, worker effective settings соответствуют manifest, права записи проверены до обработки.
- [x] 6.2 Создать отдельный параметризованный synthetic seed и API-формирование Draft кампании; проверка на малом объёме и 300k preparation: SQL подтверждает ровно заданное число Waiting и отсутствие сторонних кампаний, legacy seed не изменён, payload в export отсутствует.
- [x] 6.3 Реализовать ready barrier до API start, processing epoch, lifecycle/clock-offset probe и immutable profile; проверка: первая обработка наблюдается, подготовка не входит в processing throughput, запрос start имеет явные временные границы.
- [x] 6.4 Реализовать окончание по campaign Completed + 300k terminal, quiescence, final snapshot и раздельные workload/telemetry status; проверка малым HTTP smoke: нет завершения только по последнему terminal, сбой между commit/journal/check виден и не исправляется driver.
- [x] 6.5 Добавить stop по SessionId, Ctrl+C, stalled/completion_stalled и cleanup только принадлежащих сеансу ресурсов; проверка: graceful abort сохраняет файлы/БД, повтор создаёт новый session/project, stalled не выдаётся за успех и не перезапускает worker автоматически.
- [x] 6.6 Дополнить `scripts/performance/README.md` командами run/analyze/stop/cleanup, требованиями Docker Desktop/Python/диска, отключением sleep вручную и правилами интерпретации; проверка: выполнить команды малого smoke из чистого PowerShell без ссылок на отсутствующие docs/BMAD-каталоги.

## 7. Офлайн-анализ и графики

- [x] 7.1 Реализовать rate по фактическому duration, cumulative reconciliation с финальной БД, gaps/null/reset/clock-quality; проверка fixture с missing source/перезапуском/partial interval не создаёт нули или отрицательные скорости и не восстанавливает неизвестный хвост.
- [x] 7.2 Реализовать nearest-rank percentiles из buckets, merge только совместимых окон/распределений, bounds/overflow/малую выборку; проверка двух процессов с различными counts даёт percentile объединения, несогласованные окна не режутся пропорционально и не усредняют p95.
- [x] 7.3 Реализовать детектор warmup/queue sufficient/tail/stable candidate/baseline/slowdown candidate по сохраняемым параметрам design; проверка synthetic steady/slowdown/operator delay/empty queue/short run/gaps fixtures даёт ожидаемые метки, включая no_slowdown_detected и insufficient_data.
- [x] 7.4 Выдавать summary JSON, series CSV, offline HTML/SVG с общей шкалой elapsed/volume, разрывами и quality; проверка без БД/контейнеров и без сети воспроизводит графики из сохранённых файлов, исходные JSONL неизменны, гипотезы отделены от причин.
- [x] 7.5 Зафиксировать plotting dependencies и примеры анализа/ограничений в runbook; проверка: повторный анализ с той же версией/параметрами даёт те же численные результаты и предупреждения о несовместимой schema.

## 8. Сквозная приёмка и стоимость наблюдения

- [x] 8.1 Выполнить изолированную малую PostgreSQL+HTTP приёмку всех известных исходов, сверить applied/finished/journal/recovery с driver и БД; критерий: точное совпадение в нормальном сценарии, объяснённая неполнота при injected crash/unknown commit, неизменные бизнес-результаты и полнота manifest.
- [ ] 8.2 Выполнить два независимых worker процесса с общей SessionId для проверки instance mapping, объединения и crash prefix; критерий: результаты не смешаны, пропавший процесс даёт partial, никакого усреднения процентилей, прежние релевантные тесты не нарушены.
- [ ] 8.3 Провести минимум три чередующиеся пары off/on fresh 300k прогонов с одинаковыми seed/images/resources, сохранить сравнение throughput/p95/CPU/RAM/I/O/files и sample costs; критерий: бюджеты design соблюдены либо явный failed/inconclusive, все пары и разброс доступны. Эта оценка отдельна от одного пользовательского сеанса.
- [ ] 8.4 После успешной проверки стоимости выполнить первый профиль windows-http-300k и сохранить полный пакет; критерий: 300 000 terminal, campaign Completed, штатно сброшенные файлы, reconciliation без расхождения, достаточность наблюдений определена честно. Наличие замедления не требуется для приёмки.
- [ ] 8.5 Провести итоговый privacy/format review пакета и обновить runbook фактическими ограничениями стенда; критерий: нет секретов/сообщений/email/полного SQL, все обязательные метаданные и причины missing доступны, контрольная гипотеза содержит проверяемый следующий эксперимент, не утверждение причины по графикам.
