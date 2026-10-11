using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SevenWondersDuel.Advisor.Tts;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Ticket 32: the External Editor API client against a fake TTS that runs the received Lua on the practice save,
/// and the <c>tts-snapshot</c> command the owner uses for the live check.
/// </summary>
public sealed class TtsLinkTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly Lazy<FakeTable> Practice = new(() => FakeTable.FromSave(TtsSnapshotTests.SavePath));

    static (TtsLink Link, FakeTtsServer Tts) Connect(Func<string, List<Dictionary<string, object?>>> run)
    {
        TtsLink? link = null;
        var tts = new FakeTtsServer(run, () => link!.ListenPort);
        link = new TtsLink(tts.Port, listenPort: 0);
        return (link, tts);
    }

    [Fact]
    public async Task A_snapshot_round_trip_sends_the_committed_script_and_returns_what_TTS_sent()
    {
        var (link, tts) = Connect(Practice.Value.Run);
        using var _ = link;
        using var __ = tts;
        tts.Before.Add("{\r\n  \"message\": \"some print\",\r\n  \"messageID\": 2\r\n}"); // other traffic is ignored

        var first = await link.RequestSnapshotTextAsync(Wait);
        var second = await link.RequestSnapshotAsync(Wait);
        Assert.Equal(File.ReadAllText(TtsSnapshotTests.FixturePath), first);
        Assert.Equal(first, second.Text); // an unchanged table gives an identical snapshot
        Assert.Equal(2, tts.Scripts.Count);
        Assert.NotEqual(tts.Scripts[0], tts.Scripts[1]); // each request carries its own id in the committed script
        Assert.Equal(TtsData.SnapshotScript, Regex.Replace(tts.Scripts[0], "\"r1-\\d+\"", $"\"{TtsData.RequestPlaceholder}\""));
    }

    [Fact]
    public async Task A_script_failure_inside_TTS_is_reported()
    {
        var (link, tts) = Connect(script => Practice.Value.Run(script.Replace("local function main()", "local function main() error('boom')")));
        using var _ = link;
        using var __ = tts;
        var e = await Assert.ThrowsAsync<TtsException>(() => link.RequestSnapshotTextAsync(Wait));
        Assert.Contains("failed inside TTS", e.Message);
        Assert.Contains("boom", e.Message);
    }

    [Fact]
    public async Task No_answer_times_out_with_TTS_s_last_Lua_error()
    {
        var (link, tts) = Connect(_ => []);
        using var _ = link;
        using var __ = tts;
        var e = await Assert.ThrowsAsync<TtsException>(() => link.RequestSnapshotTextAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Contains("didn't answer", e.Message);

        tts.Before.Add("{\"error\": \"chunk_1:(3,4): unexpected symbol\", \"guid\": \"-1\", \"messageID\": 3}");
        e = await Assert.ThrowsAsync<TtsException>(() => link.RequestSnapshotTextAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Contains("unexpected symbol", e.Message);
    }

    [Fact]
    public async Task TTS_not_running_and_a_busy_listen_port_are_explained()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var unusedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using (var link = new TtsLink(unusedPort, listenPort: 0))
        {
            var e = await Assert.ThrowsAsync<TtsException>(() => link.RequestSnapshotTextAsync(Wait));
            Assert.Contains("Could not reach Tabletop Simulator", e.Message);
        }

        using var first = new TtsLink(unusedPort, listenPort: 0);
        var busy = Assert.Throws<TtsException>(() => new TtsLink(unusedPort, first.ListenPort));
        Assert.Contains("busy", busy.Message);
    }

    /// <summary>The owner's live check: the table read through the API compared with the committed practice snapshot.</summary>
    [Fact]
    public async Task The_tts_snapshot_command_compares_the_table_with_the_practice_save()
    {
        // The command opens its own link, so it gets a free port to listen on (the real one is 39998).
        var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        var listenPort = ((IPEndPoint)listen.LocalEndpoint).Port;
        listen.Stop();
        using var server = new FakeTtsServer(Practice.Value.Run, () => listenPort);

        var output = new StringWriter();
        var code = await TtsCommand.Run(["--compare", TtsSnapshotTests.FixturePath, "--count", "2", "--tts-port", server.Port.ToString(),
            "--listen-port", listenPort.ToString()], output);
        var text = output.ToString();
        Assert.True(code == 0, text);
        Assert.Contains("MATCH", text);
        Assert.Contains("same as the last one", text);
        Assert.Contains("pyramid 0.3: barracks", text);
        Assert.Contains("Seated: White (host)", text);

        // A table that differs (a city card moved into the discard pile) is listed place by place.
        var moved = new FakeTable(Practice.Value.Objects.Select(o => o.Name == "Baths" ? o with { X = -50.6, Z = 2.3 } : o).ToList(),
            FakeTable.ProbePlayers, "Age2");
        using var other = new FakeTtsServer(moved.Run, () => listenPort);
        output = new StringWriter();
        code = await TtsCommand.Run(["--compare", TtsSnapshotTests.FixturePath, "--tts-port", other.Port.ToString(),
            "--listen-port", listenPort.ToString()], output);
        text = output.ToString();
        Assert.Equal(2, code);
        Assert.Contains("missing: city White: baths", text);
        Assert.Contains("extra:   discard: baths", text);

        Assert.Equal(1, await TtsCommand.Run(["--bogus"], new StringWriter()));
    }
}
