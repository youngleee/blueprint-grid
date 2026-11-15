using Dungeon.Agent;
using Dungeon.Llm;

namespace Dungeon.Skills;

public sealed record InductionRequest(string Trigger, EntitySighting Target, FactSet Facts, IReadOnlyList<string> History);

public sealed record InductionAttempt(
    int Attempt,
    string RawReply,
    string? SkillName,
    bool Accepted,
    string Feedback,
    int InputTokens,
    int OutputTokens,
    bool FromCache,
    int TestsPassed,
    int TestsRun);

public sealed record InductionResult(InductionRequest Request, Skill? Accepted, IReadOnlyList<InductionAttempt> Attempts);

// Tested: validator and calibrated scenario tests must pass (default, PLAN §6.4).
// Valid:  the validator must pass; scenario tests still run and are recorded but do not gate.
//         Sensitivity variant for ablation E, whose macros rarely reach the tested bar.
public enum AdmissionMode { Tested, Valid }

// Asks the LLM for a skill, admits it through validator + scenario tests, and feeds every rejection
// back for another attempt (PLAN §6.3). The LLM only proposes; nothing it writes is executed
// unless it passes admission, and even then it only runs through the interpreter.
public sealed class SkillInducer(ILlmClient client, string model, double temperature, SkillForm form,
    ScenarioTestOptions? tests = null, AdmissionMode admission = AdmissionMode.Tested)
{
    public SkillForm Form => form;
    public AdmissionMode Admission => admission;
    private readonly ScenarioTestOptions _tests = tests ?? new ScenarioTestOptions();

    public async Task<InductionResult> InduceAsync(InductionRequest request, ActionSpace space, IReadOnlyList<Skill> library,
        int maxAttempts, CancellationToken ct = default)
    {
        var system = InducePrompts.System(space, form, _tests);
        var previous = new List<(string Reply, string Feedback)>();
        var attempts = new List<InductionAttempt>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var user = InducePrompts.User(request, library, form, previous);
            var r = await client.CompleteAsync(new LlmRequest(model, temperature, InducePrompts.PromptType, system, user,
                InducePrompts.MaxOutputTokens), ct);

            Skill? skill = null;
            string feedback;
            var accepted = false;
            int passed = 0, run = 0;
            if (!SkillJson.TryParse(JsonText.StripFences(r.Text), out skill, out var parseError))
            {
                feedback = $"The reply could not be read as a skill: {parseError}";
            }
            else
            {
                var result = SkillAdmission.Check(skill!, space, library, new ValidationOptions(form), _tests);
                accepted = admission == AdmissionMode.Valid ? result.Validation.Ok : result.Accepted;
                feedback = result.Feedback();
                passed = result.Tests?.Passed ?? 0;
                run = result.Tests?.Cases.Count ?? 0;
            }

            attempts.Add(new InductionAttempt(attempt, r.Text, skill?.Name, accepted, feedback, r.InputTokens, r.OutputTokens,
                r.FromCache, passed, run));
            if (accepted)
                return new InductionResult(request, skill, attempts);
            previous.Add((r.Text, feedback));
        }
        return new InductionResult(request, null, attempts);
    }
}

// MaxCallsPerEpisode caps induction LLM calls per episode; AttemptsPerRequest caps the
// feedback rounds for one request, so a later stall can still get attempts of its own.
public sealed record InductionOptions(int MaxCallsPerEpisode = 6, int AttemptsPerRequest = 3, int StallLength = 3);

// Decides when to induce during an episode and registers accepted skills (PLAN §6.3).
//   T1 "new obstacle": a blocking guard in sight, or a known closed door, and no skill in the
//      library takes a parameter of that kind yet (macros: none names that entity).
//   T2 "stalled": the last StallLength decisions all failed or were invalid while an obstacle is
//      present; once per obstacle kind per episode.
// Induction LLM calls do not count as decisions; they are capped per episode.
public sealed class InductionPolicy
{
    private readonly SkillInducer _inducer;
    private readonly InductionOptions _options;
    private readonly HashSet<string> _triedNew = new();
    private readonly HashSet<string> _triedStall = new();
    private int _callsThisEpisode;

