// Cross-check driver (ticket 11, spec A11 item 5): a line protocol over stdin/stdout around indjev99's C++ engine
// (MIT, https://github.com/indjev99/7-Wonders-Duel-AI), so `7wd crosscheck` can feed it our Moves and Reveals and
// compare legal Moves and final scores. Built by build.sh against a pinned checkout; never part of the C# build.
//
// Commands (one per line), each answered by exactly one line:
//   names              -> "names <id>=<Name_With_Underscores>;..." for every object id
//   new                -> <status> of a fresh game
//   do <type> <a1> <a2> -> <status> after applying the action, or "err <message>"
// <status> is one of
//   "next <type> <arg1> <arg2> <actor> <age> <coins0> <coins1> <military0> <military1> [<t>:<a1>:<a2> ...]"
//       the expected action and the C++ age (-2 setup, -1 draft, 0..2 for Ages I-III); the legal actions are
//       listed only when a player is to move
//   "end <result0> <score0> <score1> <blue0> <blue1> <coins0> <coins1> <military0> <military1>"
//       result0 is the C++ result code from player 0's side (0 draw, +-1 civilian, +-2 science, +-3 military,
//       +-4 civilian tie-break on blue points)

#include "game/game_state.h"
#include "game/objects.h"
#include "game/results.h"

#include <iostream>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>

static std::unique_ptr<GameState> state;

static void printStatus()
{
    std::ostringstream out;
    if (state->isTerminal())
    {
        out << "end " << state->getResult(0) << ' ' << state->getScore(0) << ' ' << state->getScore(1) << ' '
            << state->getScore(0, OT_BLUE) << ' ' << state->getScore(1, OT_BLUE) << ' '
            << state->getCoins(0) << ' ' << state->getCoins(1) << ' '
            << state->getMilitary(0) << ' ' << state->getMilitary(1);
    }
    else
    {
        Action e = state->getExpectedAction();
        out << "next " << e.type << ' ' << e.arg1 << ' ' << e.arg2 << ' ' << state->getCurrActor() << ' '
            << state->getCurrAge() << ' ' << state->getCoins(0) << ' ' << state->getCoins(1) << ' '
            << state->getMilitary(0) << ' ' << state->getMilitary(1);
        if (e.isPlayerMove())
        {
            for (const Action& a : state->getPossibleActions())
                out << ' ' << a.type << ':' << a.arg1 << ':' << a.arg2;
        }
    }
    std::cout << out.str() << '\n';
}

static std::string oneLine(std::string s)
{
    for (char& c : s)
        if (c == '\n' || c == '\r') c = ' ';
    return s;
}

int main()
{
    std::ios::sync_with_stdio(false);
    std::string line;
    while (std::getline(std::cin, line))
    {
        std::istringstream in(line);
        std::string cmd;
        in >> cmd;
        try
        {
            if (cmd == "names")
            {
                std::ostringstream out;
                out << "names";
                for (int id = 0; id < NUM_OBJECTS; id++)
                {
                    std::string name = objects[id].name;
                    for (char& c : name)
                        if (c == ' ') c = '_';
                    out << (id == 0 ? " " : ";") << id << '=' << name;
                }
                std::cout << out.str() << '\n';
            }
            else if (cmd == "new")
            {
                state = std::make_unique<GameState>();
                printStatus();
            }
            else if (cmd == "do")
            {
                if (!state) throw std::runtime_error("no game: send 'new' first");
                int type, a1, a2;
                if (!(in >> type >> a1 >> a2)) throw std::runtime_error("usage: do <type> <arg1> <arg2>");
                state->doAction(Action(type, a1, a2));
                printStatus();
            }
            else
            {
                std::cout << "err unknown command '" << cmd << "'\n";
            }
        }
        catch (const std::exception& e)
        {
            std::cout << "err " << oneLine(e.what()) << '\n';
        }
        std::cout.flush();
    }
    return 0;
}
