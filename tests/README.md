# Проверки PackCall

Все команды ниже выполняются из корня репозитория. C# требует .NET SDK 10; Python offline-проверки — Python 3.11+. Для offline performance suite установите jsonschema: `python -m pip install -r scripts/performance/requirements.txt`. Для HTTP contract оператора установите зависимости из `tests/acceptance/operators/requirements.txt`.

| Вид | Модуль и расположение | Команда | Ожидаемый результат / зависимости |
|---|---|---|---|
| Unit C# | `tests/PackCall.Test/Unit/<Module>` | `dotnet test tests/PackCall.Test --filter "Category=Unit"` | Все выбранные проверки проходят; PostgreSQL, Docker и внешний HTTP не нужны |
| Integration C# | `tests/PackCall.Test/Integration/<Module>` | `dotnet test tests/PackCall.Test --filter "Category=Integration"` | Локальный HTTP, файловая система, host; PostgreSQL-проверки требуют `PACKCALL_TEST_POSTGRES` |
| C# по модулю | Api, Campaigns, Deliveries, Workers, Operators, Measurements | `dotnet test tests/PackCall.Test --filter "Module=Workers"` | Можно сочетать: `"Category=Unit&Module=Workers"` |
| C# с изолированной БД | `tests/PackCall.Test` | `python scripts/performance/postgres_tests.py` | Docker Engine и временный PostgreSQL. Полноту прогона проверьте по числу пропусков в TRX |
| Offline Python | `tests/tools/performance` | `python -m unittest discover -s tests/tools/performance -p "test_*.py" -v` | 18 тестов инструментов анализа, чтения и сбора |
| Offline Python | `tests/tools/operators` | `python -m unittest discover -s tests/tools/operators -p "test_*.py" -v` | 4 теста orchestration с mock-процессами |
| Offline Python | `tests/tools/ci` | `python -m unittest discover -s tests/tools/ci -p "test_*.py" -v` | 13 проверок: гейт strict UTF-8 (6) и release gate (7) — валидный/невалидный UTF-8, U+FFFD, CR-строки, env-ошибки, успешный/красный/отсутствующий/pending check, произвольное имя check, HTTP-ошибка, нет токена |
| Contract Python | `tests/acceptance/operators/operator_contract.py` | `python tests/acceptance/operators/operator_contract.py` | 5 тестов ASGI, без Docker |
| Acceptance | `tests/acceptance/workers/lifecycle` | См. [README](acceptance/workers/lifecycle/README.md) | Docker Engine, Compose, SDK; последовательность подготовки БД обязательна |
| Acceptance | `tests/acceptance/workers/coordination` | См. [README](acceptance/workers/coordination/README.md) | Docker Engine, Compose, SDK; независимые worker-процессы, recovery и lease fence |
| Acceptance | `tests/acceptance/operators` | Полный порядок: [operators/README.md](acceptance/operators/README.md) | Python 3.11+, фиксированный venv для privacy, Docker Engine, Compose, SDK; отдельные run/config_matrix/precedence/privacy/prod_compose/retention/late_http/shutdown и W22 dependencies |
| Acceptance измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/postgres_repeat.py` | Реальный изолированный PostgreSQL в Docker; fixture удаляет только свой контейнер |
| Smoke измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/crash_smoke.py` | SDK и Docker; SIGKILL и сохранённый префикс телеметрии |
| Smoke измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/resource_smoke.py` | Docker; реальные collector-снимки |
| Smoke измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/control_smoke.py` | Docker и SDK; stop/cleanup владельца сессии |
| Smoke измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/stall_smoke.py` | Docker и SDK; terminal journal failure, ожидание stall 120s |
| Smoke измерений | `tests/acceptance/measurements` | `python tests/acceptance/measurements/two_worker_crash.py` | Docker и SDK; малая нагрузка, два worker и SIGKILL |

`PACKCALL_TEST_POSTGRES` должен указывать на выделенную тестовую PostgreSQL БД с правами создания/удаления схем. Каждый PostgreSQL тест создаёт отдельную случайную схему и удаляет её при disposal. При отсутствии строки подключения тесты явно пропускаются; это неполная проверка. Коллекция `Measurement` сохраняет сериализацию сценариев измерений.

Фикстуры: HTTP servers и email JSON — `tests/acceptance/operators/fixtures/` и `tests/acceptance/operators/email-fixtures.json`; clock shim — `tests/acceptance/workers/coordination/fixtures/`. Общее C# окружение PostgreSQL и HTTP server — `tests/PackCall.Test/Integration/Support`; общие fake-объекты — `tests/PackCall.Test/Support`.

Инструменты запуска, сбора и анализа, SQL, schema, examples и probe остаются в [scripts/performance](../scripts/performance/README.md). Исторические пути выходных артефактов `story-*` сохранены. Скрипты приёмки сохраняют прежние границы владения ресурсами; перед удалением стенда используйте инструкции соответствующего runner.

Проекты для запуска отдельных процессов:

- [PackCall.Worker.Probe](PackCall.Worker.Probe/PackCall.Worker.Probe.csproj) используется в [сценариях остановки worker](acceptance/workers/lifecycle/README.md).
- [PackCall.Workers.Acceptance](PackCall.Workers.Acceptance/PackCall.Workers.Acceptance.csproj) публикуется [Dockerfile координации](acceptance/workers/coordination/Dockerfile).

## История реорганизации

[Архив свидетельств](reorganization-evidence/README.md) относится к реорганизации в коммите `72228dd69a81f9eb90eb95978a8a3aceba89fe26`. Команды внутри архива относятся к структуре той ревизии. Проверка перезаписывает отчёты, поэтому запускайте её из отдельного checkout с LF. Нужен .NET SDK с Roslyn.

```powershell
git worktree add --detach --no-checkout ../packcall-evidence-replay 72228dd69a81f9eb90eb95978a8a3aceba89fe26
Set-Location ../packcall-evidence-replay
git -c core.autocrlf=false -c core.eol=lf restore --source=HEAD --staged --worktree -- .
python tests/reorganization-evidence/verify_preservation.py
```

Исторический `verify_paths.py` сравнивает байты файлов с исходным снимком. В снимке есть Compose-файлы с другими окончаниями строк и локальные артефакты сборки probe. На чистом checkout скрипт останавливается на несовпадении хеша Compose-файла. Нагрузочная приёмка 1 000 000 доставок в архиве отсутствует.

Известные исходные ограничения: operator `run.py` очищает обязательные для текущего Compose TEST_* переменные; worker lifecycle direct `docker run` не передаёт обязательные production PACKCALL_ENVIRONMENT/TEST_SENDER_MODE. Подготовка и точные ограничения описаны в README модулей; их полная приёмка не выполнялась.
