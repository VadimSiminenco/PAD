using System.Collections.ObjectModel;
using System.Text.Json;
using UnityDocsRag.Core.Generation;

namespace UnityDocsRag.Evaluation;

public enum EvaluationCategory
{
    InCorpus,
    UnityWithoutCorpusEvidence,
    OutOfDomain
}

public sealed class EvaluationQuestion
{
    internal EvaluationQuestion(
        string id,
        string language,
        EvaluationCategory category,
        string text,
        AnswerStatus expectedStatus,
        bool requiresMultipleSources,
        IReadOnlyList<string> expectedSourceUrls,
        IReadOnlyList<string> expectedFacts)
    {
        Id = id;
        Language = language;
        Category = category;
        Text = text;
        ExpectedStatus = expectedStatus;
        RequiresMultipleSources = requiresMultipleSources;
        ExpectedSourceUrls = expectedSourceUrls;
        ExpectedFacts = expectedFacts;
    }

    public string Id { get; }
    public string Language { get; }
    public EvaluationCategory Category { get; }
    public string Text { get; }
    public AnswerStatus ExpectedStatus { get; }
    public bool RequiresMultipleSources { get; }
    public IReadOnlyList<string> ExpectedSourceUrls { get; }
    public IReadOnlyList<string> ExpectedFacts { get; }
}

public sealed class EvaluationDataset
{
    private EvaluationDataset(int version, string unityVersion, IReadOnlyList<EvaluationQuestion> questions)
    {
        Version = version;
        UnityVersion = unityVersion;
        Questions = questions;
    }

    public int Version { get; }
    public string UnityVersion { get; }
    public IReadOnlyList<EvaluationQuestion> Questions { get; }

    public static EvaluationDataset Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    public static EvaluationDataset Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Root must be an object.");
            }

            var versionElement = Required(root, "Version", JsonValueKind.Number);
            if (!versionElement.TryGetInt32(out var version) || version != 1)
            {
                throw Invalid("Version must be 1.");
            }

            var unityVersion = NonEmptyString(root, "UnityVersion");
            var questionElements = Required(root, "Questions", JsonValueKind.Array);
            if (questionElements.GetArrayLength() == 0)
            {
                throw Invalid("Questions must not be empty.");
            }

            var questions = new List<EvaluationQuestion>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in questionElements.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("Each question must be an object.");
                }

                var id = NonEmptyString(element, "Id");
                if (!ids.Add(id))
                {
                    throw Invalid($"Duplicate question ID: {id}.");
                }

                var language = NonEmptyString(element, "Language");
                if (language is not ("ru" or "en"))
                {
                    throw Invalid($"Question {id}: Language must be ru or en.");
                }

                var categoryText = NonEmptyString(element, "Category");
                if (!Enum.TryParse<EvaluationCategory>(categoryText, ignoreCase: false, out var category)
                    || !Enum.IsDefined(category))
                {
                    throw Invalid($"Question {id}: invalid Category.");
                }

                var statusText = NonEmptyString(element, "ExpectedStatus");
                if (!Enum.TryParse<AnswerStatus>(statusText, ignoreCase: false, out var status)
                    || status is not (AnswerStatus.Answered or AnswerStatus.InsufficientEvidence or AnswerStatus.OutOfDomain))
                {
                    throw Invalid($"Question {id}: invalid ExpectedStatus.");
                }

                var expectedStatus = category switch
                {
                    EvaluationCategory.InCorpus => AnswerStatus.Answered,
                    EvaluationCategory.UnityWithoutCorpusEvidence => AnswerStatus.InsufficientEvidence,
                    EvaluationCategory.OutOfDomain => AnswerStatus.OutOfDomain,
                    _ => throw Invalid($"Question {id}: invalid Category.")
                };
                if (status != expectedStatus)
                {
                    throw Invalid($"Question {id}: Category and ExpectedStatus disagree.");
                }

                var text = NonEmptyString(element, "Text");
                var multipleElement = Required(element, "RequiresMultipleSources", JsonValueKind.True, JsonValueKind.False);
                var requiresMultipleSources = multipleElement.GetBoolean();
                var urls = StringArray(element, "ExpectedSourceUrls");
                var facts = StringArray(element, "ExpectedFacts");
                if (status == AnswerStatus.Answered)
                {
                    if (urls.Count == 0 || facts.Count == 0)
                    {
                        throw Invalid($"Question {id}: Answered requires source URLs and facts.");
                    }

                    foreach (var url in urls)
                    {
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                            || uri.Scheme is not ("http" or "https"))
                        {
                            throw Invalid($"Question {id}: ExpectedSourceUrls contains an invalid URL.");
                        }
                    }

                    if (requiresMultipleSources && urls.Count < 2)
                    {
                        throw Invalid($"Question {id}: multiple sources require at least two URLs.");
                    }
                }
                else if (urls.Count != 0 || facts.Count != 0 || requiresMultipleSources)
                {
                    throw Invalid($"Question {id}: refusal must not contain expected evidence.");
                }

                questions.Add(new EvaluationQuestion(id, language, category, text, status,
                    requiresMultipleSources, urls, facts));
            }

            return new EvaluationDataset(version, unityVersion, questions.AsReadOnly());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Evaluation dataset is not valid JSON.", exception);
        }
    }

    private static JsonElement Required(JsonElement parent, string name, params JsonValueKind[] kinds)
    {
        if (!parent.TryGetProperty(name, out var value) || !kinds.Contains(value.ValueKind))
        {
            throw Invalid($"Missing or invalid {name}.");
        }

        return value;
    }

    private static string NonEmptyString(JsonElement parent, string name)
    {
        var value = Required(parent, name, JsonValueKind.String).GetString();
        return string.IsNullOrWhiteSpace(value) ? throw Invalid($"{name} must not be empty.") : value;
    }

    private static IReadOnlyList<string> StringArray(JsonElement parent, string name)
    {
        var values = new List<string>();
        foreach (var element in Required(parent, name, JsonValueKind.Array).EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw Invalid($"{name} must contain non-empty strings.");
            }

            values.Add(element.GetString()!);
        }

        return new ReadOnlyCollection<string>(values);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
