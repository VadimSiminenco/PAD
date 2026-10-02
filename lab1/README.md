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
cd /d C:\Users\user\PAD\lab1
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

## Повторный запуск

PostgreSQL и модели Ollama хранятся в Docker volumes и сохраняются после остановки контейнеров. При наличии моделей в volume `unitydocs_ollama` скачивать их повторно не требуется.

```bat
docker compose --env-file .env up -d
docker compose --env-file .env ps
docker compose --env-file .env down
```

Не выполняйте `docker compose down -v`, если не намерены удалить локальные данные PostgreSQL. Не добавляйте `.env` в Git. Веса моделей и содержимое Docker volumes в GitHub не хранятся.

## Текущее состояние и наблюдаемость

Сейчас Docker Compose запускает только инфраструктуру PostgreSQL и Ollama, а не готовую RAG-систему. Grabber, API и RAG-компоненты пока не реализованы. Langfuse будет добавлен отдельным этапом для локальной self-hosted наблюдаемости.
