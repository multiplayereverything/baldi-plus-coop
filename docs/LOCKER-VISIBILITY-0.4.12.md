# Locker visibility — 0.4.12

Native HideableLocker.Clicked freezes the player's entity and moves the camera to the locker view. It does not move the player's transform into the locker. Sending only that transform left the remote sprite standing in the corridor.

The mod records native locker entry, reads its playerInside state and includes that flag in each 20 Hz pose. The host accepts it only for the current generation and ready sender, then relays it. Both receivers suppress the remote avatar while hidden; name tags already require an active avatar. The next outside pose restores the sprite. ResetWorld clears the local locker reference and peer flags. This fixes presentation; NPC detection and locker occupancy rules are unchanged. Protocol 8 requires every player to update.

The focused runtime fixture exercises a real generated locker through its native Clicked and ForceOpen methods. Serialized poses pass through actual host/client receive handlers; a synthetic remote peer checks visible/hidden/restored avatar state and reset cleanup. This is not a full two-machine visual session. The transport suite covers socket framing and lifecycle separately.
