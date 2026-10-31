namespace MudPlay.Models.Profile;

// The channel a hurt party follower asks for a heal on (`@heal`), in place of
// running off alone. Persisted in PartySettings.RequestHealChannel; the wire form
// is PartyRestSync.RequestHeal's. Say by default: the party is always in one room,
// and only it hears (user, 2026-10-30).
public enum HealRequestChannel
{
    // Local room say — leading `.`.
    Say = 0,

    // Gang channel — `bg`.
    Gangpath = 1,

    // The numbered broadcast channel — `br`.
    Broadcast = 2,
}
