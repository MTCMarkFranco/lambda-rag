using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring.ExtractFunction;

/// <summary>
/// HTTP trigger that conforms to the Azure AI Search Web API custom-skill
/// batch contract. The indexer POSTs a list of records (chunks) and we
/// return one ExtractedRule per record.
///
/// Auth model: function-level auth via AAD (configured at the Function App
/// level via authResourceId on the WebApiSkill). The trigger uses
/// AuthorizationLevel.Anonymous because access control is enforced by
/// EasyAuth/Functions auth at the front door — not by a function key.
/// </summary>
public sealed class ExtractRuleFunction
{
    private readonly RuleExtractionService _svc;
    private readonly ILogger<ExtractRuleFunction> _log;

    public ExtractRuleFunction(
        RuleExtractionService svc,
        ILogger<ExtractRuleFunction> log)
    {
        _svc = svc;
        _log = log;
    }

    [Function("extract-rule")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "extract-rule")]
        HttpRequest req,
        CancellationToken ct)
    {
        WebApiSkillContract.Request? batch;
        try
        {
            batch = await JsonSerializer.DeserializeAsync<WebApiSkillContract.Request>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Invalid request body");
            return new BadRequestObjectResult(new { error = "invalid JSON body" });
        }

        if (batch is null || batch.Values.Count == 0)
        {
            return new BadRequestObjectResult(new { error = "expected non-empty 'values' array" });
        }

        _log.LogInformation("Processing batch of {Count} records", batch.Values.Count);

        var response = new WebApiSkillContract.Response();

        foreach (var rec in batch.Values)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _svc.ExtractAsync(rec.Data, ct);
            var outRec = new WebApiSkillContract.RecordOut { RecordId = rec.RecordId };

            switch (outcome.Status)
            {
                case ExtractionStatus.Ok when outcome.Rule is not null:
                    var element = JsonSerializer.SerializeToElement(outcome.Rule);
                    outRec.Data["extractedRule"] = element;
                    break;

                case ExtractionStatus.Skipped:
                    outRec.Warnings.Add(new WebApiSkillContract.Message
                    {
                        Text = outcome.Reason ?? "skipped"
                    });
                    break;

                case ExtractionStatus.Failed:
                default:
                    outRec.Errors.Add(new WebApiSkillContract.Message
                    {
                        Text = outcome.Reason ?? "extraction failed"
                    });
                    break;
            }

            response.Values.Add(outRec);
        }

        var ok = response.Values.Count(v => v.Data.ContainsKey("extractedRule"));
        _log.LogInformation("Batch complete. ok={Ok} of {Total}", ok, response.Values.Count);

        return new OkObjectResult(response);
    }
}