    public InductionPolicy(SkillInducer inducer, InductionOptions? options = null)
    {
        _inducer = inducer;
        _options = options ?? new InductionOptions();
    }

    public SkillForm Form => _inducer.Form;

    public void StartEpisode()
    {
        _triedNew.Clear();
        _triedStall.Clear();
        _callsThisEpisode = 0;
    }

    public async Task<InductionResult?> MaybeInduceAsync(FactSet facts, IReadOnlyList<string> history, ActionSpace space,
        List<Skill> library, CancellationToken ct = default)
    {
        var budget = _options.MaxCallsPerEpisode - _callsThisEpisode;
        if (budget <= 0)
            return null;

        var obstacles = Obstacles(facts).ToList();
        if (obstacles.Count == 0)
            return null;

        InductionRequest? request = null;
        var uncovered = obstacles.FirstOrDefault(o => !Covered(o, library, facts) && !_triedNew.Contains(Key(o)));
        if (uncovered is not null)
        {
            _triedNew.Add(Key(uncovered));
            request = new InductionRequest("no skill in the library can be used on this obstacle now", uncovered, facts, history);
        }
        else if (Stalled(history))
        {
            var target = obstacles.FirstOrDefault(o => !_triedStall.Contains(Key(o)));
            if (target is not null)
            {
                _triedStall.Add(Key(target));
                request = new InductionRequest($"stalled: the last {_options.StallLength} decisions failed", target, facts, history);
            }
        }
        if (request is null)
            return null;

        var result = await _inducer.InduceAsync(request, space, library, Math.Min(budget, _options.AttemptsPerRequest), ct);
        _callsThisEpisode += result.Attempts.Count;
        if (result.Accepted is { } skill)
        {
            space.Register(SkillActions.ToAction(skill));
            library.Add(skill);
        }
        return result;
    }

    private bool Stalled(IReadOnlyList<string> history) =>
        history.Count >= _options.StallLength &&
        history.TakeLast(_options.StallLength).All(h => h.Contains(" -> failed") || h.Contains(" -> invalid"));

    // Guards in sight that block, closed doors the agent knows of; nearest first.
    private static IEnumerable<EntitySighting> Obstacles(FactSet f) =>
        f.Entities
            .Where(e => e.Sighting.Kind == EntityKind.Guard && e.InSight && e.Sighting.State is "idle" or "alert"
                        || e.Sighting is { Kind: EntityKind.Door, State: "closed" })
            .OrderBy(e => e.Distance)
            .Select(e => e.Sighting);

    // An obstacle is covered if some library skill is usable on it right now: a closed-loop skill
    // whose single parameter of the obstacle's kind makes its precondition true, or a macro that
    // names the obstacle and whose precondition holds. "Usable now" (not "exists for the kind") is
    // deliberate: a door skill that needs a key does not cover a locked door without one.
    private bool Covered(EntitySighting o, IReadOnlyList<Skill> library, FactSet facts) => library.Any(s =>
    {
        if (Form == SkillForm.Macro)
            return s.Params.Count == 0 && Mentions(s, o.Id) && SkillActions.PreHolds(s, facts, []);
        var kind = o.Kind == EntityKind.Door ? ParamType.Door : ParamType.Guard;
        return s.Params.Count == 1 && s.Params[0].Type == kind && SkillActions.PreHolds(s, facts, [o.Id]);
    });

    private static bool Mentions(Skill s, string id) =>
        s.Pre.Concat(s.Effect).Any(c => c.Parsed.Identifiers().Contains(id)) ||
        SkillJson.Serialize(s, indented: false).Contains($"\"{id}\"");

    // At most one "uncovered" and one "stalled" induction per obstacle per episode.
    private static string Key(EntitySighting o) => o.Id;
}
