using YtAuto.Core.Domain;
using Xunit;

namespace YtAuto.Tests;

public class TtsVoicesTests
{
    [Fact]
    public void Normalize_ReplacesOldKittenProfilesWithMossFemaleAndMale()
    {
        var s = new AppSettings();
        s.VoiceProfiles.Add(new VoiceProfile { Name = "Kitten EN ayol", Model = "kittentts", Voice = "expr-voice-2-f" });
        s.VoiceProfiles.Add(new VoiceProfile { Name = "Kitten EN erkak", Model = "kittentts", Voice = "expr-voice-2-m" });
        s.Defaults.VoiceProfile = "Kitten EN ayol";

        Assert.True(TtsVoices.Normalize(s));

        Assert.Equal(new[] { TtsVoices.FemaleName, TtsVoices.MaleName }, s.VoiceProfiles.Select(v => v.Name));
        Assert.All(s.VoiceProfiles, v => Assert.Equal(TtsVoices.Model, v.Model));
        Assert.Equal(TtsVoices.FemaleName, s.Defaults.VoiceProfile);
        Assert.Equal(TtsVoices.FemaleName, s.FindVoice("Kitten EN ayol")!.Name); // eski job nomi - standart MOSS ovoziga tushadi
    }

    [Fact]
    public void Normalize_KeepsExtraMossProfilesAndIsStable()
    {
        var s = new AppSettings();
        s.VoiceProfiles.Add(new VoiceProfile { Name = "Mening ovozim", Model = TtsVoices.Model, Voice = "YtAuto u-1234" });
        s.Defaults.VoiceProfile = "Mening ovozim";

        TtsVoices.Normalize(s);
        Assert.Contains(s.VoiceProfiles, v => v.Name == "Mening ovozim");
        Assert.Equal("Mening ovozim", s.Defaults.VoiceProfile);
        Assert.False(TtsVoices.Normalize(s)); // ikkinchi marta o'zgarish yo'q (bot qayta-qayta yozilmaydi)
    }

    [Fact]
    public void DefaultSettings_UseOnlyMoss()
    {
        var s = AppSettings.CreateDefault(Path.GetTempPath(), Path.GetTempPath());
        Assert.All(s.VoiceProfiles, v => Assert.True(TtsVoices.IsAllowed(v)));
        Assert.Equal(TtsVoices.FemaleName, s.Defaults.VoiceProfile);
    }
}
