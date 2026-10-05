using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace UnityDocsRag.Api;

public sealed class LangfuseSettings
{
    private readonly string? _publicKey;
    private readonly string? _secretKey;

    public const string SampleRateEnvironmentVariable = "LANGFUSE_SAMPLE_RATE";

    private LangfuseSettings(bool enabled, Uri? endpoint, double sampleRate, string? publicKey = null,
        string? secretKey = null)
    {
        Enabled = enabled;
        Endpoint = endpoint;
        SampleRate = sampleRate;
        _publicKey = publicKey;
        _secretKey = secretKey;
    }

    public bool Enabled { get; }
    public Uri? Endpoint { get; }
    public double SampleRate { get; }

    public HttpClient CreateHttpClient()
    {
        if (!Enabled || _publicKey is null || _secretKey is null)
            throw new InvalidOperationException("Langfuse HTTP client is unavailable while tracing is disabled.");

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(_publicKey + ":" + _secretKey));
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        client.DefaultRequestHeaders.Add("x-langfuse-ingestion-version", "4");
        return client;
    }

    public static LangfuseSettings FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var enabledText = read("LANGFUSE_ENABLED");
        var enabled = bool.TryParse(enabledText, out var parsedEnabled) && parsedEnabled;
        if (!enabled)
            return new LangfuseSettings(false, null, 1d);

        var sampleRate = 1d;
        var sampleText = read(SampleRateEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(sampleText) &&
            (!double.TryParse(sampleText, NumberStyles.Float, CultureInfo.InvariantCulture, out sampleRate) ||
             !double.IsFinite(sampleRate) || sampleRate is < 0 or > 1))
            throw new InvalidOperationException("LANGFUSE_SAMPLE_RATE must be a number between 0 and 1.");

        var baseUrl = read("LANGFUSE_BASE_URL");
        var publicKey = read("LANGFUSE_PUBLIC_KEY");
        var secretKey = read("LANGFUSE_SECRET_KEY");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Langfuse is enabled but its Cloud endpoint or credentials are missing or invalid.");

        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/api/public/otel/v1/traces");
        return new LangfuseSettings(true, endpoint, sampleRate, publicKey, secretKey);
    }
}

public static class LangfuseTelemetry
{
    public const string SourceName = "UnityDocsRag.Api.Langfuse";
    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static void Configure(WebApplicationBuilder builder)
    {
        var settings = LangfuseSettings.FromEnvironment(Environment.GetEnvironmentVariable);
        if (!settings.Enabled) return;

        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(
                [new KeyValuePair<string, object>("service.name", "UnityDocsRag.Api")]))
            .AddSource(SourceName)
            .SetSampler(new TraceIdRatioBasedSampler(settings.SampleRate))
            .AddOtlpExporter(exporter => ConfigureExporter(exporter, settings)));
    }

    public static void ConfigureExporter(OtlpExporterOptions exporter, LangfuseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled || settings.Endpoint is null)
            throw new InvalidOperationException("Langfuse exporter cannot be configured while tracing is disabled.");

        exporter.Endpoint = settings.Endpoint;
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.HttpClientFactory = settings.CreateHttpClient;
        exporter.TimeoutMilliseconds = 5000;
        exporter.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;
        exporter.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions
        {
            MaxQueueSize = 256,
            MaxExportBatchSize = 32,
            ScheduledDelayMilliseconds = 1000,
            ExporterTimeoutMilliseconds = 5000
        };
    }
}
