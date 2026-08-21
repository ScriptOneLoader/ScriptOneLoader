-- ScriptOne example: set the player's move speed through the game's own console.
--
-- No S1API, no C# patch. This file is the whole mod; the DLL is only the interpreter.
-- To try it: change the number, reload your save. No rebuild, no game restart.

local SPEED = 7

s1.on("game_ready", function()
    -- SPEED is deliberately a whole number. The console parses its argument with
    -- float.TryParse in the runtime's culture, so a decimal point is the least safe
    -- thing you can send. Whole numbers have no separator and no ambiguity.
    s1.console("setmovespeed " .. SPEED)

    -- Do not trust the call, measure the effect: the console only executes commands
    -- for the lobby host and stays completely silent for everyone else.
    local actual = s1.move_speed()
    if actual == SPEED then
        s1.log("Move speed multiplier is now " .. actual .. " (backend " .. s1.backend .. ")")
    else
        s1.warn("Submitted 'setmovespeed " .. SPEED .. "' but the multiplier reads " .. actual
                .. " - console commands only run for the lobby host.")
    end
end)
