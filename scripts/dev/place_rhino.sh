#!/bin/sh
# Park Rhino's window in the bottom-left quarter of the largest screen.
#
# Rhino opens wherever the mouse pointer is, so across a deploy cycle the window lands in a
# different place - and on a different display - each time, and screenshots are not
# comparable. This makes the position a constant.
#
# The largest screen rather than the primary one: the primary is the laptop's built-in
# display and the work is on the external. Pass a screen index to override, or "current" to
# use whichever screen the window is already on.
#
#     scripts/dev/place_rhino.sh            # largest
#     scripts/dev/place_rhino.sh current
#     scripts/dev/place_rhino.sh 0
#
# Written in JavaScript for Automation because AppleScript cannot enumerate displays.
# `bounds of window of desktop` is the main screen only, which put the window on the wrong
# monitor. NSScreen can list them, but measures from the bottom-left of the primary screen
# while System Events measures from its top-left, so the two disagree by a flip.
osascript -l JavaScript - "${1:-largest}" <<'JXA'
ObjC.import('AppKit');

function run(argv) {
const wanted = argv[0] || 'largest';

const events = Application('System Events');
const rhino = events.processes.byName('Rhinoceros');
if (!rhino.exists() || rhino.windows.length === 0) {
    return 'no Rhino window';
} else {
    const window = rhino.windows[0];
    const [x, y] = window.position();
    const [w, h] = window.size();
    const centre = {x: x + w / 2, y: y + h / 2};

    // NSScreen's origin is the bottom-left of the primary screen and y grows upward;
    // System Events' is its top-left and y grows downward. screens[0] is always primary.
    const screens = $.NSScreen.screens;
    const primaryHeight = screens.objectAtIndex(0).frame.size.height;

    const boxes = [];
    for (let i = 0; i < screens.count; i++) {
        const f = screens.objectAtIndex(i).frame;
        boxes.push({
            left: f.origin.x,
            top: primaryHeight - (f.origin.y + f.size.height),
            width: f.size.width,
            height: f.size.height,
        });
    }

    let target = null;
    if (wanted === 'current') {
        target = boxes.find(b => centre.x >= b.left && centre.x < b.left + b.width &&
                                 centre.y >= b.top && centre.y < b.top + b.height) || null;
    } else if (wanted === 'largest') {
        target = boxes.reduce((a, b) => (b.width * b.height > a.width * a.height ? b : a));
    } else {
        target = boxes[parseInt(wanted, 10)] || null;
    }

    // A window dragged off every screen still has to go somewhere; the primary is as good
    // a fallback as any and at least it is visible.
    if (target === null) {
        target = boxes[0];
    }

    window.size = [target.width / 2, target.height / 2];
    window.position = [target.left, target.top + target.height / 2];
    return `placed on screen at ${target.left},${target.top} ` +
        `${target.width}x${target.height}`;
}
}
JXA
