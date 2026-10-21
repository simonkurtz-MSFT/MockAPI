using System.Text.Json;
using Json.Schema;

namespace MockAPI.Tests.Configuration;

internal static class ConfigurationSchemaFixture
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static string SchemaPath { get; } = Path.Combine(
        RepositoryRoot,
        "schemas",
        "mockapi.schema.json");

    public static string AoaiPath { get; } = Path.Combine(
        RepositoryRoot,
        "config",
        "trailhead-aoai.json");

    public static string GetSamplePath(string fileName) => Path.Combine(RepositoryRoot, "config", fileName);

    public static string ReadExampleJson()
    {
        using var resource = MockAPI.Management.ManagementApiEndpoints.OpenBuiltInConfiguration("example");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromText(File.ReadAllText(SchemaPath)));

    public static EvaluationResults Evaluate(JsonElement instance) =>
        Schema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true
            });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MockAPI.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Unable to locate the MockAPI repository root.");
    }
}
