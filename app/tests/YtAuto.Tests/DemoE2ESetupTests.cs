using YtAuto.Infrastructure.Accounts;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Security;

namespace YtAuto.Tests;

/// <summary>
/// MCP demo video end-to-end sinovi uchun alohida "kompyuter": YTAUTO_E2E_BOT (lokal bot, masalan http://127.0.0.1:3110 - test bazasi bilan)
/// va YTAUTO_E2E_DATA (bo'sh papka) berilsa, shu papkada hisob ro'yxatdan o'tkaziladi (qurilma roli, DPAPI sirlar) va token
/// token.txt ga yoziladi. So'ng: YtAuto.Worker.exe --data &lt;papka&gt; --online --poll. Haqiqiy %LOCALAPPDATA%\YtAuto ga tegilmaydi.
/// Muhit o'zgaruvchilari bo'lmasa hech narsa qilmaydi.
/// </summary>
public class DemoE2ESetupTests
{
    [Fact]
    public async Task RegisterIsolatedTestMachine()
    {
        var bot = Environment.GetEnvironmentVariable("YTAUTO_E2E_BOT");
        var data = Environment.GetEnvironmentVariable("YTAUTO_E2E_DATA");
        if (string.IsNullOrEmpty(bot) || string.IsNullOrEmpty(data)) return;
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto");
        Assert.False(Path.GetFullPath(data).StartsWith(Path.GetFullPath(real), StringComparison.OrdinalIgnoreCase), "Haqiqiy YtAuto papkasi ishlatilmaydi.");
        Directory.CreateDirectory(data);
        using var http = new HttpClient();
        var secrets = new DpapiSecretStore(Path.Combine(data, "secrets"));
        var local = new SqliteJobStore(Path.Combine(data, "ytauto.db"));
        var acc = await DeviceAccount.RegisterAsync(http, bot, "E2E test PC", null, secrets, local, CancellationToken.None);
        var token = DeviceAccount.Token(secrets)!;
        await File.WriteAllTextAsync(Path.Combine(data, "token.txt"), token);
        await File.WriteAllTextAsync(Path.Combine(data, "account.txt"), acc.AccountId.ToString());
        Assert.True(DeviceAccount.IsConfigured(secrets, local));
    }
}
