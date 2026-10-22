using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// One object on a fake TTS table. <see cref="Contents"/> is set for containers (decks, bags); <see cref="Image"/> is a
/// custom object's face image URL (what getCustomObject().image returns).
/// </summary>
internal sealed record FakeObject(
    string Type, string Guid, string Name, double X, double Y, double Z,
    bool FaceDown = false, IReadOnlyList<string>? Tags = null, string Description = "",
    IReadOnlyList<FakeContent>? Contents = null, string? HeldBy = null, bool Resting = true, string? Image = null);

internal sealed record FakeContent(string Guid, string Name, IReadOnlyList<string> Tags);

/// <summary>A rectangle on the table that is a player's hand zone.</summary>
internal sealed record FakeHand(string Color, double X1, double X2, double Z1, double Z2);

/// <summary>
/// A fake Tabletop Simulator table that runs tts/snapshot.lua in MoonSharp, the Lua interpreter TTS itself is built
/// on, with TTS's sandbox modules. Objects expose only getters: calling or setting anything else (setPosition, flip,
/// destruct, ...) raises a Lua error, and so does creating a global, so a test run proves the script is read-only.
/// <see cref="sendExternalMessage"/> also fails on a nested table, which real TTS silently drops.
/// </summary>
internal sealed class FakeTable(IReadOnlyList<FakeObject> objects, IReadOnlyList<(string Color, bool Host)> players,
    string currentAge, IReadOnlyList<FakeHand>? hands = null)
{
    public const CoreModules TtsModules = CoreModules.Basic | CoreModules.Bit32 | CoreModules.Coroutine | CoreModules.Dynamic
        | CoreModules.ErrorHandling | CoreModules.Math | CoreModules.Metatables | CoreModules.OS_Time | CoreModules.String
        | CoreModules.Table | CoreModules.TableIterators;

    public IReadOnlyList<FakeObject> Objects { get; } = objects;

    /// <summary>The seats the probe recorded (research ticket 03): the owner hosts, seated White.</summary>
    public static readonly (string, bool)[] ProbePlayers = [("White", true)];

    /// <summary>Runs a script as Execute Lua against Global; returns every message it sent with sendExternalMessage.</summary>
    public List<Dictionary<string, object?>> Run(string lua)
    {
        var script = new Script(TtsModules);
        var sent = new List<Dictionary<string, object?>>();
        var tables = Objects.Select(o => ObjectTable(script, o)).ToList();

        script.Globals["getObjects"] = DynValue.NewCallback((_, _) => List(script, tables));
        script.Globals["current_age"] = currentAge;
        script.Globals["sendExternalMessage"] = DynValue.NewCallback((_, args) =>
        {
            var t = args[args.Count - 1].Table ?? throw new ScriptRuntimeException("sendExternalMessage needs a table");
            var message = new Dictionary<string, object?>();
            foreach (var pair in t.Pairs)
            {
                if (pair.Value.Type == DataType.Table) throw new ScriptRuntimeException("nested table in sendExternalMessage: TTS drops it");
                message[pair.Key.String] = pair.Value.Type switch
                {
                    DataType.String => pair.Value.String,
                    DataType.Number => pair.Value.Number,
                    DataType.Boolean => pair.Value.Boolean,
                    _ => null,
                };
            }
            sent.Add(message);
            return DynValue.Nil;
        });
        script.Globals["Player"] = PlayerTable(script, tables);

        var before = script.Globals.Keys.Select(k => k.ToPrintString()).ToHashSet();
        var globals = script.Globals;
        var guard = new Table(script);
        guard["__newindex"] = DynValue.NewCallback((_, args) =>
            throw new ScriptRuntimeException($"the script set the global '{args[1].ToPrintString()}'"));
        globals.MetaTable = guard;
        script.DoString(lua);
        var after = script.Globals.Keys.Select(k => k.ToPrintString()).ToHashSet();
        if (!after.SetEquals(before)) throw new InvalidOperationException("the script changed the globals");
        return sent;
    }

    /// <summary>Runs the committed snapshot script and returns the snapshot JSON it sent.</summary>
    public string Snapshot(string request = "test")
    {
        var sent = Run(Tts.TtsData.ScriptFor(request));
        var message = Assert.Single(sent);
        if ((string?)message["tts7wd"] == "error") throw new InvalidOperationException($"snapshot.lua failed: {message["error"]}");
        Assert.Equal("snapshot", message["tts7wd"]);
        Assert.Equal(request, message["request"]);
        return (string)message["snapshot"]!;
    }

    DynValue PlayerTable(Script script, List<Table> tables)
    {
        var player = new Table(script);
        player["getPlayers"] = DynValue.NewCallback((_, _) => List(script, players.Select(p =>
        {
            var t = new Table(script);
            t["color"] = p.Color;
            t["host"] = p.Host;
            return t;
        }).ToList()));
        foreach (var color in new[] { "White", "Red" })
        {
            var seat = new Table(script);
            seat["getHandObjects"] = DynValue.NewCallback((_, _) => List(script, Objects
                .Select((o, i) => (o, i))
                .Where(x => (hands ?? []).Any(h => h.Color == color && x.o.X >= h.X1 && x.o.X <= h.X2 && x.o.Z >= h.Z1 && x.o.Z <= h.Z2))
                .Select(x => tables[x.i]).ToList()));
            ReadOnly(script, seat, color);
            player[color] = seat;
        }
        ReadOnly(script, player, "Player");
        return DynValue.NewTable(player);
    }

    static DynValue List(Script script, IReadOnlyList<Table> items)
    {
        var t = new Table(script);
        for (var i = 0; i < items.Count; i++) t[i + 1] = items[i];
        return DynValue.NewTable(t);
    }

    static Table ObjectTable(Script script, FakeObject o)
    {
        var t = new Table(script);
        t["type"] = o.Type;
        t["guid"] = o.Guid;
        t["is_face_down"] = o.FaceDown;
        t["resting"] = o.Resting;
        if (o.HeldBy is not null) t["held_by_color"] = o.HeldBy;
        DynValue Fn(Func<CallbackArguments, DynValue> f) => DynValue.NewCallback((_, args) => f(args));
        t["getName"] = Fn(_ => DynValue.NewString(o.Name));
        t["getGUID"] = Fn(_ => DynValue.NewString(o.Guid));
        t["getDescription"] = Fn(_ => DynValue.NewString(o.Description));
        t["getPosition"] = Fn(_ =>
        {
            var p = new Table(script);
            p["x"] = o.X;
            p["y"] = o.Y;
            p["z"] = o.Z;
            return DynValue.NewTable(p);
        });
        t["hasTag"] = Fn(args => DynValue.NewBoolean((o.Tags ?? []).Any(tag =>
            string.Equals(tag, args[args.Count - 1].String, StringComparison.OrdinalIgnoreCase))));
        t["getTags"] = Fn(_ => List(script, (o.Tags ?? []).Select(tag => { var x = new Table(script); x[1] = tag; return x; }).ToList()));
        t["getQuantity"] = Fn(_ => DynValue.NewNumber(o.Contents?.Count ?? -1));
        t["getCustomObject"] = Fn(_ =>
        {
            var custom = new Table(script);
            if (o.Image is not null) custom["image"] = o.Image;
            return DynValue.NewTable(custom);
        });
        t["getObjects"] = Fn(_ =>
        {
            if (o.Contents is null) throw new ScriptRuntimeException("getObjects on a non-container");
            return List(script, o.Contents.Select((c, i) =>
            {
                var e = new Table(script);
                e["name"] = c.Name;
                e["guid"] = c.Guid;
                e["index"] = i;
                var tags = new Table(script);
                for (var k = 0; k < c.Tags.Count; k++) tags[k + 1] = c.Tags[k];
                e["tags"] = tags;
                return e;
            }).ToList());
        });
        ReadOnly(script, t, $"object {o.Guid}", nilKeys: ["held_by_color"]);
        return t;
    }

    /// <summary>Unknown keys (setPosition, flip, destruct, ...) raise an error, and so does any assignment.</summary>
    static void ReadOnly(Script script, Table t, string what, string[]? nilKeys = null)
    {
        var meta = new Table(script);
        meta["__index"] = DynValue.NewCallback((_, args) =>
        {
            var key = args[1].ToPrintString();
            if (nilKeys is not null && nilKeys.Contains(key)) return DynValue.Nil;
            throw new ScriptRuntimeException($"fake TTS: {what} has no readable '{key}' (the snapshot script must only read)");
        });
        meta["__newindex"] = DynValue.NewCallback((_, args) =>
            throw new ScriptRuntimeException($"fake TTS: the script assigned {what}.{args[1].ToPrintString()}"));
        t.MetaTable = meta;
    }

    /// <summary>The table of a TTS save file (top-level objects only; containers keep their contents).</summary>
    public static FakeTable FromSave(string path, IReadOnlyList<(string, bool)>? players = null)
    {
        var save = JsonNode.Parse(File.ReadAllText(path))!;
        var age = (string?)JsonNode.Parse((string)save["LuaScriptState"]!)?["currentAge"] ?? "None";
        var objects = new List<FakeObject>();
        var hands = new List<FakeHand>();
        foreach (var o in save["ObjectStates"]!.AsArray())
        {
            var t = o!["Transform"]!;
            double D(string k) => (double)t[k]!;
            var saveType = (string)o["Name"]!;
            if (saveType == "HandTrigger")
            {
                var (hx, hz, sx, sz) = (D("posX"), D("posZ"), D("scaleX"), D("scaleZ"));
                hands.Add(new FakeHand((string)o["FogColor"]!, hx - sx / 2, hx + sx / 2, hz - sz / 2, hz + sz / 2));
                continue;
            }
            var contents = o["ContainedObjects"]?.AsArray()
                .Select(c => new FakeContent((string)c!["GUID"]!, (string?)c["Nickname"] ?? "", Tags(c)))
                .ToList();
            objects.Add(new FakeObject(
                TtsType(saveType), (string)o["GUID"]!, (string?)o["Nickname"] ?? "", D("posX"), D("posY"), D("posZ"),
                FaceDown: IsFaceDown(D("rotX"), D("rotZ")), Tags: Tags(o), Description: (string?)o["Description"] ?? "",
                Contents: contents, Image: (string?)o["CustomImage"]?["ImageURL"]));
        }
        return new FakeTable(objects, players ?? ProbePlayers, age, hands);
    }

    static List<string> Tags(JsonNode o) => o["Tags"]?.AsArray().Select(x => (string)x!).ToList() ?? [];

    /// <summary>TTS's <c>type</c> for a saved object's Name (only Card, Deck and Bag matter to the script).</summary>
    static string TtsType(string saveName) => saveName switch
    {
        "Card" or "CardCustom" => "Card",
        "Deck" or "DeckCustom" => "Deck",
        "Bag" => "Bag",
        "Infinite_Bag" => "Infinite",
        "Custom_Model" => "Generic",
        "Custom_Token" => "Tile",
        _ => saveName,
    };

    /// <summary>Face down when the object's up axis points down (Unity's Z-X-Y Euler order).</summary>
    public static bool IsFaceDown(double rotX, double rotZ) =>
        Math.Cos(rotX * Math.PI / 180) * Math.Cos(rotZ * Math.PI / 180) < 0;

    /// <summary>A file of the repository, found from this source file's location.</summary>
    public static string RepoFile(string relative, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", relative));
}

/// <summary>
/// A fake TTS External Editor API endpoint: it accepts "Execute Lua Code" on its own port, runs the script on a
/// <see cref="FakeTable"/> and delivers each sendExternalMessage to the link's listen port the way TTS does (one
/// connection per message, pretty-printed JSON with CRLF, here written in two pieces).
/// </summary>
internal sealed class FakeTtsServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Func<string, List<Dictionary<string, object?>>> _run;
    readonly Func<int> _replyPort;

    public FakeTtsServer(Func<string, List<Dictionary<string, object?>>> run, Func<int> replyPort)
    {
        _run = run;
        _replyPort = replyPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(Loop);
    }

    public int Port { get; }
    public List<string> Scripts { get; } = [];
    /// <summary>Extra raw messages sent before the answer (e.g. a print or an error).</summary>
    public List<string> Before { get; } = [];

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            using (client)
            {
                var text = await new StreamReader(client.GetStream(), Encoding.UTF8).ReadToEndAsync();
                var message = JsonNode.Parse(text)!;
                Assert.Equal(3, (int)message["messageID"]!);
                Assert.Equal("-1", (string)message["guid"]!);
                var script = (string)message["script"]!;
                lock (Scripts) Scripts.Add(script);
                foreach (var raw in Before) await Deliver(raw);
                foreach (var sent in _run(script))
                {
                    var custom = new JsonObject();
                    foreach (var (k, v) in sent)
                        custom[k] = v switch
                        {
                            string s => s,
                            double d => d,
                            bool b => b,
                            _ => null,
                        };
                    var reply = new JsonObject { ["customMessage"] = custom, ["messageID"] = 4 };
                    await Deliver(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n"));
                }
            }
        }
    }

    async Task Deliver(string raw)
    {
        using var back = new TcpClient();
        await back.ConnectAsync(IPAddress.Loopback, _replyPort());
        var bytes = Encoding.UTF8.GetBytes(raw);
        var half = bytes.Length / 2;
        var stream = back.GetStream();
        await stream.WriteAsync(bytes.AsMemory(0, half));
        await stream.FlushAsync();
        await Task.Delay(20);
        await stream.WriteAsync(bytes.AsMemory(half));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
