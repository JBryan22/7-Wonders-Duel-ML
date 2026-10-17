using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents;

/// <summary>
/// The Heuristic's weights (spec A5, Agent Ladder). Every term of its evaluation is in "VP-equivalents", and each weight
/// scales one family of terms: <see cref="Points"/> the civilian score, <see cref="Military"/> the Conflict Pawn,
/// <see cref="Science"/> scientific symbols and the science side of Progress tokens, <see cref="Economy"/> production,
/// trading prices, coins in hand and chains, <see cref="Denial"/> how much the opponent's best next card counts against
/// a Move. Terms are scored for both players and subtracted, so a weight also sets how hard the Heuristic defends that
/// family. <see cref="Pantheon"/> scales the Pantheon-only terms (Pantheon games only; 0 plays Pantheon blind to them).
/// <see cref="Adaptive"/> reweights every decision from the board (see <see cref="HeuristicAgent.Adapt"/>).
/// </summary>
public sealed record HeuristicWeights(string Name, double Points = 1, double Military = 1, double Science = 1,
    double Economy = 1, double Coins = 1, double Denial = 0.6, bool Adaptive = false, double Pantheon = 1)
{
    /// <summary>Chases scientific symbols and pairs.</summary>
    public static readonly HeuristicWeights ScienceStyle = new("science", Points: 0.6, Military: 0.3, Science: 4);
    /// <summary>Chases shields and the opponent's capital.</summary>
    public static readonly HeuristicWeights MilitaryStyle = new("military", Points: 0.8, Military: 2.6, Science: 0.5);
    /// <summary>Builds VP: blue cards, Wonders, Guilds; plays supremacy only as defence.</summary>
    public static readonly HeuristicWeights CivilianStyle = new("civilian", Points: 1.4, Military: 0.5, Science: 0.5);
    /// <summary>Neutral weights, reweighted every turn from the board.</summary>
    public static readonly HeuristicWeights Balanced = new("balanced", Points: 0.8, Military: 0.7, Adaptive: true);

    public static IReadOnlyList<HeuristicWeights> Presets { get; } = [ScienceStyle, MilitaryStyle, CivilianStyle, Balanced];

    /// <summary>The preset with this name (<c>science</c>, <c>military</c>, <c>civilian</c>, <c>balanced</c>).</summary>
    public static HeuristicWeights Preset(string name) =>
        Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new FormatException($"Unknown Heuristic preset '{name}' (known: {string.Join(", ", Presets.Select(p => p.Name))})");

    /// <summary>
    /// Parses the argument of a <c>heuristic</c> Agent spec: a preset name, optionally followed by weight overrides,
    /// e.g. <c>balanced</c> or <c>balanced,science=2,adaptive=0</c> (for tuning). Null means <see cref="Balanced"/>.
    /// </summary>
    public static HeuristicWeights Parse(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return Balanced;
        var parts = argument.Split(',');
        var w = Preset(parts[0]);
        foreach (var part in parts[1..])
        {
            var eq = part.IndexOf('=');
            if (eq < 0 || !double.TryParse(part[(eq + 1)..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Bad Heuristic weight '{part}' (expected name=number)");
            w = part[..eq].ToLowerInvariant() switch
            {
                "points" => w with { Points = v },
                "military" => w with { Military = v },
                "science" => w with { Science = v },
                "economy" => w with { Economy = v },
                "denial" => w with { Denial = v },
                "coins" => w with { Coins = v },
                "adaptive" => w with { Adaptive = v != 0 },
                "pantheon" => w with { Pantheon = v },
                _ => throw new FormatException($"Unknown Heuristic weight '{part[..eq]}' (points, military, science, economy, coins, denial, adaptive, pantheon)"),
            };
        }
        return w;
    }
}

/// <summary>
/// The second rung of the Agent Ladder (spec A5): hand-written scoring with configurable weights. For each legal Move it
/// copies the observed state, applies the Move, follows the player's own immediate follow-up choices (a Wonder's
/// destroy, Mausoleum, Messe or Statue of Liberty pick, a Progress token), and scores the position it reaches with
/// <see cref="Evaluate"/>, which reads the board through <see cref="DerivedFeatures"/>. A taken card also pays for what
/// it leaves the opponent: the best card they could take next, including a card that would win them the game.
/// Follow-ups that start with a Reveal (the Great Library's draw, the Statue's returned cards, flipped cards) are
/// averaged over a few draws from the Agent's own seeded Chance Source, which sees only the Observation. So a promo
/// Wonder is worth its VP plus its best card grab. The Wonder draft uses a fixed value per Wonder.
/// </summary>
public sealed partial class HeuristicAgent : IAgent
{
    /// <summary>The value of winning (and, negated, of losing): larger than any other evaluation.</summary>
    public const double WinValue = 1000;
    /// <summary>Draws of the Chance Source averaged when a follow-up needs a Reveal.</summary>
    const int RevealSamples = 3;
    /// <summary>Follow-up depth: a Wonder, its power's pick, a token pick from that pick, and one more for safety.</summary>
    const int MaxDepth = 6;

    /// <summary>Science value by distinct symbols held (6 is supremacy, a win).</summary>
    static readonly double[] ScienceCurve = [0, 0.6, 1.6, 3.2, 5.5, 9.5, WinValue];
    /// <summary>Production a player normally needs of each resource per cost (wood, clay, stone, glass, papyrus).</summary>
    static readonly int[] TypicalNeed = [2, 2, 2, 1, 1];

    readonly HeuristicWeights _base;
    readonly Rng _rng;
    readonly RandomChanceSource _chance;
    readonly List<GameState> _scratch = [];
    HeuristicWeights _w;
    int _me;

    public HeuristicAgent(HeuristicWeights weights, ulong seed)
    {
        _base = _w = weights;
        _rng = new Rng(seed);
        _chance = new RandomChanceSource(_rng.NextUInt64());
    }

    public HeuristicWeights Weights => _base;

    public Move ChooseMove(Observation observation)
    {
        var root = observation.State;
        _me = observation.Player;
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var n = observation.LegalMoves(moves);
        if (n == 0) throw new InvalidOperationException("No legal Move");
        if (n == 1) return moves[0];

        _w = _base.Adaptive ? Adapt(_base, root, _me) : _base;
        var kind = root.Pending(0).Kind;
        if (kind == DecisionKind.ChooseStarter)
            return Move.StartMe; // taking the first card of an Age is worth more than seeing the opponent's choice

        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var score = (kind == DecisionKind.DraftWonder ? WonderValue(moves[i].Wonder) : ScoreMove(root, moves[i]))
                + _rng.Next(1024) * 1e-6; // seeded tie-break
            if (score > bestScore) (best, bestScore) = (i, score);
        }
        return moves[best];
    }

    /// <summary>
    /// The adaptive balanced reweighting (spec A5): science counts more as either player, above all the opponent,
    /// gathers distinct symbols; military counts more as the Conflict Pawn nears either capital; when behind on
    /// points the supremacy weights rise (a civilian count would be lost), when ahead the point weight rises.
    /// </summary>
    public static HeuristicWeights Adapt(HeuristicWeights w, GameState s, int me)
    {
        var opp = 1 - me;
        var science = w.Science * (1 + 0.45 * Math.Max(0, DerivedFeatures.ScienceSymbols(s, opp) - 2)
            + 0.3 * Math.Max(0, DerivedFeatures.ScienceSymbols(s, me) - 2));
        var nearest = Math.Min(DerivedFeatures.DistanceToCapital(s, me), DerivedFeatures.DistanceToCapital(s, opp));
        var military = w.Military * (1 + 0.3 * Math.Max(0, GameState.CapitalDistance - 2 - nearest));
        var lead = DerivedFeatures.Vp(s, me) - DerivedFeatures.Vp(s, opp);
        var points = w.Points;
        if (lead < 0)
        {
            var push = 1 + 0.04 * Math.Min(-lead, 15) * (s.Age >= 2 ? 1 : 0.5);
            (science, military) = (science * push, military * push);
        }
        else points *= 1 + 0.02 * Math.Min(lead, 15);
        return w with { Points = points, Military = military, Science = science };
    }

    // ---------------------------------------------------------------- Moves and follow-ups

    GameState Scratch(int depth)
    {
        while (_scratch.Count <= depth) _scratch.Add(GameState.New(Ruleset.Default));
        return _scratch[depth];
    }

    /// <summary>The value of one Move from <paramref name="root"/>: the position after it and its follow-ups, plus what a taken card leaves the opponent.</summary>
    double ScoreMove(GameState root, Move move)
    {
        var s = Scratch(0);
        s.CopyFrom(root);
        s.Apply(move);
        var score = FollowUp(s, 1);
        // An activation (Pantheon) takes no card but leaves the opponent the same choice of cards as a take does.
        if ((move.TakesCard || move.Kind == MoveKind.Activate) && !s.IsOver) score += AfterTake(s, root.Age);
        if (move.Kind == MoveKind.Discard) score -= 0.15; // a discard is the last resort among equals
        return score;
    }

    /// <summary>
    /// Scores <paramref name="s"/> after following this player's own choices that come straight after a Move
    /// (the best of each), and averaging over Reveals they wait on. Stops at the opponent's decisions and at the next
    /// card take.
    /// </summary>
    double FollowUp(GameState s, int depth)
    {
        if (s.IsOver || depth >= MaxDepth || s.PendingCount == 0) return Evaluate(s, _me, _w);
        var next = s.Pending(0);
        if (next.Player != _me || next.Kind is DecisionKind.TakeCard or DecisionKind.ChooseStarter or DecisionKind.DraftWonder)
            return Evaluate(s, _me, _w);

        if (s.Step == StepKind.Reveal)
        {
            var total = 0.0;
            var sample = Scratch(depth);
            for (var k = 0; k < RevealSamples; k++)
            {
                sample.CopyFrom(s);
                while (sample.Step == StepKind.Reveal) sample.Apply(_chance.Next(sample));
                total += FollowUp(sample, depth + 1);
                // the deeper levels used scratch states above this depth only, so the sample is still ours
            }
            return total / RevealSamples;
        }

        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var n = s.LegalMoves(moves);
        if (n == 0) return Evaluate(s, _me, _w);
        var child = Scratch(depth);
        var best = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            child.CopyFrom(s);
            child.Apply(moves[i]);
            best = Math.Max(best, FollowUp(child, depth + 1));
        }
        return best;
    }

    /// <summary>
    /// What a card take leaves behind, right after the take (before any Reveal): an extra turn for this player
    /// within the Age is a bonus; otherwise the opponent's best accessible known card counts against the Move
    /// (a card that wins them the game counts as a loss), with a floor when an unknown card is uncovered.
    /// </summary>
    double AfterTake(GameState s, int ageBefore)
    {
        if (s.Age != ageBefore) return 0; // the Age ended: nothing is left to the opponent
        var opp = 1 - _me;
        var mineNext = NextTaker(s) == _me;
        var threat = 0.0;
        var unknown = false;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            if (!s.IsAccessible(slot)) continue;
            var card = s.SlotCard(slot);
            if (card < 0)
            {
                unknown = true;
                continue;
            }
            threat = Math.Max(threat, CardValue(s, opp, card, _w));
        }
        if (unknown) threat = Math.Max(threat, 1.5);
        if (threat >= WinValue) return mineNext ? -0.1 * WinValue : -WinValue / 2;
        var playAgain = mineNext ? 2.5 * AgeLeft(s) + 0.5 : 0;
        return playAgain - _w.Denial * threat * (mineNext ? 0.35 : 1);
    }

    /// <summary>The player of the first take-card decision in the queue, or -1.</summary>
    static int NextTaker(GameState s)
    {
        for (var i = 0; i < s.PendingCount; i++)
            if (s.Pending(i).Kind == DecisionKind.TakeCard) return s.Pending(i).Player;
        return -1;
    }

    /// <summary>
    /// Production, trading prices and chains are scored in "resource units" (1 for a needed unit); this turns them
    /// into VP-equivalents. Measured: production is worth far more than its VP-free look suggests (a card you can
    /// afford later is a card you get), and scales of 6-9 played best in Matches between weightings.
    /// </summary>
    const double ProductionScale = 8;

    /// <summary>What a coin in hand is worth in VP-equivalents beyond its third of a VP: buying power, less in Age III.</summary>
    static double CoinValue(GameState s) => s.Age >= 3 ? 0.5 : 1.1;

    /// <summary>How much production and economy still matter: all of it in Age I, little in Age III.</summary>
    static double AgeLeft(GameState s) => s.Age switch { <= 1 => 1, 2 => 0.65, _ => 0.25 };

    // ---------------------------------------------------------------- evaluation

    /// <summary>
    /// The position from <paramref name="me"/>'s side in VP-equivalents: a win is <see cref="WinValue"/>, a loss its
    /// negative; otherwise both players' scores are subtracted, plus the Conflict Pawn's pull.
    /// </summary>
    public static double Evaluate(GameState s, int me, HeuristicWeights w)
    {
        if (s.Result is { } result)
            return result.IsDraw ? 0 : result.Winner == me ? WinValue : -WinValue;
        return PlayerValue(s, me, w) - PlayerValue(s, 1 - me, w) + w.Military * MilitaryPull(s.MilitaryLead(me));
    }

    /// <summary>
    /// The Conflict Pawn's value beyond its military VP: steeper as it nears a capital (a lead of 9 wins).
    /// Antisymmetric, so it is counted once from <c>me</c>'s side.
    /// </summary>
    static double MilitaryPull(int lead)
    {
        var a = Math.Abs(lead);
        var v = 0.6 * a + (a >= 5 ? 1.2 * (a - 4) * (a - 4) : 0);
        return Math.Sign(lead) * v;
    }

    static double PlayerValue(GameState s, int p, HeuristicWeights w)
    {
        var age = AgeLeft(s);
        var value = w.Points * DerivedFeatures.Vp(s, p);

        // Economy: production up to a typical need, cheap trading, coins in hand, chains still to come.
        var economy = 0.0;
        for (var r = 0; r < Resources.Count; r++)
        {
            var have = DerivedFeatures.Production(s, p, (Resource)r);
            var need = TypicalNeed[r];
            economy += Math.Min(have, need) * 1.3 + Math.Max(0, have - need) * 0.3;
            economy -= 0.3 * need * (DerivedFeatures.TradingPrice(s, p, (Resource)r) - 2);
        }
        economy += 1.1 * DerivedFeatures.ChoiceProducers(s, p);
        economy += 0.7 * DerivedFeatures.OpenChains(s, p);
        value += w.Economy * ProductionScale * age * economy;
        value += w.Coins * CoinValue(s) * Math.Min(s.SpendableCoins(p), 15);

        // Science: distinct symbols on a convex curve (pairs show up as Progress tokens).
        value += w.Science * ScienceCurve[Math.Min(DerivedFeatures.ScienceSymbols(s, p), ScienceCurve.Length - 1)];

        // Progress tokens beyond their printed VP, coins and symbol.
        if (s.Tokens(p) != 0)
        {
            if (s.HasToken(p, ProgressTokens.Architecture)) value += 2.5 * age * w.Economy;
            if (s.HasToken(p, ProgressTokens.Masonry)) value += 2.0 * age * w.Economy;
            if (s.HasToken(p, ProgressTokens.Urbanism)) value += 2.0 * age * w.Economy;
            if (s.HasToken(p, ProgressTokens.Economy)) value += 1.5 * w.Economy;
            if (s.HasToken(p, ProgressTokens.Theology)) value += 2.5 * age;
            if (s.HasToken(p, ProgressTokens.Strategy)) value += 2.5 * age * w.Military;
            if (s.HasToken(p, ProgressTokens.Law)) value += 0.5 * w.Science;
        }
        if (s.Pantheon is { } pantheon) value += w.Pantheon * PantheonPlayerValue(s, pantheon, p, w);
        return value;
    }

    /// <summary>
    /// A quick estimate of what building <paramref name="card"/> now is worth to <paramref name="player"/>, without
    /// applying it: <see cref="WinValue"/> if it wins by science or military, else its points, science, shields,
    /// production and coins, net of its price. A card the player cannot afford is worth little to them.
    /// </summary>
    public static double CardValue(GameState s, int player, int card, HeuristicWeights w)
    {
        var def = GameData.Card(card);
        var price = Payment.Quote(s, player, card).Total;
        if (price > s.SpendableCoins(player)) return 0.4;

        var value = 0.0;
        if (def.Science is { } symbol)
        {
            var distinct = DerivedFeatures.ScienceSymbols(s, player);
            if (DerivedFeatures.IsNewSymbol(s, player, symbol))
            {
                if (distinct + 1 >= GameState.ScienceSupremacySymbols) return WinValue;
                value += w.Science * (ScienceCurve[distinct + 1] - ScienceCurve[distinct]);
            }
            else if (s.SymbolCount(player, symbol) == 1 && s.BoardTokens != 0) value += 3 + 0.5 * w.Science;
        }
        if (def.Shields > 0)
        {
            var shields = def.Shields + (def.Color == CardColor.Red && s.HasToken(player, ProgressTokens.Strategy) ? 1 : 0);
            var lead = s.MilitaryLead(player);
            if (lead + shields >= GameState.CapitalDistance) return WinValue;
            value += w.Military * (MilitaryPull(lead + shields) - MilitaryPull(lead))
                + w.Points * (Scoring.MilitaryPoints(lead + shields) - Scoring.MilitaryPoints(lead));
        }
        value += w.Points * (def.IsGuild ? Scoring.GuildPoints(s, def.Guild) + 1
            : def.Color == CardColor.Temple ? TempleGain(s, player) : def.VictoryPoints);

        var age = AgeLeft(s);
        for (var r = 0; r < Resources.Count; r++)
            if (def.Produce[r] > 0)
                value += w.Economy * ProductionScale * age * (s.Production(player, (Resource)r) < TypicalNeed[r] ? 1.3 : 0.3) * def.Produce[r];
        if (def.ProduceChoice.Length > 0) value += w.Economy * ProductionScale * age * 1.1;
        if (def.FixedPrice.Length > 0) value += w.Economy * ProductionScale * age * 0.8 * def.FixedPrice.Length;
        value += w.Coins * CoinValue(s) * (def.Coins - price);
        return value;
    }

    // ---------------------------------------------------------------- the Wonder draft

    /// <summary>
    /// A drafted Wonder's value: its VP, coins and shields, a play-again, and its power. The choice powers are valued
    /// as their best card grab: the Messe a free top-row card, the Statue of Liberty a free returned card (net of the
    /// one given away), the Mausoleum a free discarded card, the Great Library a Progress token. Expensive Wonders
    /// count a little less.
    /// </summary>
    double WonderValue(int wonder)
    {
        var w = GameData.Wonder(wonder);
        var value = _w.Points * w.VictoryPoints + _w.Economy * 0.35 * w.Coins + 0.3 * w.OpponentLoses
            + (_w.Military * 1.6 + 0.5) * w.Shields
            + (w.PlayAgain ? 2.5 : 0)
            + (w.ProduceChoice.Length > 0 ? 2.0 * _w.Economy : 0)
            - 0.3 * w.Cost.ResourceTotal;
        value += w.Effect switch
        {
            WonderEffect.DestroyBrown or WonderEffect.DestroyGrey => 1.5,
            WonderEffect.Library => 2.5 + 1.5 * _w.Science,
            WonderEffect.Mausoleum => 2.5,
            WonderEffect.Messe => 4.0,
            WonderEffect.Liberty => 3.5,
            _ => 0,
        };
        if (wonder >= GameData.Wonders.Length) value += PantheonWonderPower(w);
        return value;
    }
}
