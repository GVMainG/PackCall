# RUN-6 — маркер `late_http.py:66` после исправления cp1251

**Результат:** PASS.
**Классификация RUN-6:** устранён — корневая причина исправлена в Story 1.5 (коммит `f831cf8`),
приёмочный сценарий `late_http.py` повторно прошёл на исправленном worker.

**Где был зафиксирован RUN-6:** necessity-аудит (addendum §7): `RUN-6 (late_http.py:66 — marker после
исправления cp1251)`. Здесь снят именно этот провал.

## Что проверяет сценарий

`tests/acceptance/operators/late_http.py:66` — строка ожидания
`wait(lambda:'истекает; доставка' in cmd(['logs',old]),25)`: сценарий читает `docker logs` worker и
ждёт русскую подстроку предупреждения об истечении закрепления.

До Story 1.5 worker писал эту строку в cp1251; при чтении лога байты декодировались с потерей и
подстрока оказывалась U+FFFD-модзибейком, поэтому точное русское `истекает; доставка` не
совпадало. После перекодирования (Story 1.5) worker пишет корректный UTF-8, и подстрока совпадает —
проверка различает кодировки по декодированному тексту, а не по наличию произвольных байтов.

## Доказательство

- Приёмочный сценарий: `python tests/acceptance/operators/late_http.py`
- Вывод: `Late HTTP process cancellation/recovery/fence: PASS`
- Артефакт: `…/story-11-1-test-email-operator/w22-http/run-20261111-182412-d24c2c1f/late-http.json` →
  `{"status": "passed"}`.
- Маркер: `истекает; доставка` — точная подстрока из
  `src/PackCall.Worker/Background/DeliveryWorker.cs:135`
  (`Время закрепления доставки {DeliveryId} истекает; доставка будет возвращена для повтора`).
  Закреплён unit-тестом `tests/PackCall.Test/Unit/Workers/DeliveryWorkerWarningTests.cs`
  (проверяет отрисовку предупреждения через собирающий Serilog-sink). Локально этот C#-тест не
  запускался (VSTest testhost падает в данной среде, см. Примечания); он исполняется в CI
  (Story 1.2, job `build_and_unit`).
- Корневая причина: `f831cf8` (Story 1.5) — перекодирование `DeliveryWorker.cs` из cp1251 в UTF-8.

> О нумерации: `story-11-1-test-email-operator` — унаследованное имя каталога приёмки оператора,
> не Epic-номер стори; к текущему плану относятся Story 1.5 (фикс кодировки) и Story 1.10 (RUN-6).

## Предусловие и команда воспроизведения

1. W22 HTTP-прогон (все 7 сценариев passed, проект `packcall-w22-b7f9fe6c`):
   `PACKCALL_ENVIRONMENT=test`, `TEST_SENDER_MODE=http`,
   `TEST_OPERATOR_URL=http://operator:8000`,
   `W22_OUTPUT=_bmad-output/implementation-artifacts/story-11-1-test-email-operator/w22-http`;
   `python tests/acceptance/workers/coordination/run.py` → `run-20261111-182412-d24c2c1f`.
2. Образ worker был устаревшим (собран 2026-11-08, до фикса), а пересборка из Dockerfile
   невозможна — `mcr.microsoft.com` недоступен (TLS timeout). Пересобран без сети:

   ```powershell
   dotnet publish src/PackCall.Worker/PackCall.Worker.csproj -c Release -o <out> /p:UseAppHost=false
   $cid = docker create packcallworker
   docker cp "<out>/." "$cid:/app/"
   docker commit $cid packcallworker
   docker rm $cid
   ```

   Итоговый образ: `sha256:7f84c943f67dc3e7a4bd613aa6ac4b30c04dcbbaec653b756c0b32415e6c880c`
   (тег `packcallworker`, перезаписан). Внешние/прогонные контейнеры не удаляются без отдельного
   решения; оригинальный Dockerfile-путь воспроизводится при доступном MCR.
3. `python tests/acceptance/operators/late_http.py`

## Окружение

- SHA исходников, из которых собран worker: `c7746566e64267429887564b4023313c35b2c81d`
- Коммит, фиксирующий этот evidence-файл: `9b30769`
- ОС: Windows; Docker Desktop (Linux containers), Docker CLI 29.8.2
- .NET SDK: `11.0.100-rc.2.26504.105` (предрелизный SDK 11 установлен в среде намеренно; собирает
  target `net10.0`); Python 3.13

## Примечания

- Полный регрессионный `dotnet test` в `coordination/verify.py` в этой среде не выполняется:
  VSTest testhost падает с Win32Exception(5) при открытии handle родителя — ограничение среды,
  не дефект кода; он не входит в предусловие `late_http.py`.
- Сценарий не меняет семантику обработки позднего ответа: изменена только кодировка строки
  предупреждения (Story 1.5), журнал и переходы статусов остались прежними.
