# Приёмка оператора

Все команды — из корня PackCall, Windows/PowerShell, Python 3.11+, .NET SDK 10, Docker Engine с Linux containers и Compose v2. Используйте только выделенные тестовые БД/проекты. `run.py` не запускает остальные файлы этой папки.

Сначала создайте локальный `.env` с тестовыми значениями ниже. Существующий `.env` с рабочими настройками не используйте и не перезаписывайте. Не добавляйте в этот файл `PACKCALL_ENVIRONMENT`/`TEST_SENDER_MODE`: отрицательные проверки runner удаляют эти переменные из process environment и требуют отсутствия fallback.

```dotenv
DOCKER_REGISTRY=
POSTGRES_DB=packcall
POSTGRES_USER=packcall
POSTGRES_PASSWORD=packcall
TEST_SIMULATE_ERRORS=false
TEST_OPERATOR_URL=
TEST_OPERATOR_TIMEOUT_SECONDS=5
```

Подготовьте именно этот venv: `privacy.py` жёстко использует его `Scripts/python.exe`.

```powershell
$operatorVenv='_bmad-output/implementation-artifacts/story-11-1-test-email-operator/venv'
py -3.11 -m venv $operatorVenv
& "$operatorVenv/Scripts/Activate.ps1"
python -m pip install -r tests/acceptance/operators/requirements.txt
python -m pip install -r scripts/performance/requirements.txt
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='inprocess'
$env:TEST_SIMULATE_ERRORS='false'
$env:TEST_OPERATOR_TIMEOUT_SECONDS='5'
python tests/acceptance/operators/operator_contract.py
python tests/acceptance/operators/privacy.py
python tests/acceptance/operators/preflight.py -p packcall-b11-config -f docker-compose.yml
```

Privacy запускает два локальных Uvicorn процесса и завершает их в finally; OUT уже создан venv. Preflight проверяет merged Compose configuration; для HTTP отдельно установите `TEST_SENDER_MODE=http`, `TEST_OPERATOR_URL=http://packcall.test-email-operator:8000` и добавьте `-f docker-compose.test-http.yml`.

Следующая последовательность описывает полную исходную приёмку и её зависимости. Она не была выполнена в этой реорганизации. Сначала прочитайте ограничения ниже; продолжайте к зависимому шагу только после успешного результата предыдущего.

```powershell
$env:TEST_SENDER_MODE='inprocess'
python tests/acceptance/operators/run.py
# Требуются опубликованные образы packcallapi/packcallworker/packcallmigrator из успешного run.py:
python tests/acceptance/operators/config_matrix.py
python tests/acceptance/operators/precedence.py
python tests/acceptance/operators/prod_compose.py
# latest-run.txt оператора должен указывать на run со status=passed; HTTP контейнер сохранён:
python tests/acceptance/operators/retention.py
```

Для `late_http.py` и `shutdown.py` обязателен отдельный успешный W22 HTTP run именно в `OUT/w22-http`; они повторно запускают его сохранённые db/operator контейнеры. W22 выполняйте последовательно, затем verify с теми же переменными. Подробности, контрольный каталог и ownership — [coordination/README.md](../workers/coordination/README.md).

```powershell
$env:PACKCALL_ENVIRONMENT='test'
$env:TEST_SENDER_MODE='inprocess'
$env:W22_OUTPUT='_bmad-output/implementation-artifacts/story-11-1-test-email-operator/w22-inprocess'
python tests/acceptance/workers/coordination/run.py
python tests/acceptance/workers/coordination/verify.py
$env:TEST_SENDER_MODE='http'
$env:TEST_OPERATOR_URL='http://operator:8000'
$env:W22_OUTPUT='_bmad-output/implementation-artifacts/story-11-1-test-email-operator/w22-http'
python tests/acceptance/workers/coordination/run.py
python tests/acceptance/workers/coordination/verify.py
# Только после passed W22 HTTP; каждый сценарий меняет/очищает его тестовые таблицы:
python tests/acceptance/operators/late_http.py
python tests/acceptance/operators/shutdown.py
```

`late_http.py` создаёт `late-control` однократно: не повторяйте на том же run без отдельной подготовки. Оба сценария используют свежие синтетические данные, очищают таблицы W22 и останавливают собственные worker/probe и db/operator в finally; volumes и stopped containers сохраняются. `retention.py` использует операторский latest run, не W22; перестановка этих проверок не заменяет их предусловия.

Известная исходная несовместимость: `run.py` удаляет `TEST_SIMULATE_ERRORS`, `TEST_OPERATOR_URL`, `TEST_OPERATOR_TIMEOUT_SECONDS` из дочернего ENV, хотя текущий production Compose требует их при interpolation. Без корректных `.env` defaults он останавливается до runtime. Статическая проверка `preexisting-config-check.json` зафиксировала exit 1 без запуска ресурсов. Defaults выше заполняют Compose значения; они не доказывают успешность всех исторических assertions. Отрицательные config-проверки также требуют отсутствия посторонних fallback. Прямые `docker run` из worker lifecycle не передают обязательные production `PACKCALL_ENVIRONMENT`/`TEST_SENDER_MODE`: process env хоста сам в контейнер не переносится. Эта прежняя несовместимость не исправлялась; полный сценарный прогон и Docker acceptance остаются непроверенными.

Cleanup после изучения результатов: `run.py` и prod_compose останавливают собственные Compose проекты; остальные ресурсы остаются. Возьмите точные project/container names из `commands.jsonl`, `results.json`, `*-commands.jsonl`; не используйте глобальный prune. Для каждого операторского проекта выполните `docker compose -p <saved-project> -f docker-compose.yml -f tests/acceptance/operators/compose.acceptance.yml down` (для HTTP добавьте `-f docker-compose.test-http.yml` перед acceptance overlay). Для W22 сохраните его W22_CONTROL и выполните `docker compose -p <saved-w22-project> -f tests/acceptance/workers/coordination/compose.yml down`. Одноразовые alternate/changed-worker/delayed/late/shutdown контейнеры удаляйте отдельно только по точным именам из логов. `down` без `-v` сохраняет volumes; артефакты, venv и данные автоматически не удаляются.
