-- ScriptOne example: timers, persistent state, and the game's own psychedelic shader.
--
-- Background: eating a shroom triggers a visual effect that belongs to the GAME, not to
-- any mod - ScheduleOne.FX.PostProcessingManager drives a PsychedelicFullScreenFeature
-- that ships with the game. Since that manager is a public singleton, it is part of the
-- generated surface, so Lua can drive it too.
--
-- This file also shows the two things the interpreter learned last: s1.after/s1.every
-- (timers) and s1.get/s1.set/s1.save (state that survives a restart).

local TRIP_SECONDS = 8

s1.on("game_ready", function()
    -- State survives restarts. Numbers are stored and read back culture-invariantly,
    -- so a file written on a German machine reads correctly on an English one.
    local trips = s1.get("trips", 0)
    s1.log("Trips taken so far: " .. trips)

    -- s1.every returns an id you can hand to s1.cancel.
    -- Timers run on a Stopwatch, NOT on game time - they keep correct time while the
    -- game is paused (the pause menu sets timeScale to 0).
    local ticks = 0
    local id
    id = s1.every(2, function()
        ticks = ticks + 1
        if ticks >= 3 then
            s1.log("Heartbeat done, cancelling timer " .. id)
            s1.cancel(id)
        end
    end)
end)

-- Getting tased is a bad trip. Turn the game's own psychedelic pass on, then off again.
s1.on("player_tased", function()
    local ok = pcall(function() s1.post_processing.set_psychedelic_effect_active(true) end)
    if not ok then
        s1.warn("psychedelic effect not available right now")
        return
    end

    local trips = s1.get("trips", 0) + 1
    s1.set("trips", trips)
    s1.save()
    s1.log("Bad trip #" .. trips .. " for " .. TRIP_SECONDS .. "s")

    s1.after(TRIP_SECONDS, function()
        pcall(function() s1.post_processing.set_psychedelic_effect_active(false) end)
        s1.log("Back to normal.")
    end)
end)
