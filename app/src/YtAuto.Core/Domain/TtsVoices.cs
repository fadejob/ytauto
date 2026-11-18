namespace YtAuto.Core.Domain;

/// <summary>
/// Ovoz (TTS) uchun yagona model: MOSS-TTS-Nano (100M, CPU'da ishlaydi, Apache-2.0). Boshqa dvigatellar ishlatilmaydi.
/// MOSS'da tayyor ovozlar yo'q - u namuna audio ovozini nusxalaydi. Shuning uchun ayol/erkak ovozi VoiceStudio'dagi
/// ovoz profillari (nomi <see cref="FemaleRef"/>/<see cref="MaleRef"/>, ichida 10-20 s toza nutq namunasi) orqali beriladi.
/// VoiceStudio'da shu nomli profil bo'lmasa MOSS o'zining standart namunasi bilan o'qiydi.
/// </summary>
public static class TtsVoices
{
    /// <summary>VoiceStudio dvigatel id'si (/v1/audio/speech "model" maydoni).</summary>
    public const string Model = "moss-tts-nano";
    /// <summary>VoiceStudio birinchi sintezda yuklaydigan HuggingFace repozitoriylari (model va audio tokenizer).</summary>
    public const string CheckpointRepo = "OpenMOSS-Team/MOSS-TTS-Nano";
    public const string TokenizerRepo = "OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano";
    public const string DisplayName = "MOSS-TTS-Nano";

    public const string FemaleName = "MOSS ayol", MaleName = "MOSS erkak";
    /// <summary>VoiceStudio ovoz profillari nomlari (namuna audio shu profillarda).</summary>
    public const string FemaleRef = "YtAuto ayol", MaleRef = "YtAuto erkak";

    public static VoiceProfile Female() => new() { Name = FemaleName, Model = Model, Voice = FemaleRef, Language = "en", Speed = 1.0 };
    public static VoiceProfile Male() => new() { Name = MaleName, Model = Model, Voice = MaleRef, Language = "en", Speed = 1.0 };

    public static bool IsAllowed(VoiceProfile? v) => v is not null && string.Equals(v.Model, Model, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Faqat MOSS profillari qoldiriladi (eski KittenTTS va boshqa dvigatel profillari olib tashlanadi), ayol va erkak ovozi
    /// doim bor, standart ovoz mavjud profilga ishora qiladi. O'zgarish bo'lsa true.
    /// </summary>
    public static bool Normalize(AppSettings s)
    {
        var before = Signature(s);
        s.VoiceProfiles = s.VoiceProfiles.Where(IsAllowed).ToList();
        if (!s.VoiceProfiles.Any(v => string.Equals(v.Name, FemaleName, StringComparison.OrdinalIgnoreCase))) s.VoiceProfiles.Insert(0, Female());
        if (!s.VoiceProfiles.Any(v => string.Equals(v.Name, MaleName, StringComparison.OrdinalIgnoreCase))) s.VoiceProfiles.Add(Male());
        if (!s.VoiceProfiles.Any(v => string.Equals(v.Name, s.Defaults.VoiceProfile, StringComparison.OrdinalIgnoreCase)))
            s.Defaults.VoiceProfile = FemaleName;
        return before != Signature(s);
    }

    private static string Signature(AppSettings s) =>
        string.Join("|", s.VoiceProfiles.Select(v => $"{v.Name}/{v.Model}/{v.Voice}")) + "#" + s.Defaults.VoiceProfile;
}
