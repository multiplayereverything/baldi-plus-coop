# Improvement ideas and decisions

## Implemented in 0.2.0

| Idea | Why it matters | Scope |
| --- | --- | --- |
| Shared free inventory pickups | Prevent two players taking the same world supply | Host claims, one delivery, acknowledgement and full-inventory refusal. Shops, repeatable rewards, swaps and dynamic drops remain separate. |
| Team location pings | Coordinate without a voice service | G marks the aimed location for eight seconds, one marker per player, rate limited by the host. |
| Automatic level entry | Host controls when play starts | Updated in 0.3.1: host starts after local loading; clients enter after local validation, without a ready action. |
| Connection latency | Distinguish a slow connection from a game logic issue | Round-trip measurements in the lobby and client HUD. |
| Explicit join errors | Make wrong codes, versions and late joins understandable | A final reason packet is sent before the connection closes. |
| Block item use with the lobby open | Avoid activating an item while interacting with the panel | ItemManager.UseItem is gated with movement and clicking. |

## Next candidates

| Priority | Candidate | Work needed |
| --- | --- | --- |
| High | Remaining NPC abilities | 0.4.0 adds remote Playtime, Bully, Sweep/1st Prize and Principal behavior plus generic poses. Beans gum, Crafters attacks, Reflex minigames and general audio remain. |
| High | Item swaps and effects | Johnny stock/wallet/purchases are implemented; inventory swaps, dynamic drops, client YTP effects and item-specific events remain. |
| High | MathMachine and other activity state | Replicate puzzle inputs/results and test every activity type, including competing answers and resets. |
| Medium | Player-to-player item transfers | Explicit recipient confirmation and transaction ownership; build on the supply claim rules. |
| Medium | Spectating or revives | Agree on whether Baldi catches end the team run or eliminate one player. Shared catches remain the current rule. |
| Medium | Rejoin mid-floor | Full state restoration, inventory ownership, generation history and stable player identities. |
| Medium | Off-screen ping arrows | Show direction while keeping the normal game HUD readable. |
| Later | LAN discovery and Internet relay | Discovery can help local setup; Internet play needs connection infrastructure and a separate privacy/security design. |
| Later | World deltas and bandwidth display | Measure large-floor traffic before replacing the current complete snapshots. |

## Research notes

The improvement choices primarily come from this mod's known gaps and local game API inspection. REA inspected Pickup and ItemManager in the installed 0.14.5 assembly; the evidence identity is recorded in MODLOG.md.

Web research also found an existing [Baldi's Basics Multiplayer project](https://gamebanana.com/mods/668785), which provides a comparison point for private-lobby setup. Its page is for Classic and uses JavaScript for its detailed body, so it did not establish Plus API compatibility or supply implementation code. No files from another mod were downloaded or copied.

The [Steam community discussion about multiplayer](https://steamcommunity.com/app/1275890/discussions/0/680733951027930796/) is player feedback, not a technical specification. It reinforced keeping this work focused on co-op usability and clear limitations. Proposed features above are our design decisions, not claims that other projects implement them.

### Prior work check, 2026-10-26 (coop-research)

No other co-op or online mod for Baldi's Basics Plus turned up. `searched:`
- the Co-op Anything directory (`search_directory "Baldi"`): no entries;
- web: "Baldi's Basics Plus multiplayer mod co-op BepInEx", "\"Baldi's Basics Plus\" multiplayer mod github gamebanana 2025", "github \"Baldi's Basics Plus\" multiplayer OR co-op OR online mod": only single-player mods and mod menus on itch.io and GameBanana; the BB+ modding scene builds on BepInEx 5 and the MTM101 modding API;
- the [Steam discussion](https://steamcommunity.com/app/1275890/discussions/0/2963893984920858040) about co-op is player talk, not a project;
- the [Classic multiplayer project on GameBanana](https://gamebanana.com/mods/668785) found in 0.2.0 is for Classic, not Plus.
- Not searched yet: the BB+ modding Discord (needs the owner's account) and non-English sites.

Game version: the developer's April 2026 status update names 0.14 and 0.14.1; this mod is built against the installed 0.14.5 (Assembly-CSharp SHA-256 in handoff.md). A newer game build changes that hash, and the mod then refuses to connect players whose builds differ.
