#!/bin/sh
# Park Rhino's window in the bottom-left quarter of the main screen.
#
# Rhino opens wherever the mouse pointer happens to be, so across a deploy cycle the window
# lands on a different screen each time and screenshots are not comparable with one another.
# This makes the position a constant instead: same corner, same size, every launch.
#
# Multi-monitor note: `bounds of window of desktop` is the main screen's, so this always
# targets that one regardless of where the pointer was.
osascript <<'APPLESCRIPT'
tell application "System Events"
    if not (exists process "Rhinoceros") then return
    tell application "Finder" to set screenBounds to bounds of window of desktop
    set screenWidth to item 3 of screenBounds
    set screenHeight to item 4 of screenBounds
    tell process "Rhinoceros"
        if (count of windows) is 0 then return
        tell window 1
            set size to {screenWidth / 2, screenHeight / 2}
            set position to {0, screenHeight / 2}
        end tell
    end tell
end tell
APPLESCRIPT
