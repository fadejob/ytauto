using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using YtAuto.Core.Domain;
using YtAuto.Core.Scheduling;

namespace YtAuto.Infrastructure.Scheduling;

public sealed class ScheduledTaskInfo
{
    public bool Exists { get; init; }
    public bool Enabled { get; init; }
    public string State { get; init; } = "";
    public DateTime? LastRunTime { get; init; }
    public int? LastResult { get; init; }
    public DateTime? NextRunTime { get; init; }
    public string Command { get; init; } = "";
    public string Xml { get; init; } = "";
}

/// <summary>
/// Windows Task Scheduler (COM "Schedule.Service"). Bitta nomli vazifa: yoqilganda yaratiladi yoki YANGILANADI (dublikat yo'q).
/// Kontekst: joriy foydalanuvchi, InteractiveToken (faqat foydalanuvchi tizimga kirgan sessiyada; ekran qulflangan bo'lsa ham).
/// O'tkazib yuborilgan ishga tushish keyingi imkoniyatda bir marta (StartWhenAvailable), parallel nusxa - IgnoreNew.
/// </summary>
public sealed class TaskSchedulerService
{
    public const string TaskName = "YtAuto Daily Worker";
    /// <summary>Masofaviy "Hozir chiqarish" buyrug'ini kutuvchi vazifa: kunduzi har 2 daqiqada yengil --poll: holat yuboradi, so'rov bo'lsagina ishlaydi (navbatga tegmaydi).</summary>
    public const string PollTaskName = "YtAuto Remote Trigger";
    public const int PollIntervalMinutes = 2;
    /// <summary>Bir ishga tushishda bir nechta video + har biridan keyin 15 daqiqa tanaffus: ish vaqti limiti kamida shuncha soat.</summary>
    public const int MinBatchExecutionHours = 10;
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    public static string BuildXml(ScheduleSettings s, string workerExe, DateTimeOffset nowUtc)
    {
        var tz = TimeZones.Find(s.TimeZoneId);
        var time = TimeZones.ParseTime(s.StartTime, new TimeOnly(1, 0));
        var todayLocal = DateOnly.FromDateTime(TimeZones.ToZone(nowUtc, tz).DateTime);
        var firstUtc = TimeZones.ToUtc(todayLocal, time, tz);
        if (firstUtc <= nowUtc) firstUtc = TimeZones.ToUtc(todayLocal.AddDays(1), time, tz);
        var inZone = TimeZones.ToZone(firstUtc, tz);
        // Offset ko'rsatilgan StartBoundary: kompyuter vaqt zonasi boshqa bo'lsa ham aynan tanlangan zona bo'yicha ishlaydi.
        var boundary = inZone.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + inZone.ToString("zzz", CultureInfo.InvariantCulture);
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string X(string v) => SecurityElement.Escape(v) ?? "";
        var dir = Path.GetDirectoryName(workerExe)!;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>YtAuto</Author>
    <Description>YtAuto: bazadan mavzularni sinxronlab, ish oynasi tugaguncha navbatdagi barcha mavzulardan video tayyorlaydi va YouTube'ga rejalashtirib yuklaydi.</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{boundary}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{X(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>{(s.WakeToRun ? "true" : "false")}</WakeToRun>
    <ExecutionTimeLimit>PT{Math.Clamp(Math.Max(s.ExecutionLimitHours, MinBatchExecutionHours), 1, 23)}H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{X(workerExe)}</Command>
      <Arguments>--scheduled</Arguments>
      <WorkingDirectory>{X(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// Masofaviy buyruq kutuvchi: foydalanuvchi tizimda bo'lganda har <see cref="PollIntervalMinutes"/> daqiqada, faqat internet bo'lsa,
    /// "YtAuto.Worker.exe --poll" ishga tushadi. So'rov bo'lmasa worker bir zumda chiqadi; bo'lsa videoni tayyorlab, darhol chiqaradi.
    /// </summary>
    public static string BuildPollXml(string workerExe, DateTimeOffset nowUtc)
    {
        var start = nowUtc.ToLocalTime();
        var boundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + start.ToString("zzz", CultureInfo.InvariantCulture);
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string X(string v) => SecurityElement.Escape(v) ?? "";
        var dir = Path.GetDirectoryName(workerExe)!;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>YtAuto</Author>
    <Description>YtAuto: online bazadagi "Hozir chiqarish" buyrug'ini kutadi. Buyruq bo'lsa videoni tayyorlab, YouTube'ga darhol chiqaradi.</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition><Interval>PT{PollIntervalMinutes}M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
      <StartBoundary>{boundary}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{X(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT8H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{X(workerExe)}</Command>
      <Arguments>--poll</Arguments>
      <WorkingDirectory>{X(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static dynamic Service()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler COM xizmati topilmadi.");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    public void Register(ScheduleSettings s, string workerExe)
    {
        if (!File.Exists(workerExe)) throw new FileNotFoundException("Worker exe topilmadi", workerExe);
        var xml = BuildXml(s, workerExe, DateTimeOffset.UtcNow);
        dynamic svc = Service();
        dynamic folder = svc.GetFolder("\\");
        folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
        folder.RegisterTask(PollTaskName, BuildPollXml(workerExe, DateTimeOffset.UtcNow), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
    }

    /// <summary>Faqat masofaviy buyruq (bot) vazifasi: kunlik jadval yoqilmagan bo'lsa ham bot so'rovlari bajarilishi uchun.</summary>
    public void RegisterPoll(string workerExe)
    {
        if (!File.Exists(workerExe)) throw new FileNotFoundException("Worker exe topilmadi", workerExe);
        dynamic svc = Service();
        svc.GetFolder("\\").RegisterTask(PollTaskName, BuildPollXml(workerExe, DateTimeOffset.UtcNow), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
    }

    public void Unregister()
    {
        dynamic svc = Service();
        dynamic folder = svc.GetFolder("\\");
        foreach (var name in new[] { TaskName, PollTaskName })
        {
            try { folder.DeleteTask(name, 0); }
            catch (COMException e) when ((uint)e.HResult == 0x80070002) { }
            catch (FileNotFoundException) { }
        }
    }

    /// <summary>Faqat kunlik jadvalni o'chiradi; masofaviy buyruq (poll) vazifasi qoladi.</summary>
    public void UnregisterDaily()
    {
        dynamic svc = Service();
        try { svc.GetFolder("\\").DeleteTask(TaskName, 0); }
        catch (COMException e) when ((uint)e.HResult == 0x80070002) { }
        catch (FileNotFoundException) { }
    }

    /// <summary>Masofaviy buyruq vazifasi ro'yxatda bormi (UI holati uchun).</summary>
    public bool PollTaskExists()
    {
        try { dynamic svc = Service(); svc.GetFolder("\\").GetTask(PollTaskName); return true; }
        catch (Exception e) when (e is COMException or FileNotFoundException || (uint)e.HResult == 0x80070002) { return false; }
    }

    public void RunNow()
    {
        dynamic svc = Service();
        dynamic task = svc.GetFolder("\\").GetTask(TaskName);
        task.Run(null);
    }

    public ScheduledTaskInfo Query()
    {
        try
        {
            dynamic svc = Service();
            dynamic task = svc.GetFolder("\\").GetTask(TaskName);
            int state = task.State;
            DateTime last = task.LastRunTime; DateTime next = task.NextRunTime;
            string xml = task.Xml;
            var cmd = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>").Groups[1].Value;
            return new ScheduledTaskInfo
            {
                Exists = true, Enabled = task.Enabled,
                State = state switch { 1 => "O'chirilgan", 2 => "Navbatda", 3 => "Tayyor", 4 => "Ishlamoqda", _ => "Noma'lum" },
                LastRunTime = last.Year < 2000 ? null : last, LastResult = task.LastTaskResult,
                NextRunTime = next.Year < 2000 ? null : next, Command = System.Net.WebUtility.HtmlDecode(cmd), Xml = xml,
            };
        }
        catch (Exception e) when (e is COMException or FileNotFoundException || (uint)e.HResult == 0x80070002)
        {
            return new ScheduledTaskInfo { Exists = false };
        }
    }

    public static string DescribeResult(int? code) => code switch
    {
        null => "-",
        0 => "0 (muvaffaqiyatli)",
        0x41300 => "Tayyor",
        0x41301 => "Hozir ishlamoqda",
        0x41303 => "Hali ishga tushmagan",
        0x41306 => "To'xtatildi",
        unchecked((int)0x800710E0) => "Operator rad etdi / boshqa nusxa ishlamoqda",
        1 => "1 (worker xato bilan tugadi - loglarni ko'ring)",
        2 => "2 (vazifa e'tibor talab qiladi)",
        _ => $"0x{code:X}",
    };

    public const string WakeNote =
        "Kompyuter o'chiq bo'lsa worker ishlamaydi. 'Uyqudan uyg'otish' (WakeToRun) faqat qurilma va Windows quvvat sxemasida " +
        "'Allow wake timers' yoqilgan bo'lsa ishlaydi; ba'zi noutbuklar batareyada uyg'onmaydi. O'tkazib yuborilgan ishga tushish " +
        "kompyuter yoqilgach bir marta bajariladi (eski kunlar uchun ketma-ket bir nechta ish yaratilmaydi). " +
        "Vazifa faqat siz Windows'ga kirgan sessiyada ishlaydi (ekran qulflangan bo'lishi mumkin).";
}
