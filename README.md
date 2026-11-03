# PackCall

## CI

CI — GitHub Actions, workflow `.github/workflows/ci.yml`.

- Триггеры: pull request, push в `master`, теги `v*`, ручной запуск (`workflow_dispatch`).
- Обязательный check: **`CI required`** — имя агрегирующего job, которое видно в GitHub UI и
  выбирается в branch protection. Он зелёный только тогда, когда все обязательные job завершились
  успешно; упавший, пропущенный (skipped) или отсутствующий job — ошибка, а не skip.
- Job'ы и их отображаемые имена:
  - `utf8_sources` — «Strict UTF-8 sources»: строгая UTF-8 исходников C#;
  - `build_and_unit` — «Build and unit tests»: `dotnet build PackCall.slnx -c Release` и
    unit-тесты (`--filter "Category=Unit"`); отдельный шаг проверяет по TRX, что тесты реально
    исполнились, а не были отфильтрованы;
  - `integration` — «Integration tests (PostgreSQL)»: сервис `postgres:18.6-alpine3.24`,
    применение EF-миграций через `PackCall.Migrator` и интеграционные тесты
    (`--filter "Category=Integration"`) с `PACKCALL_TEST_POSTGRES`; TRX-гейт требует
    executed > 0 и **0 skipped**;
  - `ci_required` — «CI required»: агрегирующий обязательный check.
- Логи и TRX: `ci-logs-<sha>-<run_id>` (сборка/unit) и `integration-logs-<sha>-<run_id>`
  (миграции/интеграционные тесты), хранение 14 дней.
- Локальные предпосылки: .NET SDK 10, Python 3.11+, Docker Engine.
- Миграции применяются к схеме `public`; интеграционные тесты работают в собственных временных
  схемах (`PostgresTestBase`). Факт применения миграции проверяет `MigrationAppliedTests`.
- Включение обязательного check: GitHub Settings → Branches → защитить `master` и добавить
  required check `CI required`. Это действие владельца, из репозитория не настраивается.

Локально воспроизвести интеграционный гейт:

```bash
docker run --rm -e POSTGRES_DB=packcall -e POSTGRES_USER=packcall -e POSTGRES_PASSWORD=packcall-ci \
  -p 5432:5432 postgres:18.6-alpine3.24
export ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=packcall;Username=packcall;Password=packcall-ci"
export PACKCALL_TEST_POSTGRES="$ConnectionStrings__Postgres"
dotnet build PackCall.slnx -c Release
dotnet run --project src/PackCall.Migrator -c Release --no-build
dotnet test tests/PackCall.Test -c Release --no-build --filter "Category=Integration"
```

### Тег релиза

Тег выпускается только для коммита с зелёным `CI required`:

```bash
export GITHUB_TOKEN=...   # не передавайте токен аргументом: он попадёт в историю shell и в ps
python3 scripts/ci/release_gate.py --commit <40-hex-sha>
```

Нужен scope `Checks: Read` (fine-grained token) или `repo` (classic PAT). Гейт падает (exit 1),
если `CI required` отсутствует, опубликован не GitHub Actions, не завершён или не успешен, и
(exit 2) при ошибке окружения: нет токена или репозитория, 401/403/5xx, сеть или некорректный JSON.
Пока гейт красный, тег не ставится. Вывод гейта (check-run id, `html_url`, время завершения)
сохраняется как evidence вехи в `docs/evidence/<tag>/`. Правила тегов и evidence —
[`docs/EVIDENCE.md`](docs/EVIDENCE.md).
