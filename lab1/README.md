# Unity Docs RAG

Учебный проект локальной RAG-системы по официальной Unity 6.3 LTS Scripting API. Разрешённая область корпуса: `https://docs.unity3d.com/6000.3/Documentation/ScriptReference/`. Unity Manual, другие версии Unity и сторонние сайты не входят в корпус.

## Выбранный стек

- C#, .NET 10 и ASP.NET Core;
- PostgreSQL 17 с pgvector и HNSW для будущего хранения документов, metadata и embeddings;
- Ollama для локального запуска моделей;
- Docker Compose для локальной инфраструктуры.

Ollama — это локальная среда запуска моделей, а не готовая RAG-система. Для проекта выбраны модели `embeddinggemma` для embeddings и `qwen3:4b` для генерации ответов на русском и английском. Размерность embeddings `embeddinggemma` — 768.

## Предварительные требования

- Git;
- .NET SDK 10;
- Docker Desktop с Docker Compose;
- не менее 16 ГБ RAM для рекомендуемого локального запуска.

## Первый запуск в Windows CMD

Откройте CMD и перейдите в каталог проекта:

```bat
git clone https://github.com/VadimSiminenco/PAD.git
cd PAD\lab1
```

Создайте локальный файл настроек из примера и вручную замените примерный пароль PostgreSQL в `.env`:

```bat
copy .env.example .env
```

Проверьте конфигурацию и запустите зависимости:

```bat
docker compose --env-file .env config --quiet
docker compose --env-file .env up -d
docker compose --env-file .env ps
```

Дождитесь статуса `healthy` у контейнера PostgreSQL перед дальнейшим использованием базы.

## Первичная загрузка моделей

Загрузите выбранные модели в Ollama и проверьте список:

```bat
docker compose --env-file .env exec ollama ollama pull embeddinggemma
docker compose --env-file .env exec ollama ollama pull qwen3:4b
docker compose --env-file .env exec ollama ollama list
```

## Проверка pgvector

При стандартных значениях из `.env.example` выполните:

```bat
docker compose --env-file .env exec postgres psql -U unitydocs -d unitydocs -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
```

Если имя пользователя или базы изменено в `.env`, замените `unitydocs` в команде соответствующими значениями.

## Схема базы данных

SQL-скрипт `001_enable_vector.sql` включает расширение pgvector (`vector`), а `002_create_rag_schema.sql` создаёт схему `rag`, таблицы документов, chunks, embedding-профилей и embeddings, а также индексы, включая HNSW для cosine-поиска текущей модели. Колонка `embedding` имеет тип `vector` без фиксированной размерности, чтобы хранить профили разных моделей; для каждого профиля и размерности нужен отдельный partial HNSW index.

Docker entrypoint автоматически выполняет SQL init scripts по порядку только при первом создании пустого PostgreSQL volume. Для существующего volume ничего удалять не нужно: примените идемпотентный `002` вручную из Windows CMD в каталоге `lab1`:

```bat
docker compose --env-file .env up -d postgres
docker compose --env-file .env exec -T postgres psql -U unitydocs -d unitydocs < sql\002_create_rag_schema.sql
```

Повторный запуск `002_create_rag_schema.sql` безопасен. Проверить таблицы и индексы можно командами:

```bat
docker compose --env-file .env exec postgres psql -U unitydocs -d unitydocs -c "\dt rag.*"
docker compose --env-file .env exec postgres psql -U unitydocs -d unitydocs -c "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'rag' ORDER BY indexname;"
```

Команды используют локальный `.env`; не публикуйте его содержимое или реальный пароль.

## Повторный запуск

PostgreSQL и модели Ollama хранятся в Docker volumes и сохраняются после остановки контейнеров. При наличии моделей в volume `unitydocs_ollama` скачивать их повторно не требуется.

```bat
docker compose --env-file .env up -d
docker compose --env-file .env ps
docker compose --env-file .env down
```

Не выполняйте `docker compose down -v`, если не намерены удалить локальные данные PostgreSQL. Не добавляйте `.env` в Git. Веса моделей и содержимое Docker volumes в GitHub не хранятся.

## Текущее состояние и наблюдаемость

Docker Compose запускает инфраструктуру PostgreSQL и Ollama. Локальная команда `ask` выполняет определение русского/английского языка, semantic search, пороговые domain/evidence gates и генерацию ответа с citations. Полный Unity corpus ещё не загружен; Langfuse и reranking будут отдельными этапами.

### Задать вопрос

Из каталога `lab1` задайте connection string PostgreSQL в указанной конфигурацией переменной окружения и запустите:

```bat
dotnet run --project src/UnityDocsRag.Ingestion -- ask "How do I set a NavMeshAgent destination?"
dotnet run --project src/UnityDocsRag.Ingestion -- ask "Как задать точку назначения NavMeshAgent?" configs\ask.json
```

Файл `configs/ask.json` содержит несекретные настройки моделей, retrieval и порогов. Пароль и connection string в него не помещаются: по умолчанию строка подключения читается из `UNITYDOCS_POSTGRES_CONNECTION_STRING`.

Команда различает три основных результата:

- `OutOfDomain` — retrieval не нашёл достаточно близких результатов, чтобы считать вопрос относящимся к Unity;
- `InsufficientEvidence` — вопрос близок к Unity, но similarity найденной документации ниже порога для обоснованного ответа;
- `Answered` — ответ сформирован по chunks, прошедшим evidence threshold, и сопровождается citations.

## Grabber Unity Scripting API

Grabber читает официальный `docdata/toc.js` со страницы Unity Scripting API, рекурсивно обходит его структуру, проверяет ссылки по allowlist версии 6000.3 и загружает страницы последовательно. Текущий безопасный лимит — 5 страниц за запуск; полный crawl намеренно ограничен. Полный корпус не загружается.

Запускайте команду из каталога `lab1`:

```bat
dotnet run --project src/UnityDocsRag.Ingestion -- configs/ingestion.json
```

Исходный HTML сохраняется в `data/raw/unity-6000.3`, а manifest — в `data/state/unity-6000.3-manifest.json`. При повторном запуске страницы с тем же содержимым показываются как `Unchanged`; дубликаты не создаются и HTML не перезаписывается. Папки `lab1/data/raw/` и `lab1/data/state/` содержат generated data и исключены из Git.

Grabber сохраняет локальный файловый cache; preprocessing, chunking и indexing выполняются отдельными командами. Команда `ask` использует уже созданный PostgreSQL индекс. API, Langfuse, reranking и полный crawl Unity corpus не входят в текущую реализацию.
