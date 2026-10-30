namespace MudPlay.Game.Quests;

// The realm's own name for a quest flag over a range of its values (To null: every
// value from From up), as the server's quest list gives it. A flag that carries
// several quests over its values has one per range.
public readonly record struct ServerQuestName(int Flag, int From, int? To, string Name)
{
    public bool Covers(int value) => value >= From && (To is not int to || value <= to);
}
