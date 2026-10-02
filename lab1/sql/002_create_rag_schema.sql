BEGIN;

CREATE SCHEMA IF NOT EXISTS rag;

CREATE TABLE IF NOT EXISTS rag.documents (
    document_id text PRIMARY KEY,
    canonical_url text NOT NULL UNIQUE,
    title text NOT NULL,
    unity_version text NOT NULL,
    content text NOT NULL,
    content_hash text NOT NULL,
    retrieved_at timestamptz NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_documents_values_nonempty CHECK (
        btrim(document_id) <> '' AND btrim(canonical_url) <> '' AND
        btrim(title) <> '' AND btrim(unity_version) <> '' AND btrim(content) <> ''
    ),
    CONSTRAINT ck_documents_content_hash CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_documents_metadata_object CHECK (jsonb_typeof(metadata) = 'object')
);

CREATE TABLE IF NOT EXISTS rag.chunks (
    chunk_id text PRIMARY KEY,
    document_id text NOT NULL REFERENCES rag.documents(document_id) ON DELETE CASCADE,
    section text NULL,
    ordinal integer NOT NULL,
    approximate_token_count integer NOT NULL,
    content text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_chunks_document_ordinal UNIQUE (document_id, ordinal),
    CONSTRAINT ck_chunks_id_hash CHECK (chunk_id ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_chunks_ordinal_nonnegative CHECK (ordinal >= 0),
    CONSTRAINT ck_chunks_token_count_positive CHECK (approximate_token_count > 0),
    CONSTRAINT ck_chunks_content_nonempty CHECK (btrim(content) <> '')
);

-- uq_chunks_document_ordinal already provides the btree access path for
-- (document_id, ordinal), so a second ordinary index on the same columns is redundant.

CREATE TABLE IF NOT EXISTS rag.embedding_profiles (
    profile_key text PRIMARY KEY,
    provider text NOT NULL,
    model_name text NOT NULL,
    dimension integer NOT NULL,
    multilingual boolean NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_embedding_profiles_provider_model_dimension UNIQUE (provider, model_name, dimension),
    CONSTRAINT uq_embedding_profiles_key_dimension UNIQUE (profile_key, dimension),
    CONSTRAINT ck_embedding_profiles_strings_nonempty CHECK (
        btrim(profile_key) <> '' AND btrim(provider) <> '' AND btrim(model_name) <> ''
    ),
    CONSTRAINT ck_embedding_profiles_dimension CHECK (dimension > 0 AND dimension <= 2000)
);

CREATE TABLE IF NOT EXISTS rag.chunk_embeddings (
    chunk_id text NOT NULL REFERENCES rag.chunks(chunk_id) ON DELETE CASCADE,
    profile_key text NOT NULL,
    dimension integer NOT NULL,
    embedding vector NOT NULL,
    embedded_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_chunk_embeddings PRIMARY KEY (chunk_id, profile_key),
    CONSTRAINT fk_chunk_embeddings_profile_dimension
        FOREIGN KEY (profile_key, dimension)
        REFERENCES rag.embedding_profiles(profile_key, dimension) ON DELETE CASCADE,
    CONSTRAINT ck_chunk_embeddings_dimension CHECK (dimension > 0 AND dimension <= 2000),
    CONSTRAINT ck_chunk_embeddings_vector_dimension CHECK (vector_dims(embedding) = dimension)
);

-- The primary key starts with chunk_id, so it cannot efficiently serve
-- profile-only filtering (or the profile/dimension foreign key).
CREATE INDEX IF NOT EXISTS idx_chunk_embeddings_profile_key
    ON rag.chunk_embeddings (profile_key);

-- Keep a separate partial HNSW index for the current 768-dimensional profile.
-- Additional embedding models/dimensions require their own profile-specific partial index.
-- Queries must constrain profile_key to this predicate and ORDER BY the same
-- embedding::vector(768) expression with cosine distance (<=>).
CREATE INDEX IF NOT EXISTS idx_chunk_embeddings_ollama_embeddinggemma_768_hnsw
    ON rag.chunk_embeddings
    USING hnsw ((embedding::vector(768)) vector_cosine_ops)
    WHERE profile_key = 'ollama:embeddinggemma:768';

COMMIT;
