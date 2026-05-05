using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LambdaRag.Authoring.Validation;

/// <summary>
/// Serialises a <see cref="RuleSetValidationReport"/> to a stable,
/// audit-traceable JSON shape under <c>out/authoring/&lt;run-id&gt;.json</c>.
///
/// The output is deterministic for a given input — keys are written in a
/// fixed order, examples are kept in their input order (positional ids
/// like <c>"P1"</c>/<c>"N1"</c> are stable), and floats are rendered with
/// <c>"G17"</c> via <see cref="JsonSerializer"/> defaults so two runs over
/// the same ruleset produce byte-identical files. UTF-8 with no BOM.
/// </summary>
public sealed class AuthoringReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Serialise(RuleSetValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var doc = new JsonObject
        {
            ["rulesetId"] = report.RulesetId,
            ["rulesetVersion"] = report.RulesetVersion,
            ["embedderId"] = report.EmbedderId,
            ["epsilon"] = report.Epsilon,
            ["ruleCount"] = report.RuleCount,
            ["acceptedCount"] = report.AcceptedCount,
            ["rejectedCount"] = report.RejectedCount,
            ["allAccepted"] = report.AllAccepted,
            ["results"] = new JsonArray(report.Results.Select(SerialiseRule).ToArray()),
        };
        return doc.ToJsonString(Options);
    }

    public async Task WriteAsync(RuleSetValidationReport report, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = Serialise(report);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static JsonObject SerialiseRule(RuleValidationResult r) => new()
    {
        ["ruleId"] = r.RuleId,
        ["accepted"] = r.Accepted,
        ["rejectionReason"] = r.RejectionReason,
        ["minPositive"] = r.MinPositive,
        ["maxNegative"] = r.MaxNegative,
        ["margin"] = r.Margin,
        ["calibratedThreshold"] = r.CalibratedThreshold,
        ["positives"] = SerialiseExamples(r.Positives, "P"),
        ["negatives"] = SerialiseExamples(r.Negatives, "N"),
    };

    private static JsonArray SerialiseExamples(IReadOnlyList<ScoredExample> exs, string idPrefix)
    {
        var arr = new JsonArray();
        for (var i = 0; i < exs.Count; i++)
        {
            var e = exs[i];
            arr.Add(new JsonObject
            {
                ["id"] = $"{idPrefix}{i + 1}",
                ["text"] = e.Text,
                ["topScore"] = e.TopScore,
                ["topConcept"] = e.TopConcept,
            });
        }
        return arr;
    }
}
