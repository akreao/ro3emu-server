# Ordinary dungeon implementation

Status: partial implementation; not a complete 1:1 encounter implementation.

Evidence: the referenced client Lua and protobufs,
`DungeonEntranceConfig`, `TenGameplayConfig`, `TenGameplayBossConfig`, `TenGameplayRefreshTimeConfig`,
`DungeonConfig`, scene editors, `NpcLevelPointRespawnConfig`, `NpcLevelRegionRespawnConfig`,
`NpcRespawnGroupConfig`, `MultiverseGroupConfig`, `AwardConfig`, and `DropPoolConfig`.
The `data/` and `re/` exports are read only. Season selection uses `SeasonService.PlayBook` and `Start`.

Implemented:

- 5062 `DBMultiDungeonInfo` before 5056, menu unlocks, one-based boss marks, personal progress and reward counters.
  The menu is client-configured; no invented menu request. Condition 3031 now uses elapsed server days from `Start`.
- 30400 strict scene/boss selection, configured player limits and eligibility; 42033 readiness and 30406 responses
  for a preformed team. Timeout, decline, changed roster, offline/ineligible members and changed season cancel entry.
  Scene 1022001's condition 1000000 now checks 1101 (`CheckRoleHaveTask`) against a running task 10102200,
  following the client condition evaluator; a delivered or absent task does not open it.
- Shared `InstanceService` for TowerService and DungeonService: persistent room identities, 70010/30005 transfers,
  saved outside positions, tracked non-respawning monsters and cleanup after the last participant leaves.
- 30401/30404 state/events, 30441 roster, 30106 surrender/reset, 30124 reset notification, 30112 used revivals,
  30403 result and 82584 personal award notification. Forged client clears and grants cannot settle a room.
- OnClickRevive option `[[0]]` permits an immediate normal 31013 request; option `[[2,5]]` uses a five-second
  auto-revive deadline in 30112 and refuses early requests. The client defines option 0 as `Immediately`, sends
  the normal-button request, and sends option 2's request when its countdown expires
  (`Lua_GameDefine.lua:8936-8952`; `LC_SceneCom_Revive.lua:597-709,748-758,1165-1182,1213-1224`). Of 65
  ordinary scenes, 33 have `[[0]]`, 22 have `[[2,5]]`, one has unrecognized `[[4]]`, and nine are NoRevive.
  These two recognized options prevent an immediate all-dead loss; the room's existing timeout still applies.
- Actual combat counters through 30110/30114/30116/30117; participant-owned persisted history and 30128 metadata.
- 82590 job-position preference and 82592 guide state; 30402 exit, disconnect and role-switch cleanup.
- Configured personal fixed items plus the existing drop-pool roll, preserving item binding and generated attributes.
  Currency, items, counters and history commit together for each role; a failed transaction does not consume rewards.
  All participants settle independently before result sends, so one failed connection cannot prevent another payout.
- Daily/weekly **reward allowances**, not hard entry limits: the client supports joining to help without a reward.
  Normal/Hard raw counters remain separate; the client may count Hard extras toward Normal eligibility.
- Matchmaking: the MatchConfig type-2 five-person queue (82681, ready panel 82636/82637, `DungeonMatchService`) hands
  the matched members to `DungeonService.BeginMatchedAsync`, which admits them and starts the run like a 30400 entry
  without a second 42033 vote.
