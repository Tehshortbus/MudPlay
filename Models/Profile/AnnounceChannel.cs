namespace FujinTerm.Models.Profile;

// Chat channel the Auto-Trainer "Announce level-ups" feature broadcasts
// "I can now train to level: N" on. Persisted in AutoTrainerSettings.AnnounceChannel;
// mapped to a MajorMUD wire form (gang / gos word-verbs, " / . punctuation prefixes)
// at send time by LevelUpAnnouncer. Telepath isn't offered — an availability
// broadcast has no single recipient.
public enum AnnounceChannel
{
    // Party gangpath — gang.
    Gangpath = 0,

    // Realm-wide gossip — gos.
    Gossip = 1,

    // Area yell — leading ".
    Yell = 2,

    // Local room say — leading .
    Say = 3,
}
