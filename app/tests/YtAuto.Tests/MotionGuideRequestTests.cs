using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Gemini;
using Xunit;

namespace YtAuto.Tests;

/// <summary>Shablon harakati: klip Gemini'ga VIDEO sifatida (fps, past o'lcham) yuboriladi; model rad etsa standart rejimda qayta so'raladi.</summary>
public class MotionGuideRequestTests
{
    private sealed class FakeGemini : HttpMessageHandler
    {
        public readonly List<JsonNode> Bodies = new();
        /// <summary>video_metadata/mediaResolution bo'lgan so'rovni 400 bilan rad etadi (eski model kabi).</summary>
        public bool RejectTuned;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            Bodies.Add(body);
            if (RejectTuned && body["generationConfig"]?["mediaResolution"] is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":400,"message":"Unknown name \"video_metadata\"","status":"INVALID_ARGUMENT"}}""", Encoding.UTF8, "application/json"),
                };
            var res = new JsonObject
            {
                ["candidates"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = "```markdown\n# Motion\n## 1. Pacing\n- 4 s scenes\n```" }) },
                    ["finishReason"] = "STOP",
                }),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(res.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static (GeminiVisualDirector D, FakeGemini F, MotionClip Clip, string Dir) Make(bool rejectTuned)
    {
        var fake = new FakeGemini { RejectTuned = rejectTuned };
        var gen = new GeminiScriptGenerator(new MemSecrets(), new HttpClient(fake)) { RemoteKeys = () => new[] { "K" } };
        var dir = Path.Combine(Path.GetTempPath(), "ytauto-motion-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var clip = Path.Combine(dir, "motion_1.mp4");
        File.WriteAllBytes(clip, new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
        return (new GeminiVisualDirector(gen, () => "gemini-2.5-flash"), fake, new MotionClip(clip, 0, 30), dir);
    }

    [Fact]
    public async Task Clip_IsSentAsVideo_WithFpsAndLowResolution()
    {
        var (d, f, clip, dir) = Make(rejectTuned: false);
        try
        {
            var text = await d.TemplateMotionGuideAsync(new[] { clip }, 5, "Brand", "", new NullLog(), CancellationToken.None);
            Assert.StartsWith("# Motion", text);                                 // ``` olib tashlangan
            var body = Assert.Single(f.Bodies);
            var parts = (JsonArray)body["contents"]![0]!["parts"]!;
            Assert.Equal("video/mp4", parts[0]!["inline_data"]!["mime_type"]!.GetValue<string>());
            Assert.Equal(5, parts[0]!["video_metadata"]!["fps"]!.GetValue<double>());
            Assert.Equal("MEDIA_RESOLUTION_LOW", body["generationConfig"]!["mediaResolution"]!.GetValue<string>());
            Assert.Contains("0–30 s of the template", parts[^1]!["text"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ModelRejectsVideoSettings_RetriesOnceInDefaultMode()
    {
        var (d, f, clip, dir) = Make(rejectTuned: true);
        try
        {
            var log = new NullLog();
            var text = await d.TemplateMotionGuideAsync(new[] { clip }, 5, "Brand", "", log, CancellationToken.None);
            Assert.StartsWith("# Motion", text);
            Assert.Equal(2, f.Bodies.Count);
            Assert.Null(f.Bodies[1]["generationConfig"]!["mediaResolution"]);
            Assert.Null(f.Bodies[1]["contents"]![0]!["parts"]![0]!["video_metadata"]);
            Assert.Contains(log.Lines, l => l.StartsWith("W ") && l.Contains("standart rejimda"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
