-- ScriptOne: the one script that ships, so you can see it works.
-- Delete it once you have your own - nothing else depends on it.

s1.on("game_ready", function()
    s1.log("hello from Lua - " .. s1.surface_size .. " things I can call")
end)
