# Story 2.3. Репетиция миграции и отката

## Решение

Репетиция пройдена. Восстановление одного и того же архива в двух пустых базах подтверждено сверкой. Исторические пустые адреса преобразованы в NULL по правилу миграции. Из них 10 000 доставок остаются в `Waiting` или `InProgress` в 75 приостановленных кампаниях. При возобновлении этих кампаний Worker не захватит такие доставки даже после истечения аренды. До выпуска beta.3 нужно согласовать судьбу таких кампаний в Story 2.4. Замер на синтетических данных не устанавливает число затронутых кампаний в рабочей БД.

## Ревизии и среда

| Роль | Ревизия |
| --- | --- |
| Кандидат `gv` | `b3ae6be64c22f8ed366ad6b1085be9a6718f390f` |
| Источник `beta.1` | `96272c849a53012f8213934d8f2364b2282c7eeb` |
| Откат `v0.5.0-beta.2` | `113d228b6803f9a2b2c98a007925764dc3f5b7b2` |

Кандидат сверен с удалённой `gv` командой `git ls-remote --heads origin gv` перед замером. Образы собраны локально из снимков Git, GitHub CI не запускался. Docker Desktop Engine 29.8.2, Linux x86_64, 16 CPU, 22 435 520 512 байт памяти. PostgreSQL 18.6 Alpine 3.24, образ `postgres@sha256:9ba53c7d41ea093baf5c1ba635ba5434c4f979880527d49bb7d9237d3193085b`. `shared_buffers` 128 МБ, `work_mem` 4 МБ, `maintenance_work_mem` 64 МБ, локаль `en_US.utf8`, провайдер libc.

Контейнеры подключены только к внутренней сети Docker без опубликованных портов. Worker в обоих окнах использует HTTP-режим и синтетический оператор в той же сети. В источнике работал только подготовительный Migrator, перед дампом все записи были завершены, а измерительные подключения выполняли только чтение.

## Выборка

Сгенерированы 10 000 получателей и 101 кампания, из которых 100 приостановлены и имеют по 10 000 доставок, а одна осталась пустым черновиком. Из 1 000 000 доставок по 250 000 находятся в каждом из четырёх статусов. У всех 250 000 `InProgress` сохранены идентификатор Worker и срок аренды. Сроки аренды заданы после даты репетиции, чтобы запуск Worker после сверки не обработал исторические доставки. В журнале 1 250 000 переходов. У 1 000 получателей текущий email записан с пробельными символами и верхним регистром. Исторические адреса отличаются от текущих, 10 000 непустых записаны неканонически, 10 000 пустые, ещё 10 000 состоят только из пробельных символов. Поле beta.1 имеет ограничение `NOT NULL`.

| После миграции | Всего | NULL в снимке |
| --- | ---: | ---: |
| Failed | 250 000 | 5 000 |
| Waiting | 250 000 | 5 000 |
| InProgress | 250 000 | 5 000 |
| Sent | 250 000 | 5 000 |

Размер исходной БД в начале замера 543 086 271 байт, размер архива 86 565 162 байта. SHA-256 архива `6a8fe7d31e781db0af62ca1b2319aaa7f52a61bc8d8f4667cfe31907c224bdd9`.

## Длительность

| Этап | Секунды |
| --- | ---: |
| Исходный Migrator beta.1 | 1,433 |
| Создание выборки | 52,973 |
| Получение исходных хешей | 15,313 |
| Дамп | 4,734 |
| Восстановление копии кандидата | 7,625 |
| Контрольные чтения до миграции | 0,266 |
| Migrator кандидата с замерами блокировок | 31,578 |
| Сверка кандидата | 11,109 |
| Готовность Api и Worker кандидата | 1,922 |
| **Окно применения** | **57,234** |
| Восстановление копии отката | 7,781 |
| Сверка копии отката | 7,625 |
| Готовность Api и Worker beta.2 | 2,641 |
| **Окно отката** | **18,047** |
| **Два окна, лимит 1 800 секунд** | **75,281** |
| Дымовой прогон кандидата | 1,297 |
| Дымовой прогон beta.2 | 3,297 |

Окно применения измерено от вызова `pg_dump` до успешной проверки готовности кандидата. Окно отката измерено от создания пустой базы под `pg_restore` того же архива до готовности beta.2. Оба интервала получены монотонным таймером. Готовность включает прохождение SchemaGate обеими службами, сообщение о старте обработчика и успешный запрос списка кампаний к API. Измерения относятся к этому набору данных и компьютеру.

## Блокировки

Опрос `pg_locks` и `pg_blocking_pids` на копии кандидата подтвердил, что Migrator удерживал `AccessExclusiveLock` на `deliveries` и `recipients`. Оба пробных чтения ожидали `AccessShareLock`, а их блокирующий PID принадлежал Migrator.

| Чтение `SELECT 1 … LIMIT 1` | До миграции, с | Во время миграции, с | Исход |
| --- | ---: | ---: | --- |
| campaigns | 0,109 | 0,094 | Успешно, ожидание блокировки не зафиксировано |
| deliveries | 0,079 | 29,016 | Успешно после фиксации миграции |
| recipients | 0,078 | 29,016 | После фиксации ожидаемая ошибка отсутствующей таблицы |

