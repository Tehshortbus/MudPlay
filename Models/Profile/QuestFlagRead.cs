namespace MudPlay.Models.Profile;

// A quest flag's value as the game last read it out (`abil <flag>` on Paradigm and
// Anomaly, `sys god <name> abil` on Stock), and when.
public sealed class QuestFlagRead
{
    public int Value { get; set; }

    public System.DateTimeOffset At { get; set; }
}