- Auction lots: each participant's committed clear joins the run's auction journal (`AuctionService.RecordDungeonClear`,
  tiers from the boss's `_iGuildAward`); once every participant is settled the configured group is rolled once and its
  lots published ([auction.md](auction.md#authoritative-reward-publication), `AuctionTests.Dungeon`).

Realm of the Gods extras (`DungeonService.RealmOfGods`, TenGameplayConfig 100101-100103):

- 82583 hall of fame: the realm's first team clear of each boss (`DBMultiDungeonHallOfFame`: leader, time, every
  participant's `RoleDetailInfo` at the clear), kept in `server_state` `multi_dungeon_server`. The client asks for it on
  every boss the group widget shows and says "No Team First Clear Yet" (UIWords 56287) without one. Later clears never
  replace it.
- Sacred Blessings: bosses with `_kBlessingID` unlock when the condition `_kBlessingBuffUnlock` ([3034, boss, days],
  "Unlocks on day N after the Current Server's First Clear") is met by that first clear. The level is the highest
  `TenGameplayBuffConfig` row of the group whose `_iUnlockDay` has passed since the unlock (inferred: counted from the
  unlock, not the clear). 5062 carries it in `sacredBlessings`; its buffs go on the run's members and `_iAddReviveNum`
  adds to the revive count. 82588 acknowledges the tip (isNeedTip until the next level).
- Final Trial honors (`_kFinalTrialID`): a won run finishes the goals whose `LC_ConditionList` holds: 7005 revives left
  (the team pool, inferred), 7004 seconds left before enrage (`Ro3Emu:RaidEnrageSeconds`, default 600, inferred),
  7006 another goal done. 82584 lists the goals the run finished, 5062 their states, 82589 pays `_iPersonAward` once per
  goal (event receipt `event/dungeon/honor-award`).
- 30125 Start Battle: the raid scenes 3020101-3020104/3020108 open in a preparation phase with dungeon var 1001 = [1]
  (DungeonLua `FunctionButtons`); the leader's button or `Ro3Emu:RaidPreparationSeconds` (default 120, inferred) starts
  the boss fight.
- 82591 Hidden Realm (50199): the entry draws a boss of the `_iRandom` 1 rows that every member has unlocked (inferred:
  uniform), sends `RandomDungeonNotify` with the countdown `FiveGamePlay_RandomDuration`, then runs that boss with the
  Hidden Realm as `MultiDungeonInitData.origin`. The clear counts as the Hidden Realm's (its awards, allowance and
  record, inferred).
- Test aids: `!dungeonsolo on` also admits a lone 30400 entry to a team boss; `!firstclear <boss> [daysAgo] | clear`
  sets the realm's first clear of a boss.

Raid boss trials (`DungeonService.RaidTrials`, `Reconstructed/raid-trials.json`), **inferred as a whole**: the raid
bosses ran server AI scripts (NPCConfig `_iAISpecial`, "Raid_S01_Boss01_Test", "RaidFive_Boss01") that the client does
not ship. Their scripted mechanics are rebuilt from what it does ship: the raid's tips (ChatConfig), the encounter
guide (TenChallengeHandbookIDConfig), the mechanic's NPCs and buffs, and the scene editor's points. Every number and
placement has its reason in the json (`why`, `evidence`, `guessed`).

- A raid boss with a script gets a 0.5 s trial tick when the fight begins. Its trials take turns: one starts
  `start.firstMs` after the boss engages and then every `start.everyMs`, and once at each `start.hp` threshold (percent
  of the boss's HP; a big hit fires the passed thresholds once). No trial starts within 6 s of the previous one's end.
  A boss that leaves combat ends its trial without a verdict and restarts its clock.
- A trial sends its tips (1022; a CombatCountdownTip's `${1}` is sent as the window's seconds, which the client counts
  down, `UI_Sys_GVE_MarqueeWidgetCtrl`), may hold the boss and play a show skill for its window, deals out colours
  (note buffs, one per player, so every colour in play is someone's), marks random players (a trap or a target), and
  puts out its objects: scene props shown as their NPCConfig `_iNPCType`, or destructible monsters outside the room's
  counted actors (an object without its own AI, or marked `still`, stands still and never strikes back; `hits` per
  player destroy it at any gear; `match` lets only a player of its colour damage it). Pickups hand out a colour through
  their 32700 interaction.
- Rules judged when the window closes: per player `stand` (inside an object, of your colour), `avoid` (outside every
  object's circle or lane), `shelter` (behind an object as seen from the boss), `guard` (a marked player with a carrier
  beside), `spread` (a marked player with nobody beside); for the party `occupy` (every object at once), `collect`
  (walk into every object; each one taken can reward its taker), `kill` (every object, or the chosen one), `carry`
  (touch the object, then hit the boss with it), `lure` (the boss stands in an object's area when the window closes).
  Boss `breakers` are skills whose landing area destroys the objects in it; `casts` are skills the boss casts at a
  random player during the window; a cast with a `beam` (length, width) is only the skill's show aimed at that player,
  and the objects on its line from the boss are destroyed (no hit, the boss keeps fighting).
- A failure costs a share of each reached player's maximum HP, sent as the boss's hit of the named skill (inferred: it
  punishes the same at any gear, and no single failure wipes a full-HP party). A solved trial can reward (a buff, a
  line) and stagger the boss (its show interrupted, a stagger show, the boss held).
- Test aid: `!raidtrial [list | n | stop]` lists the boss's trials, starts trial n now (engaging the GM if the boss is
  idle) or ends the running one without a verdict.
- Coverage: every raid boss of TenGameplayBossConfig with an AI script has trials: Realm of the Gods 100101-100103
  (Isti, Nasha, Ilmo, Mirko) and the five-player raids 50101/50104 (Golden Thief Bug, Orc Hero, Drake), 50102/50105
  (Mistress, Maya, Osiris) and 50103/50106 (Pharaoh, Moonlight Flower, Baphomet), each boss at its `_kBoss` mark 1-3.
  Not covered: the Hidden Realm's 4300099 'Unknown Boss' (50199): no AI script, and its guide only says its skills are
  'the same as those of the General Boss', without naming which. A mechanic whose boss skill the generic rotation
  already casts with the client's own show stays there (Golden Thief Bug's Earthquake, Drake's Tidal Tornado 4321001/
  4321101 and Powder Keg Party 4320401, Orc Hero's Blade Deflection cast 4310801).
- Isti (Realm of the Gods 3020101; 14720026/14720010/14720027), **Ocean Concerto** (tip 4110011; guide 1100105): from
  60% HP and then every 70 s Isti holds still playing her 40 s channel 14724101 for a 25 s window. Every player gets a
  note (14720201 Red, 14720501 Yellow, 14720601 Blue, 14720701 Green; the HUD shows it), and two bubbles per player (4 to
  16, the NPCs 14720013-14720016 of the notes in play) stand 45% of the way in from the 16 ring points of groups 17 and
  34. A player who walks into a bubble of their note takes it and gains Bubble Movement 14720301 (+5% damage per stack);
  each bubble left at the end costs everyone 10% of their maximum HP (at most 50%) as Full-Arena Wave 14721101.
  Inferred: the phase threshold, the period, the window, standing bubbles instead of moving ones, the damage.
- Nasha (3020102; 14740100/14740000/14740200), **Dance test** (tips 4618 'It's time for your test!' and 4611 'Reach your
  performance position within ${1} seconds', 15 s; guide 1100201 Backup Dancers): 25 s into the fight and every 50 s
  Nasha dances in place (14744200) while one Circle-Stepping Magic Circle 14740002 per player stands at the circle
  creation points 601-623. A player inside a circle (2 m) at the end gets Perfect State 14742501 (+30% speed and damage)
  and line 4616; one outside takes 35% as Dazzling Dance Step 14740500 and line 4615. Inferred: the timing, the circle
  count, the damage; the four-direction step copying is not reproduced.
- Ilmo (3020103; 14761100/14761000/14761200), **Beat Prison** (tip 4606 'Enter the circles in order to compose the
  melody', 30 s; guide 1100301): at 80, 60, 40 and 20% HP (the four Lock Life gates 14761601-14761901) Ilmo holds,
  drumming the Drumbeat Cage 14769000-14769300. The note NPCs 14760001-14760004 stand at points 17-20; their own
  interactions (33-36) give the Red/Blue/Yellow/Green note 14760001/14760101/14760201/14760301. Coloured circles
  14760005-14760008 appear in the editor's region groups 34, 61, 71 and 48 (2, 3, 4 and 6 circles, DungeonLua
  `numbylevel`/`npcidbyregion`); a player carrying a circle's note who steps into it lights it. All lit within 30 s:
  Green Energy Barrier 14761401 and no damage; otherwise the cage costs everyone 50%. Inferred: the thresholds, the
  circle counts per gate, any order instead of the melody's order, 50% instead of the data's 100%.
- Mirko (Normal 3020108 14780007; Hard 3020104 14780005; Final Trial 3020104 14780008): **Safe zones** (tips
  4620/4621/4622 'Activate three/four/two Safe Zones simultaneously', 30 s; skill 14783601 Mechanic Starts): 40 s in
  and every 80 s (Final Trial 30 s and 60 s) Mirko holds for 30 s while the zone markers 14780006 stand at points
  118-120 (3020108, three) or the corners 25-28 (3020104, four), one per player at most; every zone needs a player in it
  (3 m) at the end, else everyone takes 50% as 14782101. **Musical instruments** (tip 4607; guide 1100403): 75 s in and
  every 80 s every player gets a gold or blue note (14788001/14788101, DungeonLua `buffid`) and one Golden Horn or Blue
  Harp per player (two to four; 14780014/15 Normal, 16/17 Hard, 18/19 Final Trial) stands at the edge points 101-104.
  Only a player of its colour damages it and 4 hits per player destroy it; each one left after 20 s costs everyone 20%
  (at most 60%). Inferred: all timing and damage; Compose and Perform Movement (DungeonLua `music`/`skill`, the recorded
  scores) are not reproduced.
- Orc Hero (five-player raid, Orc Village 3120101; 43100003/43100000): **Lost Giant Blade** (tips 4311 'Pick up the Giant
  Blade and interrupt Orc Hero's charging!', 15 s, and 4314403; guide 1050202): 30 s in and every 70 s he raises his
  shield (4311501) for 15 s while the dropped blade 43100002 lies at one of the ring points 18-25. Walking onto it gives
  the carrier 'Blade Mountain-Greatsword' 4310401 (10 s) and line 4314404; the carrier's next hit on Orc Hero breaks the
  shield (he staggers 3 s, 4311701; line 4314405). Otherwise the full-screen AOE 4311801 costs everyone 50%. The blade's
  derived skill 4313601 is not handed out (the client's derived-skill slot comes from the skill wheel sync): any hit
  counts. **Giant-Blade Kunai** (tips 4314405, 4314406; guide 1050203): 55 s in and every 70 s the shield 43100004 lies at
  his feet and two players are marked (4311601); picking the shield up gives Shield 4310601 (-50% speed); after 12 s a
  marked player without a shield carrier within 3 m (themselves included) takes 60% as Launch Blade 4312501. **Blade
  Deflection** (guide 1050201 'players must not approach one another'; skill 4310801 'if players are within 5 m of one
  another, the Kunai ricochet between them'): 80 s in and every 70 s three random players get the Weapon Deflection
  Mechanic Callout 4311601; after 6 s a marked player with anyone within 5 m, and those beside them, take 30% as
  4310901 (three of the data's 10% ricochets). Inferred: the count for both difficulties, timing, damage; the speech
  line 4314407 'Stay away from each other!!!' is not sent.
- Drake (five-player raid, Silent Ship 3120201; easy 43200006, normal 43200000): **Ghost ships** (tips 5002 'Ghost ships
  will arrive in ${1} seconds! Destroy them with explosive barrels!', 17 s, and 5350035; guide 1050301): 35 s in and every
  60 s two ghost ships (43200007 easy, 43200002 normal, shown as the bullet units NPCConfig types them) stand at random
  lane points (31-35 on the east edge facing west, 36-40 on the north edge facing south) and Drake throws Falling
  Explosive Barrels 4323101 at a random player every 5 s. A barrel (4323101/4322501) landing on a ship sinks it (a boss
  `breaker`); after 17 s each ship left charges its lane (60 x 8 m ahead of it): 60% to every player in it.
  **Puppets** (tip 5001; guide 1050303): 70 s in and every 60 s one Puppet 43200001 per player (two to four) fights at the
  mark points 18-27 and Drake throws Falling Cannonball 4320101 at a random player every 6 s; a cannonball
  (4320101/4321701) landing on a puppet destroys it, players need 12 hits per player; each puppet left after 30 s costs
  everyone 15% (at most 45%). Inferred: lanes, timing, hits and damage; the ships do not sail.
- Moonlight Flower (five-player raid 3120801; easy 4380000, normal 4380001): **Bell Cage** (tip 5005 'A teammate is
  trapped inside a Bell! Hurry and smash the Bell!'; skill 4380501 'Traps one player inside a Bell, then attacks the
  Bell'): 25 s in and every 60 s one random player is marked (4382301) and the Bell Cage 4380002 stands on them; 4 hits
  per player break it within 12 s, else the marked player takes 60% as 4380501. **Hidden Bell** (tip 5007; guide
  1050801; skill 4380601): 45 s in and every 60 s three Riddle Bells 4380004 stand at points 21-23 beside her, one shows
  Charged Bell 4380001 for 4 s; breaking that one (3 hits per player) within 15 s solves it, else everyone takes 40%.
  **Moonlight** (tip 5006; guide 1050802 Cat and Mouse): 65 s in and every 60 s a Spotlight Projectile 4380006 lands
  under every player; whoever is still within 3 m after 4 s takes 35% as Spotlight Game 4380701 and Blind 4381901 (3 s).
  **Charged bells** (tip 5008; guide 1050803 Nowhere to Hide): 85 s in and every 60 s the Four-Corner Bells 4380008
  stand at points 24-27; within 6 m of one after 6 s: 40% as 4380801. Inferred: all timing, hits and damage; the bells
  do not visibly shuffle (remember the glowing one), the spotlights do not wander, and the second (uncharged) blast of
  Nowhere to Hide is left out.
- Golden Thief Bug (five-player raid, sewers 3120401; easy 4300001, normal 4300000): **Sandstorm** (tips 4802 'A
  sandstorm is coming! Use the wormholes to avoid it!' and 4801; guide 1050101 Wormhole, 1050102 Sandstorm): 30 s in and
  every 60 s he burrows (4301801) for 10 s while two storms 4300004 come in from random edge points of the editor's
  'Sandstorm' group (21/23/24/25), each a 60 x 16 m lane across the field. The 'Wormhole 2' burrows 4300007 stand at the
  corner points 41, 38, 42, 45 just outside both lanes; their interaction takes a player to the next one. Still in a lane
  at the end: 40% as 4305201. **Burrow** (tips 4803 'Guide Golden Thief Bug toward a wormhole!' and 4809 'Golden Thief Bug
  will burrow again in ${1} seconds!', 15 s): 55 s in and every 60 s two burrows 4300002 stand at random points of the
  'Wormhole' group (27-36); the boss must be within 4 m of one when the count ends (he burrows there and resurfaces,
  4305901, 4 s), else everyone takes 40% as 4305301. Inferred: lanes and their width, timing, damage, the lure rule
  itself. Earthquake (guide 1050103; 4305601, easy 4307001/4307101) stays in his own rotation.
- Mistress (five-player raid, Mjolnir 3120301; easy 4330001, normal 4330000): **Hornet Formation** (tip 4314409 'A swarm
  of Bee Infantry is approaching'; guide 1050401): 25 s in and every 75 s four Marching Hornets 4330006 from the north
  edge points 22-27 and four from the west edge points 48-53, each a 60 x 5 m lane; in a lane after 8 s: 40% as Swarming
  Bees 4331301. **Hornet Advance** (tip 4314409; guide 1050402): 50 s in and every 75 s five of the six offset west rows
  61-66 march (60 x 10 m), one gap left; in a lane after 8 s: 30% as 4331601. **Hornet Charge** (tip 4314410 'Bee Cavalry
  will target players and charge'; guide 1050403): 70 s in and every 75 s two random players get a warning zone 4330004
  (6 m) under them; anyone in a zone after 5 s takes 45% as 4331501. **Angry** (tip 4314411 'Infantry and cavalry will
  appear together'): at 50% HP she holds (4332101) while four north lanes and the two zones come together; 50% to those
  caught after 8 s. Inferred: all lanes, widths, timing and damage; no knockback, no eight-way charge.
- Maya (five-player raid, Ant Hell 3120501; easy 4340005, normal 4340000): **Quicksand** (tip 5201 'Quicksand will strike
  in ${1} seconds! Hide behind a stone pillar', 10 s; guide 1050502): 35 s in and every 70 s Maya holds (New Ultimate
  4342601) while one stone pillar 4340006 per player (two to four) rises at the diagonal points 32-35, 14 m out; a player
  not in a pillar's shadow (2 m either side, 6 m deep, as seen from Maya) takes 50%. **Petrify Mist** (guide 1050503;
  4342301): 55 s in and every 70 s a mist circle 4340015 lands under every player; still within 4 m after 4 s: 30% and
  Petrify 4340301 (3 s). **Sandstorm meteors** (tips 5202 and 5203, 8 s): at 40% HP the same shelter with the pillars at
  the row points 36, 37, 40, 41 (4343401). Inferred: timing, shadow size, damage; the pull to the centre and Silk Spit's
  link (guide 1050501) are not reproduced: the link's pull is a server-side movement the client data does not describe.
- Osiris (five-player raid, Pyramid 3120601; easy 4360004, normal 4360000): **Dark Pulse** (tip 5003 'Dark Pulse will be
  unleashed in ${1} seconds! Use Dark Pulse to destroy the Horongs!', 20 s; guide 1050601/1050602): 25 s in and every 60 s
  one Horong 4360006 per player (two to four) stands at random points of 58-63/65-73; every 4 s Osiris aims a Dark Pulse
  (4361301, a beam) at a random player: the 40 x 4 m line from him through that player detonates the Horongs on it, so
  the players steer it by standing behind them. Each Horong left costs everyone 15% (at most 45%) as Horong Split
  4361401. **Astral Shift** (tip 5004 'Star Shift will strike in ${1} seconds! Keep moving to dodge it!', 5 s; guide
  1050603): 55 s in and every 60 s a star-shift circle 4360008 lands under every player; still within 3 m: 35% as
  4361701. Inferred: the beam's size, that it neither hurts nor bounces, timing and damage; the walls do not rotate.
- Pharaoh (five-player raid, Sphinx 3120701; easy 4370000, normal 4370001): **Coffin Trap** (tips 4370001 'Your teammate
  ${1} is trapped in a coffin!' with the player's name, and 4370004; guide 1050702): 25 s in and every 60 s a random
  player is wrapped in the Shroud 4370501 and Pharaoh's Coffin 4370002 stands on them; 4 hits per player break it within
  12 s, else the trapped player takes 60% as 4372101. **Divine Light Purification** (tips 4370002 'hide behind the
  Pyramid!' and 4314408; guide 1050703): 45 s in and every 60 s he holds charging (4371101) for 8 s while the pyramids
  4370003 stand at the four corner points 66-69 (each with an air wall in the editor); a player outside every pyramid's
  shadow (2 m either side, 6 m deep) takes 55%. **Marked** (tip 4370005 'Move Away from the marked player!'): 65 s in and
  every 60 s two random players get the AOE Marker 4374901; after 6 s a marked player with anyone within 6 m, and those
  beside them, take 40% as Grasp of the Dead 4370401. **Death Ray** (guide 1050701): 85 s in and every 60 s two pyramids
  4370004 (with Pyramid Laser 4375101) stand at random inner corner points 22-25 facing the centre, each firing a 45 x
  6 m lane; in a lane after 6 s: 50% as 4375101. Inferred: all timing, sizes and damage; the coffin does not sink and the
  ray does not sweep.
- Baphomet (five-player raid, Labyrinth 3120901; easy 4390000, normal 4390001): **Demonic Pillar** (tip 4314472 'Full-screen
  attack! Quick, get behind a stone pillar!'; guide 1050902): 30 s in and every 70 s he holds (4393301) for 8 s while the
  six stone pillars 4390004 stand at the hexagon 29-34, 20 m out (the editor's six air walls are their bodies); a player
  outside every pillar's shadow (2 m either side, 6 m deep) takes 50%. **Pillar blast** (tip 4314475 'The stone pillar is
  about to explode! Move Away!'): 44 s in and every 70 s the pillars stand again; within 5 m of one after 5 s: 30% as
  4392501. **Summon Evil** (tip 4314474 'Stop the illusions from approaching Baphomet!'; guide 1050903): 60 s in and every
  70 s one illusion 4390003 per player (two to six) appears on the outer hexagon 36-41; 6 hits per player kill one; each
  left after 20 s costs everyone 15% (at most 45%) as 4392801. **Scythe Dance** (tip 4314476; guide 1050901): at 60% and
  30% HP he holds sweeping (4391601) while a hell ring 4390002 lands under every player; still within 3 m after 5 s: 35%.
  Inferred: timing, sizes and damage; the illusions stand where they appear and never strike (an object's `still`),
  and the charge at a player (4314473) is not reproduced.

Policies below remain **(inferred)** except for explicitly identified client contracts; they do not establish
original-server encounter parity:

1. Boss placement uses a uniquely named `BOSS` scene-editor point (scene 3020101 point 93), otherwise the first
   ungrouped server point. Generic story waves group explicit server NPC
   points by ascending `GroupId`, with immediate advancement after every actor in the current group dies. A point
   respawn row can resolve a zero-ID editor point to a real NPC. A region respawn row supplies its real NPC group and
   count; spreading that group over authored inner points is inferred. Client-only actors and zero-template mechanics
   are not converted to enemies. Solo scenes without a resolved spawn source refuse entry.
2. Every remaining participant must finish scene loading before the clock starts. Loading expires after 60 seconds.
   The initiator is already ready; 30-second readiness comes from the configured public parameter.
3. Stage reporting uses one level and sequential stage numbers. The client confirms map states 0/1/2 as
   initializing/running/settling and the four stage/map timestamps as Unix milliseconds. Pending deadlines stay
   zero until all participants load. Variable 145 carries the actual participant list at startup (inferred).
   A preformed team is represented as squad 1.
4. Story-wave enemies immediately engage an available living participant through the existing AI/combat service.
   Multi-dungeon bosses retain their template AI without a forced target on entry. For Culvert's non-roaming
   scripted AI 25000001, waiting for a player hit/taunt is an (inferred) fallback: the original RaidFive server
   script is absent and no five-player preparation countdown/start-button contract was recovered.
   Killing all configured actors before the time limit wins; timeout, surrender or defeat loses.
5. For finite OnClickRevive counts, the boss's initial count is per participant. The existing map resurrection point,
   full HP and all-dead loss when no revival option remains are inferred. Scene 3120401 is explicitly NoRevive.
   The top-level automatic mode appears only in other gameplay types here; no script-only mode is given a generic
   self-revive bypass.
6. Only the leader may surrender or reset. Reset requires settlement, repeats admission/readiness and creates a new
   room. Continuations retain the original outside return point; they cannot combine unrelated private rooms.
7. Disconnect/exit detaches that member and cancels their pending ready queue. The remaining team may continue below
   the admission minimum. Reconnect returns outside without rejoining. The final departure destroys room actors.
8. Reward balances are keyed by group, boss-family tuple and difficulty. Refresh type 1 is daily; type 2 is weekly
   using `[weekday,hour]`, local server time (the shipped `[1,5]` means Monday 05:00). A new balance gets one configured
   increment, and later periods accumulate increments up to the configured cap. No allowance is spent on entry.
   Not original: `Ro3Emu:Testing:UnlimitedRewards` (a test realm switch, [activities](activities.md#test-realm-switches-ro3emutesting))
   pays every won clear without checking or spending the balance, and 5062 shows nothing used.
9. A first personal clear grants the same configured personal reward without spending a recurring balance.
   Separately, the boss's first-clear reward (`TenGameplayBossConfig._iPartAward`, MultiverseGroupConfig ->
   AwardConfig) is paid by claim: 5062 `isFirst` is 1 (Complete) once the boss is cleared and unpaid, and 82587 pays it,
   rolled once, in the commit that marks it paid (`State.FirstPassPaid`); `isFirst` is then 2 (Finish). Claiming a Hard
   boss also pays the Normal boss in the same `_kBoss` slot when that one is still unpaid ("you will directly Claim that
   Boss's Normal First Clear Rewards", Phantom Realm rules). A boss with `_iPartAward` 0 shows Finish once cleared.
   Paying by claim instead of at settlement (82584 `firstPassAward`), and offering it to roles cleared earlier, is the
   maintainer's choice *(inferred: the client supports both)*. The paid flags reset with the season, as the records do.
10. Normal spends its own extra balance first, then an unlocked same-type Hard balance, paying the selected Normal
    boss's reward. When Lua's same-type lookup has multiple Hard candidates, the lowest config id wins.
11. Team-boss world loot is suppressed because the personal award is paid with its allowance. Story results report
    actual inventory/currency deltas from existing NPC/task rewards; they do not synthesize a personal award.
12. A changed `(PlayBook, Start)` resets seasonal progress, allowances and report history, retaining role job/guide
    preferences. An attempt from a previous season ID or playbook cannot grant its old-season reward, including a new
    cycle with the same playbook. A session from the old cycle must relog before admission.
13. Keep 20 reports per participating role, including participant stats, with area/server id 1. Private room ids are
    persistent monotonically allocated integers and also identify stored results. Unknown/foreign results stay hidden.
14. Recovered error texts determine refusals, but the exact original-server mapping to each check is unproven.

Reconstructed story encounters (`ReconstructedDungeons`, switch `Ro3Emu:ReconstructedDungeons`, on by default):

- [dungeon-inventory.md](dungeon-inventory.md) (generated from the client data) lists every eligible scene with its
  spawn points, labels, quest stages and base map: 28 story scenes, 7 tutorial, 4 minigames, 29 Territory War zones
  and 4 developer test maps. The story scenes' combat templates (2000xxxx) exist only on the original server.
- `src/Ro3Emu.Server/Reconstructed/dungeon-monsters.json` (embedded) rebuilds 9 of them as waves of client-known
  NPCConfig monsters at authored points, one wave per quest stage, each spawn with its reason (point label, quest
  text, base map) and each dungeon with a confidence: 6 high, 2 medium, 1 low. Bosses use the client's story
  templates (530xxxx: Golden Thief Bug, Baphomet, Pharaoh, the airship's Evil Warriors/Archers/Boss,
  Elusive Pecopeco); minions use the base map's field monsters. No elite stat scaling, phase or mechanic is invented.
- Southern Payon's portal waves (1021301, quest 10102100) use the client's own encounter monsters instead: NPCConfig
  5001046-5001050 (Poring, Fabre, Boa, Poring, Willow; levels 15-18, AI 20200004 with a 150 m chase, placed nowhere
  else). The Poring 5001049 drops Card: Poring 1420300 (`_iNormalDrop` 241949, DropGroupConfig 144 `_iQuestLimit`
  10102100), the card the delivery dialogue 1010211001 mentions and guide 26 (after 10102110) has the player insert
  into the quest's Cotton Shirt. Which wave held which monster is **(inferred)**.
- Story templates have no normal attack in the client (their server scripts chose skills); `monsterAttacks` names
  the monster whose normal attack each borrows (its field MVP, or a human warrior/archer for the airship crews).
  This also gives Payon Forest's story Eddga (5300001) an attack.
- Not reconstructed as monster waves (listed in the file): pure scripted scenes without a fight (ant nest
  exploration, Deniro ride, Louie's game, Moonlight Flower's hide-and-seek), the MVP party event, the Ring Test map and
  scenes without points. The four scenes are rebuilt as scripted scenes (below).
  The Goblin Forest transfer is not reproduced. Maya's nest (1000401) moved to the scripted scenes below, with its Maya
  fight as a fight step, and so did the Egg Thief's portal waves (1021301). The airship's cannons are played as
  scripted stages (below).
- The task widget: each reconstructed wave names the DungeonQuestConfig quest it plays. 30401 carries var 10001
  `PDungeonQuestIdList` (the scene's quests up to the current one; the widget shows the last one's title and goals)
  and var 10003 `PDungeonQuestGoalParamList` (the wave's kills and size, the goal's "(done/total)"). Seen live: the
  widget reads vars from 30401 only, not from 30404, so every wave and every kill republishes 30401. Without these
  the widget showed its prefab placeholder ("Quest Name / Defend the Base").
- Quest target 101018 [scene, n] "dungeon pass" now counts a committed successful clear (`DungeonService.Completed`
  to `TaskService.OnDungeonClearedAsync`); entering is not a pass. Story steps waiting on a reconstructed scene
  (Payon Forest, the airship, Desert Crossroad, the sphinx, the Baphomet maze, Southern Payon) can now complete.
  Steps waiting on Louie's shell game in the Pharaoh branch story (3120703) or on Moonlight Flower (3120802) complete
  through their scripted scenes (below).
- Live check with the real client: Culvert waves 1-3 spawn and render with names and models, the
  tracker follows the Culvert quests with its counter, the story Golden Thief Bug fights back with its borrowed
  attack, the elite Scorpion run ends in DungeonResSuccess, leaving returns to the field. The client shows no result
  window for these Plane scenes (as with Payon Forest): 30403 only raises `LC_DunGeon_Base`'s DungeonSettlement event.
- A solo Plane scene (DungeonConfig `_iPlaneMapID` set, `_iRoleCntLimit` 1) returns the player to the field
  `DungeonService.PlaneExitSeconds` (5) seconds after a successful clear, through the exit button's own 30402
  (`LC_DunGeon_Base.CELL_LEAVE_DUNGEON` takes it unasked). **(inferred)**: the original encounter scripts are not in the
  client; without it a cleared plane left the player in an empty copy of the field (seen on Southern Payon 1021301,
  whose story continues on the field: 10102110 is delivered to NPC 400037 on map 10213). A failed run stays, so the
  client's DungeonReset can retry it.
- Guide arrows: a solo story scene whose scene editor file has guide routes (`m_kGuidePoints`, e.g. the Culvert
  plane's 19 and 21, the Geffen field plane's 17) sets dungeon var 10021 `PGuideWayList` to their ids in 30401
  (`LC_DunGeon_VarDefine.lua:736-741`, processed by `LC_SceneCom_GuideGfx`, which draws each route as the guide arrow
  line), and to an empty list in a 30404 var change after the clear. **(inferred)**: the scripts that set 10021 are
  not in the client, so all routes show from the start (a scene's routes chain into one path: the Culvert's 19 ends
  where 21 starts). `DungeonService.GuideWays`, `SceneEditorGuides`.
- Solo tuning: the story bosses were built for the original's allied NPC
  companions (mostly not reproduced); untuned, a level-matched player died in 3-36 s and needed 3-38 minutes to win.
  `soloTuning` scales each story template's HP and attack (basis points) so a Swordman at the boss level, every stat
  point spent STR:VIT 2:1, no equipment, normal attacks only, kills it in about 90 s and would fall in about 1.4 times
  that. `CombatService.Duel` computes both sides' expected damage per second from the same formula as a real hit
  (its roll-free part was extracted, rolls unchanged). The airship's nine boarders come in three groups of three, their
  attack at 20%. A boss whose scripted step shows companions falls to them and the player together: Payon Forest's
  Eddga (HP 1170 basis points) counts Boya's and Wule's expected damage per second standing in reach
  (`CombatService.CompanionDps`, their rotation and the same formula as their hits), about a quarter of the total.
  `DungeonTests.StoryBossTuning` keeps every boss and a boarder group inside the band. Live: the tuned
  Culvert Golden Thief Bug (4126 HP, hits of about 47) fell to a test character and the tracker showed (1/1).
- `DungeonTests.Reconstructed` resolves every reconstructed plan, clears each dungeon wave by wave to a win with the
  tracker vars checked, counts a clear toward its 101018 quest, and switches off.

Leaving after a win (`DungeonService.AutoLeave`): a won ordinary (non-team) run sends each player still in the room out
through the normal exit, with no countdown and no notice. The client has no story exit script and shows no result window
for these scenes. The timing follows the Genesis capture (2026-10-25, one run of each; Genesis differs from the official
beta): 250 ms after the result, or about 3 s after a story encounter's last closing line. It replaced a 15 s countdown
borrowed from the team-dungeon close time (`PublicParamConfig` FiveGamePlay_CloseTime) that Genesis does not use.

Story encounters (`Reconstructed/story-encounters.json`, same switch; `DungeonService.Encounter`):

- What the client holds: the boss and companion templates, their SkillConfig kits (story rows 5300101-5380101, named by
  `_kScriptLogic`), skill timelines (`data/skills`), the ChatConfig story lines (5300001-5380010, never referenced
  by client code, so server-pushed), the scene-editor points and the DungeonQuestConfig stages. What it does not: the
  encounter scripts, the boss behaviour trees (`_iAISpecial` MainStory_HuWang etc.; only MainStory_MuShi ships) and
  any hit shape (`_kRange` is empty). Every rotation, hit area, hit time, stage trigger and line timing in the file is
  chosen and says so.
- First encounter: Payon Forest Eddga (1022001), retimed from one Genesis run (traffic capture 2026-10-25; Genesis is a
  private server that differs from the official beta, and the entry's `source` says which values came from it). Air
  wall 32 is up for the whole run. Stage 1 "Cover Boya and Wule": the non-fighting Boya and Wule (5300080/81, camp 3,
  no skills) stand at points 23 and 21 and only talk; the boss leaves them alone. After 15 s the screen goes black for
  a second (`LC_DunGeon_Actions.BlackScreenMs`), the quest shows done (var 10003 [1,1]) and a second later Eddga casts
  5300901 on himself (its `_kBuff1`, buff 5301001). Stage 2 "Join the Adventurers" starts 2.5 s later: the swordman
  and priest at points 34-35, the mage, hunter, thief and merchant at 36-39 3.65 s in, and the six '2' adventurers at
  41-46 5.7 s in. Eddga casts the skills Genesis showed (jump impact and a second swing in both stages; ground flames
  and 5300801 from stage 2) between swings, hitting players and companions in the area fixed at the cast start; he
  retargets every 8 s (a player weighs twice a companion), and the swordman's Provoke pulls him. His closing lines
  come 6 s and 9 s after the win.
- Ground marks (`DungeonService.EncounterMarks`): Eddga's ground flames, jump and 5300801 place warnings the way the
  Genesis capture shows them: a 31109 from the boss with effect type 6 and an EffectData warning effect (its
  `m_kWarningRange` is the ring radius or the strip's length and width, and how long it shows), then, when it ends, a
  31109 of the damage effect (types 4 and 2) and the hit on everyone inside that shape (once per target per moment).
  5300801 also dashes Eddga (31112, effect 5300851, 600 ms). The positions are Genesis's from one run, turned with
  the boss's facing or aim; which fighter a "random" aim picks is (inferred). The second swing 5300601 has no marks in
  the capture and keeps a chosen cone.
- Companions are camp-1 monsters with scripted targeting: their normal swing is the kit's first damaging row
  (`MonsterService.FightMonster`), the rest is cast when ready (`CombatService.Allies`, same damage formula as other
  hits). The priest follows tree 10803: Blessing and Increase AGI, then Sanctuary when three are below 95% HP, else
  Heal. Companions stop at 1 HP; a companion's killing blow settles as the first loaded player's kill.
- Type-2 (Bubbling) lines go out as Genesis sends them: 1022 from MsgSource_npc with the speaker's uid and
  CHAT_PARAM_NPC_NAME its NPC id. Other lines are 30404 EventType 6 `Shouting(chat)`. Unverified live: whether a cast
  shows a telegraph. `!encounter` shows the stage and HPs; `!encounter next` starts the next stage (test aid).
- Eddga cannot fall in stage 1: hits stop at 1 HP until stage 2 starts (`MonsterUnit.HpFloor`). His HP
  is the client's again (`soloTuning` hp 10000; attack keeps the solo 1500): the twelve adventurers take about 300 HP/s
  off him, so stage 2 lasts about a minute.
- `DungeonTests.Encounter` checks every named row, the hit shapes, and the Eddga run as Genesis played it: bystanders,
  bubbles, the stage-1 end, the staggered arrivals and a companion kill settling the win.

Reconstructed scripted scenes (`Reconstructed/scripted-scenes.json`, same switch):

- A step may carry a fight: client-known monsters at scene-editor points (`fight`), spawned with the step; it then
  waits for every kill and, when it also lists events, for its event too. A fight-only step ends on its kills. A step
  with `all` needs every listed event, not any one, and its tracker counter follows them. A Journey scene (gameplay
  type 24) with a scripted reconstruction plays it instead of its bare point respawns. A step's `strike` makes an
  actor's interaction cast a skill from the player at the nearest monster of the fight; with `shots`, that many strikes
  defeat it whatever the damage (inferred, for the airship's cannons).
- A fight monster may come out `ms` after its step starts (the wave cannot end before the last one is out) and say a
  ChatConfig line once out (`say`): 1022 from MsgSource_npc with its uid and CHAT_PARAM_NPC_NAME its NPC id, 850 ms
  after it appears. A fight step that names its `goal` shows it on the tracker (var 10003) instead of its kills. A scene's
  `airWalls` raise those scene-editor air walls (m_kAirWalls) for the whole run: dungeon var 133 CAirWallList
  (LC_DunGeon_VarDefine, processed by LC_SceneCom_AirWall) in 30401. All four come from the Genesis traffic capture.
- Moonlight Flower's hide-and-seek (3120802) is played as a game, inferred as a whole from its ChatConfig lines (a step
  `seek`, DungeonService.HideAndSeek): her three opening bubbles over her head, then rounds of a warning
  (5310005), the 5 s countdown tip (5310010) and a look. A player within 3 m of a group 27 point (the darkness that
  rings the room) passes the round; anyone else is found (5310014) and the game starts over. A passed round searches
  the darkness within 8 m of the hiding place, so the second round needs another side of the room; it wins (5310007)
  and completes the step. The lines go out as 1022 with her uid, which the client's Bubbling rows need for the head
  bubble. `ScriptedScenePacePercent` shortens these timers for tests.
- The Egg Thief's portal waves (Southern Payon, 1021301) are rebuilt from one Genesis run (traffic capture
  2026-10-25; Genesis is a private server that differs from the official beta, so this is how Genesis runs it): the
  arena's air walls 31-34 go up, three neutral 5001051 stand on the arrow points 28-30 and three 5001018 just behind the
  portals, and three waves of 3, 6 and 9 monsters come out one by one, each type always from the same portal (25: Poring
  5001046 and the card Poring 5001049, 26: Fabre 5001047 and 5001050, 27: Boa 5001048), each saying 5380004. The
  tracker counts the waves (0/3 to 2/3) and the lines 5380005-5380010 are said at the captured moments. One run cannot
  tell fixed waves from rolled ones; the run is played as the fixed sequence. The scene keeps its 550 cm/s pace and
  room-sized leash so the monsters reach the player from the portals.
- Louie's shell game in the Pharaoh branch story (3120703, entered by task 205170 'A New Round') reuses the Culvert's
  gang, rings and Bells on the Culvert editor's layout moved 5 m south (`sc_pm_branchstory_sphinx_001`). It plays two
  rounds, each won by Katie's ring (400134, dungeon message 11); the other rings (message 12) do not end the round.
  Louis speaks through the step's `react` (DungeonService.Reaction): his Secretary rows go out as 1022 with Louie's uid,
  round 1 opening with 5310008 'Let's start the game!' and 5310002 'Guess which Bell my cat is hiding in!' (each once
  the previous one's `_iTime` has run out), round 2 with 5310002; a wrong ring answers 5310004 'Sorry, wrong guess!'
  and shatters the Bell under it (buff 5402201, SXGD_WRONG); Katie's ring answers 5310003 'Congratulations ...' in round
  1 and 5310009 'Darn ... I give up.' in round 2 and lights her Bell (5402101, SXGD_RIGHT), and the next round (or the
  settlement) waits until that line's `_iTime` has run out. Guessed: the two rounds (from Louis's lines 'Let's go again'
  and 'I give up'), where Katie hides in each round (the east Bell, then the west one), the Bell row 400138, and when
  each line and effect goes out; the effects have not been checked in the live client. The bubbles 5310019-5310033 are
  not sent.
- Four story dungeons that were monster waves now play their scripted stages around the fights, each with its
  evidence and guessed parts in the file (Payon Forest, 1022001, stays a story encounter, above: Eddga with Boya and
  Wule fighting beside the player):
  - Maya's nest (1000401): Maya, then 'Find the Professor' through the three dialogue regions on the way to the egg,
    'Free the Ant Egg' (5300056, interaction 5300021, message 5300022), 'Help the Professor Up' (5300026, message
    5300023) and the chest (5300052, message 5300024), each interaction opened by buff group 53014, with the scene's
    ChatConfig lines said along the way.
  - The airship (1000501): the chitchat with Bobo, the captain, Wule and Boya (their gated auto-range bubbles
    5300031-5300034, all four), the boarding and the Evil Boss as before, the look around after the battle (five
    bubbles 5300048-5300052) and the talk with the injured captain (dialogue 530003802, whose option triggers
    interaction 5300039 and message 5300039). Stage 2, the cannons, is **inferred**: each 'Board the Cannon' (5300006) on
    one of the three Cannons 5300022 fires 5365001 from the player at the nearest enemy airship (5300015 at point 24,
    5300053 at 61), and three boardings sink a ship at any level; sinking both ends the stage. The original's
    UpperCannon gameplay status (GameplayStatusConfig 12) has no abilities in the client data.
  - The Peco Peco nest (1051001): the Elusive Pecopeco with the Ancient Nest and Bragis shown.
  - Pharaoh (main story, 3120702): Pharaoh with the Relic he leaps out of, his coffin 5300013 at point 24, and the
    Pyramid 5300011 beside him (6 m east, 7 m north of point 20: the scene has no point for it). Its use is **inferred**
    from the raid Pharaoh's tip 4370002 'Pharaoh is unleashing Cleansing Light! Quickly, hide behind the Pyramid!': every
    20 s of the fight the tip goes out, Pharaoh holds still for 4 s, then his strongest skill 5340301 (whose effect the
    client draws as a cross of light) hits everyone outside the Pyramid's shadow (a step `shelter`, DungeonService.Shelter).
  - Prontera North Gate (1011301): stage 1 'Investigate the fork in the road' completes within 17 m of Baphomet's point 20, which takes in the end of the client's guide line 24 but not the born point (inferred), then stage 2 is Baphomet (5300003) at point 20.
- Companions (`DungeonService.Companions`, `CompanionUnit`): an actor marked `"companion": true` is shown as an allied
  fighter instead of a plain scene actor. From the client: only an NPCConfig `_iNPCType` 1 row of the player's camp
  (`_iCamp` 1) with a monster template and its own `_kSkills` qualifies (the catalog refuses the scene otherwise); its
  HP and attack come from that template (`_iAttribute`, `_kAttriScale`, `_iLevel`), each skill's cooldown, action and
  reach from SkillConfig (`_iCD`, `_iActionTime`, `_iDistanceMax`), its walking speed from NPCConfig `_iSpeed`, and
  its search cadence, attack tick, view range and chase distance from AIConfig `_iAI` (5300007: `_iSearchTime` 100,
  `_iAttackTickTime` 1000, `_iViewRange` 10000, `_iChaseDistance` 10000, `_iStayInPlace` 1). **Inferred** (the server
  script is not in the client): it attacks the nearest living monster of its room within view, with the first listed
  skill whose cooldown is ready and whose reach holds it, at most one cast per action and per attack tick; the hit
  lands at 40% of the action, as for tower pets. When no ready skill reaches the monster it walks toward it (31081,
  as a tower pet walks) until one does, never farther than `_iChaseDistance` from its point, and with no target it
  walks back to its point (`_iStayInPlace` 1 read as "goes back to its point", not "never moves"). Its casts show like a monster's (31100, 31101), deal the skill's damage through the shared
  formula and settle like a player's hit; a kill it makes counts for the run exactly like a player's (wave and step
  completion, statistics), credited to the first present, loaded participant who is alive, or the first present, loaded
  one when none is (so the run still completes). A monster it hits answers it with its normal attack once no player
  holds that monster. A player comes first: one it already fights, and for an aggressive monster one it sees, as when
  idle (the player scan keeps running while it answers a companion). A monster that cannot move stops answering a
  companion out of its reach, so its player scan and return home run as when idle. At 0 HP the companion dies, its body leaves
  after the template's remain time, and it does not come back in that run (`_kRespawnSkill` unused). Players cannot
  target it and players' area effects do not hit it (it is no monster); a boss special skill's area that hits tower
  pets hits the companions inside it too, through the same damage path as a monster's swing (at the boss skills'
  damage percent), and one it takes to 0 HP falls the same way. It never counts in the run's spawned or killed
  monsters. It is separate from the Pet Tower's pets, whose behaviour does not change. In Payon Forest the pair stand
  about 14 m from Eddga's point and reach 3.5-6 m, while Eddga's swing reaches 10 m: they walk up to him wherever the
  player fights him, so the solo tuning counts them as always in reach.


- The last three main-story blockers of the story walk: the ant nest vortex and egg passage (1000201, task 10104290),
  Bobo and the Deniro (1000301, task 10104300) and Louie's game in the Culvert (3120402, task 10106270). The original
  scene script is not in the client; each scene is rebuilt as steps, one per stage, every decision with its evidence
  and the scene's guessed parts listed in the file (confidence: medium for all three).
- Maya's nest (1000401, task 10104310), the last Ant Hell scene: it used to be only the Maya wave, so the run cleared
  after stage 1 and skipped the Professor. Now: a fight step (Maya 5300006 at point 21, the tracker counting the kill),
  then the search through the three dialogue regions 30, 29, 28 (a later region also counts, so missing one never
  strands a player), freeing the Professor from the ant egg 5300056 at point 32 (interaction 5300021, message 5300022),
  helping him up at point 26 (5300026, interaction 5300022, message 5300023) and opening the Precious Treasure Chest
  5300052 at point 34 (interaction 5300023, message 5300024). All three interactions are gated by buff group 53014
  (conditions 7100022-7100024), so only the current step's actor offers one. Bobo's and the Professor's lines
  (ChatConfig 5300501-5300511, 5300601-5300603) are said at the steps; which step says which line is chosen. The chest
  gives no item of its own (none is named in the client data); the clear is the usual settlement.
- A fight step (`spawns`: NPCConfig templates at server spawn points) spawns its monsters and completes when all are
  killed; it has no completing client event. A step's `lines` are said at its start (after `ms`), its `doneLines`
  as it completes, through the same 30404 calls as the story encounters (a type-2 bubble needs its speaker, one of the
  step's actors or monsters). A participant already standing in a new step's region completes it without moving.
- The vortex passage is a slide and the Deniro a vehicle (`DungeonService.Ride`). A step with `slide` puts its players
  on the client's own slide (LC_Movement_Controller_Slide, created by the local player's movement on its first slide
  custom data): 31018 CustomDataChange on the role with Player_SlideSpeed 5400003 (ListLong [side, down] in cm/s)
  then Player_SlideState 5400001 (Int 1); the client slides downhill, A/D steer (ChatConfig 5300306 says so), walks are
  reported every 0.1 s, and state 0 at the step's end stops it. Not stored as the role's custom data. 1000201's passage
  uses the controller's defaults (10 m/s down, 8 to the side) and ends in region 22.
- In 1000301 'Try to Ride the Deniro' (5300008, no status, buff 5365101) is step 2 and 'Ride the Deniro' (5300005,
  GameplayStatusConfig 3 vehicle driver, buff 5365401) the last step; they were swapped before. A ride interaction on a
  vehicle scene actor (NPCConfig _iNPCSubType 103, VehicleConfig seat) seats the player as WorldInteractionService
  does in the world: 32702 InteractChangeNtf (EType 3, status, interaction, actor uid) and 32704 Change3CNtf (seat
  _iChange3C 9). Only while the actor carries the buff its condition asks for. The seat's button 26 (32703) or exit
  interaction 4900025 gets the player off; the step's end (region 31), settlement and leaving do too. 31074 turns the
  rider.
- `fallback` completes a step by itself after its time (from its start, or `from: mount`), optionally moving the
  players to a point: the slide after 120 s, the ride 120 s after getting on, at region 31's point.
- A won scene with `next` goes straight on to that scene after 3 s *(inferred delay)* instead of the countdown out:
  1000201 continues into 1000301 (DungeonConfig 1000301 _iSameDupLoadingType 2). When the next scene refuses entry,
  the players are sent out as usual.
- The rest of Ant Hell: 1054001 (sc_anthell02_001_pl01, "Ant Hell (story)") is not entered by any task, item or NPC in
  the client data and has no spawn points or stages, and the Ant Hell field quests (10104120-10104280 on map 10540) are
  ordinary field steps the server already plays. The two Anthell dungeons 3120501/3302101 (sc_pm_pve5anthell) are team
  and other gameplay types, not story.
- A step names the DungeonQuestConfig quest the tracker shows (var 10001) and its goal counter (var 10003), the scene
  actors the server shows (client NPCConfig ids at scene-editor points, entity type 1 or 2 from `_iNPCType`, with the
  buffs that open their interactions), an optional story, the client events that complete it and an optional teleport
  after it. The engine is generic (`DungeonService.Scripted`): nothing knows a scene, task or NPC id.
  A step may also have a ride (`after.ride`, a `m_kGuidePoints` route): when a player completes it by using an actor,
  that actor walks the route at its own `UnitConfig._fSpeed` (the Deniro: 5.25 m/s) and the participants are moved
  with it in 100 ms forced-position steps, then the teleport and the next step follow (`DungeonService.Ride.cs`).
  **(inferred)**: the client has no ride rule (the route and the scene's camera animation are data with no trigger, and
  the original's server script is not shipped), so the speed, the step size and the missing mount are guesses and the
  camera animation (`mianstorybingyicamera`) is not played.
  A step may also hold (`after.hold`, ms, scaled by `ScriptedScenePacePercent`): once it is completed, the next step or the
  result waits that long, so what the step showed is not cut. **(inferred)**: the original script is not shipped and the
  client data has no duration for these, so the airship's holds are estimates (the chitchat's 13 s is the longest of its four
  bubbles, 12 to 22 words each at `PublicParamConfig.BubblePerWordTime` 2; 2 s between boarding groups, 3 s after the Evil Boss,
  4.5 s for the captain's closing line). When a scripted scene is settled its defeated enemies keep their bodies and go by their
  own `_iBodyRemainTime` (Eddga's body stays where it fell); the living ones and the scene actors are cleared as before.
  An actor interaction whose `NPCInteractConfig._iStatusID` is a vehicle status (the Deniro's first Ride, 5300005)
  seats the player in the actor's seat through `WorldInteractionService` (32702, seat 32704; the client's exit button
  works as for any vehicle); the actor puts them down after 2 s (`SeatedRideMs`, **(inferred)**: nothing in the
  client says how long the first, failing ride lasts) or when they leave the scene. The second Ride (5300008) has no
  gameplay status, so the player is not seated during the ride to the depths.
- Client events: 32700 on a scene actor (its own UID namespace) with one of the actor's `_iInteractID`, also counted as
  the dungeon message of that interaction's server-hosted action 210 `SendDungeonMessage` (the client never runs it);
  a walk into a server region (regionType 2; no client consumer registers them, so the client never reports them;
  `LC_AreaHelper.CheckInArea` geometry) or near a point; 84011 at a story's end. The 32700 request is always echoed:
  the client runs the interaction's client-hosted actions (e.g. 53 opens a dialogue) from the reply.
- Interaction gates: an interaction's `_iNPCCondition` 1004 CheckHaveBuff asks for a buff group on the actor; the step
  that waits on it gives its actor that buff (in the entity's AoIBuffInfo, and 31104/31105 + 31048 when it changes on
  a kept actor). In 1000301 the five interactions are gated by five different buffs, so exactly one is open per step.
- Stories: dungeon var 100020016 (`DungeonVariableFunctionConfig`, start function PlayCutSceneStory) with [plot] plays
  a PlotInterpretation run-time story; at its end (also when skipped) action 601 sends 84011 and action 150 sends 84012
  (PlotActingTrigger); either completes the step, neither is answered (fire-and-forget in the client). While the story
  plays, every participant has cutscene protection (`GameSession.CutsceneProtected`): no damage lands and monsters hold
  their swing until 84011/84012/32709, the player's own cast, a scene change, or 120 s *(inferred)*. The var is sent
  once in a 30404 var-change event and
  never kept in 30401 (the client calls the function on every push). Louie's game plays stories 1013 and 1014 (plots
  140010 and 140020), which no task triggers and which show this Culvert, Louie's gang and dialogues 1010627001-004.
- The next step (or the settlement) after a completing event runs in its own owner scope (`GameLoop.PostLegacy`): the
  route of the message that completed it does not reserve the settlement's role locks. Scene actors are room props
  (`InstanceService.AddPropAsync`), removed at settlement and before the room's final cleanup.
- Guessed (see each scene's "guessed" list): the corridor region that ends 1000201's first stage, Bobo's place there,
  the jump as a teleport, the slide's speeds, the ride to the depths as a walk of guide route 36 (see below), no exploding
  ant eggs, no seamless 1000201 to 1000301 transfer, which bell hides Katie (from both stories), a wrong guess changing
  nothing, the instrument retrieved within 4 m of mark point 37.
- `DungeonTests.Scripted` resolves each scene against the client data, plays each one through its client events (wrong
  and premature events included) to a clear, and checks that the story step is delivered and the next one offered.
  The story walk plays these scenes with the same events (a fight step by its kills) and has no known-open blocker left.
- Offering an interaction is the client's own (LC_InteractManager, every 0.2 s from the player's position, so a teleport
  is enough): the unit registers when NPCConfig _iInteractID[1] is set, is in range within its _iInteractDistance (3D;
  Louie's bell rings 2 m, Bobo 2 m, the Deniro 4 m, the fluorescent substance and the vortex 5 m), and of the units in
  range the highest _iInteractRank, then the nearest, is possessed; mode 2 (the rings, the vortex) acts by itself on
  entering range, mode 3 shows a skill button. DungeonTests.ScriptedClient replays the wire data the client received
  (31037, 31104/31105) against these rules for every step. GM test aid: !actors lists the step and its actors, !actors
  <npcId> puts the player on one.

Not implemented or unqualified:

- Of 65 eligible ordinary dungeon config rows (64 have an editor file, representing 60 distinct exports), 1022001 joins editor point 20
  to NPC 5300001 through `NpcLevelPointRespawnConfig`; 4400001 joins region 52 to NPC 4400001 and a configured
  count of 50 through the region and group respawn tables. The remaining 63 rows have no resolved combat spawn source
  in the exported tables. `DungeonStageConfig` is empty. The available `DungeonLua_1000201` stage/skill modules are
  empty; `monsterType=1` ant eggs are client actors. Those unresolved entries return DungeonDataMissing unless a
  reconstructed encounter (above) covers the scene. See
  [dungeon-inventory.md](dungeon-inventory.md) for the per-scene inventory.
- Exported team-boss identity and scene are used, but boss phases, scripted waves/delays, mechanics and task scores
  are not recovered from UI phase descriptions. The team test qualifies a basic boss combat loop, not its encounter.
- Tactical-team changes: a run keeps its preformed squad (`DungeonService.SendRosterAsync`); there is no reorganization.
- Auction lots come only from the boss's configured `_iGuildAward` roll; `_iPartAward` and preview items never become
  lots.
- 30123 belongs to Dragon Island and 30132 to Dragon Island/LeisureForty client paths, so ordinary dungeons do not
  emit those mode-specific pushes. The 30126 alias/scoring formula is unverified; no fabricated scoring push is sent.
- Full client UI behavior, especially empty auction/first-pass repeated fields in 82584. This has not been checked
  in the original client; protobuf/wire tests do not prove screen rendering.

Tests (`DungeonTests`, `TowerTests`, `TowerShopTests`, `FirstPassAwardTests`):

- The revive case moves the player with the authoritative in-map teleport before its wire kill; the server rejects
  a 33 m walk immediately after a revive as a speed violation.
- The real-config team case uses a leader plus two bots (50101 requires at least three), completes readiness and
  scene loading, sets the configured boss to 1 HP, then kills it with a client combat request. It verifies one payout
  per role and scoped statistics/exit; this is not full-health encounter qualification.
- Other cases cover ready cancellation, live season changes, forged requests/results, NoRevive, disconnect during
  loading, reset, reward exhaustion, daily accrual/cap and transaction rollback. Weekly refresh boundaries are checked.
- The 1022001 test proves refusal before its running story task, accepts task 10102200 through the persisted task
  service, enters the real point-respawn scene, kills its NPC through the wire and receives a result. The 4400001
  test spawns its source-backed 50-actor region plan. Separate two-wave execution uses a synthetic host-local
  editor fixture. The exported data is never modified; these tests do not claim complete solo script parity.

## Dungeon task trackers

TaskConfig type 4 (Dungeon) and type 24 (RingTask) son types 2/3 with an `_iDungeonID` are the run's tracker. The
client shows them only inside that dungeon (`LC_TaskHelper`); they carry no award. `TaskService.DungeonTasks` chains
each scene's rows by `_kPreTask` and shows the first unmet step as an extra 5027 entry, never stored or delivered.
DungeonService reports the step: the Dracula lair's wave, the last step (the boss) for a Guild Challenge stage, and
over at settlement. The siege reports its fight stage, and its 210003/200001 steps read the participant's assists and
score. A siege assist is a hit on the victim in the 10 s before another participant's kill (inferred, Battle Merit's
rule). Boneyard and Whaleback Chute (3011111/3011112/3011123/3011124) have trackers but no Guild Challenge entry.
