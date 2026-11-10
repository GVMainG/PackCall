# Архитектура as-is (HEAD, beta.2)

Документ описывает **текущее** поведение и модель на `beta.2`, а не целевой 0.5. Целевые решения
зафиксированы в ADR (`../adr/`) и спинном документе архитектуры; расхождения с целевой моделью
(контакты/каналы/Telegram) относятся к `beta.3`/`beta.4` и здесь не заявляются.

## Компоненты

- **`src/PackCall.Core`** — домен и интерфейсы: сущности (`Campaign`, `Delivery`, `Recipient`),
  сервисы (`CampaignService`, `DeliveryProcessingService`), seam отправки (`IMessageSender`),
  диагностика/замеры. Внешних зависимостей нет (AD-2).
- **`src/PackCall.Infrastructure`** — EF Core + raw SQL, операторы (mock email inprocess/http),
  hosting-конфигурация и DI (AD-2).
- **`src/PackCall.Api`** — контроллеры (`/api/recipients`, `/api/campaigns`, `/api/deliveries`),
  DTO, middleware ошибок (ProblemDetails), OpenAPI (`MapOpenApi`, только Development).
- **`src/PackCall.Worker`** — фоновый `DeliveryWorker`: захват пакетами, закрепление, отправка,
  фиксация результата, recovery по истёкшим закреплениям (AD-4).
- **`src/PackCall.Migrator`** — единственный владелец схемы: применяет EF-миграции (AD-5);
  API/Worker проверяют схему read-only (`SchemaGate`).
- **`src/PackCall.TestEmailOperator`** — FastAPI mock-оператор email (режимы inprocess/http,
  имитация отказов).

## Модель данных (HEAD)

- **`Recipient`** (`recipients`): `id`, `email` — один email на получателя.
- **`Campaign`** (`campaigns`): `id`, `name`, `messageText`, `status`, `createdAt/updatedAt/startedAt/completedAt`.
- **`Delivery`** (`deliveries`): `id`, `campaignId`, `recipientId`, `recipientEmail` (снимок),
  `deliveryStatus`, `workerId?`, `lockExpiresAt?`, даты.
- **`CampaignStatus`**: Draft=0, InProgress=1, Completed=2, Forming=3, Paused=4, Failed=-1
  (недостижим, ADR 0001). Значения — исторические; порядок (`Forming=3` между `Completed` и
  `Paused`) не выражает хронологию состояний.
- **`DeliveryStatus`**: Waiting=0, InProgress=1, Sent=2, Failed=-1.
- **`delivery_status_journal`** — append-only, без FK, только аудит (AD-6).

Значения енумов — двуязычный контракт C#/Python (инты в БД совпадают, AD-5).

## Потоки обработки

1. **Формирование** (`Forming`): синхронно при создании кампании, создаёт доставки из получателей
   с адресом; при сбое — компенсация (FR-6).
2. **Обработка** Worker: захват пакетом (`FOR UPDATE SKIP LOCKED`), закрепление на ограниченное
   время, отправка через `IMessageSender`, фиксация результата условным `UPDATE`.
3. **Recovery**: просроченные закрепления возвращаются в очередь; неизвестный исход не терминален
   (AD-4).
4. **Завершение**: кампания становится `Completed`, когда не осталось незавершённых доставок.

## Принятые решения

Спинной документ архитектуры (AD-1…AD-8) и ADR 0001–0003 фиксируют ключевые решения:
координация только через PostgreSQL (AD-1), границы слоёв (AD-2), seam и реестр операторов (AD-3),
машина состояний и матрица писателей (AD-4), владение схемой (AD-5), журнал (AD-6), эволюция API
(AD-7), снимок выборки и уникальность (AD-8). Детали — в соответствующих ADR.

## Границы демо-продукта (NFR-8)

Аутентификация, авторизация, multi-tenancy и публичный SLA отсутствуют; API предназначен для
локального/демонстрационного стенда. Доставка получателю не гарантируется — только подтверждённый
приём оператором канала. Telegram на `beta.2` не включён.
