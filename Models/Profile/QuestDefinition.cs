namespace FujinTerm.Models.Profile;

/// <summary>
/// One user-authored quest definition for a game-data set, persisted in the
/// per-set <c>{set}/quests.json</c> overlay and seeded by the universal
/// <c>QuestDefs.seed.json</c> underlay. Identity is the
/// (<see cref="Flag"/>, <see cref="Step"/>) pair: a quest is a TBInfo chain
/// that terminally grants quest-flag ability <see cref="Flag"/> at step
/// <see cref="Step"/> (alignment flags split into per-band quests, each carrying
/// its band's representative step).
/// </summary>
/// <remarks>
/// Only the user-owned fields persist here. The mechanical data — ordered draft
/// steps and the permanent stat bonuses a quest grants — is crawled from the
/// active set's <c>TBInfo</c> at runtime (PR 10.10a), never stored, so it stays
/// realm-correct as the loaded set changes.
/// </remarks>
public sealed class QuestDefinition
{
    /// <summary>
    /// Flag base for user-added quests the crawler never produces. Real quest flags are
    /// small TBInfo ability ids (low hundreds); reserving the ≥ <see cref="ManualFlagBase"/>
    /// range keeps a manual quest's identity from ever colliding with a crawled one and
    /// marks it self-contained — it has no crawl/seed baseline, so it persists verbatim.
    /// </summary>
    public const int ManualFlagBase = 1_000_000;

    /// <summary>True when <paramref name="flag"/> is a user-added (manual) quest, not a crawled one.</summary>
    public static bool IsManual(int flag) => flag >= ManualFlagBase;

    /// <summary>Quest-flag ability id — the <c>giveability &lt;flag&gt; &lt;step&gt;</c> target.</summary>
    public int Flag { get; set; }

    /// <summary>Step value identifying this quest within <see cref="Flag"/>.</summary>
    public int Step { get; set; }

    /// <summary>
    /// User-assigned display name. Empty until named — the intended first-run
    /// state before the universal seed (or the user) supplies a name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the quest shows in the journal. Defaults to shown.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// User-edited step checklist as markdown, or <c>null</c> to fall back to the
    /// crawler's auto-drafted steps. Lets the user add amplifying information the
    /// raw game-data crawl can't infer.
    /// </summary>
    public string? Steps { get; set; }

    /// <summary>
    /// User-edited reward label, or <c>null</c> to fall back to the crawler's
    /// inferred award (final-step keeper items, or the granted ability itself). Lets
    /// the user correct awards the raw crawl can't see — e.g. the 5th-tier alignment
    /// weapons handed out by per-class <c>random</c> textblocks the give-chain crawl
    /// never reaches.
    /// </summary>
    public string? Rewards { get; set; }

    /// <summary>
    /// User-set required-level gate, or <c>null</c> to fall back to the crawler's
    /// inferred <c>minlevel</c>. Lets the user supply a gate the raw crawl misses (the
    /// DaoLord sunstone-wristband quest's level-20 requirement, say, when its gate
    /// lives in a chain the give-step scan never reaches) — or force <c>0</c> to mark a
    /// quest ungated when the crawl over-reads one.
    /// </summary>
    public int? RequiredLevel { get; set; }

    /// <summary>
    /// True to suppress this quest from the journal entirely — a "block" for a crawled
    /// quest a particular game-data set surfaces spuriously. Distinct from
    /// <see cref="Visible"/> (a per-taste hide the user toggles freely): a block flags the
    /// row as not-a-real-quest. It stays in the editor so it can be un-blocked. Has no
    /// meaning on a manual quest (delete it instead). Defaults to false.
    /// </summary>
    public bool Blocked { get; set; }

    public QuestDefinition() { }

    public QuestDefinition(int flag, int step, string name = "", bool visible = true,
                           string? steps = null, string? rewards = null, int? requiredLevel = null,
                           bool blocked = false)
    {
        Flag = flag;
        Step = step;
        Name = name;
        Visible = visible;
        Steps = steps;
        Rewards = rewards;
        RequiredLevel = requiredLevel;
        Blocked = blocked;
    }
}
