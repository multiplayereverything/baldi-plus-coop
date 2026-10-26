# LAN discovery, player colours and spectator HUD, 0.6.0

Protocol 11. Everyone must update to 0.6.0.

## LAN discovery

Choose **MULTIPLAYER → FIND LAN GAMES**. Open rooms on your network appear within a second or two. Select one to join it.

- **Rooms with a code:** the Join form opens on the code field, because codes are never broadcast.
- **Greyed-out rooms:** these are rooms that are already in a run, are full (4/4), or need a different mod version.
- **Rooms that don't appear:** **JOIN GAME** with the host's address still works.

How it works:
- While a host's room is open and no run has started, the host sends a small UDP beacon on port **27016** once a second. It goes to the network broadcast address, to each adapter's subnet broadcast address, and to the local machine.
- The beacon contains the protocol, the mod version, a random room id, the host's name, the TCP port, the player count, whether a code is required, and whether a run has started.
- The game only listens for beacons while the FIND LAN GAMES page is open.
- Several game copies on one PC can browse at the same time.

Limits:
- Discovery covers the local network only. Routers usually do not forward broadcasts between networks.
- Windows Firewall must allow the game on private networks.
- In the lab, beacons arrived over VPN adapters as well as the LAN.

## Player colours

Choose **PLAYER OPTIONS → COLOR** to cycle through eight colours: green, red, orange, yellow, blue, purple, pink and white. A preview shows the character in the chosen colour.

- Teammates see your sprite in your chosen colour.
- The choice is saved in `UserData/BaldiTogether.ini` as `Colour=`.
- The other Player Options entries remain Coming Soon.

How it works:
- Only the supplied sheet's teal clothing pixels are recoloured. Hue and saturation change, while brightness and shading are kept.
- Skin, hair and eyes keep their original pixels.
- Each colour's sheet is built once, on first use.
- Colour travels in the join and host-migration identity and in the room roster, so it survives host migration.

## Spectator HUD

While you spectate a teammate, your HUD shows their inventory, selected item and stamina meter, and the spectator banner says so.

- **Updates:** each living player shares their inventory and stamina four times a second. The host relays it to the other players.
- **Hidden HUD:** the HUD is hidden when the view is concealed (the teammate is in a locker or no teammate is available) and when the data is more than 3 seconds old.
- **After spectating:** the next floor or attempt restores your own inventory and stamina.
- **Notebook counter:** it already shows the team total.
- **Display only:** the spectator cannot use the teammate's items.

## Verification

- **Build and transport:** both loaders build, and all 22 transport checks pass.
- **Two-process lab** (BepInEx host coloured red, MelonLoader client coloured blue):
  - **Beacon:** an external UDP listener received the host's beacon over loopback, the LAN and a VPN adapter, with the correct fields. The beacon stopped when the run started.
  - **Colours:** each process reported the other player's colour, and its avatar rendering that colour's sheet.
  - **HUD share:** each process received fresh 5-slot HUD data from the other.
  - **Logs:** no exceptions in either game log.
- **Recolour output:** checked visually on the sheet. In-game screenshots did not show the other avatar, so the in-world colour was not seen directly.
- **Not runtime-tested:** the FIND LAN GAMES page, the colour picker page, and the spectator HUD itself (no capture happened in this lab run).
