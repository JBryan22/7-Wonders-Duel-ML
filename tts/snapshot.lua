-- 7wd Table Snapshot (spec A9, ADR 0004, ticket 32).
--
-- The Advisor sends this script to Tabletop Simulator about once a second through the External Editor API
-- ("Execute Lua Code" against Global, guid "-1"). It reports every game piece on the table and sends the result back
-- with sendExternalMessage as ONE JSON STRING: TTS silently drops nested tables in that message, and Execute Lua's
-- return value never comes back (probe, research ticket 03).
--
-- READ-ONLY. This script must never move, flip, lock, spawn, destroy, shuffle, deal, highlight or otherwise change
-- anything on the table, and must never set a global variable (it runs inside the mod's Global script). Everything
-- below is local, and only getters are called. Advisor.Tests runs it against a fake table that fails on any other
-- call (TtsSnapshotScriptTests).
--
-- HIDDEN INFORMATION IS FILTERED HERE, so a hidden name never leaves TTS:
--   * a card is named only if it lies face up, or lies in the discard pile (public by the rules, though the mod
--     stacks discards face down);
--   * a deck is named card by card only in the discard pile; any other deck (removed cards, undealt Ages, Guilds,
--     unused Wonders) is reported as a count;
--   * a Progress token is named only if face up and not in a Great Library draw strip (the mod's button deals the
--     3 drawn tokens face up there, but the draw is private to its drawer);
--   * anything in a player's hand is never named;
--   * no GUIDs are reported: the mod file fixes each card's GUID, so a GUID would name a face-down card.
-- Face-down cards still report their back ("age", "guild" or "wonder"), which every player can see.
--
-- PANTHEON (spec-pantheon P-A9; nothing below changes what a base-game table reports, which has none of these pieces):
--   * a Divinity card or the Gate reports its back (its Mythology, or "gate") and a Grand Temple "temple": the backs
--     differ, so this is public; the name follows the rules above;
--   * before Age II (the mod's current_age "None" or "Age1") a Divinity is named on the table only on a Divinity board
--     (one taken with the Divine Theater): anywhere else, even face up, it is someone's private look (TTS's blindfold
--     hides it only from one player's eyes);
--   * a Divinity in the HOST's hand (the owner's private zone: their own draw, or a deck their Divine Theater looks
--     through) is named, marked "own"; nothing in any other hand is ever named (the opponent's draws stay hidden);
--   * Mythology and Offering tokens have no TTS name: one is reported as kind "ptoken", named by its face image (only
--     face up and not in a hand); the Snake and the Minerva pawn are kinds of their own.
-- The PANTHEON table (names to backs, token images) is filled in from tts/names.json when the script is sent.
--
-- Coordinates are sent as integer hundredths ("x":-5065 is -50.65) so the text never depends on the PC's locale.
-- Pieces are sorted, so two snapshots of an unchanged table are identical strings (settled-table detection).

do
  local REQUEST = "@REQUEST@"
  local PANTHEON = @PANTHEON@

  -- The discard pile: one face-down deck on a locked tile at about (-50.6, 2.3). Same rectangle as tts/places.json.
  local DISCARD = { x1 = -54, x2 = -47, z1 = -1.2, z2 = 5.8 }
  -- The Great Library draw strips: the mod's button deals the 3 tokens at x = -/+12, 8, 4 and z = -24.
  local LIBRARY = {
    { x1 = -14, x2 = -2, z1 = -26, z2 = -22 },
    { x1 = 2, x2 = 14, z1 = -26, z2 = -22 },
  }

  local function inRect(p, r)
    return p.x >= r.x1 and p.x <= r.x2 and p.z >= r.z1 and p.z <= r.z2
  end

  local function inLibrary(p)
    for _, r in ipairs(LIBRARY) do
      if inRect(p, r) then return true end
    end
    return false
  end

  local function onBoard(p)
    for _, r in ipairs(PANTHEON.boards) do
      if inRect(p, r) then return true end
    end
    return false
  end

  local function num(v)
    return string.format("%d", math.floor(v * 100 + 0.5))
  end

  local function str(s)
    s = string.gsub(tostring(s), '[%c"\\]', function(c)
      if c == '"' then return '\\"' end
      if c == "\\" then return "\\\\" end
      return string.format("\\u%04x", string.byte(c))
    end)
    return '"' .. s .. '"'
  end

  local function hasTag(o, tag)
    local ok, r = pcall(function() return o.hasTag(tag) end)
    return ok and r == true
  end

  -- A nameless Pantheon token's identity from its face image (the URL's last segment), or nil.
  local function pantheonToken(o)
    local ok, custom = pcall(function() return o.getCustomObject() end)
    if not ok or type(custom) ~= "table" or type(custom.image) ~= "string" then return nil end
    local hash = string.match(custom.image, "(%x+)/?$")
    if hash == nil then return nil end
    return PANTHEON.tokens[string.upper(hash)]
  end

  local function main()
    local host = nil
    for _, pl in ipairs(Player.getPlayers()) do
      if pl.host == true then host = pl.color end
    end
    -- Only the hands that exist (the mod has White's and Red's): in real TTS, Player[c].getHandObjects() for a colour
    -- without a hand zone throws a C# NullReferenceException, which pcall can't catch, and the whole script dies
    -- (live check, 2026-10-26).
    local inHand = {}
    local ok, hands = pcall(function() return Hands.getHands() end)
    if ok and type(hands) == "table" then
      for _, h in ipairs(hands) do
        local color = h.getValue()
        if type(color) == "string" then
          for _, o in ipairs(Player[color].getHandObjects() or {}) do inHand[o.getGUID()] = color end
        end
      end
    end

    local age = "None"
    if type(current_age) == "string" then age = current_age end
    local early = age == "None" or age == "Age1"

    local pieces = {}
    for _, o in ipairs(getObjects()) do
      -- Explicit nils: MoonSharp (TTS's Lua) can leave a bare "local x" in a loop holding a value from an earlier pass.
      local kind, value = nil, nil
      local objectType = o.type
      local name = o.getName() or ""
      if objectType == "Card" then
        if not hasTag(o, "color card") then kind = "card" end
      elseif objectType == "Deck" then
        kind = "deck"
      elseif PANTHEON.pieces[name] then
        kind = PANTHEON.pieces[name]
      elseif hasTag(o, "progress token") then
        kind = "token"
      elseif hasTag(o, "conflict pawn") then
        kind = "pawn"
      elseif hasTag(o, "military token") then
        kind = "military"
        value = tonumber(string.match(o.getDescription() or "", "loses (%d+)"))
      elseif objectType == "Bag" and name == "Progress tokens" then
        kind = "bag"
      else
        local gold = string.match(name, "^(%d+) Gold$")
        if gold then
          kind = "coin"
          value = tonumber(gold)
        elseif name == "" then
          value = pantheonToken(o)
          if value then kind = "ptoken" end
        end
      end

      if kind then
        local p = o.getPosition()
        local down = o.is_face_down == true
        local public = not inHand[o.getGUID()]
        local f = { '"kind":' .. str(kind), '"x":' .. num(p.x), '"y":' .. num(p.y), '"z":' .. num(p.z) }
        if down then table.insert(f, '"down":true') end
        if o.held_by_color then table.insert(f, '"held":true') end
        if o.resting == false then table.insert(f, '"moving":true') end
        if not public then table.insert(f, '"hand":true') end

        if kind == "card" then
          local back = PANTHEON.backs[name]
          local pantheon = back ~= nil
          if not pantheon then
            back = "age"
            if hasTag(o, "wonder") then back = "wonder" elseif hasTag(o, "purple") then back = "guild" end
          end
          table.insert(f, '"back":' .. str(back))
          -- Before Age II a Divinity is public only on a Divinity board (one taken with the Divine Theater); they lie face
          -- down until Age II's setup turns them up. A Divinity face up anywhere else then is a private look (e.g. the
          -- opponent's draw, flipped while the owner wears TTS's blindfold, which hides it only from the owner's eyes),
          -- so it is never named.
          local private = early and pantheon and back ~= "temple" and not onBoard(p)
          if public and not private and (not down or inRect(p, DISCARD)) then
            table.insert(f, '"name":' .. str(name))
          elseif pantheon and back ~= "temple" and host ~= nil and inHand[o.getGUID()] == host then
            table.insert(f, '"name":' .. str(name))
            table.insert(f, '"own":true')
          end
        elseif kind == "deck" then
          local contents = o.getObjects() or {}
          table.insert(f, '"count":' .. #contents)
          if public and inRect(p, DISCARD) then
            local names = {}
            for _, c in ipairs(contents) do table.insert(names, str(c.name or "")) end
            table.insert(f, '"names":[' .. table.concat(names, ",") .. ']')
          end
        elseif kind == "token" then
          if public and not down and not inLibrary(p) then table.insert(f, '"name":' .. str(name)) end
        elseif kind == "ptoken" then
          local back = "mythology"
          if string.sub(value, 1, 9) == "offering-" then back = "offering" end
          table.insert(f, '"back":' .. str(back))
          if public and not down then table.insert(f, '"name":' .. str(value)) end
        elseif kind == "bag" then
          table.insert(f, '"count":' .. #(o.getObjects() or {}))
        elseif value then
          table.insert(f, '"value":' .. value)
        end
        table.insert(pieces, "{" .. table.concat(f, ",") .. "}")
      end
    end
    table.sort(pieces)

    local players = {}
    for _, pl in ipairs(Player.getPlayers()) do
      table.insert(players, '{"color":' .. str(pl.color) .. ',"host":' .. tostring(pl.host == true) .. '}')
    end
    table.sort(players)

    return '{"v":1,"age":' .. str(age) .. ',"players":[' .. table.concat(players, ",") ..
        '],"pieces":[' .. table.concat(pieces, ",") .. ']}'
  end

  local ok, result = pcall(main)
  if ok then
    sendExternalMessage({ tts7wd = "snapshot", request = REQUEST, snapshot = result })
  else
    sendExternalMessage({ tts7wd = "error", request = REQUEST, error = tostring(result) })
  end
end
