# Решения necessity-аудита

Записи PASS/FAIL-RECORDED по пунктам аудита. Источник: `docs/evidence/necessity-audit/`
(переиздание Story 1.11), актуализация по мере закрытия стори Epic 1.

## RUN-5 — `lifecycle.py:33` (owner after API restart)

- **Вердикт:** PASS.
- **Причина исходного падения:** гонка приёмочного скрипта — mock-оператор завершает все доставки
  за время рестарта API, `owner()` (запрос InProgress) возвращал пусто. Не дефект продукта.
- **Исправление:** остановка Worker перед рестартом API (фиксация лизинга). Commit
  `00343305d8145cad7aa76addcb3e79b0fe4cba29`.
- **Доказательство:** `docs/evidence/beta2/RUN-5.md` — `lifecycle.py` exit 0,
  `apiRestartWorkerUnchanged: true`, `finalDeliveries: 2|1000`.

## RUN-4 — пустой `TEST_SIMULATE_ERRORS`

- **Вердикт:** PASS. `config_matrix.py` exit 0 (пустое значение → «имитация выключена»).
- **Доказательство:** `docs/evidence/beta2/RUN-4-GAP-01.md`.

## GAP-01 — `Failed=-1`

- **Вердикт:** PASS (статически исправлено + enum-контракт закреплён тестом).
- **Commit SHA:** `6af6d8900d143f5f096f04620370fe0e541b987a` (фикс), `a5d68e9` (контракт-тест).
- **Доказательство:** `docs/evidence/beta2/RUN-4-GAP-01.md`; `DeliveryStatusContractTests`.

## RUN-2 — прямой `docker run` без обязательных env

- **Вердикт:** PASS. `startup.py` и `shutdown-probes.py` exit 0.
- **Доказательство:** `docs/evidence/beta2/RUN-2.md`.
