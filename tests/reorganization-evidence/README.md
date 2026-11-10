# Свидетельства реорганизации тестов

Исходная точка: `e5c41ed93656f1bdb5669f35abd932310ecae85b`. `original-sources.json` содержит исходные тексты, SHA-256, Python функции/методы и entrypoints, включая файлы, которые прежний ignore мог скрыть. Подготовительные скрипты `prepare.py`, `transfer_verify.py`, `organize_csharp.py`, `organize_python.py` — запись выполненных одноразовых этапов; повторно их не запускать на финальном дереве.

Порядок подтверждён артефактами:

1. `baseline/` и `baseline-*.log`: исходные запуски.
2. `transfer-copy.json`: 20 C# файлов скопированы побайтово. Project metadata/пути остались прежними, кроме имени проекта.
3. `transfer/transfer.trx`, `transfer-comparison.json`: 148 identity/outcome записей равны baseline, 131 pass и 17 skip; callers заменены при существующем старом проекте. `transfer-solution.log`: solution build без ошибок/предупреждений.
4. Исходники и csproj `PackCall.Core.Test` удалены через apply_patch после checkpoint. Рекурсивная очистка каталога отклонена политикой shell tool; локальные ignored `bin/obj` остались. `old-project-removal.json` фиксирует это ограничение. На чистом checkout старого каталога не будет.
5. После удаления исходников выполнены переносы и извлечение общей PostgreSQL базы. Package versions, namespaces, тела сценариев, InlineData, lifecycle, skip attributes и Measurement collection сохранены.

| Проверка | Baseline | Финал |
|---|---:|---:|
| C# все | 131 passed / 17 skipped | 131 passed / 17 skipped |
| C# Unit | Не было фильтра | 109 passed / 0 skipped |
| C# Integration | Не было фильтра | 22 passed / 17 skipped |
| Python performance offline | 18 passed | 18 passed |
| Python operator harness | 4 passed | 4 passed |
| Python operator contract | 5 passed | 5 passed |
| Solution build | Transfer: 0 warnings / 0 errors | 0 warnings / 0 errors |

После review четыре InlineData сценария ProductionRegistrationMapsSimulateErrorsIntoProcessing перенесены из Unit в Integration без изменения тела/параметров. Финальные полные прогоны выполнены повторно родительским агентом: все 131 passed / 17 skipped; Unit 109 passed / 0 skipped; Integration 22 passed / 17 skipped. Повторены также Module=Operators и три Python набора (18+4+5 passed). `csharp-category-partition.json` сравнивает полные финальные category TRX с полным финальным TRX и подтверждает непересекающееся разбиение всех 148 case identities.

Все шесть фильтров `Module=...` выполнены отдельно; TRX и логи находятся в `filters/` и `final-module-*.log`. Разделение Unit/Integration не меняет полный набор исходов.

Сохранение подтверждается не только числами:

- `csharp-source-mapping.json` связывает все 20 исходников; shared lifecycle из CampaignPause и SQL helpers из DeliveryResultLease вынесены в `Integration/Support/PostgresTestBase.cs`, HTTP server — в `Integration/Support/OperatorServer.cs`.
- `csharp-scenario-mapping.json`: 87 test methods, 135 InlineData записей; для каждого сохранены параметры, SHA-256 полного тела с attributes, старый/новый путь и identity. `identity-renames.json` описывает только намеренные изменения class identity четырёх бывших partial-файлов. Сопоставление всех 148 TRX identity/outcome записей успешно.
- `csharp-member-mapping.json` и `csharp-structural-comparison.json`: Roslyn из установленного SDK сравнивает все 268 members (поля, свойства, конструкторы, methods, helpers и lifecycle), включая 3 expression-bodied теста. Сопоставление ограничено разрешёнными путями/владельцами; private → private protected допустимо только у явно перечисленных members общей PostgreSQL базы и Config/SequenceRandom подготовки оператора. Дополнительно сравниваются class containers с разрешёнными изменениями inheritance/traits, все комментарии (с нормализацией CRLF/LF) и using directives исходных сценариев.
- `csharp-source-extractions.json`: только один операторский сценарий (4 InlineData) извлечён в Integration/Operators/OperatorRegistrationTests; Config и SequenceRandom разделяются через OperatorTestBase, который не содержит тестов. `csharp-trait-validation.json` проверяет ровно Category/Module traits каждого тестового класса против его пути.
- `python-source-mapping.json` связывает перенесённые файлы, включая fixture/assets/docs. `python-scenario-mapping.json` сравнивает 141 функцию (27 test methods). `python-full-ast-comparison.json` подтверждает все 43 Python файла целиком, включая top-level acceptance шаги, с разрешёнными изменениями путей/import bootstrap. Общий docker helper извлечён только из startup/shutdown-probes после AST equality; другие helpers сохранены.
- `nonpython-asset-comparison.json`: сравнивает 23 неизменяемых assets из snapshot и requirements.txt из baseline Git; разрешены только явные пути coordination и дополнительный уровень Compose context. Для requirements дополнительно сравнивается Git blob с checkout normalization CRLF/LF.
- `static-path-checks.json`: compile всех Python, существование literal runner/fixture путей, Compose build contexts и Dockerfile COPY sources. Реальные runtime imports инструментов проверены offline suites.
- Команды повторной механической проверки из корня: `python tests/reorganization-evidence/verify_preservation.py` и `python tests/reorganization-evidence/verify_paths.py` (для первой нужен .NET SDK 10 с bundled Roslyn, для последней — PyYAML).

Ограничения: Docker CLI есть, Engine недоступен; `PACKCALL_TEST_POSTGRES` не задан. Все 17 PostgreSQL discovered records остаются skipped; PostgreSQL theories не раскрывают InlineData при skip, поэтому параметры дополнительно сопоставлены по исходникам. Docker acceptance, реальные PostgreSQL проверки и smoke измерений не выполнялись и не считаются passed. Full throughput acceptance не запускалась. Проверки HTTP/файловой системы и ASGI contract выполнены локально. Неисправленных регрессий в доступных проверках не обнаружено.

Известный исходный configuration failure текущего production Compose зафиксирован отдельно в `preexisting-config-check.json` (exit 1 без старта runtime ресурсов). Полные commands/venv/W22/cleanup зависимости оператора описаны в `tests/acceptance/operators/README.md`.


Независимое review: три слоя bmad-build выполнены. Исправлены классификация четырёх HTTP случаев, проверка traits и содержимого ресурсов, prerequisites и навигация операторских сценариев. Два исходных дефекта тестовых runners отложены: operator ENV/Compose interpolation (подтверждён config-only exit 1 в `preexisting-config-check.json`) и отсутствие обязательных host settings в прямых docker run worker lifecycle (статическая несовместимость, runtime не выполнен). Сценарии и продуктовый код для этих дефектов не изменялись. Полный журнал решений: `review-triage.md`.
