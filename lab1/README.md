# Unity Docs RAG

Локальный проект RAG по официальной Unity 6.3 LTS Scripting API. Разрешённый корпус ограничен страницами `https://docs.unity3d.com/6000.3/Documentation/ScriptReference/`; Unity Manual, другие версии Unity и сторонние источники не входят в корпус.

## Выбранный стек

- C#, .NET 10 и ASP.NET Core;
- PostgreSQL 17 с pgvector и HNSW для документов, metadata и embeddings;
- Ollama как локальная среда запуска моделей;
- Docker Compose для запуска зависимостей.

Ollama предоставляет локальный runtime для моделей и сам по себе не является готовой RAG-системой. Модели проекта: `embeddinggemma` для embeddings (размерность вектора — 768) и `qwen3:4b` для генерации ответов на русском и английском. Их файлы уже могут находиться в Docker volume `unitydocs_ollama`, поэтому повторное скачивание может не потребоваться.

## Локальный запуск зависимостей

В PowerShell из каталога `lab1` скопируйте пример переменных окружения:

```powershell
Copy-Item .env.example .env
```

Измените пароль PostgreSQL в личном `.env`, затем проверьте итоговую конфигурацию и запустите зависимости:

```powershell
docker compose --env-file .env config
docker compose --env-file .env up -d
docker compose --env-file .env ps
```

Проверить состояние PostgreSQL можно через health status в `docker compose ps`. Ollama не объявляет отдельный healthcheck; готовность API можно проверить запросом:

```powershell
Invoke-RestMethod http://localhost:11434/api/tags
```

Остановить контейнеры, сохранив данные в volumes:

```powershell
docker compose --env-file .env down
```

PostgreSQL volume создаётся отдельно и хранит базу между запусками. Ollama подключается к существующему внешнему volume с точным именем `unitydocs_ollama`; его нужно создать заранее, если он отсутствует. Инициализационный SQL включает только расширение `vector`; таблицы приложения пока не создаются.

## Наблюдаемость

Обычные системные логи приложения будут использовать стандартный `ILogger`. Локальный self-hosted Langfuse будет подключён отдельным этапом через OpenTelemetry .NET и будет запускаться отдельным Docker Compose-профилем или отдельным Compose-файлом. Langfuse предназначен для RAG/LLM traces, prompts, retrieval, generation и evaluation. Контейнеры Langfuse, ClickHouse, Redis и MinIO на текущем этапе не входят в Compose.
