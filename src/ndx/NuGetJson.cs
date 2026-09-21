using System.Text.Json.Serialization;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace ndx;

sealed class ServiceIndex
{
    public ServiceResource[]? Resources { get; set; }
}

sealed class ServiceResource
{
    [JsonPropertyName("@id")]
    public string? Id { get; set; }

    [JsonPropertyName("@type")]
    public string? Type { get; set; }
}

sealed class FlatContainerIndex
{
    public string[]? Versions { get; set; }
}

sealed class RegistrationLeaf
{
    [JsonPropertyName("catalogEntry")]
    public System.Text.Json.JsonElement CatalogEntry { get; set; }
}

sealed class CatalogLeaf
{
    public long? PackageSize { get; set; }
    public string? PackageHash { get; set; }
    public string? PackageHashAlgorithm { get; set; }
}

sealed class RuntimeGraphFile
{
    public Dictionary<string, RuntimeGraphNode>? Runtimes { get; set; }
}

sealed class RuntimeGraphNode
{
    [JsonPropertyName("#import")]
    public string[]? Import { get; set; }
}

sealed class RuntimeConfigFile
{
    public RuntimeConfigOptions? RuntimeOptions { get; set; }
}

sealed class RuntimeConfigOptions
{
    public string? Tfm { get; set; }
    public RuntimeConfigFramework? Framework { get; set; }
    public RuntimeConfigFramework[]? Frameworks { get; set; }
}

sealed class RuntimeConfigFramework
{
    public string? Name { get; set; }
    public string? Version { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = WhenWritingNull)]
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(FlatContainerIndex))]
[JsonSerializable(typeof(RegistrationLeaf))]
[JsonSerializable(typeof(CatalogLeaf))]
[JsonSerializable(typeof(RuntimeGraphFile))]
[JsonSerializable(typeof(RuntimeConfigFile))]
sealed partial class NuGetJsonContext : JsonSerializerContext;
