using System.Text.Json.Nodes;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Semantic;

var endpoint = Environment.GetEnvironmentVariable("LAMBDA_RAG_FOUNDRY_ENDPOINT");
if (string.IsNullOrEmpty(endpoint))
{
    Console.Error.WriteLine("Set LAMBDA_RAG_FOUNDRY_ENDPOINT first.");
    return 1;
}
var embedder = FoundryEmbedderFactory.TryCreateFromEnvironment()
    ?? throw new InvalidOperationException("Foundry env vars missing.");

var ruleNl = "Deliverables must vest in Contoso as works made for hire (or be irrevocably assigned).";
var concepts = new[]
{
    "works made for hire",
    "work made for hire",
    "hereby assigns",
    "irrevocably assigned to Contoso",
    "vests in Contoso",
};
var ruleVec = await embedder.EmbedAsync(ruleNl);

var projected = JsonNode.Parse(File.ReadAllText("out/projected.json"))!;
var sections = projected["sections"]?.AsArray() ?? new JsonArray();
Console.WriteLine($"{"sectionId",-22} {"category",-22} {"ruleCos",8} {"bestConceptCos",16}  heading");
foreach (var s in sections.OfType<JsonObject>())
{
    var id = s["id"]?.GetValue<string>() ?? "?";
    var category = s["category"]?.GetValue<string>() ?? "";
    var heading = s["heading"]?.GetValue<string>() ?? "";
    var text = s["text"]?.GetValue<string>() ?? "";
    if (string.IsNullOrWhiteSpace(text)) continue;
    var sectionVec = await embedder.EmbedAsync(text);
    var ruleCos = SemanticFunctions.Cosine(ruleVec, sectionVec);
    double bestConcept = 0;
    foreach (var c in concepts)
    {
        var cv = await embedder.EmbedAsync(c);
        bestConcept = Math.Max(bestConcept, SemanticFunctions.Cosine(cv, sectionVec));
    }
    if (ruleCos >= 0.20 || category == "ip_ownership")
        Console.WriteLine($"{id,-22} {category,-22} {ruleCos,8:F3} {bestConcept,16:F3}  {heading}");
}
return 0;
