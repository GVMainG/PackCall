# PackCall

## CI

CI — GitHub Actions, workflow `.github/workflows/ci.yml`.

- Триггеры: pull request, push в `master`, теги `v*`.
- Обязательный check: **`CI required`** — агрегирующий job. Он зелёный только тогда, когда все
  обязательные job завершились успешно; упавший, пропущенный (skipped) или не запустившийся job —
  ошибка, а не skip.
- Что проверяется: строгая UTF-8 исходников (`utf8_sources`), сборка `dotnet build PackCall.slnx
  -c Release` и unit-тесты `Category=Unit` (`build_and_unit`). Логи сборки и TRX прикладываются
  артефактом `ci-logs-<sha>`.
- Включение обязательного check: в GitHub Settings → Branches защитить `master` и добавить
  required check `CI required`. Это действие владельца, из репозитория не настраивается.

### Тег релиза

Тег выпускается только для коммита с зелёным агрегирующим check:

```bash
GITHUB_TOKEN=... python3 scripts/ci/release_gate.py --commit <sha>
```

Гейт падает (exit 1), если `CI required` отсутствует, не завершён или не успешен, и (exit 2) при
ошибке доступа к API. Пока гейт красный, тег не ставится. Правила доказательств и тегов —
`docs/EVIDENCE.md`.
