# runtime-configuration Specification

## Purpose

Настройки и запуск существующих API, Worker и Migrator, выбор тестового sender, параметры фоновой обработки, логирование и Compose-варианты. Проверка и применение схемы описаны в [database-lifecycle](../database-lifecycle/spec.md). Рабочего режима реальных отправок `prod` нет.

Базовая спецификация по коду и конфигурации на 2026-10-31. SHALL описывает реализованные правила. Наличие приёмочного скрипта не означает его запуска.

## Входы, результаты и ошибки

| Настройка | Фактическое правило |
|---|---|
| `PACKCALL_ENVIRONMENT` | Обязательное `test` после trim/lowercase; `prod` отклоняется отдельной ошибкой раньше режима и подключения к БД |
| `TEST_SENDER_MODE` | Обязательное `inprocess` или `http` после trim/lowercase |
| `TEST_SIMULATE_ERRORS` | Отсутствует → `false`; задано → `bool.TryParse` после trim |
| `TEST_OPERATOR_URL` | Только в HTTP-режиме: абсолютный HTTP(S) origin с host, без userinfo, query, fragment и пути кроме `/` |
| `TEST_OPERATOR_TIMEOUT_SECONDS` | Только в HTTP-режиме: по умолчанию 5; конечное число `(0, 30]`, invariant culture; округление вверх до миллисекунды |
| `ConnectionStrings:Postgres` | Непустая строка подключения; для Npgsql настроен command timeout 30 секунд |

В inprocess-режиме URL и входное значение timeout не валидируются. API и Migrator также проверяют настройки оператора, хотя сами не регистрируют отправку. Ошибки настройки оператора завершают процесс кодом `1` с сообщением о ключе; прочие перехваченные ошибки верхнего уровня выводят тип исключения без его подробностей. Нормальное завершение возвращает `0`.

## Requirements

### Requirement: Порядок источников конфигурации

После очистки исходных источников приложение SHALL загружать из `AppContext.BaseDirectory` обязательный `appsettings.packcall.json`, затем optional `appsettings.json`, `appsettings.{environment}.json`, environment с префиксами `DOTNET_`, `ASPNETCORE_`, все environment variables и аргументы командной строки именно в таком порядке. Поздний источник SHALL заменять прежнее значение, без fallback при невалидном позднем значении. JSON reload выключен.

#### Scenario: Поздняя некорректная настройка

- **GIVEN** ранний источник содержит корректное значение, поздний — некорректное
- **WHEN** выполняется разбор итоговой конфигурации
- **THEN** возвращается ошибка позднего значения вместо использования раннего.

### Requirement: Явный тестовый режим и один sender

Приложения SHALL отвергать `prod` до настройки хранилища. Worker SHALL регистрировать ровно один `IMessageSender` по выбранному режиму и передавать политику симуляции в обработку.

#### Scenario: Попытка prod-запуска

- **WHEN** `PACKCALL_ENVIRONMENT=prod`
- **THEN** процесс завершается с `1` и `PACKCALL_ENVIRONMENT=prod is not supported`, до обращения к хранилищу.

#### Scenario: HTTP-режим

- **GIVEN** заданы корректные HTTP-настройки
- **WHEN** регистрируется обработка доставок
- **THEN** выбирается `HttpTestEmailSender`, а не одновременно два sender.

### Requirement: Проверка настроек воркера

Worker SHALL использовать секцию `DeliveryWorker`, проверять её при старте и в конструкторе фонового сервиса.

| Параметр | По умолчанию | Ограничение |
|---|---|---|
| `Parallelism` | 4 | 1..64 |
| `BatchSize` | 10 | Не менее 1 |
| `LockDuration` | 90 секунд | Не менее 10 секунд |
| `IdleDelay` | 250 мс | Положительный |
| `RecoveryInterval` | 15 секунд | Положительный, менее 2 минут |
| `ErrorDelay` | 5 секунд | Положительный |

Сумма `LockDuration + RecoveryInterval` SHALL быть не более 2 минут.

#### Scenario: Неположительный пакет

- **WHEN** `BatchSize=0`
- **THEN** конфигурация воркера отклоняется до выполнения обработки.

### Requirement: Идентичность и остановка воркера

Штатный Worker SHALL создавать идентификатор из имени машины, обрезанного до 200 символов, и случайного GUID, использовать его в обработке и контексте логирования. Таймаут graceful shutdown Worker SHALL составлять 30 секунд; Compose задаёт API и Worker grace period 40 секунд.

#### Scenario: Перезапуск процесса

