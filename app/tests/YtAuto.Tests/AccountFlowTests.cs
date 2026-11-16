using System.Net.Http.Json;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Accounts;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Security;

namespace YtAuto.Tests;

/// <summary>
/// Desktop hisob oqimi, haqiqiy bot (YTAUTO_BOT_TEST - masalan http://localhost:3100, ALLOW_BROWSER_DEV=1) va uning bazasi bilan:
/// ro'yxatdan o'tish → qurilma roli bilan ombor → botga token bilan kirish → tokenni yangilash (eski bog'lanish bekor bo'ladi)
/// → mavjud token bilan ikkinchi kompyuterni ulash (bir xil ma'lumotlar). Muhit o'zgaruvchisi bo'lmasa o'tkazib yuboriladi.
/// </summary>
public class AccountFlowTests
{
    private static string? Bot => Environment.GetEnvironmentVariable("YTAUTO_BOT_TEST");

    private static (DpapiSecretStore, SqliteJobStore) NewMachine(TempDir t, string name)
    {
        var dir = Path.Combine(t.Path, name);
        Directory.CreateDirectory(dir);
        return (new DpapiSecretStore(Path.Combine(dir, "secrets")), new SqliteJobStore(Path.Combine(dir, "db.sqlite")));
    }

    private static async Task<(bool Ok, string Msg)> BotLogin(HttpClient http, string token)
    {
        var r = await http.PostAsJsonAsync($"{Bot}/api/login", new { token });
        var j = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return (j.TryGetProperty("ok", out var ok) && ok.GetBoolean(), j.ToString());
    }

    [Fact]
    public async Task Register_Store_Login_Rotate_SecondMachine()
    {
        if (string.IsNullOrEmpty(Bot)) return;
        using var t = new TempDir();
        using var http = new HttpClient();

        var (sec1, db1) = NewMachine(t, "pc1");
        var acc = await DeviceAccount.RegisterAsync(http, Bot, "Test kompyuter", null, sec1, db1, CancellationToken.None);
        Assert.True(DeviceAccount.IsConfigured(sec1, db1));
        var token = DeviceAccount.Token(sec1)!;
        Assert.True(DeviceAccount.LooksLikeToken(token));

        // Qurilma roli bilan ombor: faqat o'z hisobi.
        var store = new PostgresJobStore(sec1.Get(SecretNames.DeviceConnection)!);
        Assert.True(store.IsDeviceRole);
        Assert.Equal(acc.AccountId, store.AccountInfo()!.Value.AccountId);
        Assert.Equal(0, store.CountJobs());
        var job = new VideoJob { Topic = "Test", Language = "en", PublishAtUtc = DateTimeOffset.UtcNow.AddDays(2), Status = JobStatus.Queued };
        store.Insert(job);

        // Botga token bilan kirish; tokenni yangilagach eski token ishlamaydi, yangisi ishlaydi.
        Assert.True((await BotLogin(http, token)).Ok);
        Assert.Equal(1, store.AccountInfo()!.Value.TelegramUsers);
        var fresh = DeviceAccount.RotateToken(store, sec1);
        Assert.Equal(0, store.AccountInfo()!.Value.TelegramUsers);
        Assert.False((await BotLogin(http, token)).Ok);
        Assert.True((await BotLogin(http, fresh)).Ok);

        // Ikkinchi kompyuter mavjud token bilan: o'sha hisob, o'sha mavzular (o'z alohida roli bilan).
        var (sec2, db2) = NewMachine(t, "pc2");
        var acc2 = await DeviceAccount.RegisterAsync(http, Bot, "Ikkinchi kompyuter", fresh, sec2, db2, CancellationToken.None);
        Assert.Equal(acc.AccountId, acc2.AccountId);
        var store2 = new PostgresJobStore(sec2.Get(SecretNames.DeviceConnection)!);
        Assert.Equal("Test", store2.Get(job.Id)!.Topic);

        // Noto'g'ri token bilan ulanish rad etiladi.
        var (sec3, db3) = NewMachine(t, "pc3");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeviceAccount.RegisterAsync(http, Bot, "Begona", DeviceAccount.NewToken(), sec3, db3, CancellationToken.None));
        Assert.False(DeviceAccount.IsConfigured(sec3, db3));
    }

    [Fact]
    public void Token_Format_And_Hash()
    {
        var a = DeviceAccount.NewToken(); var b = DeviceAccount.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(DeviceAccount.LooksLikeToken(a));
        Assert.Matches("^[0-9a-f]{64}$", DeviceAccount.Hash(a));
        Assert.Equal(DeviceAccount.Hash(a), DeviceAccount.Hash(" " + a + "\n"));
        Assert.False(DeviceAccount.LooksLikeToken("yt_short"));
    }
}
