using YtAuto.Infrastructure.Voice;

namespace YtAuto.Tests;

public class MossSidecarPatchTests
{
    // VoiceStudio 0.5.8 backend\engines\moss_tts_nano_subprocess\main.py dagi o'ram (qisqartirilgan).
    private const string Sidecar =
        "def _accept_list_ignore_patterns():\r\n" +
        "    try:\r\n" +
        "        from transformers.modeling_utils import PreTrainedModel  # noqa: PLC0415\r\n" +
        "    except Exception:  # noqa: BLE001\r\n" +
        "        return\r\n" +
        "    original = getattr(PreTrainedModel, \"_adjust_missing_and_unexpected_keys\", None)\r\n" +
        "    if original is None or getattr(original, \"_voicestudio_accepts_lists\", False):\r\n" +
        "        return\r\n" +
        "\r\n" +
        "    def adjust_missing_and_unexpected_keys(self, *args, **kwargs):\r\n" +
        "        for name in (\"_keys_to_ignore_on_load_unexpected\", \"_keys_to_ignore_on_load_missing\"):\r\n" +
        "            patterns = getattr(self, name, None)\r\n" +
        "            if isinstance(patterns, (list, tuple)):\r\n" +
        "                setattr(self, name, set(patterns))\r\n" +
        "        return original(self, *args, **kwargs)\r\n";

    [Fact]
    public void Patch_InsertsTransformers5GuardBeforeWrapper()
    {
        var patched = MossEngine.PatchSidecarSource(Sidecar);
        Assert.NotNull(patched);
        var guard = patched!.IndexOf("< 5:\r\n        return\r\n", StringComparison.Ordinal);
        var anchor = patched.IndexOf("    original = getattr(PreTrainedModel", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < anchor, patched);
        Assert.Contains("    import transformers as _ytauto_tf", patched);
        Assert.DoesNotContain("\r\r", patched);
        Assert.DoesNotContain("\n\n\n", patched.Replace("\r", ""));
    }

    [Fact]
    public void Patch_IsIdempotent() =>
        Assert.Null(MossEngine.PatchSidecarSource(MossEngine.PatchSidecarSource(Sidecar)!));

    [Fact]
    public void Patch_KeepsLfLineEndings()
    {
        var patched = MossEngine.PatchSidecarSource(Sidecar.Replace("\r\n", "\n"));
        Assert.NotNull(patched);
        Assert.DoesNotContain("\r", patched);
    }

    [Fact]
    public void Patch_LeavesOtherVersionsAlone()
    {
        Assert.Null(MossEngine.PatchSidecarSource("print('hello')\n"));
        // VoiceStudio o'zi tuzatgan bo'lsa (set'ga aylantirish yo'q) - tegilmaydi.
        Assert.Null(MossEngine.PatchSidecarSource(Sidecar.Replace("set(patterns)", "patterns")));
    }
}
