using System.Text.Json;
using System.Text.Json.Serialization;

namespace LambdaRag.Authoring.ExtractFunction;

/// <summary>
/// Wire-format DTOs for the Azure AI Search WebApiSkill batch contract.
/// See https://learn.microsoft.com/en-us/azure/search/cognitive-search-custom-skill-web-api
/// </summary>
public static class WebApiSkillContract
{
    public sealed class Request
    {
        [JsonPropertyName("values")]
        public List<RecordIn> Values { get; set; } = new();
    }

    public sealed class RecordIn
    {
        [JsonPropertyName("recordId")]
        public string RecordId { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public InputData Data { get; set; } = new();
    }

    /// <summary>
    /// Inputs the skillset will pass through. We expect at minimum a chunk
    /// of text plus the source document name.
    /// </summary>
    public sealed class InputData
    {
        [JsonPropertyName("chunk")]
        public string Chunk { get; set; } = string.Empty;

        [JsonPropertyName("documentId")]
        public string? DocumentId { get; set; }

        [JsonPropertyName("parentDocumentId")]
        public string? ParentDocumentId { get; set; }

        [JsonPropertyName("headingPath")]
        public string? HeadingPath { get; set; }

        [JsonPropertyName("chunkOrdinal")]
        public int? ChunkOrdinal { get; set; }
    }

    public sealed class Response
    {
        [JsonPropertyName("values")]
        public List<RecordOut> Values { get; set; } = new();
    }

    public sealed class RecordOut
    {
        [JsonPropertyName("recordId")]
        public string RecordId { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public Dictionary<string, JsonElement> Data { get; set; } = new();

        [JsonPropertyName("errors")]
        public List<Message> Errors { get; set; } = new();

        [JsonPropertyName("warnings")]
        public List<Message> Warnings { get; set; } = new();
    }

    public sealed class Message
    {
        [JsonPropertyName("message")]
        public string Text { get; set; } = string.Empty;
    }
}
