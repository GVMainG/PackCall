# BMAD в PackCall

Установлен BMAD-METHOD **6.12.4** (стабильный релиз): модули Core и Method (`bmm`), 29 навыков для Codex. Дополнительные модули и устаревшие совместимые команды не установлены.

## Начало работы

Навыки доступны со следующего сообщения в Codex. Если каталог не обновился, открой новый чат в этом проекте.

- `$bmad-help` — определить следующий шаг с учётом текущего проекта.
- `$bmad-build <описание изменения>` — провести конкретное изменение через уточнение, реализацию и проверку.
- `$bmad-prd` — работа с требованиями к продукту.
- `$bmad-architecture` — работа с архитектурой.

Пример первого запроса: «$bmad-help Изучи существующую документацию PackCall и предложи следующий шаг. Код и требования пока не изменяй».

Для нового этапа работы удобно открывать отдельный чат. В PackCall уже есть документация: полный цикл создания проекта с нуля не требуется. Правила в `AGENTS.md` и существующие навыки PackCall применяются и при использовании BMAD.

## Файлы и настройки

- `.agents/skills/` — навыки Codex.
- `_bmad/` — runtime, каталог и конфигурация BMAD.
- `_bmad/custom/config.toml` — сохраняемые при обновлении общие настройки, включая русский язык общения и документов.
- `_bmad/custom/bmad-build.toml` — загрузка `AGENTS.md` при работе `bmad-build`.
- `_bmad/config.user.toml` — персональные ответы установщику; не включается в Git.
- `_bmad-output/planning-artifacts/` — новые планы и спецификации BMAD.
- `_bmad-output/implementation-artifacts/` — материалы реализации и ревью.
- `docs/` — существующие знания о проекте; правила их использования указаны в `AGENTS.md`.

Меняй сохраняемые настройки в `_bmad/custom`, а не файлы с пометкой installer-managed. Сгенерированные снимки `_bmad/render` не включаются в Git. Материалы `_bmad-output` можно сохранять в Git по мере появления.

## Повторная установка этой версии

Из корня проекта (PowerShell, Node.js >=20.12, uv и Python >=3.11):

```powershell
npx.cmd --yes bmad-method@6.12.4 install --directory . --modules bmm --tools codex --user-name GV --communication-language Russian --document-output-language Russian --output-folder _bmad-output --set bmm.project_knowledge=docs --no-shims --yes
```

Версия закреплена в команде: запуск без `@6.12.4` может установить другую версию. Переход на v7 — отдельное обновление.

Проверка объединённой конфигурации:

```powershell
uv run --no-cache _bmad/scripts/resolve_config.py --project-root . --key core --key modules.bmm
```

Источники: [релиз 6.12.4](https://github.com/bmad-code-org/BMAD-METHOD/releases/tag/v6.12.4), [исходники стабильной версии](https://github.com/bmad-code-org/BMAD-METHOD/tree/v6.12.4).
