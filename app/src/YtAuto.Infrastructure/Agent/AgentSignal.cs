using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YtAuto.Infrastructure.Agent;

/// <summary>
/// Worker (alohida jarayon) → agent: "shu vazifa o'zgardi" signali. Loopback UDP datagramma: yuborish bloklamaydi, agent ishlamasa
/// hech narsa bo'lmaydi. Signal faqat "qayta o'qi" degani - agent ma'lumotni baribir bazadan oladi (soxta signal zarar qilmaydi).
/// Agent porti %LOCALAPPDATA%\YtAuto\agent.json da (agent ishga tushganda yozadi).
/// </summary>
public static class AgentSignal
{
    public static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "agent.json");

    private static readonly object Gate = new();
    private static int _port;
    private static DateTime _readAt = DateTime.MinValue;

    public static void JobChanged(long jobId, Guid? scope) => Send($"{{\"j\":{jobId},\"a\":\"{scope?.ToString() ?? ""}\"}}");

    public static void MachineChanged() => Send("{\"m\":1}");

    private static void Send(string msg)
    {
        try
        {
            var port = Port();
            if (port <= 0) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            using var u = new UdpClient(AddressFamily.InterNetwork);
            u.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
        catch { /* agent yo'q yoki port band - signal ixtiyoriy */ }
    }

    private static int Port()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(20)) return _port;
            _readAt = DateTime.UtcNow;
            _port = 0;
            try
            {
                if (!File.Exists(StateFile)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
                if (doc.RootElement.TryGetProperty("udp", out var u) && u.TryGetInt32(out var p)) _port = p;
            }
            catch { }
            return _port;
        }
    }
}
