using Npgsql;
using YtAuto.Core.Domain;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

/// <summary>
/// Online ombor shartnomasi (haqiqiy PostgreSQL). YTAUTO_PG_TEST muhit o'zgaruvchisida jadval egasi ulanish satri bo'lsa ishlaydi;
/// vaqtinchalik sxemada ishlaydi va oxirida uni (va yaratilgan qurilma rollarini) o'chiradi (asosiy "ytauto" sxemasiga tegmaydi).
/// Ombor haqiqiy rejimdagidek qurilma roli bilan ishlatiladi: ikkinchi hisob birinchisining ma'lumotini ko'rmasligi ham tekshiriladi.
/// </summary>
public class PostgresStoreTests
{
    private static string? Cs => Environment.GetEnvironmentVariable("YTAUTO_PG_TEST");

    private static VideoJob J(string topic, DateTimeOffset created) => new()
    {
        Topic = topic, Language = "uz", PublishAtUtc = DateTimeOffset.UtcNow.AddDays(2), Status = JobStatus.Queued, CreatedAtUtc = created,
    };

    [Fact]
    public async Task OnlineStore_ClaimCrudStagesSettings_InTempSchema()
    {
        if (string.IsNullOrEmpty(Cs)) return; // online baza berilmagan - o'tkazib yuboriladi
        var schema = "ytauto_test_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _ = new PostgresJobStore(Cs, schema); // egasi: sxema yaratiladi (bot/db/schema.sql)
            var csA = Device(schema, "A"); var csB = Device(schema, "B");
            var s = new PostgresJobStore(csA, schema);
            Assert.True(s.IsDeviceRole);
            var now = DateTimeOffset.UtcNow;
            var a = J("A", now.AddMinutes(-30)); s.Insert(a);
            var b = J("B", now.AddMinutes(-20)); s.Insert(b);
            var c = J("C", now.AddMinutes(-40)); c.Status = JobStatus.NeedsAttention; s.Insert(c);

            // CRUD: o'qish va yangilash
            var got = s.Get(a.Id)!; Assert.Equal("A", got.Topic);
            got.Topic = "A2"; got.Notes = "izoh"; s.Update(got);
            Assert.Equal("A2", s.Get(a.Id)!.Topic);

            // Atomar band qilish: eng eskisi, NeedsAttention emas; parallel - dublikatsiz
            var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                var store = new PostgresJobStore(csA, schema);
                while (store.ClaimNext($"worker-PC{i}-20260101000000-{i}", DateTimeOffset.UtcNow) is { } j) claimed.Add(j.Id);
            })));
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), claimed.OrderBy(x => x));

            // Eskirgan band qilish (deterministik): ikkala vazifani bo'shatib, ikki xil "kompyuter" band qiladi.
            foreach (var id in claimed) s.ReleaseClaim(id, s.Get(id)!.ClaimedBy!);
            var mine = s.ClaimNext("worker-PCX-20260101000000-1", DateTimeOffset.UtcNow)!.Id;
            var other = s.ClaimNext("worker-PCY-20260101000000-2", DateTimeOffset.UtcNow)!.Id;
            s.SaveStage(new StageRecord { JobId = mine, Stage = StageKind.Voice, State = StageState.Running });
            s.Heartbeat(other, "worker-PCY-20260101000000-2");
            // PCX'dagi yangi worker: o'z kompyuterining oldingi run'ini bo'shatadi, PCY'nikiga (tirik) tegmaydi.
            Assert.Equal(1, s.ReleaseStaleClaims("worker-PCX-20260102000000-999"));
            Assert.Equal(StageState.Failed, s.GetStage(mine, StageKind.Voice).State);
            Assert.Null(s.Get(mine)!.ClaimedBy);
            Assert.NotNull(s.Get(other)!.ClaimedBy);

            // Hodisalar, sozlamalar (promptlar), qayta navbat, o'chirish (cascade)
            s.AddEvent(a.Id, "INFO", "Script", "salom");
            Assert.Single(s.GetEvents(a.Id));
            s.SetSetting("prompt.script", "P1"); s.SetSetting("prompt.script", "P2");
            Assert.Equal("P2", s.GetSetting("prompt.script"));
            s.Requeue(c.Id, true);
            Assert.Equal(JobStatus.Queued, s.Get(c.Id)!.Status);
            Assert.False(s.Delete(other));          // band qilingan - o'chirilmaydi
            Assert.True(s.Delete(a.Id));
            Assert.Null(s.Get(a.Id));
            Assert.Empty(s.GetEvents(a.Id));
            Assert.Equal(2, s.Page(false, 0, 10, out var total).Count);
            Assert.Equal(2, total);

            // Izolyatsiya: B hisobi A ning mavzularini, sozlamalarini va kompyuterlarini ko'rmaydi, o'zgartira olmaydi.
            s.Heartbeat("PC1", true, null, null, null, "1");
            var bStore = new PostgresJobStore(csB, schema);
            Assert.Equal(0, bStore.CountJobs());
            Assert.Null(bStore.Get(b.Id));
            Assert.False(bStore.Delete(b.Id));
            Assert.Null(bStore.GetSetting("prompt.script"));
            bStore.SetSetting("prompt.script", "B");                 // bir xil kalit - har hisobda alohida
            bStore.Heartbeat("PC1", true, null, null, null, "1");     // bir xil kompyuter nomi - to'qnashmaydi
            Assert.Equal("P2", s.GetSetting("prompt.script"));
            Assert.Equal("B", bStore.GetSetting("prompt.script"));
            var bJob = J("B-only", now); bStore.Insert(bJob);
            Assert.Null(s.Get(bJob.Id));
            Assert.Equal(1, bStore.CountJobs());
            Assert.Equal(2, s.CountJobs());
        }
        finally
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync();
            var roles = new List<string>();
            try
            {
                await using (var sel = new NpgsqlCommand($"SELECT role_name FROM \"{schema}\".account_roles", conn))
                await using (var r = await sel.ExecuteReaderAsync()) while (await r.ReadAsync()) roles.Add(r.GetString(0));
            }
            catch { }
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
            foreach (var role in roles)
            {
                await using var dr = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", conn);
                await dr.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Doimiy agent: tunnel manzili va sir bazaga yoziladi (o'z hisobi), kutayotgan so'rovlar bitta yengil so'rov bilan topiladi,
    /// vazifaning jonli holati (bosqich, oxirgi hodisa) o'qiladi; boshqa hisob agent ma'lumotini ko'rmaydi.
    /// </summary>
    [Fact]
    public async Task OnlineStore_AgentBeatPendingWorkAndJobLive()
    {
        if (string.IsNullOrEmpty(Cs)) return;
        var schema = "ytauto_test_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _ = new PostgresJobStore(Cs, schema);
            var s = new PostgresJobStore(Device(schema, "A"), schema);
            var b = new PostgresJobStore(Device(schema, "B"), schema);

            Assert.True(s.AgentBeat("PC1", "https://abc-def.trycloudflare.com", "secret-0123456789abcdefghijklmnopqrstuv", "1.2"));
            Assert.True(s.AgentBeat("PC1", null, null, null)); // tunnel uzildi: manzil olib tashlanadi, sir qoladi
            await using (var c = new NpgsqlConnection(Cs))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"SELECT agent_url, agent_secret, agent_seen_utc IS NOT NULL, version FROM \"{schema}\".machines WHERE machine = 'PC1'", c);
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.True(r.IsDBNull(0));
                Assert.Equal("secret-0123456789abcdefghijklmnopqrstuv", r.GetString(1));
                Assert.True(r.GetBoolean(2));
                Assert.Equal("1.2", r.GetString(3));
            }
            s.AgentBeat("PC1", "https://abc-def.trycloudflare.com", null, null);

            // Kutayotgan so'rovlar: yo'q → hozir chiqarish → tekshiruv → buyruq
            Assert.False(s.HasPendingRemoteWork("PC1"));
            var j = J("Live", DateTimeOffset.UtcNow); s.Insert(j);
            Assert.False(s.HasPendingRemoteWork("PC1")); // oddiy mavzu - jadvalda, uyg'otish shart emas
            s.SetPublishNow(j.Id, true);
            Assert.True(s.HasPendingRemoteWork("PC1"));
            s.SetPublishNow(j.Id, false);
            Assert.False(s.HasPendingRemoteWork("PC1"));
            Assert.False(b.HasPendingRemoteWork("PC1")); // boshqa hisob A'ning so'rovini ko'rmaydi

            // Jonli holat: bajarilayotgan bosqich va oxirgi hodisa
            s.SaveStage(new StageRecord { JobId = j.Id, Stage = StageKind.Script, State = StageState.Done });
            s.SaveStage(new StageRecord { JobId = j.Id, Stage = StageKind.Voice, State = StageState.Running });
            s.AddEvent(j.Id, "INFO", "Voice", "ovoz yaratilmoqda");
            var live = s.JobLive(j.Id)!;
            Assert.Equal((int)StageKind.Voice, live.RunningStage);
            Assert.Equal((int)StageKind.Script, live.DoneStage);
            Assert.Equal("ovoz yaratilmoqda", live.LastMessage);
            Assert.False(live.HasVideo);
            Assert.Null(b.JobLive(j.Id)); // RLS

            s.AgentStopped("PC1");
            await using (var c = new NpgsqlConnection(Cs))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"SELECT agent_url IS NULL AND agent_seen_utc IS NULL FROM \"{schema}\".machines WHERE machine = 'PC1'", c);
                Assert.Equal(true, await cmd.ExecuteScalarAsync());
            }
        }
        finally
        {
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync();
            var roles = new List<string>();
            try
            {
                await using (var sel = new NpgsqlCommand($"SELECT role_name FROM \"{schema}\".account_roles", conn))
                await using (var r = await sel.ExecuteReaderAsync()) while (await r.ReadAsync()) roles.Add(r.GetString(0));
            }
            catch { }
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
            foreach (var role in roles)
            {
                await using var dr = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", conn);
                await dr.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>Hisob va qurilma rolini yaratadi; shu rol bilan ulanish satrini qaytaradi.</summary>
    private static string Device(string schema, string name)
    {
        var pw = "test-" + Guid.NewGuid().ToString("N");
        using var c = new NpgsqlConnection(Cs);
        c.Open();
        using var cmd = new NpgsqlCommand($"SELECT role_name FROM \"{schema}\".register_account(@d, @m, @h, @p)", c);
        cmd.Parameters.AddWithValue("d", name); cmd.Parameters.AddWithValue("m", name);
        cmd.Parameters.AddWithValue("h", YtAuto.Infrastructure.Accounts.DeviceAccount.Hash(Guid.NewGuid().ToString()));
        cmd.Parameters.AddWithValue("p", pw);
        var role = (string)cmd.ExecuteScalar()!;
        var b = new NpgsqlConnectionStringBuilder(Cs) { Username = role, Password = pw };
        return b.ConnectionString;
    }

    [Fact]
    public void LocalStore_DeleteRemovesStagesAndEvents_ButNotClaimed()
    {
        using var t = new TempDir();
        var s = new SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        var j = J("X", DateTimeOffset.UtcNow); s.Insert(j);
        s.SaveStage(new StageRecord { JobId = j.Id, Stage = StageKind.Script, State = StageState.Done });
        s.AddEvent(j.Id, "INFO", null, "m");
        var k = J("Y", DateTimeOffset.UtcNow.AddMinutes(1)); s.Insert(k);
        s.ClaimNext("w", DateTimeOffset.UtcNow); // X band qilinadi
        Assert.False(s.Delete(j.Id));
        s.ReleaseClaim(j.Id, "w");
        Assert.True(s.Delete(j.Id));
        Assert.Null(s.Get(j.Id));
        Assert.Empty(s.GetStages(j.Id));
        Assert.Empty(s.GetEvents(j.Id));
        Assert.NotNull(s.Get(k.Id));
    }

    [Theory]
    [InlineData("worker-DESKTOPABC-20261114120000-1234", "worker-DESKTOPABC-")]
    [InlineData("desktop-PC1-20261114120000-1", "desktop-PC1-")]
    public void MachinePrefixFromRunId(string run, string prefix) => Assert.Equal(prefix, PostgresJobStore.MachineRunPrefix(run));
}
