using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>Plays one game to the end: Reveals from the Chance Source, Moves from the Agents, everything logged.</summary>
public static class GameRunner
{
    public static Game Play(Ruleset rules, IChanceSource chance, IAgent p1, IAgent p2)
    {
        var game = new Game(rules);
        while (true)
        {
            switch (game.State.Step)
            {
                case StepKind.GameOver:
                    return game;
                case StepKind.Reveal:
                    game.Apply(chance.Next(game.State));
                    break;
                default:
                    var player = game.State.PlayerToDecide;
                    var agent = player == 0 ? p1 : p2;
                    game.Apply(agent.ChooseMove(game.State.Observe(player)));
                    break;
            }
        }
    }

    /// <summary>A random game from one seed: the Chance Source and both Agents get seeds derived from it.</summary>
    public static Game PlayRandom(ulong seed, Ruleset? rules = null)
    {
        var seeds = new Rng(seed);
        return Play(rules ?? Ruleset.Default,
            new RandomChanceSource(seeds.NextUInt64()),
            new RandomAgent(seeds.NextUInt64()),
            new RandomAgent(seeds.NextUInt64()));
    }
}
