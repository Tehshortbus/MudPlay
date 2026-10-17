using System.IO;
using MudPlay.Services;

namespace MudPlay.Tests;

// A ProfileService as the client builds one, except that its default-profile template
// and reset marker are paths under the data root nothing writes: LoadDefaultProfile
// then starts the unnamed draft from the installed defaults, as on a fresh install,
// and no test reads another's default profile.
internal static class TestProfiles
{
    public static ProfileService Service()
    {
        string tag = Guid.NewGuid().ToString("N");
        return new ProfileService(
            Path.Combine(AppPaths.DataRoot, $"test-default-profile-{tag}.json"),
            Path.Combine(AppPaths.DataRoot, $"test-default-profile-{tag}.reset"));
    }

    // The client's start with no character named: the default profile, loaded.
    public static ProfileService Loaded()
    {
        ProfileService profile = Service();
        profile.LoadDefaultProfile();
        return profile;
    }
}
