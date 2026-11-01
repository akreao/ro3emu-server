# Leaderboards

Code: `src/Ro3Emu.Server/Micro/RankService.cs` (the boards). Tests: `RankServiceTests`
(client data).

## Boards

The client reads the boards over the micro gate (`RankService`: `rangeUserRank`, `queryUserRank`,
`getUserRankSurrounding`, LC_DAL_Rank.lua). The server builds every board from committed role and guild state,
offline roles included, and caches it for `PublicParamConfig.LeaderBoardRefreshIntervalTs` seconds. The class summary
lists each board's source and tie-break.

## Rank rewards

The daily rank rewards (`LeaderBoardsConfig._iRankRewards`, `_iMailId`) are described in
[leaderboard rewards](leaderboard-rewards.md).
