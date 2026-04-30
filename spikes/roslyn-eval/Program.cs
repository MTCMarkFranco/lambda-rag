// spikes/roslyn-eval — proof that we can swap microsoft/RulesEngine for
// Roslyn scripting if upstream goes unmaintained. See README.md.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LambdaRag.Spikes.RoslynEval;

// The fact shape mirrors what the real pipeline projects out of a
// contract. Kept tiny on purpose — this is a spike, not a clone.
public sealed record ContractFact(
    int PaymentNetDays,
    bool HasGovernanceClause,
    bool ReferencesIso27001OrSoc2);

public sealed record SpikeRule(
    string Id,
    string Lambda,
    bool ExpectedVerdict);

public static class Program
{
    public static async Task<int> Main()
    {
        // Lambdas use the same dialect we already emit for the production
        // pipeline: boolean expressions over a typed fact, no closures,
        // no external types.
        var rules = new[]
        {
            new SpikeRule(
                Id:              "PAY-001",
                Lambda:          "fact.PaymentNetDays <= 30",
                ExpectedVerdict: false),
            new SpikeRule(
                Id:              "GOV-001",
                Lambda:          "fact.HasGovernanceClause",
                ExpectedVerdict: true),
            new SpikeRule(
                Id:              "DPA-001",
                Lambda:          "fact.ReferencesIso27001OrSoc2",
                ExpectedVerdict: false),
        };

        var sampleFact = new ContractFact(
            PaymentNetDays:           45,   // > 30 ⇒ PAY-001 fails
            HasGovernanceClause:      true, // ⇒ GOV-001 passes
            ReferencesIso27001OrSoc2: false // ⇒ DPA-001 fails
        );

        var scriptOptions = ScriptOptions.Default
            .AddReferences(typeof(ContractFact).Assembly)
            .AddImports("System");

        var allPassed = true;
        Console.WriteLine($"{"Rule",-8} {"expected",-9} {"actual",-7} {"compile",-12} {"eval",-9} status");
        foreach (var rule in rules)
        {
            var compileSw = Stopwatch.StartNew();
            var script = CSharpScript.Create<bool>(
                code:    rule.Lambda,
                options: scriptOptions,
                globalsType: typeof(Globals));
            script.Compile();
            compileSw.Stop();

            var evalSw = Stopwatch.StartNew();
            var result = await script.RunAsync(globals: new Globals(sampleFact));
            evalSw.Stop();

            var actual = result.ReturnValue;
            var ok = actual == rule.ExpectedVerdict;
            allPassed &= ok;
            Console.WriteLine(
                $"{rule.Id,-8} {rule.ExpectedVerdict,-9} {actual,-7} " +
                $"compile={compileSw.ElapsedMilliseconds,4}ms eval={evalSw.ElapsedMilliseconds,4}ms " +
                $"{(ok ? "PASS" : "FAIL")}");
        }

        if (allPassed)
        {
            Console.WriteLine($"all {rules.Length} rules passed");
            return 0;
        }
        Console.Error.WriteLine("one or more rules failed");
        return 1;
    }
}

// Roslyn scripting needs a globals type whose public members are
// addressable directly inside the lambda string.
public sealed class Globals
{
    public Globals(ContractFact fact) => this.fact = fact;
    public ContractFact fact { get; }
}
