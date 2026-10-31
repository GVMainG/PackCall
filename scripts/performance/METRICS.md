# Каталог метрик v1

Все длительности операций — ms, события — целые неотрицательные counts.
`SeriesKey` состоит только из enum metric/outcome/purpose. Сеанс и процесс
относятся к envelope, идентификаторы доставок никогда не являются метками.

| Счётчик | Подтверждение и смысл |
|---|---|
| TerminalApplied / Sent, Failed | `TryCompleteAsync == true`, до следующей проверки cancellation. Sent означает приём оператором, не доставку в ящик |
| OperatorFinished | Возврат sender или исключение: Accepted, Rejected, Validation, Service, Protocol, Transport, Timeout, Cancelled, Error |
| ResultFinished | Applied=true, NotApplied=false, Error/Cancelled — отсутствие наблюдённого подтверждения |
| ServiceStarted | Вход ProcessAsync, включая ошибочный вход |
| ProcessingFinished | TerminalPathComplete после persistence+journal+campaign check; StaleResult при false; PausedRelease/PausedNoRelease; Error/Cancelled |
| AfterTerminalApplied | Ошибка/отмена после true persistence; terminal counter сохраняется |
| JournalFinished | Success/Error/Cancelled, назначение Capture/Terminal/Recovery/Pause |
| CampaignChecks | Вход проверки кампании, в том числе no-op |
| CampaignCompleted | Только true условного сохранения Completed |

| Таймер | Граница |
|---|---|
| OperatorCall | Вход IMessageSender.SendAsync — результат/исключение; включает сериализацию, сеть, обработку ответа |
| ResultPersist | Вход TryCompleteAsync — true/false/исключение; ожидание соединения и SQL включены |
| JournalAppend | Вход AppendAsync — завершение его Task, с назначением |
| CampaignCheck | Вход CompleteCampaignIfFinishedAsync — возврат/ошибка, включая чтение и pending check |
| CampaignPersist | Только условная запись Completed — подтверждение/отказ/ошибка |

Отказы оператора Failed могут завершить всю терминальную ветку без технической
ошибки. Error/Cancelled persistence не доказывают rollback: commit мог произойти
до потери ответа. Счётчики фиксируют только наблюдённые подтверждения. Финальная
БД необходима для reconciliation; неизвестный остаток нельзя распределять по окнам.
Журнал и terminal counter не атомарны с commit. Ошибка журнала не отменяет terminal.

Измерение завершается в finally при обычном возврате/исключении. SIGKILL не даёт
длительности незаконченной операции. Операция целиком попадает в окно окончания.
Родительский CampaignCheck включает CampaignPersist; ProcessingTotal будет
включать операции сервиса и lease check. Складывать их как независимые затраты
нельзя. Параллельные lanes могут суммарно занять больше стеночного времени.

Для operator исключения инфраструктуры реализуют `IMeasuredFailure`; тип,
сообщение и stack исключения в метрики не копируются. Неизвестное исключение
попадает в Error. Отмена sender — Cancelled; причина host/lease относится к worker.

## Захват и recovery

CaptureCalls/RecoveryCalls считаются на входе сервиса. CaptureStorage и
RecoveryStorage заканчиваются при материализованном возврате репозитория;
CaptureRows/RecoveryRows записываются сразу, прежде cancellation/journal.
CaptureEmpty увеличивается только при успешном возврате пустого пакета.
CaptureTotal/RecoveryTotal включают журнал и заканчиваются в finally.

BatchWait начинается сразу после возврата репозитория, включает capture journal
и предыдущие строки пакета. Ссылка на доставку хранится только слабо и удаляется
при начале обработки. ProcessingStarted — вход ProcessCapturedAsync.
LeaseCheck включает получение времени БД и расчёт остатка; ProcessingTotal
включает lease check и сервис, но не BatchWait. InvalidCapture/LeaseSkipped не
вызывают sender. Cancellation различает Host/Lease/Other.

`sql/repeat_captures.sql` — итоговый read-only запрос с timeout 500 ms, scoped
по campaign UUID. Суммирует `max(capture_count - 1, 0)` по существующему журналу.
RecoveryRows не означает новую попытку. Прежний владелец recovery не выводится.
Отсутствие capture entries занижает observed_repeat_captures; результат всегда
lower_bound с journal_non_atomic=true, без individual DeliveryId/email/payload.
Малая проверка: `python scripts/performance/test_postgres_repeat.py`.

## Ресурсные samples

Каждый runtime sampler имеет отдельный instance UUID, связанный с process UUID.
CPU seconds, allocations, GC collections и GC pause seconds накопительны в пределах
process instance; новый процесс начинает отдельную серию. RSS/private/heap — bytes,
ThreadPool threads и pending work items — gauge. CPU cores вычисляются из delta
CPU seconds / фактические elapsed seconds, denominator — одно ядро. Не вычитать
значения разных instance. Runtime exceptions пока missing/unsupported.

Runtime снимается каждые 5 s после окончания предыдущего sample. Collector duration
измеряется монотонно; collector CPU для встроенного sampler — изменение CPU всего
процесса за sample (включает конкурентную бизнес-работу, не точная самооценка).
Внешний Python collector сохраняет собственные CPU/RSS; CPU дочерних docker/psql/
PowerShell команд не входит в Python process CPU, но их ожидание входит в duration.

Docker CPU — cores, 100% Docker CLI означает одно ядро; Linux memory usage CLI
исключает cache. Network/block counters — округлённые CLI bytes, не точный raw I/O.
Restart/OOM/running/health сохраняются отдельно. У отсутствующего healthcheck
healthy=null/unsupported. Inventory экспортирует только выбранные поля inspect.

PostgreSQL database/WAL/I/O counters накопительны до соответствующего stats_reset;
server_start и отдельные reset tokens сохраняются в context. Изменение token
помечает sample reset и запрещает разность с предыдущим. Activity — снимок числа
соединений по state/wait categories, не длительность ожидания. Collector connection
исключается по application_name. hidden_connections показывает ограничение прав.
Table live/dead tuples — оценки; размеры — bytes; vacuum/analyze timestamps — epoch s.
Статистика не сбрасывается, track_io_timing/track_wal_io_timing не включаются.
Основание: [PostgreSQL 18 statistics](https://www.postgresql.org/docs/18/monitoring-stats.html).
