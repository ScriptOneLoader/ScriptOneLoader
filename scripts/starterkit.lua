-- =====================================================================
-- ScriptOne :: starterkit.lua
--
-- How much fits into ONE Lua file without touching the host DLL?
-- This is the answer. It uses only s1.console(), s1.log(), s1.warn() and
-- s1.on() - the surface ScriptOne already has.
--
-- The lever is not the surface, it is the GAME's console: 63 registered commands,
-- all public, all reachable through Console.SubmitCommand(string).
--
-- The second lever is 'bind'. The game polls bound keys itself in
-- Console.Update(), so Lua installs the key bindings once and the
-- interactivity afterwards costs neither a frame loop nor a host change.
--
-- ⚠ THIS SCRIPT NEEDS  console_policy = unrestricted  IN ScriptOne-Starter.cfg.
--   Under the default 'safe' policy most of it is refused, and that is on purpose:
--   'bind' hands an arbitrary command to the game to run on a key press, which
--   defeats any allow-list. Nothing breaks if you leave the default - the script
--   reports what was refused and installs the rest. Read what it binds below and
--   decide for yourself before you loosen the policy.
--
-- WATCH OUT: the game's own ExampleUsage reads   bind t 'settime 1200'
--   The quotes are WRONG. BindCommand.Execute reassembles the command with
--   string.Join(" ", args).Substring(...) and does NOT strip them, so the
--   bound string would be  'settime 1200'  including the apostrophes, and on
--   trigger the console finds no command named "'settime". Bind WITHOUT
--   quotes. (Derived from Console.cs:1153-1169.)
-- =====================================================================

local VERSION = "1.0"

-- Every console command goes through this one place, so there is exactly one
-- spot for logging and error handling and the rest stays plain data tables.
local sent = 0
local function run(cmd)
    -- WATCH OUT: pcall only tells you the call did not THROW. s1.console returns
    -- false when the console policy refuses the command, and this script used to
    -- ignore that - it then reported "12/12 installed" while all twelve had been
    -- refused. Check the return value, not just the absence of an error.
    local ok, result = pcall(function() return s1.console(cmd) end)
    if not ok then
        s1.warn("command failed: " .. cmd .. " (" .. tostring(result) .. ")")
        return false
    end
    if result then
        sent = sent + 1
        return true
    end
    return false            -- refused; s1.console already logged the reason
end

local function runAll(list)
    local n = 0
    for _, cmd in ipairs(list) do
        if run(cmd) then n = n + 1 end
    end
    return n
end

-- ---------------------------------------------------------------------
-- 1. Startup profile - what should hold once a save is loaded.
--    A table, not a command sequence: changing it means changing one line.
--
--    ALL VALUES ARE WHOLE NUMBERS, and that is a safeguard, not a style
--    choice. The host wraps script execution in InvariantCulture (otherwise
--    Lua would turn 1.5 into the string "1,5" on a German machine), but the
--    other side is not covered by that: the game console reads its number
--    with float.TryParse WITHOUT an IFormatProvider (Console.cs:247), so in
--    the runtime's culture. On a de-DE runtime "1.5" would become 15 - the
--    dot counts as a thousands separator there. Two culture-dependent steps
--    in a row, and neither reports an error.
--    Whole numbers have no decimal separator, so the question never arises.
-- ---------------------------------------------------------------------
local profile = {
    move_speed   = 2,        -- setmovespeed
    jump_force   = 2,        -- setjumpforce
    stamina      = 500,      -- setstaminareserve
    day_duration = 24,       -- setdayduration (minutes per in-game day)
    show_fps     = true,
}

-- ---------------------------------------------------------------------
-- 2. Key bindings - declarative. The game evaluates them itself afterwards.
--    Keys are Unity KeyCode names (Enum.TryParse, case-insensitive).
--    F-keys were chosen because they are free during normal play; if one
--    collides with another mod, change the line here.
-- ---------------------------------------------------------------------
local keys = {
    { "f5",  "save",                 "Quick save" },
    { "f6",  "settime 700",          "Morning" },
    { "f7",  "settime 2000",         "Evening" },
    { "f8",  "setweather clear",     "Weather: clear" },
    { "f9",  "setweather heavyrain", "Weather: heavy rain" },
    { "f10", "clearwanted",          "Clear wanted level" },
    { "f11", "sethealth 100",        "Full heal" },
    { "f12", "freecam",              "Free camera" },
    { "keypad1", "setmovespeed 1",   "Speed: normal" },
    { "keypad2", "setmovespeed 4",   "Speed: fast" },
    { "keypad3", "setmovespeed 8",   "Speed: very fast" },
    { "keypad0", "cleartrash",       "Clear litter" },
}

-- ---------------------------------------------------------------------
-- 3. Scenes - named command sequences. This is what Lua is actually good
--    for here: bundling several console commands into ONE intent.
-- ---------------------------------------------------------------------
local scenes = {
    thunderstorm = {
        "setweather heavyrain",
        "settime 2300",
        "triggerlightning",
        "triggerdistantthunder",
    },
    fresh_start = {
        "setmovespeed 1",
        "setjumpforce 1",
        "sethealth 100",
        "setstaminareserve 200",
        "clearwanted",
        "setweather clear",
    },
}

local function scene(name)
    local list = scenes[name]
    if not list then
        s1.warn("unknown scene '" .. tostring(name) .. "'")
        return 0
    end
    local n = runAll(list)
    s1.log("Scene '" .. name .. "': " .. n .. "/" .. #list .. " commands sent.")
    return n
end

-- ---------------------------------------------------------------------
-- 4. Runs once per loaded save.
-- ---------------------------------------------------------------------
s1.on("game_ready", function()
    s1.log("Starter Kit v" .. VERSION .. " on " .. s1.backend)

    run("setmovespeed "      .. profile.move_speed)
    run("setjumpforce "      .. profile.jump_force)
    run("setstaminareserve " .. profile.stamina)
    run("setdayduration "    .. profile.day_duration)
    run(profile.show_fps and "showfps" or "hidefps")

    -- Measure the effect instead of trusting the call: the console only
    -- executes for the lobby host and is completely silent otherwise.
    local actual = s1.move_speed()
    if actual ~= profile.move_speed then
        s1.warn("Console commands are being ignored (move speed is " .. actual
                .. ", expected " .. profile.move_speed
                .. ") - you are probably a multiplayer guest. Skipping key bindings.")
        return
    end

    -- Install the key bindings. From here on the game takes over.
    local bound = 0
    for _, k in ipairs(keys) do
        if run("bind " .. k[1] .. " " .. k[2]) then
            bound = bound + 1
        end
    end

    s1.log("Key bindings installed: " .. bound .. "/" .. #keys)
    for _, k in ipairs(keys) do
        s1.log(string.format("   %-8s %-22s %s", k[1], k[2], k[3]))
    end

    s1.log("Available scenes: thunderstorm, fresh_start (edit this file to trigger them)")
    s1.log("Starter Kit ready - " .. sent .. " console commands sent in total.")
end)
