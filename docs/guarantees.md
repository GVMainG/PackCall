# Гарантии PackCall — матрица as-is

Матрица описывает текущую реализацию. Формирование поддерживает email и Telegram,
отправка доступна через тестовый оператор email. Статус `Sent` подтверждает приём
оператором канала. Доставка конечному получателю остаётся за пределами этой гарантии.

| Гарантия | Формулировка (as-is) | Проверка | Команда | Условия / ограничения |
| --- | --- | --- | --- | --- |
| **FR-3 / FR-16** канал и маршрутизация | Захват включает доставки активных кампаний с зарегистрированным каналом и непустым сохранённым адресом. Воркер выбирает keyed-оператор по каналу доставки и передаёт снимок адреса. При отсутствии оператора для захваченной доставки её закрепление сохраняется до восстановления. Запуск кампании возвращает `409`, если её канал недоступен | unit TestOperatorTests; integration OperatorPostgresTests, ChannelsApiPostgresTests | `dotnet test tests/PackCall.Test --filter "Module=Operators&#124;Module=Channels"` (нужен `PACKCALL_TEST_POSTGRES`) | Реестр включает тестовый email-оператор. Подключение Telegram запланировано на `beta.4` |
| **FR-9** выборка | Кампания создаёт доставки только для сохранённых пар «контакт × канал» с адресом | `CampaignLifecyclePostgresTests`, `ChannelsApiPostgresTests` | `dotnet test tests/PackCall.Test --filter "Category=Integration"` (нужен `PACKCALL_TEST_POSTGRES`) | API принимает `all` или `contactIds` и явный набор каналов. Сквозная проверка отправок вне выборки (SM-2) запланирована на Story 2.17 |
| **FR-10** семантика `Sent` | `Sent` — только после подтверждённого приёма оператором; `Failed` — только после подтверждённого отказа; неизвестный исход не терминален (NFR-2) | unit DeliveryProcessingServiceTests; integration DeliveryResultLeasePostgresTests | `dotnet test tests/PackCall.Test --filter "Category=Unit&Module=Deliveries"`; `--filter "Category=Integration"` | mock-оператор email; HTTP-попытка/ответ без подтверждения не `Sent` |
| **FR-11** захват/восстановление | Worker захватывает пакетами, закрепляет за собой, просроченные закрепления возвращаются в очередь | WorkerPostgresTests; W22 (независимые процессы, recovery, fence) | `python tests/acceptance/workers/coordination/run.py` + `verify.py` | без потери доставок при штатном восстановлении; без автоповторов/backoff |
| **FR-12** пауза/resume | Пауза останавливает новые отправки, не отменяя начатые; resume продолжает с оставшихся | CampaignPausePostgresTests | `dotnet test tests/PackCall.Test --filter "Category=Integration&Module=Campaigns"` | неотправленный хвост освобождается владельцем закрепления |
| **FR-13** завершение | Кампания `Completed`, когда нет доставок в `Waiting`/`InProgress` | интеграционные Campaign-тесты | `dotnet test tests/PackCall.Test --filter "Category=Integration"` | есть путь довести до `Completed` без ручных правок БД |

## Источники

PRD FR-3, FR-9…FR-13, FR-16; NFR-2 (ложные терминалы → FR-10), NFR-3 (корректность выборки → FR-9).
Команды — [`tests/README.md`](../tests/README.md). Правила evidence — [`EVIDENCE.md`](EVIDENCE.md).
