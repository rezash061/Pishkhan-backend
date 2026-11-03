using Pishkhan.AI;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Features;
using Pishkhan.Modules.Agent.Replies;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.3: how the reply pipeline and the eval read Finglish, and the eval gate's Finglish check.</summary>
public class FinglishReadingTests
{
    [Fact]
    public void A_finglish_turn_is_read_in_persian_script_and_shown_to_the_classifier_with_the_original()
    {
        var reading = FinglishReader.ReadWithRules("salam gheymatesh chande?");
        reading.IsFinglish.ShouldBeTrue();
        reading.Persian.ShouldStartWith("سلام قیمتش چنده");
        reading.Method.ShouldBe("rules");
        reading.ForClassifier.ShouldBe("salam gheymatesh chande?\n(Persian: " + reading.Persian + ")");
    }

    [Fact]
    public void A_persian_turn_is_read_as_it_is()
    {
        var reading = FinglishReader.ReadWithRules("سلام قیمتش چنده؟");
        reading.IsFinglish.ShouldBeFalse();
        reading.Persian.ShouldBe("سلام قیمتش چنده؟");
        reading.ForClassifier.ShouldBe("سلام قیمتش چنده؟");
    }

    [Theory]
    [InlineData("""{"persian":"سلام مانتو موجوده؟"}""", "سلام مانتو موجوده؟")]
    [InlineData("""{"persian":""}""", null)]
    [InlineData("""{"text":"سلام"}""", null)]
    [InlineData("nope", null)]
    public void The_model_answer_must_hold_the_persian_text(string json, string? expected) => FinglishReader.Read(json).ShouldBe(expected);

    private static EvalRun Run(decimal? finglish, decimal? script) => new()
    {
        Task = AiTasks.Classify,
        PromptKey = "intent.classify",
        PromptVersion = "2",
        Status = "completed",
        CaseCount = 100,
        IntentAccuracy = 0.9m,
        FinglishCaseCount = finglish is null ? 0 : 30,
        FinglishIntentAccuracy = finglish,
        ScriptIntentAccuracy = script,
    };

    [Fact]
    public void Finglish_may_trail_persian_script_by_at_most_five_points()
    {
        var options = new AiEvalGateOptions();
        var close = AiAdminEndpoints.Gate(Run(0.88m, 0.92m), options);
        close.Passed.ShouldBeTrue();
        close.Checks.Single(c => c.Name == "finglish_gap").Actual.ShouldBe(0.04m);

        var far = AiAdminEndpoints.Gate(Run(0.80m, 0.92m), options);
        far.Passed.ShouldBeFalse();
        far.Checks.Single(c => c.Name == "finglish_gap").Passed.ShouldBeFalse();

        AiAdminEndpoints.Gate(Run(null, 0.92m), options).Checks.ShouldNotContain(c => c.Name == "finglish_gap");
    }
}
