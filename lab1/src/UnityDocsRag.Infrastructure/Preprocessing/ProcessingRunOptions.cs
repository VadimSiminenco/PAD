using UnityDocsRag.Core.Configuration;

namespace UnityDocsRag.Infrastructure.Preprocessing;

public sealed class ProcessingRunOptions
{
    public string UnityVersion { get; init; } = "6000.3";
    public string RawInputDirectory { get; init; } = "data/raw/unity-6000.3";
    public string ManifestPath { get; init; } = "data/state/unity-6000.3-manifest.json";
    public string ProcessedDocumentsPath { get; init; } = "data/processed/unity-6000.3/documents.json";
    public string ChunksPath { get; init; } = "data/chunks/unity-6000.3/chunks.json";
    public int ChunkSize { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 64;

    public void Validate()
    {
        Require(UnityVersion, nameof(UnityVersion));
        Require(RawInputDirectory, nameof(RawInputDirectory));
        Require(ManifestPath, nameof(ManifestPath));
        Require(ProcessedDocumentsPath, nameof(ProcessedDocumentsPath));
        Require(ChunksPath, nameof(ChunksPath));
        if (ChunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(ChunkSize), "Chunk size must be positive.");
        if (ChunkOverlap < 0 || ChunkOverlap >= ChunkSize) throw new ArgumentOutOfRangeException(nameof(ChunkOverlap), "Chunk overlap must be non-negative and less than chunk size.");
        ValidateJsonPath(ManifestPath, nameof(ManifestPath));
        ValidateJsonPath(ProcessedDocumentsPath, nameof(ProcessedDocumentsPath));
        ValidateJsonPath(ChunksPath, nameof(ChunksPath));
        var documentOutput = Path.GetFullPath(ProcessedDocumentsPath);
        var chunksOutput = Path.GetFullPath(ChunksPath);
        var manifest = Path.GetFullPath(ManifestPath);
        if (PathsEqual(documentOutput, chunksOutput) || PathsEqual(documentOutput, manifest) || PathsEqual(chunksOutput, manifest))
            throw new ArgumentException("Manifest, processed documents, and chunks must use distinct paths.");
        var raw = Path.GetFullPath(RawInputDirectory);
        foreach (var output in new[] { ManifestPath, ProcessedDocumentsPath, ChunksPath })
        {
            if (PathsEqual(Path.TrimEndingDirectorySeparator(raw), Path.GetFullPath(output)))
                throw new ArgumentException("Raw input directory cannot be the same as an output file.", nameof(RawInputDirectory));
        }
    }

    public ChunkingOptions CreateChunkingOptions() => new() { ChunkSize = ChunkSize, ChunkOverlap = ChunkOverlap };

    private static void ValidateJsonPath(string path, string name)
    {
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must point to a .json file.", name);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value must not be empty.", name);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
