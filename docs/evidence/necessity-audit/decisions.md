# Решения necessity-аудита

Исторические наблюдения получены 2 ноября 2026 года на `72228dd69a81f9eb90eb95978a8a3aceba89fe26`. Подтверждённые исправления и результаты последующих прогонов приведены со своими evidence. C# baseline от 12 ноября выполнен отдельно. [Журнал прогонов](execution-record.md).

| ID | Наблюдение 2 ноября | Решение на 12 ноября |
|---|---|---|
| RUN-1 | `operators/run.py` прошёл проверки отсутствующих и пустых обязательных ключей и оба режима оператора. | **Исторический PASS** для указанного профиля `COMPOSE_ENV_FILES`. [Исторический прогон](execution-record.md#исторический-аудит) |
| RUN-2 | Startup и direct shutdown probes заблокированы из-за отсутствующих ключей режима. | **PASS** после исправления прямых запусков. `startup.py` и `shutdown-probes.py` завершились с exit 0. [RUN-2](../beta2/RUN-2.md) |
| RUN-3 | `images.py` прошёл. Для Worker отдельно воспроизведён `FileNotFoundException` при отсутствии конфигурационного файла. | **Исторический PASS** для image-проверок и диагностики Worker. Причина отказа на всех трёх образах остаётся предложенным усилением проверки. [Исторический прогон](execution-record.md#исторический-аудит), [UNC-06](uncertainties.md) |
| RUN-4 | Пустой `TEST_SIMULATE_ERRORS` приводил к ошибке Compose. | **PASS**. В Compose пустое значение означает «имитация выключена». `config_matrix.py` завершился с exit 0. [RUN-4](../beta2/RUN-4-GAP-01.md) |
| RUN-5 | Проверка владельца после рестарта API упала. К концу исторического прогона все 1000 доставок были в Sent. | **PASS** для сравнения `worker_id`, который возвращает запрос к одной строке InProgress до и после рестарта API. Поздняя диагностика подтвердила гонку в сценарии. Mock-оператор завершал доставки за время рестарта, поэтому запрос возвращал пустой результат. Коммит `00343305d8145cad7aa76addcb3e79b0fe4cba29` останавливает Worker перед рестартом API. `lifecycle.py` завершился с exit 0, `apiRestartWorkerUnchanged=true`, итог — 1000 доставок в Sent. Время graceful shutdown измерялось отдельными probes в RUN-2. [RUN-5](../beta2/RUN-5.md), [RUN-2](../beta2/RUN-2.md) |
| RUN-6 | `late_http.py` завершился по тайм-ауту на маркере из-за cp1251 в исходнике Worker. | **PASS** на исправленном Worker, повтор дошёл до проверок позднего ответа, восстановления и fence. [RUN-6](../beta2/RUN-6.md) |
| GAP-01 | Предикат `lifecycle.py` искал `delivery_status=3` при `Failed=-1`. | **PASS** для исправленного lifecycle-предиката и enum-контракта. Исправление `6af6d8900d143f5f096f04620370fe0e541b987a`, тест `a5d68e927c9e1385fee96d9886cfe8c6ca1b5a83`. Тест прошёл в Unit baseline, lifecycle прошёл в RUN-5. Проверка журнального `Failed=-1` в shutdown probe остаётся открытой. [GAP-01](coverage-gaps.md), [RUN-4/GAP-01](../beta2/RUN-4-GAP-01.md), [RUN-5](../beta2/RUN-5.md) |

Решения по GAP-02 и GAP-04 приняты в Story 1.12. Статусы проверок и результаты stall smoke приведены в отчёте о пробелах. [Пробелы](coverage-gaps.md).

В блоке воспроизведения RUN-2 используется перенос строки Bash через `\` и переменная PowerShell `$env:TEMP`. Для PowerShell выполните `docker exec` одной строкой и сохраните синтаксис пути публикации probe. Остальные команды и предусловия остаются в отчёте RUN-2. [RUN-2](../beta2/RUN-2.md#команда-воспроизведения).

```powershell
docker exec packcall-w21-packcall.db-1 psql -U packcall -d packcall -Atc "INSERT INTO recipients(id,email) VALUES(gen_random_uuid(),'probe@example.test') ON CONFLICT DO NOTHING"
dotnet publish tests/PackCall.Worker.Probe/PackCall.Worker.Probe.csproj -c Release -o $env:TEMP/packcall-w21-probe
```

Исторический аудит не обосновал удаления или объединения сценариев. Для рассмотренных пар отсутствует доказательство эквивалентности условий, дефектов и наблюдаемых эффектов. Мутации для такой проверки не выполнялись.

## GAP-02

**Решение принято для 0.5. Реализация ожидается** в Story 2.14 после входного шлюза Story 2.1. Сейчас локальный HTTP-тест проверяет путь и флаги запроса, а возвращённые `SendResult` остаются без проверок. [Наблюдение GAP-02](coverage-gaps.md#gap-02), [текущий тест](../../../tests/PackCall.Test/Integration/Operators/OperatorHttpTests.cs#L35).

- Сохранить проверку пути `/v1/email/send` и последовательности `simulate_errors` (`false, false, true, true`). Для каждого значения флага проверить преобразование обоих HTTP 200 из ожидаемой пары `result/code` в `SendResult`. Для `accepted/AcceptedByProvider` ожидать `IsSuccess=true` и `Error=null`, для `rejected/SimulatedOperatorRejected` — `IsSuccess=false` и `Error=SimulatedOperatorRejected`. Если адаптер всегда возвращает success, тест должен упасть на подтверждённом отказе.
- По контракту PRD FR-14 тест `ErrorsNeverBecomeConfirmedResults` уже ожидает `TestOperatorProtocolException` для HTTP 200 с парой `accepted/SimulatedOperatorRejected`. В Story 2.14 сохранить эту проверку и добавить обратную пару `rejected/AcceptedByProvider` с тем же ожидаемым исключением. Оба несовпадения означают неизвестный исход. [Существующая проверка](../../../tests/PackCall.Test/Integration/Operators/OperatorHttpTests.cs#L57).
- По FR-10 и AD-4 подтверждённый приём разрешает `Sent`, подтверждённый отказ — `Failed`. Даже при позднем подтверждении запись допустима только пока строка в `InProgress`, её `worker_id` и `lock_expires_at` совпадают с закреплением отправителя и `lock_expires_at > clock_timestamp()` после блокировки строки в PostgreSQL. При неизвестном исходе доставка остаётся без терминального статуса и доступна для восстановления после истечения закрепления. [Условие записи](../../../src/PackCall.Infrastructure/Repositories/DeliveryRepository.cs#L189), [матрица гарантий](../../guarantees.md).

## GAP-04

**Решение принято для 0.5. Реализация ожидается** в Story 2.14 для доставки и Story 2.15 для кампании после входного шлюза Story 2.1. Полные таблицы допустимых переходов входят в доменный контракт AD-4. Сейчас workflow-тесты проверяют отдельные переходы, а `DeliveryStatusContractTests` закрепляет числовые значения статусов. Прямой матрицы пар пока нет. [Наблюдение GAP-04](coverage-gaps.md#gap-04), [тест числовых значений](../../../tests/PackCall.Test/Unit/Deliveries/DeliveryStatusContractTests.cs).

- Для `DeliveryStatusTransitions` взять ожидаемые пары из FR-8, FR-10 и FR-11. Разрешены `Waiting → InProgress`, `InProgress → Sent`, `InProgress → Failed` и `InProgress → Waiting` при паузе или восстановлении. Из терминальных `Sent` и `Failed` выходов нет. [Текущая таблица доставки](../../../src/PackCall.Core/Entities/DeliveryStatus.cs).
- Для `CampaignStatusTransitions` взять ожидаемые пары из правил формирования и жизненного цикла по FR-6, FR-7, FR-12 и FR-13. Разрешены `Draft → Forming`, `Forming → Draft`, `Draft → InProgress`, `InProgress → Paused`, `Paused → InProgress` и `InProgress → Completed`. Возобновление с завершением проходит через `InProgress`. Из терминального `Completed` выходов нет. У недостижимого `CampaignStatus.Failed=-1` нет входящих и исходящих переходов. [Текущая таблица кампании](../../../src/PackCall.Core/Entities/CampaignStatus.cs), [ADR 0001](../../adr/0001-contact-model-selection-snapshot.md#решение-по-campaignstatusfailed-1-ad-5).
- Задать ожидаемые пары в независимых тестовых фикстурах по указанным правилам PRD. Брать ожидаемые результаты только из этих фикстур. Для каждой пары исходного и целевого статусов проверить `CanTransition`, для каждого исходного статуса проверить точное множество `GetAllowedTargets`. Включить неизвестное значение enum как источник и как цель. Таблицы запрещают переход в тот же статус. Идемпотентность методов жизненного цикла проверяется отдельно по FR-7. Проверки SQL, защиты от записи по чужому или истёкшему закреплению и восстановления по-прежнему подтверждают поведение при записи и обработке доставки. [Матрица гарантий](../../guarantees.md).

## Рассмотренные кандидаты

Ниже сохранены рассмотренные кандидаты 2 ноября. Пути `PackCall.Test/` относятся к историческому checkout. `inventory.md`, `inventory-checks.json`, `inventory-files.json`, `guarantee-map.md`, `preservation-plan.md` и `raw/*.trx` относятся к внешнему архиву. [Расположение архива](README.md#где-хранятся-внешние-артефакты).

### C-01 — убрать fake pause workflow после PostgreSQL pause

- Вид: scenario, uncertain → retain—complementary layer.
- Что исчезло бы: `PackCall.Test/Unit/Campaigns/CampaignWorkflowTests.cs::PauseDuringLastSend_AllowsAcknowledgementAndResumeCompletesCampaign()`.
- Предполагаемая замена: `PackCall.Test/Integration/Campaigns/CampaignPausePostgresTests.cs::PauseOnLastDelivery_ResumeCompletesWithoutSendingAgain()`; **не** одноимённый batch-сценарий как автоматическая замена.
- G-02/F-02: преждевременное completion при paused, повторная отправка при resume, разрешённое удаление paused-кампании.
- Одинаково: одна доставка, InProgress, sender удерживается TaskCompletionSource, pause после входа в sender, accepted после pause, resume, ровно один send; исходный StartedAt сохраняется в PG oracle.
- Различия: fake repositories и одна логическая модель против PG/EF и нескольких scopes; unit дополнительно проверяет `DeleteCampaignAsync` → InvalidOperationException. PG проверяет пустой повторный capture, fresh stored row, StartedAt. Union не совпадает. HTTP pause проверяет DELETE=409, но это другой путь с handler и не эквивалентная проверка service exception.
- Baseline: оба прошли, raw/unit.trx и raw/integration.trx. Fault challenge отсутствует. Недостаёт доказательства, что дефект paused-delete обязательно обнаружит точная остающаяся проверка, а также решения, можно ли убрать быстрый fake feedback.
- Польза: минус один небольшой сценарий; влияние на время не измерялось. Риск: потеря отдельного service oracle/локализации. Сохранить оба. Перед любым будущим merge перенести delete assertion и выполнить P-01.

### C-02 — заменить unit исключения оператора PostgreSQL-матрицей

- Вид: scenario, retain—complementary layer.
- Точные кандидаты: `PackCall.Test/Unit/Deliveries/DeliveryProcessingServiceTests.cs::Process_WhenOperatorThrows_PreservesLockForRecovery()` и `Process_OperatorCancellation_DoesNotBecomeFailure()`.
- Предполагаемая замена: `PackCall.Test/Integration/Operators/OperatorPostgresTests.cs::OperatorErrorsPreserveLeaseWithoutTerminalJournal(failure,simulate)` — все 14 вариантов (`422,415,500,malformed,disconnect,refused,timeout` × false/true).
- G-01/G-06/F-06: неизвестный результат ошибочно становится terminal, теряется lease, появляется terminal journal или запускается completion.
- Различия: произвольный InvalidOperationException и OperationCanceledException fake sender против типизированных ошибок реального HTTP adapter; PG timeout — внутренний deadline, не внешняя cancellation. Unit проверяет отсутствие completion-check вызовов (`PendingChecks=0`), PG вызывает completion явно и проверяет false. Одинаковое конечное состояние не доказывает одинаковый orchestration путь.
- Оба набора прошли. `OperatorHttpTests.ResponseLimitTimeoutAndExternalCancellation` проверяет отмену в HTTP-адаптере. PostgreSQL-сценарий `LateHttpResponseAfterLeaseLossAndCancellationCannotOverwriteNewOwner` проверяет внешнюю отмену после истечения лизинга и неизменность строки и журнала после позднего ответа при новом владельце. Unit сохраняет отдельную проверку отмены при активном лизинге, включая `PendingChecks=0`. Доказательство эквивалентности для этого случая остаётся открытым. [PostgreSQL-сценарий](../../../tests/PackCall.Test/Integration/Operators/OperatorPostgresTests.cs#L83).
- Польза: минус два коротких unit; риск потери cancellation-контракта и call-order. Никакой экономии не подтверждено.

### C-03 — убрать Result_ActiveLease в пользу confirmed operator results

- Вид: scenario, retain—unique protection.
- Кандидат: `PackCall.Test/Integration/Deliveries/DeliveryResultLeasePostgresTests.cs::Result_ActiveLease_CompletesWithJournal(success)` — true и false.
- Замена: `PackCall.Test/Integration/Operators/OperatorPostgresTests.cs::ConfirmedOperatorResultsAreTerminal(http,rejected)` — все четыре false/true × false/true.
- G-03/G-06/F-03: terminal row/journal ошибочно сохраняют ownership, время или содержимое.
- Совпадает: PG, isolated schema, положительный lease, accepted/rejected, terminal state. Различается sender: fake SendResult с `declined` против MockEmailSender/локального HTTP с `SimulatedOperatorRejected`.
- `PostgresTestBase.AssertTerminal` проверяет CompletedAt=UpdatedAt, cleared WorkerId/LockExpiresAt, CreatedAt/email immutable, **одну** terminal journal entry с previous/new status, worker-A, occurred_at и operator_result; candidate также проверяет campaign Completed. Замена проверяет terminal status, substring journal и отсутствие capture. Она не сохраняет эти assertions.
- Baseline green не устраняет разницу. Будущий merge должен перенести весь AssertTerminal, адаптировать ожидаемые worker/result и сохранять campaign oracle; текущая замена недостаточна. Нужны mutations каждого потерянного поля.
- Польза: минус два случая; риск существенный (SQL/journal integrity). Сохранить.

### C-04 — объединить Python wait/docker/sql helpers

- Вид: support, uncertain; не считать сокращением тестов.
- Точные источники: `tests/acceptance/workers/lifecycle/support.py::docker`, `lifecycle.py::docker/wait`, `coordination/run.py::command/wait/stop_all`, `operators/run.py::cmd/wait`, `operators/late_http.py::cmd/wait`, `operators/shutdown.py::cmd/wait`.
- Возможная остающаяся реализация **пока не выбрана**: существующего helper, эквивалентного всему union, нет. Нельзя заявлять уже работающую замену.
- Ошибки: чужие ресурсы остановлены, потеря stderr/timeout evidence, transient DB error преждевременно прерывает readiness, failure превращается в success.
- Команды. `operators/run.py::cmd` возвращает `CompletedProcess`. `support.py::docker`, `late_http.py::cmd` и `shutdown.py::cmd` возвращают очищенный stdout. Эти четыре функции выбрасывают `RuntimeError` при ненулевом коде и `check=True`. `lifecycle.py::docker` возвращает очищенный stdout, подавляет stderr и пробрасывает `CalledProcessError`. `coordination/run.py::command` возвращает очищенный stdout, выбрасывает `RuntimeError` при ненулевом коде и журналирует тайм-аут перед пробросом `TimeoutExpired`. Тайм-аут команды в shutdown — 100 с, в late — 90 с.
- Ожидание. `operators/run.py::wait` возвращает первое истинное значение и подавляет `Exception` от callback. `lifecycle.py::wait` и `coordination/run.py::wait` возвращают первое истинное значение и пробрасывают исключения callback. `late_http.py::wait` и `shutdown.py::wait` возвращают `None` при успехе и также пробрасывают исключения callback. Истечение срока ожидания вызывает `AssertionError`. W22 сохраняет команды и логи остановки. Владение ресурсами и очистка различаются по сценарию.
- Файловый consumer search сохранён в inventory-files.json. Это поиск ссылок, не полное доказательство отсутствия динамических вызовов. Mocked HarnessFixes подтверждает лишь отдельные orchestration пути; реальные W22/operator runs защищают другую границу.
- Польза: меньше повторяющегося текста, размер/время не измерялись. Риск изменения cleanup/deadline высокий. Не объединять в первой группе. Нужна таблица caller → return/error/timeout/log/cleanup и replay fault controls; P-02.

### C-05 — оставить только одну проверку shared email fixtures

- Вид: scenario; retain—unique protection.
- Кандидаты: `PackCall.Test/Integration/Operators/OperatorHttpTests.cs::SharedEmailFixturesMatchProductValidation()` и `tests/acceptance/operators/operator_contract.py::Contract.test_email_parity()`; один общий `email-fixtures.json`.
- Предполагаемая замена в каждом направлении — другой из этих двух сценариев, все строки JSON.
- G-06/F-06: разное принятие email/message C# и Python реализациями. Одинаковый dataset — средство сравнения разных реализаций, а не дублирование oracle. C# вызывает MockEmailSender → product validator, Python — app.valid_email/app.valid_message; runtime/Unicode/exception semantics различаются.
- Оба прошли. Удаление любого теряет одну сторону parity. Польза ничтожна; риск несовместимости внешнего оператора. Сохранить оба и project-linked fixture asset.

### C-06 — убрать оценку памяти writer после collector bound

- Вид: scenario; retain—complementary layer.
- Кандидат: `PackCall.Test/Integration/Measurements/MeasurementWriterTests.cs::BoundedRetainedTelemetryStateFitsSixteenMiB()`.
- Замена: `PackCall.Test/Unit/Measurements/MeasurementCollectorTests.cs::ListenerRecordsAndRejectsUnboundedCategories()`.
- G-07/F-07: превышение удерживаемой памяти. Unit проверяет число серий и арифметическую границу массивов histogram (<4 MiB); integration удерживает closed window, две очереди writer, текущий serialization buffer и меряет GC.GetTotalMemory (<16 MiB). Это разные объекты и бюджеты.
- Оба прошли; точность абсолютного GC delta и чувствительность к лишнему буферу потребуют изолированной мутации. Нет доказательства, что unit увидит leak/рост writer queue. Польза одного удаления не измерена; риск потерять writer protection. Сохранить.

## Подтверждённое сохранение границ

- Entity/service/controller unit не заменяют реальные HTTP routing/exception mapping и persistence. Фейки не доказывают SQL atomicity, SKIP LOCKED или time-after-lock.
- Сохранить все 18 вариантов `Result_StaleLease_DoesNotChangeRowOrJournal` до доказательства эквивалентности. Success/failure, состояния waiting/captured/sent/failed/owner/expired и same-worker варианты различают состояние строки, владельца и границу лизинга. Терминальная ветка различается для success и failure.
- Три SQL concurrent режима double/recovery/expiry и commit-before-recovery — разные interleavings; WaitBlocked подтверждает достижение блокировки. W22 добавляет реальную смерть процесса, натуральную expiry, независимые часы. Fake budget добавляет детерминированные elapsed/error/margin controls.
- Journal, row, HTTP acceptance и metric — разные эффекты. Journal не атомарен с terminal update. Unknown commit в PerformancePostgres использует proxy, теряющий acknowledgement **после** настоящего commit; он не доказывает сетевую потерю commit response.
- Measurement collection сериализацию сохранить; отключение Meter listener, количественные окна, файловая ротация/checksum, reader corruption и SIGKILL prefix не взаимозаменяемы.
- Production throughput и overhead acceptance не запускались и для этого аудита не нужны.

## История и tooling

Сохранить tools, SQL, schemas, examples, probes, fixtures и reorganization evidence отдельно от сценариев. Одноразовые prepare/transfer/organize скрипты не запускались. Нет решения об их удалении: UNC-04 (retention authority). Существующий verifier подтверждает перенос, не необходимость теста. No-consumer текстовый результат не является разрешением удалить helper.
