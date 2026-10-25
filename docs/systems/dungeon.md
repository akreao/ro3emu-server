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
- `src/Ro3Emu.Server/Reconstructed/dungeon-monsters.json` (embedded) rebuilds 10 of them as waves of client-known
  NPCConfig monsters at authored points, one wave per quest stage, each spawn with its reason (point label, quest
  text, base map) and each dungeon with a confidence: 7 high, 2 medium, 1 low. Bosses use the client's story
  templates (530xxxx: Golden Thief Bug, Baphomet, Pharaoh, the airship's Evil Warriors/Archers/Boss,
  Elusive Pecopeco); minions use the base map's field monsters. No elite stat scaling, phase or mechanic is invented.
- Story templates have no normal attack in the client (their server scripts chose skills); `monsterAttacks` names
  the monster whose normal attack each borrows (its field MVP, or a human warrior/archer for the airship crews).
  This also gives Payon Forest's story Eddga (5300001) an attack.
- Not reconstructed as monster waves (listed in the file): pure scripted scenes without a fight (ant nest
  exploration, Deniro ride, Louie's game, Moonlight Flower's hide-and-seek), the MVP party event, the Ring Test map and
  scenes without points. The first three are rebuilt as scripted scenes (below).
  Airship cannons, allied NPC companions and the Goblin Forest transfer are not reproduced. Maya's nest (1000401) moved
  to the scripted scenes below, with its Maya fight as a fight step, and so did the Egg Thief's portal waves (1021301).
- The task widget: each reconstructed wave names the DungeonQuestConfig quest it plays. 30401 carries var 10001
  `PDungeonQuestIdList` (the scene's quests up to the current one; the widget shows the last one's title and goals)
  and var 10003 `PDungeonQuestGoalParamList` (the wave's kills and size, the goal's "(done/total)"). Seen live: the
  widget reads vars from 30401 only, not from 30404, so every wave and every kill republishes 30401. Without these
  the widget showed its prefab placeholder ("Quest Name / Defend the Base").
- Quest target 101018 [scene, n] "dungeon pass" now counts a committed successful clear (`DungeonService.Completed`
  to `TaskService.OnDungeonClearedAsync`); entering is not a pass. Story steps waiting on a reconstructed scene
  (Payon Forest, the airship, Desert Crossroad, the sphinx, the Baphomet maze, Southern Payon) can now complete.
  Steps waiting on a scene that is still not reconstructed stay blocked as before: Louie's game in the Pharaoh
  branch story (3120703) and Moonlight Flower (3120802).
- Live check with the real client: Culvert waves 1-3 spawn and render with names and models, the
  tracker follows the Culvert quests with its counter, the story Golden Thief Bug fights back with its borrowed
  attack, the elite Scorpion run ends in DungeonResSuccess, leaving returns to the field. The client shows no result
  window for these Plane scenes (as with Payon Forest).
- Solo tuning: the story bosses were built for the original's allied NPC
  companions (not reproduced); untuned, a level-matched player died in 3-36 s and needed 3-38 minutes to win.
  `soloTuning` scales each story template's HP and attack (basis points) so a Swordman at the boss level, every stat
  point spent STR:VIT 2:1, no equipment, normal attacks only, kills it in about 90 s and would fall in about 1.4 times
  that. `CombatService.Duel` computes both sides' expected damage per second from the same formula as a real hit
  (its roll-free part was extracted, rolls unchanged). The airship's nine boarders come in three groups of three, their
  attack at 20%. `DungeonTests.StoryBossTuning` keeps every boss and a boarder group inside the band. Live: the tuned
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
  the jump as a teleport, the slide's speeds, no exploding ant eggs, which bell hides
  Katie (from both stories), a wrong guess changing nothing, the instrument being the Strange Device (400046) at mark
  point 37, picked up with its collect interaction 40004601.
- A step's guide line: `"guide": [ids]` names scene-editor guide points (m_kGuidePoints) that the client draws as guide
  arrow lines while the step runs: dungeon var 10021 PGuideWayList (LC_SceneCom_GuideGfx), sent in a 30404 var-change
  event when the step starts and, empty, when it completes, and kept in 30401 while it runs. Louie's game draws guide
  point 38 from the bells to the instrument.
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