- **WHEN** Worker запускается вновь
- **THEN** он получает новый случайный суффикс идентификатора и не принимает прежний идентификатор как постоянную идентичность процесса.

### Requirement: Фактические варианты Compose и HTTP-диагностики

Базовый Compose SHALL описывать API, Worker, Migrator и PostgreSQL; API и Worker зависят от успешного завершения Migrator, Migrator — от healthcheck БД. HTTP-overlay SHALL добавлять тестовый оператор и зависимость Worker от его healthcheck. Development override SHALL публиковать API на `8080` и PostgreSQL на `5432`. OpenAPI и Swagger UI SHALL включаться API только при ASP.NET environment `Development`.

#### Scenario: Development override

- **WHEN** используется существующий `docker-compose.override.yml`
- **THEN** задаётся `ASPNETCORE_ENVIRONMENT=Development` и публикация обоих портов; `PACKCALL_ENVIRONMENT=test` само по себе не включает Swagger.

## Реализация и существующие тесты

- [PackCallHosting](../../../src/PackCall.Infrastructure/Hosting/PackCallHosting.cs), [TestOperatorSettings](../../../src/PackCall.Infrastructure/Hosting/TestOperatorSettings.cs), [DI](../../../src/PackCall.Infrastructure/ServiceCollectionExtensions.cs), [DeliveryWorkerOptions](../../../src/PackCall.Worker/Background/DeliveryWorkerOptions.cs).
- [Worker entrypoint](../../../src/PackCall.Worker/Program.cs), [API entrypoint](../../../src/PackCall.Api/Program.cs), [Migrator entrypoint](../../../src/PackCall.Migrator/Program.cs), [общий JSON](../../../config/appsettings.packcall.json).
- [Compose](../../../docker-compose.yml), [Development override](../../../docker-compose.override.yml), [HTTP overlay](../../../docker-compose.test-http.yml).
- [TestOperatorTests](../../../PackCall.Core.Test/TestOperatorTests.cs): `RequiredKeysNeverFallback`, `ProdGuardPrecedesModeAndStorage`, `StrictBool`, `InvalidBool`, `InvalidUrlsNeverLeakInput`, `InvalidTimeoutsFail`, `LastConfigurationSourceWinsWithoutValidValueFallback`, `PositiveTimeoutRoundsUpToTimerResolution`, `OneSender`, `ProductionRegistrationMapsSimulateErrorsIntoProcessing`.
- [DeliveryWorkerTests](../../../PackCall.Core.Test/DeliveryWorkerTests.cs): `Worker_RejectsNonPositiveBatchSize`, `Worker_CapturesProcessesAndRecoversWithAnAssignedId`.
- [Приёмка настроек](../../../tests/acceptance/story-11-1-test-email-operator/config_matrix.py), [приоритетов](../../../tests/acceptance/story-11-1-test-email-operator/precedence.py), [остановки](../../../tests/acceptance/story-11-1-test-email-operator/shutdown.py), [preflight](../../../tests/acceptance/story-11-1-test-email-operator/preflight.py).

## Спорное поведение и вероятные дефекты

- Compose требует явно задать часть ключей, для которых C# допускает отсутствие или игнорирует значение в inprocess-режиме. Условия Compose и парсера не тождественны.
- `PACKCALL_ENVIRONMENT` и ASP.NET/.NET environment — разные настройки с разными назначениями.
- В коде запуска нет автоматического вызова acceptance `preflight.py`. Его наличие не гарантирует совпадение настроек независимо запущенных процессов.
- Development override публикует PostgreSQL наружу хоста без привязки к loopback в файле. Утверждение «опубликован только API» для этого варианта неверно.
- Таймауты shutdown и конфигурационное ограничение восстановления не дают гарантии завершения внешнего побочного эффекта за это время.

## Области, которые не удалось проверить

.NET-тесты запущены командой `dotnet test PackCall.Core.Test/PackCall.Core.Test.csproj --no-restore --verbosity minimal -m:1 -nr:false -p:BuildInParallel=false`: сборка тестового проекта завершилась, тестовый запуск был прерван без итогового результата. Python contract также прерван без результата. Повторный прогон при записи спецификаций не выполнялся. Compose-матрица, приоритеты источников на реальных процессах, shutdown-приёмка и доступность образов не проверены запуском в этой работе.

Недоступны указанные в AGENTS.md проектные навыки; отсутствует рабочий каталог `docs`. Содержимое удалённых документов не использовалось как доказательство текущей реализации. CLI `openspec` недоступен в PATH; официальная валидация OpenSpec не выполнена. Проверка Markdown-структуры и ссылок не заменяет её.
