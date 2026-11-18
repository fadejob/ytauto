using YtAuto.Core.Domain;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Kanal kontekstini ssenariy promptiga qo'yadi. Promptda <c>{channel_context}</c> bo'lsa - shu joyga; bo'lmasa (eski yoki
/// foydalanuvchi o'zgartirgan prompt) kontekst yoqilgan bo'lsa oxiriga alohida bo'lim bo'lib qo'shiladi. O'chiq bo'lsa placeholder olib tashlanadi.
/// </summary>
public static class ChannelPrompt
{
    public const string Placeholder = "{channel_context}";
    public const string ScriptHeading = "## Channel context (learned from this channel's latest videos; match its style)";

    public static string ApplyToScript(string template, ChannelContextSettings? ctx)
    {
        var block = ctx?.ScriptBlock ?? "";
        if (template.Contains(Placeholder, StringComparison.Ordinal)) return template.Replace(Placeholder, block);
        if (block.Length == 0) return template;
        return template.TrimEnd() + "\n\n" + ScriptHeading + "\n" + block + "\n";
    }
}
