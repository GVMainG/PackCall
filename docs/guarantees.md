# Гарантии PackCall — матрица as-is

Статус: HEAD `beta.2` (email-only, mock-оператор). Матрица описывает существующее поведение, а не
целевой 0.5: выборка контактов/каналов, Telegram и нулевой допуск SM-2 — `beta.3`/`beta.4`.
Каждая строка ссылается на PRD/addendum, а не переписывает их. Доставка получателю **не
гарантируется** — гарантируется только подтверждённый приём оператором канала.

| Гарантия | Формулировка (as-is) | Проверка | Команда | Условия / ограничения |
| --- | --- | --- | --- | --- |
| **FR-3 / FR-16** канал и маршрутизация | Ровно один оператор на канал; доставка маршрутизируется только в оператор своего канала, fallback запрещён | unit TestOperatorTests; integration OperatorRegistrationTests | `dotnet test tests/PackCall.Test --filter "Category=Unit&Module=Operators"`; `--filter "Category=Integration&Module=Operators"` | as-is: единственный канал email (mock). Telegram и multi-канал — `beta.4` |
| **FR-9** выборка | Кампания создаёт доставки только для получателей этой кампании; доставка вне кампании не создаётся | интеграционные Campaign-тесты; приёмка W22 | `dotnet test tests/PackCall.Test --filter "Category=Integration"` (нужен `PACKCALL_TEST_POSTGRES`); `python tests/acceptance/workers/coordination/run.py` | as-is: выборка = **все** получатели (email) — гарантия «вне кампании» тривиальна. Выборка «все | список» × каналы и **нулевой допуск SM-2** (0 отправок вне выборки, NFR-3) — `beta.3` |
| **FR-10** семантика `Sent` | `Sent` — только после подтверждённого приёма оператором; `Failed` — только после подтверждённого отказа; неизвестный исход не терминален (NFR-2) | unit DeliveryProcessingServiceTests; integration DeliveryResultLeasePostgresTests | `dotnet test tests/PackCall.Test --filter "Category=Unit&Module=Deliveries"`; `--filter "Category=Integration"` | mock-оператор email; HTTP-попытка/ответ без подтверждения не `Sent` |
| **FR-11** захват/восстановление | Worker захватывает пакетами, закрепляет за собой, просроченные закрепления возвращаются в очередь | WorkerPostgresTests; W22 (независимые процессы, recovery, fence) | `python tests/acceptance/workers/coordination/run.py` + `verify.py` | без потери доставок при штатном восстановлении; без автоповторов/backoff |
| **FR-12** пауза/resume | Пауза останавливает новые отправки, не отменяя начатые; resume продолжает с оставшихся | CampaignPausePostgresTests | `dotnet test tests/PackCall.Test --filter "Category=Integration&Module=Campaigns"` | неотправленный хвост освобождается владельцем закрепления |
| **FR-13** завершение | Кампания `Completed`, когда нет доставок в `Waiting`/`InProgress` | интеграционные Campaign-тесты | `dotnet test tests/PackCall.Test --filter "Category=Integration"` | есть путь довести до `Completed` без ручных правок БД |

## Источники

PRD FR-3, FR-9…FR-13, FR-16; NFR-2 (ложные терминалы → FR-10), NFR-3 (корректность выборки → FR-9).
Команды — [`tests/README.md`](../tests/README.md). Правила evidence — [`EVIDENCE.md`](EVIDENCE.md).
