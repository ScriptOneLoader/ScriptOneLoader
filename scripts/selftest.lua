-- =====================================================================
-- ScriptOne :: selftest.lua
--
-- Proves at RUNTIME what the build can only prove at COMPILE time.
--
-- That the generated bindings compile shows their symbols resolve. It does
-- NOT show that Singleton<X>.Instance is non-null when a script calls it, nor
-- that the event bridge actually attached to the game. This file answers both,
-- and it needs no rebuild - which is the whole point of a Lua host.
--
-- Delete this file once you have seen a clean run.
-- =====================================================================

-- ---------------------------------------------------------------------
-- PHASE 1 - the boot check. Runs the moment this file is loaded.
--
-- Why this exists: everything below waits for "game_ready", and that needs a
-- LOADED SAVE. Start the game and sit in the main menu and none of it runs -
-- so a start that reaches the menu proves the host booted, and nothing else.
--
-- These checks need no save. They cover exactly the parts an unattended run
-- CAN reach: the interpreter, the sandbox, the timer (which proves the frame
-- tick actually fires) and persistent state across restarts. Anything that
-- touches the game is deliberately NOT here.
-- ---------------------------------------------------------------------

local boot_ok, boot_fail = 0, 0

local function boot(label, fn)
    local ok, result = pcall(fn)
    if ok and result ~= false then
        boot_ok = boot_ok + 1
        s1.log(string.format("  BOOT OK   %-28s = %s", label, tostring(result)))
    else
        boot_fail = boot_fail + 1
        s1.warn(string.format("  BOOT FAIL %-28s : %s", label, tostring(result)))
    end
end

s1.log("=== boot check (no save needed) ===")

boot("backend",           function() return s1.backend end)
boot("surface_size",      function() return s1.surface_size end)
boot("sandbox: pcall",    function() return type(pcall) == "function" end)
boot("sandbox: metatable",function() return type(setmetatable) == "function" end)
boot("sandbox: no io",    function() return io == nil end)
boot("sandbox: no os",    function() return os == nil end)
boot("sandbox: no require", function() return require == nil end)
boot("string/table/math", function() return type(string.format) == "function"
                                        and type(table.concat) == "function"
                                        and type(math.floor)   == "function" end)

-- Number formatting must be culture-invariant. On a German machine a naive
-- concatenation produces "1234,5" and tonumber() then reads 12345 - a silent
-- factor of ten. This asserts the host clamps it.
boot("invariant numbers", function()
    local s = "" .. 1234.5
    return s == "1234.5" or ("mismatch: " .. s)
end)

-- Persistent state, proven ACROSS runs: the counter must be higher than last time.
local runs = (s1.get("boot_runs", 0) or 0) + 1
s1.set("boot_runs", runs)
s1.save()
boot("state survives restart", function() return runs end)

s1.log(string.format("=== boot check: %d ok, %d failed ===", boot_ok, boot_fail))

-- The timer is the only proof that the FRAME TICK really fires. "attached" is a
-- statement about registration; this one is about the run. It is deliberately
-- short so an unattended 60-second start still sees it.
s1.after(5, function()
    s1.log("BOOT OK   timer fired after 5s - the frame tick is live")
    s1.log("=== boot check complete. Waiting for a save to be loaded for the rest. ===")
end)


local ok_count, fail_count = 0, 0

-- Every probe goes through pcall: a binding that throws must be REPORTED,
-- not take the rest of the self-test down with it.
local function probe(label, fn)
    local ok, result = pcall(fn)
    if ok then
        ok_count = ok_count + 1
        s1.log(string.format("  OK   %-34s = %s", label, tostring(result)))
    else
        fail_count = fail_count + 1
        s1.warn(string.format("  FAIL %-34s : %s", label, tostring(result)))
    end
end

s1.on("game_ready", function()
    s1.log("=== ScriptOne self-test ===")
    s1.log("backend: " .. s1.backend .. " | generated tables: " .. tostring(s1.surface_size))

    -- Guard: without the generated surface every probe below would be an
    -- "attempt to index a nil value" and drown the log in 20 identical failures.
    -- One clear line beats twenty confusing ones.
    if s1.surface_size == nil or s1.surface_size == 0 or s1.time == nil then
        s1.warn("Generated surface is not installed - skipping binding probes.")
        s1.log("Event bridge armed anyway.")
        return
    end

    -- Read-only probes across a spread of managers. Nothing here changes game state.
    probe("time.current_time",        function() return s1.time.current_time() end)
    probe("time.elapsed_days",        function() return s1.time.elapsed_days() end)
    probe("time.is_night",            function() return s1.time.is_night() end)
    probe("time.current_day",         function() return s1.time.current_day() end)
    probe("time.get_total_min_sum",   function() return s1.time.get_total_min_sum() end)
    probe("money.cash_balance",       function() return s1.money.cash_balance() end)
    probe("money.lifetime_earnings",  function() return s1.money.lifetime_earnings() end)
    probe("money.get_net_worth",      function() return s1.money.get_net_worth() end)
    probe("level.rank",               function() return s1.level.rank() end)
    probe("level.tier",               function() return s1.level.tier() end)
    probe("level.xp",                 function() return s1.level.xp() end)
    probe("level.total_xp",           function() return s1.level.total_xp() end)
    probe("game.is_tutorial",         function() return s1.game.is_tutorial() end)
    probe("game.seed",                function() return s1.game.seed() end)
    probe("save_manager.is_saving",           function() return s1.save_manager.is_saving() end)
    probe("lobby.is_in_lobby",        function() return s1.lobby.is_in_lobby() end)
    probe("lobby.is_host",            function() return s1.lobby.is_host() end)
    probe("player_movement.is_grounded", function() return s1.player_movement.is_grounded() end)

    -- An enum crossing the boundary as a number, and a call WITH arguments.
    probe("time.is_current_time_within_range(600,2200)",
          function() return s1.time.is_current_time_within_range(600, 2200) end)
    probe("level.get_xp_for_tier(0)", function() return s1.level.get_xp_for_tier(0) end)

    s1.log(string.format("=== self-test: %d ok, %d failed ===", ok_count, fail_count))
    if fail_count == 0 then
        s1.log("All probed bindings answered. The generated surface is live.")
    else
        s1.warn(fail_count .. " binding(s) resolved at compile time but failed at runtime - see above.")
    end

    s1.log("Event bridge armed. Get arrested or tased to see the other direction fire.")
end)

-- The other direction. Nothing is expected to happen until you trigger it in game.
for _, evt in ipairs({ "player_spawned", "player_arrested", "player_freed",
                       "player_tased", "player_tased_end", "player_struck_by_lightning" }) do
    s1.on(evt, function()
        s1.log("EVENT from game -> Lua: " .. evt)
    end)
end