Время чтения включает запуск команды Docker и подключение psql. Отнесение ожидания к Migrator подтверждено отдельно снимками блокировок. Рабочая нагрузка, длительные клиентские транзакции и конкурирующие записи отсутствовали.

## Сверка

Источник имел ровно одну EF-миграцию `20261028193126_InitialCreate`. У кандидата появилась `20261113214910_ContactsChannels`, история EF после восстановления снова совпала с исходной.

У кандидата 10 000 контактов и 10 000 email-каналов. Отсортированные хеши сравнивались с источником после ожидаемой нормализации и включали все идентификаторы, связи, статусы, даты, аренды и журнал. Хеш исторических снимков сохранён отдельно от хеша истории доставок. Совпадение с ожидаемым снимком включает строки с NULL и строки, где исторический адрес отличается от текущего email. Состав `campaign_contacts` и `campaign_channels` сравнивался с точными множествами из доставок в обе стороны. Обе таблицы снимков пусты для кампании без доставок. При откате сверены исходные строки включая ненормализованные email и адреса, а также позиция последовательности журнала.

Дымовой прогон вынесен за пределы окон. Для каждого релиза после сверки создан черновик с одной синтетической доставкой. SQL ограничивает аудиторию одной доставкой, поскольку создание кампании через API выбирает всех получателей. Запуск выполнен через `POST /api/campaigns/{id}/start`. API подтвердил состояние `Completed`, а строка доставки и журнал подтвердили `Sent` и `AcceptedByProvider`.

До первого полного замера был записан `FAIL-RECORDED`. Сверка остановилась при подстановке целого числа в булево выражение SQL. Проверка исправлена, сбой возник до дампа и миграции. Кандидат и исходная выборка изменений не требовали.

## Воспроизведение

Из корня репозитория, PowerShell и Python 3.11+. Нужны свободные имена сети и контейнеров ниже. Сборка и генерация выполняются до измерительных окон.

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$dir = 'artifacts/story-2-3'
New-Item -Force -ItemType Directory $dir | Out-Null
$candidate = git rev-parse gv
if ((git ls-remote --heads origin gv) -notlike "$candidate*") { throw 'gv отличается от origin/gv' }
foreach ($entry in @(@('gv','candidate'), @('beta.1','source'), @('v0.5.0-beta.2','rollback'))) {
    $ref, $role = $entry
    $sha = git rev-parse "$ref^{}"
    "$role $sha" | Add-Content "$dir/revisions.txt"
    $context = "$dir/build/$role"
    New-Item -Force -ItemType Directory $context | Out-Null
    git archive -o "$dir/$role.tar" $sha
    tar -xf "$dir/$role.tar" -C $context
    $services = switch ($role) {
        candidate { 'Migrator'; 'Api'; 'Worker' }
        source { 'Migrator' }
        rollback { 'Api'; 'Worker' }
    }
    foreach ($service in $services) {
        docker build --label "org.opencontainers.image.revision=$sha" -f "$context/src/PackCall.$service/Dockerfile" -t "packcall-rehearsal-${role}:$($service.ToLowerInvariant())" $context
    }
}
docker build -t packcall-rehearsal:testoperator "$dir/build/candidate/src/PackCall.TestEmailOperator"
docker network create --internal packcall-rehearsal-23
docker run -d --name packcall-rehearsal-23-db --network packcall-rehearsal-23 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=source postgres:18.6-alpine3.24
docker exec packcall-rehearsal-23-db sh -ec 'until pg_isready -h localhost -U postgres -d source; do sleep 1; done'
docker run --rm --network packcall-rehearsal-23 --env-file docker-compose.test-http.env -e 'ConnectionStrings__Postgres=Host=packcall-rehearsal-23-db;Database=source;Username=postgres' packcall-rehearsal-source:migrator
docker cp scripts/load-test/seed_beta1_migration_rehearsal.sql packcall-rehearsal-23-db:/tmp/seed.sql
docker exec packcall-rehearsal-23-db psql -X -U postgres -d source -f /tmp/seed.sql
python scripts/load-test/run_migration_rehearsal.py
```

Команды `pg_dump -Fc`, `pg_restore --exit-on-error`, запуск контейнеров, проверки и фиксирование блокировок находятся в [сценарии репетиции](../../../scripts/load-test/run_migration_rehearsal.py). В `artifacts/story-2-3/` остаются агрегаты, журнал замера и архив. В Git хранится этот отчёт. Дамп, значения адресов и сообщения в отчёт не выводятся.

После замера можно удалить контейнеры и сеть. Команды затрагивают только среду репетиции. Скопированный дамп сохраняется в `artifacts/story-2-3/`.

```powershell
docker rm -f packcall-rehearsal-23-candidate-api packcall-rehearsal-23-candidate-worker packcall-rehearsal-23-rollback-api packcall-rehearsal-23-rollback-worker packcall-rehearsal-23-operator
docker rm -fv packcall-rehearsal-23-db
docker network rm packcall-rehearsal-23
```
