# test-email-operator Specification

## Purpose

Тестовая имитация приёма email-сообщения: локальный `MockEmailSender` или отдельный HTTP-сервис Python/FastAPI. SMTP, реальные почтовые провайдеры, доставка в ящик и дедупликация по delivery ID не реализованы.

Базовая спецификация по коду на 2026-10-31. SHALL описывает текущий контракт; тесты перечислены как существующие проверки, без утверждения об успешном прогоне.

## Входы, результаты и ошибки

HTTP: `POST /v1/email/send`, `Content-Type: application/json`, объект ровно из шести полей: `delivery_id`, `campaign_id`, `recipient_id`, `recipient_email`, `message_text`, `simulate_errors`. UUID — непустые строки стандартной формы с дефисами; флаг — JSON boolean. Текст должен содержать непробельный символ; email проверяется синтаксически, с ограничением 255 единиц UTF-16. Валидация не проверяет существование получателя в БД.

| Ответ HTTP-сервиса | Смысл |
|---|---|
| `200`, `{"result":"accepted","code":"AcceptedByProvider"}` | Подтверждённый приём |
| `200`, `{"result":"rejected","code":"SimulatedOperatorRejected"}` | Подтверждённый симулированный отказ |
| `415`, `{"error":{"code":"UnsupportedMediaType"}}` | Иной Content-Type |
| `422`, `{"error":{"code":"InvalidRequest"}}` | Некорректный JSON или payload |
| `500`, `{"error":{"code":"ServiceError"}}` | Перехваченная внутренняя ошибка |

`GET /healthz` возвращает `200`, `{"status":"ok"}`. Inprocess-режим получает типизированный `SendRequest` и отдельный bool; перед решением проверяет UUID, email и сообщение.

## Requirements

### Requirement: Независимая симуляция результата

При `simulate_errors=false` оператор SHALL подтверждать корректный запрос без случайного выбора. При `true` каждый корректный вызов SHALL независимо возвращать отказ при значении генератора `< 0.01`, иначе приём. Оба режима SHALL использовать одинаковые коды подтверждённых результатов.

#### Scenario: Граница вероятности

- **GIVEN** симуляция включена
- **WHEN** генератор возвращает `0.009999`, затем `0.01`
- **THEN** первый результат — отказ, второй — приём.

#### Scenario: Симуляция выключена

- **WHEN** корректный запрос содержит `simulate_errors=false`
- **THEN** результат — приём, генератор не вызывается.

### Requirement: Проверка HTTP-запроса

HTTP-сервис SHALL отклонять отсутствующие и лишние поля, неверные UUID, неподходящие типы, некорректные email и пустое сообщение до определения результата. Неверный запрос SHALL NOT становиться симулированным отказом.

#### Scenario: Флаг передан строкой

- **WHEN** `simulate_errors` равен строке `"true"`
- **THEN** ответ — `422` с `InvalidRequest`.

#### Scenario: Неверный тип содержимого

- **WHEN** тело отправлено без Content-Type `application/json`
- **THEN** ответ — `415` с `UnsupportedMediaType`.

### Requirement: Строгий разбор ответа адаптером

C# HTTP-адаптер SHALL отправлять все шесть полей, включая явный boolean, на `/v1/email/send`; не следовать redirects и не использовать cookies. Только `200` с `application/json`, телом не более 4096 байт и ровно двумя строковыми полями `result` и `code` с одной из двух согласованных пар SHALL давать `SendResult`.

#### Scenario: Неизвестная пара результата

- **WHEN** ответ `200` содержит неизвестные `result`/`code` либо лишнее поле
- **THEN** адаптер выбрасывает `TestOperatorProtocolException`, без подтверждённого результата.

### Requirement: Разделение ошибок адаптера

Адаптер SHALL различать `422` как validation error, `5xx` как service error, остальные непредусмотренные статусы/Content-Type/JSON/размер как protocol error, ошибки соединения/чтения как transport error, собственный deadline как timeout. Внешняя отмена SHALL оставаться `OperationCanceledException`.

#### Scenario: Таймаут не является отказом оператора

- **WHEN** истекает настроенное время ожидания без внешней отмены
- **THEN** возникает `TestOperatorTimeoutException`, а не `SendResult.Failure`.

#### Scenario: Повтор того же payload

- **GIVEN** симуляция выключена
- **WHEN** одинаковый корректный payload отправляется несколько раз
- **THEN** каждый вызов подтверждается независимо; HTTP-сервис не хранит ключи дедупликации.

## Реализация и существующие тесты

- [MockEmailSender](../../../src/PackCall.Infrastructure/Sending/MockEmailSender.cs), [HttpTestEmailSender](../../../src/PackCall.Infrastructure/Sending/HttpTestEmailSender.cs), [исключения](../../../src/PackCall.Infrastructure/Sending/TestOperatorExceptions.cs).
- [Python-сервис](../../../src/PackCall.TestEmailOperator/app.py), [Dockerfile](../../../src/PackCall.TestEmailOperator/Dockerfile): непривилегированный пользователь, access log выключен; [Compose overlay](../../../docker-compose.test-http.yml): `read_only` и healthcheck, без volume оператора.
- [TestOperatorTests](../../../PackCall.Core.Test/TestOperatorTests.cs): `MockIndependentDrawPerCallAndExplicitFalse`, `InvalidRequestIsNeverConfirmedRejection`.
- [OperatorHttpTests](../../../PackCall.Core.Test/OperatorHttpTests.cs): `ActualHttpPayloadContainsEveryBooleanAndParsesConfirmedResults`, `ErrorsNeverBecomeConfirmedResults`, `ResponseLimitTimeoutAndExternalCancellation`, `DisconnectAndConnectionRefusedAreTransportErrors`, `SharedEmailFixturesMatchProductValidation`.
- [Python contract](../../../tests/acceptance/story-11-1-test-email-operator/operator_contract.py): `test_independent_boundaries`, `test_http_validation`, `test_safe_exception`, `test_email_parity`, `test_health_and_repeat`; [privacy-приёмка](../../../tests/acceptance/story-11-1-test-email-operator/privacy.py), [retention-приёмка](../../../tests/acceptance/story-11-1-test-email-operator/retention.py).

## Спорное поведение и вероятные дефекты

- Python проверяет email собственной регулярной логикой и требует NFC; C# использует `MailAddress`. Общие fixtures проверяют конечный набор примеров, но не доказывают эквивалентность для любых Unicode-строк.
- Приложение оператора не хранит запросы и не добавляет access log; это не гарантия отсутствия payload в любой внешней инфраструктуре или при другом способе запуска.
- Вероятность 1% не означает ровно 1% отказов в конкретной партии. Хранения истории результатов и воспроизведения случайной последовательности в API нет.

## Области, которые не удалось проверить

.NET-набор и Python contract были запущены и прерваны без итогового результата; успешное прохождение не подтверждено. Privacy/retention, Compose-приёмка и реальное взаимодействие контейнеров в этой работе не запускались.
