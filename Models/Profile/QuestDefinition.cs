namespace FujinTerm.Models.Profile;

// One user-authored quest definition for a game-data set, persisted in the
// per-set {set}/quests.json overlay and seeded by the universal
// QuestDefs.seed.json underlay. Identity is the (Flag, Step) pair: a quest is a
// TBInfo chain that terminally grants quest-flag ability Flag at step Step
// (alignment flags split into per-band quests, each carrying its band's
// representative step).
//
// Only the user-owned fields persist here. The mechanical data — ordered draft
// steps and the permanent stat bonuses a quest grants — is crawled from the
// active set's TBInfo at runtime, never stored, so it stays realm-correct as
// the loaded set changes.
public sealed class QuestDefinition
{
    // Flag base for user-added quests the crawler never produces. Real quest
    // flags are small TBInfo ability ids (low hundreds); reserving the ≥
    // ManualFlagBase range keeps a manual quest's identity from ever colliding
    // with a crawled one and marks it self-contained — it has no crawl/seed
    // baseline, so it persists verbatim.
    public const int ManualFlagBase = 1_000_000;

    // True when flag is a user-added (manual) quest, not a crawled one.
    public static bool IsManual(int flag) => flag >= ManualFlagBase;

    // Quest-flag ability id — the giveability <flag> <step> target.
    public int Flag { get; set; }

    // Step value identifying this quest within Flag.
    public int Step { get; set; }

    // User-assigned display name. Empty until named — the intended first-run
    // state before the universal seed (or the user) supplies a name.
    public string Name { get; set; } = string.Empty;

    // Whether the quest shows in the journal. Defaults to shown.
    public bool Visible { get; set; } = true;

    // User-edited step checklist as markdown, or null to fall back to the
    // crawler's auto-drafted steps. Lets the user add amplifying information the
    // raw game-data crawl can't infer.
    public string? Steps { get; set; }

    // User-edited reward label, or null to fall back to the crawler's inferred
    // award (final-step keeper items, or the granted ability itself). Lets the
    // user correct awards the raw crawl can't see — e.g. the 5th-tier alignment
    // weapons handed out by per-class random textblocks the give-chain crawl
    // never reaches.
    public string? Rewards { get; set; }

    // User-set required-level gate, or null to fall back to the crawler's
    // inferred minlevel. Lets the user supply a gate the raw crawl misses (the
    // DaoLord sunstone-wristband quest's level-20 requirement, say, when its
    // gate lives in a chain the give-step scan never reaches) — or force 0 to
    // mark a quest ungated when the crawl over-reads one.
    public int? RequiredLevel { get; set; }

    // True to suppress this quest from the journal entirely — a "block" for a
    // crawled quest a particular game-data set surfaces spuriously. Distinct
    // from Visible (a per-taste hide the user toggles freely): a block flags the
    // row as not-a-real-quest. It stays in the editor so it can be un-blocked.
    // Has no meaning on a manual quest (delete it instead). Defaults to false.
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
