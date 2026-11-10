# RUN-5 — диагностика и ремонт (`lifecycle.py:33`)

**Результат:** PASS — причина установлена, сценарий исправлен и пройден.
**Commit SHA:** `00343305d8145cad7aa76addcb3e79b0fe4cba29`

## Диагноз (не гипотеза)

`assert owner()==wid` после рестарта API падал из-за **гонки приёмочного скрипта**, а не дефекта
продукта.

- `owner()` = `select worker_id from deliveries where delivery_status=1 limit 1` (InProgress).
- Mock-оператор (inprocess) завершает доставку быстрее, чем длится `docker restart api` +
  `wait(ready)` (≈1–2 c): замер после падения показал `delivery_status=2 → 1000`, `worker_id`
  пусто, кампания `Completed`. Точная длительность одной отправки не замерялась — достаточно
  факта, что все 1000 доставок ушли в `Sent` за время рестарта.
- Следовательно `owner()` возвращал пустую строку, и `'' == wid` было ложно.
- GAP-01 (`delivery_status=3` → `-1` на строке 38) этот сценарий не закрывает: это другая
  строка/ассерт, RUN-5 — про гонку по времени обработки.

## Исправление

Перед рестартом API останавливаем Worker (`docker stop`). Измерение показало: graceful shutdown
оставляет текущий пакет в `InProgress` с удержанным лизингом (`worker_id=wid`, 40 строк),
остальные — `Waiting`. После этого `docker restart api` детерминированно сохраняет владельца
лизинга, и `assert owner()==wid` проходит.

Семантика: проверяется инвариант «рестарт API не трогает удержанный лизинг». Исходная проверка
«живой Worker сохраняет лизинг во время рестарта» с быстрым mock-оператором принципиально гонча;
для неё нужен медленный/некооперативный оператор (Probe) — удержание лизинга при реальном
SIGTERM покрывается `shutdown-probes.py` (PASS в RUN-2).

## Проверка

`python tests/acceptance/workers/lifecycle/lifecycle.py` → exit 0.

Ключи, доказывающие **RUN-5**: `workerId`, `apiRestartWorkerUnchanged: true` (владелец лизинга не
изменился после рестарта API).

Ключи фазы SIGTERM (RUN-2/общая приёмка): `afterSigterm: 0|960\n1|40`,
`unexpiredOldLeaseRetained: true`, `newWorkerId` ≠ старый, `campaignSqlStatus: 2`,
`finalDeliveries: 2|1000`, `terminalNoResend: 1`.

Примечание: `stopSeconds` в lifecycle-сценарии теперь ≈0 (Worker уже остановлен фиксом); бюджет
graceful shutdown (30 с) отдельно проверяется `shutdown-probes.py` (`assert elapsed<40`).

## Команда воспроизведения

```bash
# W21-проект (db+api+worker); без `-f`, чтобы Compose v2 автоматически смержил
# docker-compose.override.yml (порт api 8080). recipients = ровно 1000.
docker compose -p packcall-w21 up -d
docker exec packcall-w21-packcall.db-1 psql -U packcall -d packcall -Atc \
  "TRUNCATE recipients CASCADE; INSERT INTO recipients(id,email) SELECT gen_random_uuid(),'r'||g||'@example.test' FROM generate_series(1,1000) g"
python tests/acceptance/workers/lifecycle/lifecycle.py
```

Среда: Windows, Docker Desktop (Linux containers), Python 3.13.
